using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The clip launcher: two independent fill channels with their own pictures, transport and takes,
/// one merged wall of clip and camera thumbnails, and preview/audience monitors for a display or
/// venue channel of the operator's choosing. It is deliberately not a tab inside Live Switch — a
/// replay operator keeps this window open while the director keeps Vision open.
/// </summary>
public sealed class LiveClipsWindow : Window
{
    readonly MediaSession? _session;
    readonly MediaPlayback? _playback;
    readonly VenueStore _venue;
    readonly ScorebugCatalog? _catalog;
    readonly GameEngine? _engine;
    readonly BroadcastHeadend _headend = BroadcastHeadend.Shared;
    readonly ClipChannelPlayer _channelA = new("FILL A");
    readonly ClipChannelPlayer _channelB = new("FILL B");
    readonly Image _cameraA = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    readonly Image _cameraB = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    readonly WrapPanel _clipPanel = new();
    readonly WrapPanel _cameraPanel = new();
    readonly ScrollViewer _wallScroll;
    readonly TextBlock _status = new() { FontSize = 10, Foreground = Solid("#B0B0B0"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _channelAStatus = new() { FontSize = 10, Foreground = Solid("#8DEBCB") };
    readonly TextBlock _channelBStatus = new() { FontSize = 10, Foreground = Solid("#8DEBCB") };
    readonly TextBlock _onAir = new() { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Solid("#FF9B9B") };
    Border? _badgeA;
    Border? _badgeB;
    TextBlock? _badgeAText;
    TextBlock? _badgeBText;
    readonly TextBlock _health = new() { FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = Solid("#6C7A8C"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
    readonly Border _cardA;
    readonly Border _cardB;
    readonly List<(CameraFeed Feed, Action<BitmapSource> Handler)> _cameraHandlers = [];
    (CameraFeed? Feed, Action<BitmapSource>? Handler) _channelCameraA;
    (CameraFeed? Feed, Action<BitmapSource>? Handler) _channelCameraB;
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly ContentControl _previewHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly ContentControl _audienceHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly TextBlock _previewLabel = new() { FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _audienceLabel = new() { FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Solid("#FF9B9B"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly WrapPanel _outputChoices = new() { Margin = new Thickness(0, 6, 0, 0) };
    ScorebugCanvasView? _previewView;
    ScorebugCanvasView? _audienceView;
    string? _outputDisplayId;
    string? _outputChannelId;
    string _outputSignature = "";
    string _outputViewKey = "";
    string _clipSignature = "";
    string _cameraSignature = "";
    int _target;
    DateTime _lastSessionRefresh = DateTime.MinValue;
    DateTime _lastCameraScan = DateTime.MinValue;
    int? _cameraIndexA;
    int? _cameraIndexB;
    MediaEntry? _entryA;
    MediaEntry? _entryB;

    static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public LiveClipsWindow(MediaSession? session, MediaPlayback? playback, VenueStore venue,
        ScorebugCatalog? catalog = null, GameEngine? engine = null)
    {
        _session = session;
        _playback = playback;
        _venue = venue;
        _catalog = catalog;
        _engine = engine;
        Title = "ScorePlus — Live Clips";
        Width = 1320;
        Height = 900;
        MinWidth = 1040;
        MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // The rest of the operator workspace is neutral black, not navy. The old blue-black
        // background made Live Clips look like a different product beside Vision and the Show page.
        Background = Solid("#0B0B0B");
        Foreground = Solid("#F4F7FB");
        FontFamily = new FontFamily("Segoe UI");
        WindowOwnerGuard.Attach(this);

        _wallScroll = new ScrollViewer
        {
            Content = BuildWallSections(),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _cardA = ChannelCard("FILL A", _channelA, _cameraA, _channelAStatus, 0);
        _cardB = ChannelCard("FILL B", _channelB, _cameraB, _channelBStatus, 1);
        Content = BuildChrome();
        ThemeManager.SetSkipTheme(this, true);
        _channelA.Changed += ChannelChanged;
        _channelB.Changed += ChannelChanged;
        _session?.StateChanged += SessionChanged;
        _session?.ThumbnailReady += ThumbnailReady;
        if (_catalog is not null) _catalog.Changed += CatalogChanged;
        _watch.Tick += (_, _) => RefreshProduction();
        _watch.Start();
        Loaded += (_, _) => { RefreshChoices(); SelectTarget(0); UpdateOutputSection(); };
        Closed += (_, _) => Shutdown();
        RefreshChoices();
        SelectTarget(0);
    }

    public ClipChannelPlayer ChannelA => _channelA;
    public ClipChannelPlayer ChannelB => _channelB;
    public int ClipTileCount => _clipPanel.Children.Count;
    public int CameraTileCount => _cameraPanel.Children.Count;
    public string? TargetChannel => _target == 0 ? "A" : "B";

    UIElement BuildChrome()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var returnLive = new Button { Content = "RETURN TO LIVE", MinHeight = 32, MinWidth = 128, Margin = new Thickness(0, 0, 6, 0) };
        returnLive.Click += (_, _) =>
        {
            _session?.ReturnToLive();
            _onAir.Text = "PROGRAMME · layout returned";
            LiveClipsBus.Shared.SetLive("Layout", null);
            _status.Text = "PROGRAMME · returned to the published layout";
        };
        actions.Children.Add(returnLive);
        var close = new Button { Content = "Close", MinHeight = 32, MinWidth = 68 };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Border
        {
            Background = Solid("#3D8FB8"), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock { Text = "CLIPS", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Black }
        });
        title.Children.Add(new TextBlock { Text = "LIVE CLIPS", FontSize = 17, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock
        {
            Text = "   two fills · clips and cameras on one page · preview and audience", Foreground = Solid("#B0B0B0"),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(title);
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(286) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var channels = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        channels.Children.Add(_cardA);
        channels.Children.Add(_cardB);
        channels.Children.Add(new TextBlock
        {
            Text = "Click a thumbnail to load it into the selected fill. Double-click a thumbnail to take it straight to air.",
            FontSize = 9, Foreground = Solid("#8A98AA"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0)
        });
        body.Children.Add(channels);

        var wall = new Border { Background = Solid("#141414"), CornerRadius = new CornerRadius(9), Padding = new Thickness(9) };
        Grid.SetColumn(wall, 2);
        var wallStack = new Grid();
        wallStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wallStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        wallStack.Children.Add(BuildOutputMonitors());
        Grid.SetRow(_wallScroll, 1);
        wallStack.Children.Add(_wallScroll);
        wall.Child = wallStack;
        body.Children.Add(wall);
        root.Children.Add(body);

        var footer = new Border { Background = Solid("#141414"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0) };
        var foot = new DockPanel();
        DockPanel.SetDock(_onAir, Dock.Right);
        _onAir.Margin = new Thickness(12, 0, 0, 0);
        foot.Children.Add(_onAir);
        DockPanel.SetDock(_health, Dock.Right);
        foot.Children.Add(_health);
        foot.Children.Add(_status);
        footer.Child = foot;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        _status.Text = "LIVE CLIPS READY · choose a clip or a camera";
        return root;
    }

    /// <summary>Clips and cameras are one wall with a divider, not two modes. A replay operator
    /// scrolls one page, the same way the media dock lists one library.</summary>
    UIElement BuildWallSections()
    {
        var stack = new StackPanel();
        stack.Children.Add(SectionHeader("CLIPS", "thumbnail launcher · no drop-downs"));
        stack.Children.Add(_clipPanel);
        stack.Children.Add(new Border
        {
            Height = 1, Background = Solid("#383838"), Margin = new Thickness(0, 8, 0, 8)
        });
        stack.Children.Add(SectionHeader("CAMERAS", "every capture device on this machine"));
        stack.Children.Add(_cameraPanel);
        return stack;
    }

    static UIElement SectionHeader(string title, string hint)
    {
        var row = new DockPanel { Margin = new Thickness(2, 0, 2, 6), LastChildFill = true };
        var heading = new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Solid("#B0B0B0"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(heading, Dock.Left);
        row.Children.Add(heading);
        row.Children.Add(new TextBlock
        {
            Text = hint, FontSize = 9, Foreground = Solid("#6C7A8C"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    /// <summary>The chosen display or venue channel, shown twice: what the preview will look like
    /// and what the audience output is publishing right now.</summary>
    UIElement BuildOutputMonitors()
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        var head = new DockPanel { Margin = new Thickness(2, 0, 2, 5), LastChildFill = true };
        head.Children.Add(new TextBlock
        {
            Text = "PREVIEW + LIVE AUDIENCE", FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Solid("#B0B0B0"), VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(new TextBlock
        {
            Text = "choose a display or channel below", FontSize = 9, Foreground = Solid("#6C7A8C"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        });
        section.Children.Add(head);
        var monitors = new UniformGrid { Columns = 2 };
        monitors.Children.Add(OutputMonitor(_previewLabel, _previewHost, "#2B7A62"));
        monitors.Children.Add(OutputMonitor(_audienceLabel, _audienceHost, "#7A2B2B"));
        section.Children.Add(monitors);
        section.Children.Add(_outputChoices);
        return section;
    }

    static Border OutputMonitor(TextBlock label, ContentControl host, string accent)
    {
        var picture = new Border
        {
            Background = Brushes.Black, BorderBrush = Solid(accent), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), ClipToBounds = true,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = new Grid { Width = 320, Height = 180, Background = Brushes.Black, Children = { host } }
            }
        };
        var caption = new Border
        {
            Background = Solid("#0B0B0B"), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 3, 0, 0),
            Child = label
        };
        // Keep the 16:9 picture and its caption in separate rows. A fixed-height stack clipped the
        // caption because the picture took the whole cell and pushed it past the bottom edge.
        var layout = new Grid { Margin = new Thickness(0, 0, 6, 0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(picture);
        Grid.SetRow(caption, 1);
        layout.Children.Add(caption);
        return new Border { Margin = new Thickness(0, 0, 2, 0), Child = layout };
    }

    void UpdateOutputSection()
    {
        if (_catalog is null || _engine is null) return;
        var signature = string.Join("|", _catalog.Displays.Select(display => $"{display.Id}:{display.Name}:{display.PublishedIndex}"))
            + "||" + string.Join("|", _venue.State.Channels.Select(channel => $"{channel.Id}:{channel.Name}:{channel.DisplayId}:{channel.LayoutId}"));
        if (signature != _outputSignature)
        {
            _outputSignature = signature;
            RebuildOutputChoices();
            if (_outputDisplayId is null || _catalog.FindDisplay(_outputDisplayId) is null)
                _outputDisplayId = _catalog.ActiveDisplay.Id;
            if (_outputChannelId is not null && _venue.State.Channels.All(channel => channel.Id != _outputChannelId))
                _outputChannelId = null;
        }
        var viewKey = $"{_outputDisplayId}|{_outputChannelId}";
        if (viewKey != _outputViewKey)
        {
            _outputViewKey = viewKey;
            RebuildOutputViews();
        }
        _previewLabel.Text = "PREVIEW · " + OutputName(preview: true);
        _audienceLabel.Text = "AUDIENCE · " + OutputName(preview: false);
    }

    string OutputName(bool preview)
    {
        if (_catalog is null) return "no display";
        var channel = _outputChannelId is null ? null : _venue.State.Channels.FirstOrDefault(candidate => candidate.Id == _outputChannelId);
        var display = channel is not null ? _catalog.FindDisplay(channel.DisplayId) : _catalog.FindDisplay(_outputDisplayId);
        display ??= _catalog.ActiveDisplay;
        var layout = OutputLayout(channel);
        return $"{display.Name}{(layout is null ? "" : " · " + layout.Name)}" + (channel is null ? "" : $" · {channel.Name}");
    }

    void RebuildOutputChoices()
    {
        _outputChoices.Children.Clear();
        if (_catalog is null) return;
        foreach (var display in _catalog.Displays)
        {
            var button = OutputChoiceButton($"DISPLAY · {display.Name}", _outputChannelId is null && display.Id == _outputDisplayId);
            var id = display.Id;
            button.Click += (_, _) =>
            {
                _outputDisplayId = id;
                _outputChannelId = null;
                _outputSignature = "";
                UpdateOutputSection();
            };
            _outputChoices.Children.Add(button);
        }
        foreach (var channel in _venue.State.Channels)
        {
            var button = OutputChoiceButton($"CHANNEL · {channel.Name}", channel.Id == _outputChannelId);
            var id = channel.Id;
            button.Click += (_, _) =>
            {
                _outputChannelId = id;
                var linked = _venue.State.Channels.FirstOrDefault(candidate => candidate.Id == id)?.DisplayId;
                if (!string.IsNullOrWhiteSpace(linked)) _outputDisplayId = linked;
                _outputSignature = "";
                UpdateOutputSection();
            };
            _outputChoices.Children.Add(button);
        }
    }

    static Button OutputChoiceButton(string text, bool selected)
    {
        var button = new Button
        {
            Content = text, FontSize = 9, FontWeight = FontWeights.Bold, MinHeight = 26,
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4),
            Background = selected ? Solid("#123C33") : Solid("#1A1A1A"),
            Foreground = selected ? Solid("#8DEBCB") : Solid("#B0B0B0"),
            BorderBrush = selected ? Solid("#38D7A3") : Solid("#383838")
        };
        ThemeManager.SetSkipTheme(button, true);
        return button;
    }

    void RebuildOutputViews()
    {
        if (_catalog is null || _engine is null) return;
        _previewView?.Dispose();
        _audienceView?.Dispose();
        _previewView = null;
        _audienceView = null;
        var channelId = _outputChannelId;
        _previewView = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
            playback: _playback, layoutProvider: () => OutputLayout(channelId is null ? null : _venue.State.Channels.FirstOrDefault(channel => channel.Id == channelId)) ?? _catalog.Layout,
            liveValues: true)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _audienceView = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
            playback: _playback, layoutProvider: () => OutputLayout(channelId is null ? null : _venue.State.Channels.FirstOrDefault(channel => channel.Id == channelId)) ?? _catalog.Layout,
            liveValues: true)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _previewHost.Content = _previewView;
        _audienceHost.Content = _audienceView;
    }

    ScorebugLayout? OutputLayout(VenueChannel? channel)
    {
        if (_catalog is null) return null;
        if (channel is not null)
        {
            var display = _catalog.FindDisplay(channel.DisplayId);
            if (display is not null && !string.IsNullOrWhiteSpace(channel.LayoutId))
            {
                var pinned = display.Layouts.FirstOrDefault(layout =>
                    string.Equals(layout.Id, channel.LayoutId, StringComparison.OrdinalIgnoreCase));
                if (pinned is not null) return pinned;
            }
            if (display is not null)
                return display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0,
                    Math.Max(0, display.Layouts.Count - 1)));
            return _catalog.FindLayout(channel.LayoutId);
        }
        var chosen = _catalog.FindDisplay(_outputDisplayId) ?? _catalog.ActiveDisplay;
        return chosen.Layouts.ElementAtOrDefault(Math.Clamp(chosen.PublishedIndex, 0, Math.Max(0, chosen.Layouts.Count - 1)));
    }

    Border ChannelCard(string label, ClipChannelPlayer player, Image camera, TextBlock status, int channel)
    {
        var card = new Border
        {
            Background = Solid("#141414"), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
            Tag = channel
        };
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        var title = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Solid("#8DEBCB"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        var badgeText = new TextBlock
        {
            Text = "CLEAR", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Solid("#93A0B2")
        };
        var badge = new Border
        {
            Background = Solid("#0B0B0B"), BorderBrush = Solid("#383838"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Child = badgeText
        };
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        if (channel == 0) { _badgeA = badge; _badgeAText = badgeText; }
        else { _badgeB = badge; _badgeBText = badgeText; }
        stack.Children.Add(head);
        // A 16:9 fill monitor; the old fixed 144-high picture in a 270-wide card was a squashed strip.
        var picture = new Grid { Background = Brushes.Black, ClipToBounds = true, Height = 152 };
        picture.Children.Add(player.Visual);
        picture.Children.Add(camera);
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = picture });
        var transport = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        transport.Children.Add(SmallButton("PLAY", () => Player(player, channel, camera).TogglePlay()));
        transport.Children.Add(SmallButton("STOP", () =>
        {
            Player(player, channel, camera).Stop();
            if (channel == 0) _cameraIndexA = null; else _cameraIndexB = null;
            camera.Visibility = Visibility.Collapsed;
            player.Visual.Visibility = Visibility.Visible;
        }));
        transport.Children.Add(SmallButton("TAKE", () => Take(channel)));
        transport.Children.Add(SmallButton("CLEAR", () => Clear(channel)));
        stack.Children.Add(transport);
        status.Margin = new Thickness(2, 4, 0, 0);
        status.Text = "No clip loaded";
        stack.Children.Add(status);
        card.Child = stack;
        card.MouseLeftButtonDown += (_, _) => SelectTarget(channel);
        return card;
    }

    static Button SmallButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text, FontSize = 9, FontWeight = FontWeights.Bold, MinHeight = 28, MinWidth = 48,
            Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 4, 0),
            Background = Solid("#1D2A38"), Foreground = Solid("#D2E1EE"), BorderBrush = Solid("#3D5064")
        };
        button.Click += (_, _) => action();
        return button;
    }

    ClipChannelPlayer Player(ClipChannelPlayer player, int channel, Image camera)
    {
        camera.Visibility = Visibility.Collapsed;
        player.Visual.Visibility = Visibility.Visible;
        return player;
    }

    void SelectTarget(int channel)
    {
        _target = channel;
        _cardA.BorderBrush = channel == 0 ? Solid("#38D7A3") : Brushes.Transparent;
        _cardB.BorderBrush = channel == 1 ? Solid("#38D7A3") : Brushes.Transparent;
        _status.Text = $"FILL {(channel == 0 ? "A" : "B")} selected · click a clip or camera thumbnail to load it";
    }

    void SessionChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(SessionChanged); return; }
        // Playback raises StateChanged many times a second. Re-enumerating capture devices on every
        // tick made the two fill monitors and the clip wall stutter; a library refresh a second is
        // enough, while ThumbnailReady still forces an immediate rebuild.
        var now = DateTime.UtcNow;
        if ((now - _lastSessionRefresh).TotalMilliseconds < 800) return;
        _lastSessionRefresh = now;
        RefreshChoices();
    }

    void CatalogChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CatalogChanged); return; }
        _outputSignature = "";
        UpdateOutputSection();
    }

    void ThumbnailReady(string path)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ThumbnailReady(path)); return; }
        // The signature only covers library paths, so a freshly decoded thumbnail would otherwise
        // leave the blank plate in place. Force the tiles to rebuild for the new picture.
        _clipSignature = "";
        RefreshChoices();
    }

    void RefreshChoices()
    {
        if (_session is null) return;
        var clips = _session.Library
            .Where(entry => entry is { IsCamera: false, IsMicrophone: false, IsScreen: false, IsSpeaker: false }
                && (entry.IsVideo || entry.IsAudio))
            .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var signature = string.Join("|", clips.Select(entry => entry.Path));
        if (signature != _clipSignature)
        {
            _clipSignature = signature;
            _clipPanel.Children.Clear();
            foreach (var entry in clips) _clipPanel.Children.Add(ClipTile(entry));
            if (clips.Count == 0)
                _clipPanel.Children.Add(new TextBlock
                {
                    Text = "No clips in the media library yet. Import clips in the MEDIA dock.",
                    FontSize = 10, Foreground = Solid("#8A98AA"), Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap
                });
        }
        RefreshCameras();
        UpdateOutputSection();
    }

    UIElement ClipTile(MediaEntry entry)
    {
        var tile = new Border
        {
            Width = 190, Margin = new Thickness(0, 0, 8, 8), Background = Solid("#0A0A0A"),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Solid("#383838"),
            Cursor = System.Windows.Input.Cursors.Hand, Tag = entry,
            ToolTip = $"{entry.Label}\nTags: {entry.Tag}\nClick: load into fill {(_target == 0 ? "A" : "B")} · double-click: take to air"
        };
        var stack = new StackPanel();
        var picture = new Grid { Height = 106, Background = Brushes.Black, ClipToBounds = true };
        var thumb = new Image { Stretch = Stretch.UniformToFill, Source = _session?.Thumbnail(entry) };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        picture.Children.Add(thumb);
        picture.Children.Add(new Border
        {
            Background = Solid("#CC000000"), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4),
            Child = new TextBlock { Text = entry.KindLabel, FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Solid("#BFE7FA") }
        });
        stack.Children.Add(picture);
        stack.Children.Add(new TextBlock
        {
            Text = entry.Label, FontSize = 10, Margin = new Thickness(6, 4, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.ReplayBadge) ? entry.Tag : $"{entry.Tag} · {entry.ReplayBadge}",
            FontSize = 8, Foreground = Solid("#8A98AA"), Margin = new Thickness(6, 1, 6, 5)
        });
        tile.Child = stack;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { LoadClip(entry, _target); Take(_target); }
            else LoadClip(entry, _target);
        };
        return tile;
    }

    void RefreshCameras()
    {
        // Enumerate the capture devices themselves, not only cameras someone already added to the
        // media library. A camera plugged in this morning must appear here without first being put
        // on air or imported. The scan is cached briefly because device enumeration is expensive
        // enough to make the fill previews hitch when it runs on every media tick.
        if (_cameraPanel.Children.Count > 0 && (DateTime.UtcNow - _lastCameraScan).TotalSeconds < 3) return;
        _lastCameraScan = DateTime.UtcNow;
        var devices = _session?.CameraDevices() ?? CameraFeed.NamedDevices();
        var cameras = devices.Select(device =>
            _session?.Library.FirstOrDefault(entry => entry.IsCamera && entry.CameraIndex == device.Index)
            ?? new MediaEntry(MediaLibrary.CameraPath(device.Index), "Camera", Name: device.Label))
            .OrderBy(entry => entry.CameraIndex).ToList();
        if (cameras.Count == 0 && _session is not null)
            cameras.AddRange(_session.Library.Where(entry => entry.IsCamera)
                .GroupBy(entry => entry.CameraIndex).Select(group => group.First()).OrderBy(entry => entry.CameraIndex));
        var signature = string.Join("|", cameras.Select(entry => $"{entry.CameraIndex}:{entry.Label}"));
        if (signature == _cameraSignature) return;
        _cameraSignature = signature;
        // Playback fires StateChanged constantly. Rebuilding the camera wall on every transport
        // tick would restart every capture feed, so the tiles only follow a real library change.
        foreach (var (feed, handler) in _cameraHandlers) feed.FrameReady -= handler;
        _cameraHandlers.Clear();
        _cameraPanel.Children.Clear();
        if (cameras.Count == 0)
            _cameraPanel.Children.Add(new TextBlock
            {
                Text = "No capture devices found. Plug in a camera and it appears here immediately.",
                FontSize = 10, Foreground = Solid("#8A98AA"), Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap
            });
        foreach (var entry in cameras) _cameraPanel.Children.Add(CameraTile(entry));
    }

    UIElement CameraTile(MediaEntry entry)
    {
        var tile = new Border
        {
            Width = 216, Margin = new Thickness(0, 0, 8, 8), Background = Solid("#0A0A0A"),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Solid("#383838"),
            Cursor = System.Windows.Input.Cursors.Hand, Tag = entry,
            ToolTip = $"{entry.Label}\nClick: load into fill {(_target == 0 ? "A" : "B")} · double-click: take to air"
        };
        var stack = new StackPanel();
        var picture = new Grid { Height = 122, Background = Brushes.Black, ClipToBounds = true };
        var image = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        picture.Children.Add(image);
        picture.Children.Add(new Border
        {
            Background = Solid("#CC000000"), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4),
            Child = new TextBlock { Text = "LIVE", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Solid("#FF9B9B") }
        });
        stack.Children.Add(picture);
        stack.Children.Add(new TextBlock
        {
            Text = entry.Label, FontSize = 10, Margin = new Thickness(6, 4, 6, 5),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        tile.Child = stack;
        var feed = CameraFeed.Get(entry.CameraIndex);
        Action<BitmapSource> handler = frame =>
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => image.Source = frame); return; }
            image.Source = frame;
        };
        feed.FrameReady += handler;
        _cameraHandlers.Add((feed, handler));
        try { feed.Start(); } catch { }
        if (feed.LastFrame is { } last) image.Source = last;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { LoadCamera(entry, _target); Take(_target); }
            else LoadCamera(entry, _target);
        };
        return tile;
    }

    void SetBadge(int channel, string text, string colour, string background)
    {
        var badge = channel == 0 ? _badgeA : _badgeB;
        var label = channel == 0 ? _badgeAText : _badgeBText;
        if (badge is null || label is null) return;
        label.Text = text;
        label.Foreground = Solid(colour);
        badge.BorderBrush = Solid(colour);
        badge.Background = Solid(background);
    }

    void LoadClip(MediaEntry entry, int channel)
    {
        var player = channel == 0 ? _channelA : _channelB;
        var camera = channel == 0 ? _cameraA : _cameraB;
        camera.Visibility = Visibility.Collapsed;
        player.Visual.Visibility = Visibility.Visible;
        player.Open(entry, play: false);
        if (channel == 0) { _entryA = entry; _cameraIndexA = null; _channelAStatus.Text = $"{entry.Label} · loaded"; }
        else { _entryB = entry; _cameraIndexB = null; _channelBStatus.Text = $"{entry.Label} · loaded"; }
        SetBadge(channel, "PREVIEW", "#8DEBCB", "#10231D");
        LiveClipsBus.Shared.SetChannel(channel, entry.Label, _session?.Thumbnail(entry));
        _status.Text = $"FILL {(channel == 0 ? "A" : "B")} · loaded {entry.Label} · press TAKE (or double-click) to go live";
    }

    void LoadCamera(MediaEntry entry, int channel)
    {
        var player = channel == 0 ? _channelA : _channelB;
        var camera = channel == 0 ? _cameraA : _cameraB;
        player.Pause();
        player.Visual.Visibility = Visibility.Collapsed;
        camera.Visibility = Visibility.Visible;
        var feed = CameraFeed.Get(entry.CameraIndex);
        try { feed.Start(); } catch { }
        if (feed.LastFrame is { } last) camera.Source = last;
        // One handler per channel monitor, replaced rather than stacked when the operator loads a
        // different camera into the same fill.
        if (channel == 0 && _channelCameraA.Handler is { } oldA) _channelCameraA.Feed?.FrameReady -= oldA;
        if (channel == 1 && _channelCameraB.Handler is { } oldB) _channelCameraB.Feed?.FrameReady -= oldB;
        Action<BitmapSource> handler = frame =>
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => camera.Source = frame); return; }
            camera.Source = frame;
        };
        feed.FrameReady += handler;
        if (channel == 0) _channelCameraA = (feed, handler); else _channelCameraB = (feed, handler);
        if (channel == 0) { _entryA = entry; _cameraIndexA = entry.CameraIndex; _channelAStatus.Text = $"{entry.Label} · camera"; }
        else { _entryB = entry; _cameraIndexB = entry.CameraIndex; _channelBStatus.Text = $"{entry.Label} · camera"; }
        SetBadge(channel, "PREVIEW", "#8DEBCB", "#10231D");
        LiveClipsBus.Shared.SetChannel(channel, entry.Label, feed.LastFrame is { } still ? still : null);
        _status.Text = $"FILL {(channel == 0 ? "A" : "B")} · camera {entry.Label} · press TAKE to go live";
    }

    void Clear(int channel)
    {
        var player = channel == 0 ? _channelA : _channelB;
        var camera = channel == 0 ? _cameraA : _cameraB;
        player.Open(null);
        player.Visual.Visibility = Visibility.Visible;
        camera.Visibility = Visibility.Collapsed;
        if (channel == 0) { _entryA = null; _cameraIndexA = null; _channelAStatus.Text = "No clip loaded"; }
        else { _entryB = null; _cameraIndexB = null; _channelBStatus.Text = "No clip loaded"; }
        SetBadge(channel, "CLEAR", "#93A0B2", "#0B0B0B");
        LiveClipsBus.Shared.SetChannel(channel, "", null);
        _status.Text = $"FILL {(channel == 0 ? "A" : "B")} · cleared";
    }

    void Take(int channel)
    {
        var player = channel == 0 ? _channelA : _channelB;
        var entry = channel == 0 ? _entryA : _entryB;
        if (entry is null)
        {
            _status.Text = $"FILL {(channel == 0 ? "A" : "B")} · nothing loaded";
            return;
        }
        player.Play();
        if (_session is not null) _session.Play(entry);
        SetBadge(channel, "ON AIR", "#FF9B9B", "#2A1214");
        SetBadge(channel == 0 ? 1 : 0, (channel == 0 ? _entryB : _entryA) is null ? "CLEAR" : "PREVIEW",
            (channel == 0 ? _entryB : _entryA) is null ? "#93A0B2" : "#8DEBCB",
            (channel == 0 ? _entryB : _entryA) is null ? "#0B0B0B" : "#10231D");
        _onAir.Text = $"ON AIR · {(channel == 0 ? "FILL A" : "FILL B")} · {entry.Label}";
        LiveClipsBus.Shared.SetLive($"{entry.Label} · {entry.KindLabel}", _session?.Thumbnail(entry));
        _status.Text = $"TAKEN · {entry.Label} · fill {(channel == 0 ? "A" : "B")} is on the programme bus";
    }

    void ChannelChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ChannelChanged); return; }
        if (_entryA is null) _channelAStatus.Text = _channelA.Error ?? "No clip loaded";
        if (_entryB is null) _channelBStatus.Text = _channelB.Error ?? "No clip loaded";
    }

    void RefreshProduction()
    {
        if (!IsVisible) return;
        var settings = _venue.State.Broadcast;
        settings.Normalize();
        var fill = settings.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.FillKey);
        var stream = settings.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.Stream);
        var health = _headend.IsRunning ? $"headend :{_headend.Port}" : "headend idle";
        _health.Text = $"{health} · fill {_headend.ViewerCount(fill?.Id ?? "router-fill-key")} · stream {_headend.ViewerCount(stream?.Id ?? "stream-main")}";
        UpdateOutputSection();
    }

    void Shutdown()
    {
        _watch.Stop();
        foreach (var (feed, handler) in _cameraHandlers) feed.FrameReady -= handler;
        _cameraHandlers.Clear();
        if (_channelCameraA.Handler is { } channelA) _channelCameraA.Feed?.FrameReady -= channelA;
        if (_channelCameraB.Handler is { } channelB) _channelCameraB.Feed?.FrameReady -= channelB;
        _channelA.Changed -= ChannelChanged;
        _channelB.Changed -= ChannelChanged;
        if (_catalog is not null) _catalog.Changed -= CatalogChanged;
        if (_session is not null)
        {
            _session.StateChanged -= SessionChanged;
            _session.ThumbnailReady -= ThumbnailReady;
        }
        _previewView?.Dispose();
        _audienceView?.Dispose();
        _channelA.Dispose();
        _channelB.Dispose();
    }
}
