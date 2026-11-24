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
    readonly VenueStore _venue = VenueStore.Load();
    readonly Dictionary<int, CameraFeed> _cameraFeeds = [];
    readonly Dictionary<int, Action<BitmapSource>> _cameraHandlers = [];
    readonly HashSet<int> _ownedCameras = [];
    readonly Dictionary<int, BitmapSource> _cameraFrames = [];
    string _displayId;
    ScorebugCanvasView? _channelView;
    bool _refreshing;

    sealed record EventRow(string Time, string Text);

    static readonly (string Background, string Border, string Foreground)[] TileColours =
    [
        ("#1D3C50", "#3D8FB8", "#BFE7FA"), ("#2C2440", "#8A6BC8", "#D9C9FF"),
        ("#163C32", "#38D7A3", "#9BF2D7"), ("#3F2E18", "#D9A441", "#FFE0A3"),
        ("#3C1B28", "#D45C86", "#FFC1D4"), ("#23313F", "#6E8BA5", "#D2E1EE")
    ];

    public LiveChannelWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback = null,
        AutomationRuntime? automation = null, string theme = "Dark")
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _automation = automation;
        _theme = theme;
        _displayId = _catalog.ActiveDisplay.Id;
        ThemeManager.Apply(this, theme);
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        _stream.HealthProvider = BuildStreamHealth;
        _streamTimer.Tick += (_, _) => CaptureStreamFrames();
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
            _streamTimer.Stop();
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
            _displayId = display.Id;
            var layout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
            ChannelName.Text = display.Name.ToUpperInvariant();
            NowPlayingText.Text = layout is null ? "No layout on air" : $"{layout.Name} · {layout.Width} × {layout.Height}";
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
            BuildActions(display);
            BuildHealth();
            UpdateEvents();
            UpdateClock();
            StatusText.Text = $"LIVE CHANNEL · {display.Name} · {display.Layouts.Count} layouts · {_engine.State.Events.Count} events";
        }
        finally { _refreshing = false; }
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
            var selected = string.Equals(display.Id, _displayId, StringComparison.OrdinalIgnoreCase);
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
                var displayIndex = _catalog.Displays.FindIndex(item => string.Equals(item.Id, target, StringComparison.OrdinalIgnoreCase));
                if (displayIndex >= 0) _catalog.SelectDisplay(displayIndex);
                Refresh();
            };
            OutputStrip.Children.Add(tile);
        }
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

    void BuildActions(ScorebugDisplay display)
    {
        ActionPanel.Children.Clear();
        ActionCount.Text = (display.Layouts.Count + 2).ToString();
        for (var index = 0; index < display.Layouts.Count; index++)
        {
            var layout = display.Layouts[index];
            var palette = TileColours[index % TileColours.Length];
            ActionPanel.Children.Add(ActionButton(
                $"{index + 1}  {layout.Name}",
                $"Publish {layout.Name} to {display.Name}",
                palette.Background, palette.Border, palette.Foreground,
                () => Publish(display.Id, index)));
        }
        ActionPanel.Children.Add(ActionButton("CLOCK  ▶ / ■", "Start or stop the game clock",
            "#23313F", "#6E8BA5", "#D2E1EE", () => { _engine.ToggleClock(); UpdateClock(); }));
        ActionPanel.Children.Add(ActionButton("PLAY CLOCK  ▶ / ■", "Start or stop the play clock",
            "#23313F", "#6E8BA5", "#D2E1EE", () => { _engine.TogglePlayClock(); UpdateClock(); }));
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
