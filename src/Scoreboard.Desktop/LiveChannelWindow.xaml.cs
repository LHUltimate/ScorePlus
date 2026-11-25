using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class LiveChannelWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly MediaPlayback? _playback;
    readonly AutomationRuntime? _automation;
    readonly string _theme;
    readonly ChannelStreamServer _stream = new();
    readonly DispatcherTimer _streamTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly VenueStore _venue;
    readonly DispatcherTimer _venueWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<int, CameraFeed> _cameraFeeds = [];
    readonly Dictionary<int, Action<BitmapSource>> _cameraHandlers = [];
    readonly HashSet<int> _ownedCameras = [];
    readonly Dictionary<int, BitmapSource> _cameraFrames = [];
    string _displayId;
    string? _viewChannelId;
    string? _viewLayoutId;
    string _venueSignature = "";
    ScorebugCanvasView? _channelView;
    bool _refreshing;

    sealed record EventRow(string Time, string Text);
    sealed record OverlayChoice(string LayoutId, string Label) { public override string ToString() => Label; }

    static readonly (string Background, string Border, string Foreground)[] TileColours =
    [
        ("#1D3C50", "#3D8FB8", "#BFE7FA"), ("#2C2440", "#8A6BC8", "#D9C9FF"),
        ("#163C32", "#38D7A3", "#9BF2D7"), ("#3F2E18", "#D9A441", "#FFE0A3"),
        ("#3C1B28", "#D45C86", "#FFC1D4"), ("#23313F", "#6E8BA5", "#D2E1EE")
    ];

    // The colour menu is deliberately a short, readable palette rather than a full colour picker.
    // These are visual labels on the monitor buttons; they never publish a layout.
    static readonly (string Name, string Hex)[] LayoutColourChoices =
    [
        ("Red", "#B52B32"), ("Blue", "#2467B3"), ("Purple", "#6842A8"),
        ("Green", "#24754F"), ("Gold", "#A56B16"), ("Pink", "#A43A68"),
        ("Slate", "#34495E"), ("Charcoal", "#2B2F36")
    ];

    public LiveChannelWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback = null,
        AutomationRuntime? automation = null, string theme = "Dark", VenueStore? venueStore = null)
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _automation = automation;
        _theme = theme;
        _venue = venueStore ?? VenueStore.Load();
        _displayId = _catalog.ActiveDisplay.Id;
        ThemeManager.Apply(this, theme);
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        _stream.HealthProvider = BuildStreamHealth;
        _streamTimer.Tick += (_, _) => CaptureStreamFrames();
        _venueWatch.Tick += (_, _) => RefreshVenueIfChanged();
        _venueWatch.Start();
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
            _streamTimer.Stop();
            _venueWatch.Stop();
            StopCameras();
            _stream.Dispose();
            DisposeView();
        };
    }

    void CatalogChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CatalogChanged); return; }
        if (_refreshing) return;
        Refresh();
    }

    void EngineChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(EngineChanged); return; }
        UpdateEvents();
        UpdateClock();
    }

    void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var display = _catalog.FindDisplay(_displayId) ?? _catalog.ActiveDisplay;
            var channel = _viewChannelId is null ? null : _venue.State.Channels.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, _viewChannelId, StringComparison.OrdinalIgnoreCase));
            if (_viewChannelId is not null && channel is null) _viewChannelId = null;
            if (channel is not null && _catalog.FindDisplay(channel.DisplayId) is { } channelDisplay)
                display = channelDisplay;
            _displayId = display.Id;
            var publishedLayout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
            if (channel is null)
            {
                if (string.IsNullOrWhiteSpace(_viewLayoutId) || display.Layouts.All(candidate =>
                        !string.Equals(candidate.Id, _viewLayoutId, StringComparison.OrdinalIgnoreCase)))
                    _viewLayoutId = publishedLayout?.Id;
            }
            var layout = channel is not null
                ? ChannelLayout(channel)
                : display.Layouts.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, _viewLayoutId, StringComparison.OrdinalIgnoreCase)) ?? publishedLayout;
            var live = channel is null && layout is not null && publishedLayout is not null
                && string.Equals(layout.Id, publishedLayout.Id, StringComparison.OrdinalIgnoreCase);
            ChannelName.Text = channel is null ? display.Name.ToUpperInvariant() : $"{channel.Name.ToUpperInvariant()} · CHANNEL";
            NowPlayingText.Text = layout is null
                ? (channel is null ? "No layout on air" : "No layout assigned to this channel")
                : $"{layout.Name} · {layout.Width} × {layout.Height}"
                    + (live ? " · ON AIR" : channel is not null ? " · CHANNEL OVERLAY" : " · MONITOR ONLY");
            OnAirText.Text = channel is not null ? "● CHANNEL" : live ? "● ON AIR" : "● MONITOR";
            OnAirBadge.Background = new SolidColorBrush(channel is not null ? Color.FromRgb(0x17, 0x34, 0x3A)
                : live ? Color.FromRgb(0x3A, 0x17, 0x17) : Color.FromRgb(0x3A, 0x2E, 0x17));
            OnAirText.Foreground = new SolidColorBrush(channel is not null ? Color.FromRgb(0x7F, 0xD1, 0xFF)
                : live ? Color.FromRgb(0xFF, 0x9B, 0x9B) : Color.FromRgb(0xFF, 0xD1, 0x66));
            DisposeView();
            if (layout is not null)
            {
                _channelView = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
                    playback: null, layoutProvider: () => layout, liveValues: true)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    IsHitTestVisible = false
                };
                ChannelHost.Content = _channelView;
            }
            BuildOutputStrip();
            BuildChannelOverlay(channel);
            LayoutMonitorsLabel.Text = channel is null
                ? "LAYOUT MONITORS · CLICK TO VIEW · RIGHT-CLICK FOR COLOUR"
                : "DISPLAY LAYOUTS · CLICK TO VIEW · RIGHT-CLICK FOR COLOUR";
            BuildActions(display, channel);
            BuildHealth();
            UpdateEvents();
            UpdateClock();
            StatusText.Text = channel is null
                ? $"LIVE CHANNEL · {display.Name} · {display.Layouts.Count} layouts · view only · {_engine.State.Events.Count} events"
                : $"LIVE CHANNEL · {channel.Name} · {ChannelLayout(channel)?.Name ?? "no layout"} · channel overlay · {_engine.State.Events.Count} events";
            _venueSignature = VenueSignature();
        }
        finally { _refreshing = false; }
    }

    string VenueSignature() => string.Join("|", _venue.State.Channels.Select(channel =>
        $"{channel.Id}:{channel.Name}:{channel.DisplayId}:{channel.LayoutId}:{channel.CameraIndex}:{channel.Enabled}"))
        + "|" + string.Join("|", _venue.State.LayoutColors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    void RefreshVenueIfChanged()
    {
        if (_refreshing) return;
        var signature = VenueSignature();
        if (string.Equals(signature, _venueSignature, StringComparison.Ordinal)) return;
        _venueSignature = signature;
        Refresh();
    }

    void DisposeView()
    {
        ChannelHost.Content = null;
        _channelView?.Dispose();
        _channelView = null;
    }

    void BuildOutputStrip()
    {
        OutputStrip.Children.Clear();
        for (var index = 0; index < _catalog.Displays.Count; index++)
        {
            var display = _catalog.Displays[index];
            var selected = _viewChannelId is null
                && string.Equals(display.Id, _displayId, StringComparison.OrdinalIgnoreCase);
            var layout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
            var tile = new Button
            {
                Width = 158, Height = 110, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 0),
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0x38, 0xD7, 0xA3) : Color.FromRgb(0x2E, 0x3B, 0x4A)),
                ToolTip = $"{display.Name}\n{layout?.Name ?? "no layout"}"
            };
            ThemeManager.SetSkipTheme(tile, true);
            var stack = new StackPanel();
            var image = new Image { Source = layout is null ? null : Thumbnail(layout, 146, 58), Height = 58, Stretch = Stretch.UniformToFill };
            stack.Children.Add(new Border { Height = 58, Background = new SolidColorBrush(Color.FromRgb(5, 8, 12)), Child = image, ClipToBounds = true });
            stack.Children.Add(new TextBlock
            {
                Text = $"{index + 1}. {display.Name}", FontSize = 9, Margin = new Thickness(2, 3, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(selected ? Color.FromRgb(0x8D, 0xEB, 0xCB) : Color.FromRgb(0x93, 0xA0, 0xB2))
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{display.Layouts.Count} layouts · {display.PublishedIndex + 1} on air",
                FontSize = 8, Margin = new Thickness(2, 1, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E))
            });
            tile.Content = stack;
            var target = display.Id;
            tile.Click += (_, _) =>
            {
                _displayId = target;
                _viewChannelId = null;
                _viewLayoutId = null;
                Refresh();
            };
            OutputStrip.Children.Add(tile);
        }
        if (_venue.State.Channels.Count > 0)
        {
            OutputStrip.Children.Add(new Border
            {
                Width = 1, Height = 94, Margin = new Thickness(2, 7, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x3B, 0x4A))
            });
            foreach (var channel in _venue.State.Channels)
                OutputStrip.Children.Add(BuildChannelTile(channel));
        }
    }

    Button BuildChannelTile(VenueChannel channel)
    {
        var display = _catalog.FindDisplay(channel.DisplayId);
        var layout = ChannelLayout(channel);
        var selected = string.Equals(_viewChannelId, channel.Id, StringComparison.OrdinalIgnoreCase);
        var tile = new Button
        {
            Width = 158, Height = 110, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0x7F, 0xD1, 0xFF) : Color.FromRgb(0x2E, 0x3B, 0x4A)),
            Opacity = channel.Enabled ? 1 : 0.55,
            Tag = channel.Id,
            ToolTip = $"{channel.Name}\n{layout?.Name ?? "no layout"} · CAM {channel.CameraIndex}\nView only — this never changes the output"
        };
        ThemeManager.SetSkipTheme(tile, true);
        var stack = new StackPanel();
        var image = new Image
        {
            Source = layout is null ? null : Thumbnail(layout, 146, 58),
            Height = 58, Stretch = Stretch.UniformToFill
        };
        stack.Children.Add(new Border
        {
            Height = 58, Background = new SolidColorBrush(Color.FromRgb(5, 8, 12)),
            Child = image, ClipToBounds = true
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"CHANNEL · {channel.Name}", FontSize = 9, Margin = new Thickness(2, 3, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(selected ? Color.FromRgb(0xBF, 0xE7, 0xFA) : Color.FromRgb(0x93, 0xA0, 0xB2))
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{display?.Name ?? "No display"} · CAM {channel.CameraIndex} · {(channel.Enabled ? "ENABLED" : "OFF")}",
            FontSize = 8, Margin = new Thickness(2, 1, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E))
        });
        tile.Content = stack;
        tile.Click += (_, _) => SelectChannel(channel);
        return tile;
    }

    /// <summary>Selections made from the Venue window open Live Channel already watching that
    /// channel, without publishing its overlay to a display.</summary>
    public void ShowChannel(string channelId)
    {
        var channel = _venue.State.Channels.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (channel is not null) SelectChannel(channel);
    }

    void SelectChannel(VenueChannel channel)
    {
        _viewChannelId = channel.Id;
        _viewLayoutId = null;
        if (_catalog.FindDisplay(channel.DisplayId) is { } display) _displayId = display.Id;
        Refresh();
    }

    void StartStream_Click(object sender, RoutedEventArgs e)
    {
        if (_stream.Start(8099))
        {
            EnsureCameras();
            _streamTimer.Start();
            StreamStatusText.Text = $"STREAM ON · {_stream.BaseUrl}/health";
            StatusText.Text = $"LIVE CHANNEL · headend on {_stream.BaseUrl} · every display has an .mjpeg URL";
            CaptureStreamFrames();
            BuildHealth();
        }
        else
        {
            StreamStatusText.Text = _stream.Error ?? "Stream could not start.";
        }
    }

    void StopStream_Click(object sender, RoutedEventArgs e)
    {
        _streamTimer.Stop();
        _stream.Stop();
        StopCameras();
        StreamStatusText.Text = "STREAM OFF";
        BuildHealth();
    }

    /// <summary>
    /// Opens only the cameras a configured venue channel actually uses. A camera already opened by
    /// the media desk is shared and left running; one this page opened is stopped when the stream
    /// stops, so a channel does not leave the webcam light on overnight.
    /// </summary>
    void EnsureCameras()
    {
        var wanted = _venue.State.Channels
            .Where(channel => channel.Enabled)
            .Select(channel => Math.Clamp(channel.CameraIndex, 1, 64))
            .Distinct()
            .ToHashSet();
        foreach (var index in _cameraFeeds.Keys.Where(index => !wanted.Contains(index)).ToArray())
        {
            if (_cameraHandlers.Remove(index, out var handler)) _cameraFeeds[index].FrameReady -= handler;
            _cameraFeeds.Remove(index, out var feed);
            _cameraFrames.Remove(index);
            if (_ownedCameras.Remove(index)) feed?.Stop();
        }
        foreach (var index in wanted)
        {
            if (_cameraFeeds.ContainsKey(index)) continue;
            // Venue channels number cameras from 1 for the operator; OpenCV opens from 0.
            var feed = CameraFeed.Get(index - 1);
            void OnFrame(BitmapSource frame)
            {
                if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => _cameraFrames[index] = frame)); return; }
                _cameraFrames[index] = frame;
            }
            feed.FrameReady += OnFrame;
            _cameraFeeds[index] = feed;
            _cameraHandlers[index] = OnFrame;
            if (feed.IsLive) continue;
            feed.Start();
            _ownedCameras.Add(index);
        }
    }

    void StopCameras()
    {
        foreach (var (index, feed) in _cameraFeeds)
            if (_cameraHandlers.TryGetValue(index, out var handler)) feed.FrameReady -= handler;
        foreach (var index in _ownedCameras) _cameraFeeds.GetValueOrDefault(index)?.Stop();
        _cameraFeeds.Clear();
        _cameraHandlers.Clear();
        _cameraFrames.Clear();
        _ownedCameras.Clear();
    }

    void CaptureStreamFrames()
    {
        if (!_stream.IsRunning) return;
        EnsureCameras();
        foreach (var display in _catalog.Displays)
        {
            var layout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
            if (layout is null) continue;
            var frame = JpegFrame(layout, 480, 270);
            if (frame.Length > 0) _stream.UpdateFrame(display.Id, frame);
        }
        // A channel is a camera with the scorebug composited over it. Its URL is separate from the
        // clean display feed, so an operator can send either the graphic alone or the full channel.
        foreach (var channel in _venue.State.Channels.Where(channel => channel.Enabled))
        {
            var layout = ChannelLayout(channel);
            if (layout is null) continue;
            _cameraFrames.TryGetValue(Math.Clamp(channel.CameraIndex, 1, 64), out var camera);
            var frame = JpegFrame(layout, 480, 270, camera);
            if (frame.Length > 0) _stream.UpdateFrame(channel.Id, frame);
        }
    }

    ScorebugLayout? ChannelLayout(VenueChannel channel)
    {
        var direct = _catalog.FindLayout(channel.LayoutId);
        if (direct is not null) return direct;
        var display = _catalog.FindDisplay(channel.DisplayId);
        return display?.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
    }

    IReadOnlyList<ChannelHealth> BuildStreamHealth()
    {
        var rows = new List<ChannelHealth>();
        foreach (var display in _catalog.Displays)
        {
            var layout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
            rows.Add(new ChannelHealth(display.Id, display.Name, layout?.Name ?? "no layout",
                _stream.IsRunning ? "LIVE" : "READY", $"{_stream.BaseUrl}/display/{display.Id}.mjpeg"));
        }
        foreach (var channel in _venue.State.Channels)
        {
            var display = _catalog.FindDisplay(channel.DisplayId);
            var layout = ChannelLayout(channel);
            rows.Add(new ChannelHealth(channel.Id, channel.Name,
                $"{layout?.Name ?? display?.Name ?? "unassigned"} · CAM {Math.Clamp(channel.CameraIndex, 1, 64)}",
                channel.Enabled ? (_stream.IsRunning ? "LIVE" : "READY") : "OFF",
                $"{_stream.BaseUrl}/channel/{channel.Id}.mjpeg"));
        }
        return rows;
    }

    void BuildHealth()
    {
        HealthPanel.Children.Clear();
        var health = BuildStreamHealth();
        foreach (var row in health)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2), ToolTip = row.Url };
            var dot = new Border
            {
                Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(row.Status == "LIVE" ? Color.FromRgb(0x38, 0xD7, 0xA3)
                    : row.Status == "OFF" ? Color.FromRgb(0x5C, 0x6B, 0x7E) : Color.FromRgb(0xD9, 0xA4, 0x41)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(dot, Dock.Left);
            line.Children.Add(dot);
            line.Children.Add(new TextBlock
            {
                Text = $"{row.Name} · {row.Layout}", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0xD2, 0xE1, 0xEE)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var state = new TextBlock
            {
                Text = row.Status, FontSize = 8, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 16, 0),
                Foreground = new SolidColorBrush(row.Status == "LIVE" ? Color.FromRgb(0x8D, 0xEB, 0xCB)
                    : Color.FromRgb(0x93, 0xA0, 0xB2))
            };
            DockPanel.SetDock(state, Dock.Right);
            line.Children.Insert(0, state);
            HealthPanel.Children.Add(line);
        }
        StreamStatusText.Text = _stream.IsRunning
            ? $"STREAM ON · {_stream.BaseUrl}/health"
            : "STREAM OFF · Start stream to publish every display";
    }

    byte[] JpegFrame(ScorebugLayout layout, int width, int height, BitmapSource? camera = null)
    {
        try
        {
            var root = new Grid
            {
                Width = width, Height = height, Background = Brushes.Black, ClipToBounds = true
            };
            if (camera is not null)
            {
                var picture = new Image { Source = camera, Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                root.Children.Add(picture);
            }
            var view = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
                playback: null, layoutProvider: () => layout, fillBox: camera is not null,
                transparentBackdrop: camera is not null)
            {
                Width = width, Height = height
            };
            root.Children.Add(view);
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            view.Dispose();
            return output.ToArray();
        }
        catch { return []; }
    }

    void BuildChannelOverlay(VenueChannel? channel)
    {
        ChannelOverlayPanel.Children.Clear();
        if (channel is null)
        {
            ChannelOverlayPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ChannelOverlayPanel.Visibility = Visibility.Visible;
        ChannelOverlayPanel.Children.Add(new TextBlock
        {
            Text = "CHANNEL OVERLAY", FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)), Margin = new Thickness(1, 0, 1, 4)
        });
        var choices = new List<OverlayChoice> { new("", "Follow this display's on-air layout") };
        foreach (var display in _catalog.Displays)
            foreach (var layout in display.Layouts)
                choices.Add(new OverlayChoice(layout.Id, $"{display.Name} · {layout.Name}"));
        var selected = choices.FirstOrDefault(choice =>
            string.Equals(choice.LayoutId, channel.LayoutId, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(channel.LayoutId))
        {
            selected = new OverlayChoice(channel.LayoutId, "Missing layout · choose another");
            choices.Insert(1, selected);
        }
        var box = new ComboBox
        {
            ItemsSource = choices, DisplayMemberPath = nameof(OverlayChoice.Label),
            SelectedItem = selected ?? choices[0], MinHeight = 30, Margin = new Thickness(0, 0, 0, 3),
            ToolTip = "Switch this channel's overlay. This does not change any display's on-air layout."
        };
        box.SelectionChanged += (_, _) =>
        {
            if (_refreshing || box.SelectedItem is not OverlayChoice choice) return;
            var layoutId = choice.LayoutId.Trim();
            if (string.Equals(channel.LayoutId, layoutId, StringComparison.OrdinalIgnoreCase)) return;
            channel.LayoutId = layoutId;
            _venue.Save();
            _venueSignature = "";
            Refresh();
        };
        ChannelOverlayPanel.Children.Add(box);
        ChannelOverlayPanel.Children.Add(new TextBlock
        {
            Text = "Changes this channel only. Displays keep their own on-air layouts.",
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 0, 1, 2)
        });
    }

    void BuildActions(ScorebugDisplay display, VenueChannel? channel)
    {
        ActionPanel.Children.Clear();
        ActionCount.Text = (display.Layouts.Count + 2 + (channel is null ? 0 : 1)).ToString();
        for (var index = 0; index < display.Layouts.Count; index++)
        {
            var layout = display.Layouts[index];
            var palette = PaletteFor(layout, index);
            ActionPanel.Children.Add(LayoutActionButton(display, index, palette));
        }
        ActionPanel.Children.Add(ActionButton("CLOCK  ▶ / ■", "Start or stop the game clock",
            "#23313F", "#6E8BA5", "#D2E1EE", () => { _engine.ToggleClock(); UpdateClock(); }));
        ActionPanel.Children.Add(ActionButton("PLAY CLOCK  ▶ / ■", "Start or stop the play clock",
            "#23313F", "#6E8BA5", "#D2E1EE", () => { _engine.TogglePlayClock(); UpdateClock(); }));
    }

    /// <summary>
    /// A layout action is also its preview: the operator can recognise a graphic before taking it
    /// to air instead of choosing only by name from the live-channel menu.
    /// </summary>
    Button LayoutActionButton(ScorebugDisplay display, int index,
        (string Background, string Border, string Foreground) palette)
    {
        var layout = display.Layouts[index];
        var onAir = display.PublishedIndex == index;
        var monitored = _viewChannelId is null && string.Equals(_viewLayoutId, layout.Id, StringComparison.OrdinalIgnoreCase);
        var preview = Thumbnail(layout, 248, 140);
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Height = 140,
            Background = new SolidColorBrush(Color.FromRgb(5, 8, 12)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border)),
            BorderThickness = new Thickness(monitored || onAir ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = new Image { Source = preview, Stretch = Stretch.Uniform }
        });
        var caption = new DockPanel { Margin = new Thickness(2, 5, 2, 1) };
        if (onAir || monitored)
        {
            var badge = new TextBlock
            {
                Text = monitored ? "● VIEWING" : "● ON AIR",
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(monitored ? Color.FromRgb(0xFF, 0xD1, 0x66) : Color.FromRgb(0x8D, 0xEB, 0xCB)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            DockPanel.SetDock(badge, Dock.Right);
            caption.Children.Add(badge);
        }
        caption.Children.Add(new TextBlock
        {
            Text = $"{index + 1}  {layout.Name}",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Foreground)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(caption);

        var button = new Button
        {
            Content = stack,
            Margin = new Thickness(0, 0, 0, 7),
            Padding = new Thickness(5, 5, 5, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Background)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border)),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Foreground)),
            BorderThickness = new Thickness(monitored || onAir ? 2 : 1),
            Tag = layout.Id,
            ToolTip = $"View {layout.Name} on this monitor. This never changes what is on air. Right-click for button colour."
        };
        ThemeManager.SetSkipTheme(button, true);
        button.ContextMenu = BuildLayoutColourMenu(layout);
        button.Click += (_, _) =>
        {
            _viewChannelId = null;
            _viewLayoutId = layout.Id;
            Refresh();
        };
        return button;
    }

    (string Background, string Border, string Foreground) PaletteFor(ScorebugLayout layout, int index)
    {
        if (_venue.State.LayoutColors.TryGetValue(layout.Id, out var custom) && TryColour(custom, out var colour))
        {
            var border = UiPalette.Mix(colour, Colors.White, 0.38);
            return (Hex(colour), Hex(border), Hex(Readable(colour)));
        }
        return TileColours[index % TileColours.Length];
    }

    static bool TryColour(string? text, out Color colour)
    {
        colour = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            colour = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static Color Readable(Color background) =>
        (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255 > 0.6
            ? Color.FromRgb(24, 32, 43) : Colors.White;

    static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    ContextMenu BuildLayoutColourMenu(ScorebugLayout layout)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "BUTTON COLOUR", IsEnabled = false, FontWeight = FontWeights.Bold });
        var current = _venue.State.LayoutColors.TryGetValue(layout.Id, out var chosen) ? chosen : "";
        foreach (var (name, hex) in LayoutColourChoices)
        {
            var item = new MenuItem
            {
                Header = name,
                IsChecked = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase),
                Icon = new Border
                {
                    Width = 13, Height = 13, CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    BorderBrush = Brushes.Black, BorderThickness = new Thickness(1)
                }
            };
            item.Click += (_, _) => SetLayoutColour(layout.Id, hex);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var automatic = new MenuItem { Header = "Use automatic colour", IsChecked = current.Length == 0 };
        automatic.Click += (_, _) => SetLayoutColour(layout.Id, null);
        menu.Items.Add(automatic);
        return menu;
    }

    void SetLayoutColour(string layoutId, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) _venue.State.LayoutColors.Remove(layoutId);
        else _venue.State.LayoutColors[layoutId] = hex;
        _venue.Save();
        _venueSignature = "";
        Refresh();
    }

    Button ActionButton(string label, string tip, string background, string border, string foreground, Action action)
    {
        var button = new Button
        {
            Content = label, MinHeight = 38, Margin = new Thickness(0, 0, 0, 5), FontSize = 10, FontWeight = FontWeights.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 5, 9, 5),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border)),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground)),
            BorderThickness = new Thickness(1), ToolTip = tip
        };
        ThemeManager.SetSkipTheme(button, true);
        button.Click += (_, _) => action();
        return button;
    }

    void Publish(string displayId, int index)
    {
        var display = _catalog.FindDisplay(displayId);
        if (display is null || index < 0 || index >= display.Layouts.Count) return;
        var layout = display.Layouts[index];
        var scene = new StoryboardScene
        {
            Name = layout.Name, LayoutId = layout.Id,
            Transition = layout.Transition, TransitionSeconds = layout.TransitionSeconds
        };
        if (!_catalog.PublishStoryboardScene(scene)) return;
        _displayId = displayId;
        StatusText.Text = $"ON AIR · {display.Name} · {layout.Name}";
        Refresh();
    }

    void UpdateEvents()
    {
        var rows = _engine.State.Events
            .TakeLast(200)
            .Reverse()
            .Select(gameEvent => new EventRow(gameEvent.At.ToLocalTime().ToString("HH:mm:ss"), $"{gameEvent.Type} · {gameEvent.Description}"))
            .ToArray();
        EventList.ItemsSource = rows;
        EventCountText.Text = $"{_engine.State.Events.Count}";
        DataStatus.Text = _engine.State.SavedAt == default ? "DATA · live" : "DATA · live";
    }

    void UpdateClock()
    {
        var state = _engine.State;
        ClockStatus.Text = $"{state.HomeAbbreviation} {state.HomeScore} – {state.AwayScore} {state.AwayAbbreviation}   ·   " +
            $"{GameEngine.FormatClock(state.ClockSeconds, ClockStyle.Padded)}   ·   {PeriodDisplay.Format(state)}";
    }

    void UpdateOutput_Click(object sender, RoutedEventArgs e)
    {
        var display = _catalog.FindDisplay(_displayId) ?? _catalog.ActiveDisplay;
        Publish(display.Id, Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
    }

    void OpenStudio_Click(object sender, RoutedEventArgs e)
    {
        var studio = new ScorebugEditorWindow(_catalog, _engine, _playback, _theme, _automation) { Owner = this };
        studio.ShowDialog();
        Refresh();
    }

    ImageSource Thumbnail(ScorebugLayout layout, int width, int height)
    {
        try
        {
            var view = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
                playback: null, layoutProvider: () => layout)
            {
                Width = width, Height = height
            };
            view.ShowAllLayers();
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            bitmap.Freeze();
            view.Dispose();
            return bitmap;
        }
        catch { return new DrawingImage(); }
    }
}
