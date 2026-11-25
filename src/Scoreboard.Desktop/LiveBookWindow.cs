using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Stream Studio previews the configured display or channel and hands that composed output to a
/// third-party RTMP provider through ffmpeg. There is deliberately no camera monitor here: the
/// picture is exactly the display the venue is showing, plus its assigned overlay.
/// </summary>
public sealed class LiveBookWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly MediaPlayback? _playback;
    readonly MediaSession? _session;
    readonly VenueStore _venue;
    readonly string _theme;
    readonly Action? _openGraphics;
    readonly BroadcastHeadend _headend = BroadcastHeadend.Shared;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Grid _stage = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly TextBlock _displayBadge = new()
    {
        FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(12), Padding = new Thickness(8, 3, 8, 3)
    };
    readonly TextBlock _displayCaption = new()
    {
        FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF5)),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(12), Padding = new Thickness(8, 4, 8, 4)
    };
    readonly ComboBox _sourceBox = new() { MinHeight = 30, Margin = new Thickness(0, 0, 0, 7) };
    readonly ComboBox _layoutBox = new() { MinHeight = 30, Margin = new Thickness(0, 0, 0, 7) };
    readonly ComboBox _providerBox = new() { MinHeight = 28, Margin = new Thickness(0, 0, 0, 5) };
    readonly TextBox _serverInput = new() { MinHeight = 27, Margin = new Thickness(0, 0, 0, 5), FontFamily = new FontFamily("Consolas"), FontSize = 10 };
    readonly TextBox _streamKeyInput = new() { MinHeight = 27, Margin = new Thickness(0, 0, 0, 5), FontFamily = new FontFamily("Consolas"), FontSize = 10 };
    readonly TextBlock _pushState = new() { FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    readonly Button _pushButton = new() { Content = "Push to provider", MinHeight = 28, Margin = new Thickness(0, 3, 0, 0) };
    readonly StreamPushService _push = new();
    readonly TextBlock _layoutInfo = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), TextWrapping = TextWrapping.Wrap };
    // A direct look at the chosen overlay, even before the camera picture arrives or while the
    // scorebug is switched off. Without this Live Book read as a camera monitor with no graphics.
    readonly Image _layoutThumb = new() { Height = 118, Stretch = Stretch.Uniform, Margin = new Thickness(0, 5, 0, 5) };
    readonly TextBlock _stats = new() { FontFamily = new FontFamily("Consolas"), FontSize = 10, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _streamUrl = new() { FontFamily = new FontFamily("Consolas"), FontSize = 10, TextWrapping = TextWrapping.Wrap };
    readonly Button _runButton = new() { Content = "Go live", MinHeight = 36, MinWidth = 94 };
    readonly Button _scorebugButton = new() { Content = "Scorebug ON", MinHeight = 30, MinWidth = 106 };
    readonly StackPanel _destinationPanel = new();
    readonly List<(BroadcastDestination Destination, TextBlock State, TextBlock Url)> _destinationRows = [];
    ScorebugCanvasView? _overlay;
    bool _running;
    bool _rendering;
    long _frames;
    long _dropped;
    double _renderMs;
    bool _refreshingSource;
    DateTime _lastRender = DateTime.UtcNow;

    static readonly (string Name, string Server)[] Providers =
    [
        ("Custom RTMP", ""),
        ("YouTube", "rtmp://a.rtmp.youtube.com/live2"),
        ("Twitch", "rtmp://live.twitch.tv/app"),
        ("Facebook", "rtmps://live-api-s.facebook.com:443/rtmp/"),
        ("Kick", "rtmps://fa723fc1b171.global-contribute.live-video.net/app")
    ];

    sealed record SourceChoice(string DisplayId, string LayoutId, string Label)
    {
        public override string ToString() => Label;
    }

    sealed record CameraChoice(int Index, string Label)
    {
        public override string ToString() => Label;
    }

    sealed record LayoutChoice(string? LayoutId, string Label)
    {
        public override string ToString() => Label;
    }

    public LiveBookWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback,
        MediaSession? session, VenueStore venue, string theme = "Dark", Action? openGraphics = null)
    {
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _session = session;
        _venue = venue;
        _theme = theme;
        _openGraphics = openGraphics;
        Title = "ScorePlus — Stream Studio";
        Width = 1220;
        Height = 820;
        MinWidth = 940;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        Content = BuildChrome();
        ThemeManager.Apply(this, theme);
        WindowOwnerGuard.Attach(this);
        _runButton.Click += (_, _) => ToggleRunning();
        _scorebugButton.Click += (_, _) => ToggleScorebug();
        _layoutBox.SelectionChanged += (_, _) => SelectLayout();
        _sourceBox.SelectionChanged += (_, _) => SelectSource();
        _providerBox.SelectionChanged += (_, _) => ProviderChanged();
        _pushButton.Click += (_, _) => TogglePush();
        _engine.Changed += EngineChanged;
        _catalog.Changed += CatalogChanged;
        _tick.Tick += (_, _) => Render();
        Loaded += (_, _) =>
        {
            RefreshSources();
            RefreshLayouts();
            BindDestinations();
            RefreshProvider();
            _tick.Start();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _engine.Changed -= EngineChanged;
            _catalog.Changed -= CatalogChanged;
            foreach (var destination in _venue.State.Broadcast.Destinations.Where(item => item.Kind == BroadcastDestinationKind.Stream))
                _headend.Unregister(destination.Id);
            _push.Dispose();
            _overlay?.Dispose();
        };
    }

    public bool IsLive => _running;
    public long RenderedFrames => _frames;
    public double LastRenderMilliseconds => _renderMs;
    public string StreamUrl => _headend.StreamUrl(FirstStreamDestination().Id);

    UIElement BuildChrome()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _runButton.Margin = new Thickness(0, 0, 6, 0);
        actions.Children.Add(_runButton);
        var close = new Button { Content = "Close", MinHeight = 36, MinWidth = 70 };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x38, 0xD7, 0xA3)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock { Text = "LIVE", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Black }
        });
        title.Children.Add(new TextBlock { Text = "STREAM STUDIO · DISPLAY OUTPUT", FontSize = 17, FontWeight = FontWeights.Bold });
        title.Children.Add(new TextBlock
        {
            Text = "   configured display, channel and provider output", Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(title);
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        var stageFrame = new Grid { Background = Brushes.Black, ClipToBounds = true };
        stageFrame.Children.Add(_stage);
        stageFrame.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x05, 0x08, 0x0C)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12), Child = _displayBadge
        });
        stageFrame.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x05, 0x08, 0x0C)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 5, 9, 5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12), Child = _displayCaption
        });
        var stageCard = new Border
        {
            Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x35, 0x42)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), ClipToBounds = true,
            Child = stageFrame
        };
        body.Children.Add(stageCard);
        var side = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x25)),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12)
        };
        Grid.SetColumn(side, 2);
        var panel = new StackPanel();
        panel.Children.Add(Section("DISPLAY / CHANNEL ASSIGNMENT"));
        panel.Children.Add(_sourceBox);
        panel.Children.Add(Section("PROGRAMME LAYOUT"));
        panel.Children.Add(_layoutBox);
        panel.Children.Add(_layoutInfo);
        panel.Children.Add(new Border
        {
            Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x35, 0x42)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), ClipToBounds = true,
            Child = _layoutThumb
        });
        panel.Children.Add(_scorebugButton);
        panel.Children.Add(Section("LOCAL STREAM"));
        panel.Children.Add(_streamUrl);
        var copyStream = new Button { Content = "Copy stream URL", MinHeight = 30, Margin = new Thickness(0, 5, 0, 0) };
        copyStream.Click += (_, _) => Copy(_streamUrl.Text);
        panel.Children.Add(copyStream);
        panel.Children.Add(Section("DESTINATIONS"));
        panel.Children.Add(_destinationPanel);
        panel.Children.Add(Section("3RD-PARTY PROVIDER"));
        panel.Children.Add(_providerBox);
        panel.Children.Add(FieldLabel("INGEST URL"));
        panel.Children.Add(_serverInput);
        panel.Children.Add(FieldLabel("STREAM KEY"));
        panel.Children.Add(_streamKeyInput);
        var saveProvider = new Button { Content = "Save provider settings", MinHeight = 27, Margin = new Thickness(0, 2, 0, 0) };
        saveProvider.Click += (_, _) => ApplyProvider();
        panel.Children.Add(saveProvider);
        panel.Children.Add(_pushButton);
        panel.Children.Add(_pushState);
        panel.Children.Add(Section("FRAME HEALTH"));
        panel.Children.Add(_stats);
        side.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(side);
        root.Children.Add(body);
        Grid.SetRow(body, 1);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x25)),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0),
            Child = _status
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        _status.Text = "STREAM STUDIO READY · choose a channel or display, then Go live";
        return root;
    }

    static TextBlock FieldLabel(string text) => new()
    {
        Text = text, FontSize = 8, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x7E, 0x91)), Margin = new Thickness(0, 0, 0, 2)
    };

    static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)),
        Margin = new Thickness(0, 8, 0, 4)
    };

    void RefreshSources()
    {
        var stream = FirstStreamDestination();
        var choices = new List<SourceChoice>
        {
            new("", "", "Venue programme (published display)")
        };
        choices.AddRange(_catalog.Displays.Select(display => new SourceChoice(display.Id, "", $"DISPLAY · {display.Name}")));
        choices.AddRange(_venue.State.Channels.Select(channel => new SourceChoice(
            channel.DisplayId, channel.LayoutId, $"CHANNEL · {channel.Name}")));
        var pinned = _venue.State.Broadcast.StreamLayoutId;
        var selected = choices.FirstOrDefault(choice => !string.IsNullOrWhiteSpace(pinned)
                && string.Equals(choice.LayoutId, pinned, StringComparison.OrdinalIgnoreCase)
                && string.Equals(choice.DisplayId, stream.DisplayId, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(choice => string.IsNullOrWhiteSpace(pinned)
                && string.Equals(choice.DisplayId, stream.DisplayId, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
        _refreshingSource = true;
        try
        {
            _sourceBox.ItemsSource = choices;
            _sourceBox.SelectedItem = selected;
        }
        finally { _refreshingSource = false; }
    }

    void SelectSource()
    {
        if (_refreshingSource || _sourceBox.SelectedItem is not SourceChoice choice) return;
        var stream = FirstStreamDestination();
        stream.DisplayId = choice.DisplayId;
        _venue.State.Broadcast.StreamLayoutId = choice.LayoutId;
        // Keep the visible layout picker on the same choice as the channel assignment. Without
        // this the old picker selection kept winning in CurrentLayout when a channel carried a
        // pinned overlay.
        if (_layoutBox.ItemsSource is IEnumerable<LayoutChoice> layouts)
            _layoutBox.SelectedItem = layouts.FirstOrDefault(layout =>
                string.Equals(layout.LayoutId ?? "", choice.LayoutId, StringComparison.OrdinalIgnoreCase)) ?? layouts.FirstOrDefault();
        _venue.Save();
        RebuildOverlay();
        _status.Text = string.IsNullOrWhiteSpace(choice.DisplayId)
            ? "SOURCE · venue programme · follows the active display"
            : $"SOURCE · {choice.Label} · assigned to the stream";
    }

    void RefreshProvider()
    {
        var stream = FirstStreamDestination();
        _providerBox.ItemsSource = Providers.Select(provider => provider.Name).ToArray();
        _providerBox.SelectedItem = Providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, stream.Provider, StringComparison.OrdinalIgnoreCase)).Name ?? "Custom RTMP";
        _serverInput.Text = stream.ServerUrl;
        _streamKeyInput.Text = stream.StreamKey;
        _pushState.Text = StreamPushService.FindFfmpeg() is null
            ? "ffmpeg not found · put ffmpeg.exe beside ScorePlus or on PATH"
            : "ffmpeg ready · enter the provider key and start the local stream first";
    }

    void ProviderChanged()
    {
        if (_providerBox.SelectedItem is not string name) return;
        var provider = Providers.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        if (provider.Server.Length > 0 && string.IsNullOrWhiteSpace(_serverInput.Text)) _serverInput.Text = provider.Server;
    }

    void ApplyProvider()
    {
        var stream = FirstStreamDestination();
        stream.Provider = _providerBox.SelectedItem as string ?? "Custom RTMP";
        stream.ServerUrl = _serverInput.Text.Trim();
        stream.StreamKey = _streamKeyInput.Text.Trim();
        _venue.Save();
        _pushState.Text = string.IsNullOrWhiteSpace(stream.StreamKey)
            ? "Provider settings saved · add the stream key from the provider dashboard"
            : "Provider settings saved · press Push to provider";
    }

    void TogglePush()
    {
        if (_push.IsRunning)
        {
            _push.Stop();
            _pushButton.Content = "Push to provider";
            _pushState.Text = "Provider push stopped.";
            return;
        }
        ApplyProvider();
        var stream = FirstStreamDestination();
        if (!_running)
        {
            ToggleRunning();
            if (!_running) { _pushState.Text = "Start the local stream before pushing to a provider."; return; }
        }
        if (_push.Start(_headend.LocalStreamUrl(stream.Id), _headend.LocalAudioUrl(stream.Id), stream.ServerUrl, stream.StreamKey))
        {
            _pushButton.Content = "Stop provider push";
            _pushState.Text = "PUSHING · " + stream.Provider + " · programme audio";
        }
        else
        {
            _pushButton.Content = "Push to provider";
            _pushState.Text = "PUSH ERROR · " + (_push.Error ?? "unknown error");
        }
    }

    void RefreshPushState()
    {
        if (_push.IsRunning)
        {
            _pushState.Text = "PUSHING · " + (string.IsNullOrWhiteSpace(_push.LastMessage) ? "ffmpeg active" : _push.LastMessage);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_push.Error))
        {
            _pushState.Text = "PUSH ERROR · " + _push.Error;
            _pushButton.Content = "Push to provider";
        }
    }

    void RefreshLayouts()
    {
        var previous = (_layoutBox.SelectedItem as LayoutChoice)?.LayoutId
            ?? (_venue.State.Broadcast.StreamLayoutId is { Length: > 0 } pinned ? pinned : null);
        var choices = new List<LayoutChoice> { new(null, "Venue programme (published)") };
        choices.AddRange(_catalog.Displays.SelectMany(display => display.Layouts)
            .Select(layout => new LayoutChoice(layout.Id, $"{layout.Name} · {layout.Id[..Math.Min(6, layout.Id.Length)]}")));
        _layoutBox.ItemsSource = choices;
        _layoutBox.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.LayoutId, previous, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        RebuildOverlay();
    }

    void SelectLayout()
    {
        if (_layoutBox.SelectedItem is not LayoutChoice choice) return;
        _venue.State.Broadcast.StreamLayoutId = choice.LayoutId ?? "";
        _venue.Save();
        RebuildOverlay();
    }

    void ToggleScorebug()
    {
        _venue.State.Broadcast.StreamScorebug = !_venue.State.Broadcast.StreamScorebug;
        _venue.Save();
        _scorebugButton.Content = _venue.State.Broadcast.StreamScorebug ? "Scorebug ON" : "Scorebug OFF";
        // The monitor always shows the composed picture so the operator can see and position the
        // graphic. The ON/OFF switch only decides whether the stream itself includes it.
        RebuildOverlay();
    }

    ScorebugLayout CurrentLayout()
    {
        // The saved channel assignment is authoritative. The picker is kept in step by SelectSource,
        // but a stale picker must never silently override a channel's pinned overlay.
        if (_venue.State.Broadcast.StreamLayoutId is { Length: > 0 } assigned
            && _catalog.FindLayout(assigned) is { } assignedLayout)
            return assignedLayout;
        if (_layoutBox.SelectedItem is LayoutChoice { LayoutId: { Length: > 0 } pinned }
            && _catalog.FindLayout(pinned) is { } pinnedLayout)
            return pinnedLayout;
        var stream = FirstStreamDestination();
        if (!string.IsNullOrWhiteSpace(stream.DisplayId) && _catalog.FindDisplay(stream.DisplayId) is { } display)
            return _catalog.PublishedLayoutOf(display.Id);
        return _catalog.PublishedLayoutOf(_catalog.ActiveDisplay.Id);
    }

    void RebuildOverlay()
    {
        if (_overlay is not null)
        {
            _stage.Children.Remove(_overlay);
            _overlay.Dispose();
            _overlay = null;
        }
        var layout = CurrentLayout();
        _overlay = new ScorebugCanvasView(_catalog, _engine,
            stateProvider: () => _engine.State,
            playback: _playback,
            layoutProvider: CurrentLayout,
            liveValues: true,
            transparentBackdrop: true)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _overlay.ShowAllLayers();
        _overlay.Visibility = Visibility.Visible;
        // The composed scorebug belongs above the camera, never behind it. Without an explicit
        // z-order it could end up underneath the raw preview and look as if Live Book only showed
        // a camera.
        Panel.SetZIndex(_overlay, 10);
        _stage.Children.Add(_overlay);
        // The large monitor is the configured display, so its own backdrop is part of the picture.
        // A transparent layout would otherwise preview on black and hide the display's colour.
        _stage.Background = StageBrush(layout.Background);
        _scorebugButton.Content = _venue.State.Broadcast.StreamScorebug ? "Scorebug ON" : "Scorebug OFF";
        var display = _catalog.FindDisplayOfLayout(layout.Id)?.Name ?? _catalog.ActiveDisplay.Name;
        _displayBadge.Text = $"DISPLAY · {display}";
        _displayCaption.Text = $"{layout.Name} · {layout.Width}×{layout.Height} · "
            + (string.IsNullOrWhiteSpace(_venue.State.Broadcast.StreamLayoutId) ? "follows venue programme" : "assigned to stream")
            + (_venue.State.Broadcast.StreamScorebug ? " · SCOREBUG ON" : " · SCOREBUG OFF");
        _layoutInfo.Text = $"{layout.Name} · {layout.Width}×{layout.Height} · "
            + (string.IsNullOrWhiteSpace(_venue.State.Broadcast.StreamLayoutId) ? "follows venue programme" : "pinned for the stream")
            + (_venue.State.Broadcast.StreamScorebug ? "" : " · SCOREBUG OFF");
        _layoutThumb.Source = Thumbnail(layout, 320, 180);
        _streamUrl.Text = _headend.StreamUrl(FirstStreamDestination().Id);
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

    string DisplayNameFor(ScorebugLayout layout) =>
        _catalog.FindDisplayOfLayout(layout.Id)?.Name ?? _catalog.ActiveDisplay.Name;

    static Brush StageBrush(string hex)
    {
        try
        {
            var colour = (Color)ColorConverter.ConvertFromString(hex);
            if (colour.A == 0) colour = Color.FromRgb(0x05, 0x08, 0x0C);
            return new SolidColorBrush(colour);
        }
        catch { return new SolidColorBrush(Color.FromRgb(0x05, 0x08, 0x0C)); }
    }

    BroadcastDestination FirstStreamDestination()
    {
        var settings = _venue.State.Broadcast;
        settings.Normalize();
        var stream = settings.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.Stream);
        if (stream is not null) return stream;
        stream = new BroadcastDestination { Name = "Stream Studio output", Kind = BroadcastDestinationKind.Stream, Standard = "720p60" };
        settings.Destinations.Add(stream);
        _venue.Save();
        return stream;
    }

    void BindDestinations()
    {
        _destinationPanel.Children.Clear();
        _destinationRows.Clear();
        var settings = _venue.State.Broadcast;
        settings.Normalize();
        foreach (var destination in settings.Destinations)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x25, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x35, 0x42)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5)
            };
            var stack = new StackPanel();
            var top = new DockPanel { LastChildFill = true };
            var enabled = new CheckBox { IsChecked = destination.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            enabled.Checked += (_, _) => SetDestinationEnabled(destination, true);
            enabled.Unchecked += (_, _) => SetDestinationEnabled(destination, false);
            DockPanel.SetDock(enabled, Dock.Left);
            top.Children.Add(enabled);
            top.Children.Add(new TextBlock { Text = destination.Name, FontWeight = FontWeights.Bold, FontSize = 11 });
            stack.Children.Add(top);
            var state = new TextBlock { Text = destination.StateLabel, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)) };
            stack.Children.Add(state);
            var url = new TextBlock
            {
                Text = _headend.UrlsFor(destination).FirstOrDefault() ?? "",
                FontFamily = new FontFamily("Consolas"), FontSize = 9, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)), Margin = new Thickness(0, 3, 0, 0)
            };
            stack.Children.Add(url);
            if (destination.Kind == BroadcastDestinationKind.FillKey)
            {
                var keyUrl = new TextBlock
                {
                    Text = _headend.KeyUrl(destination.Id), FontFamily = new FontFamily("Consolas"),
                    FontSize = 9, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C))
                };
                stack.Children.Add(keyUrl);
            }
            var copy = new Button { Content = "Copy", MinHeight = 24, MinWidth = 48, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0) };
            copy.Click += (_, _) => Copy(string.Join(Environment.NewLine, _headend.UrlsFor(destination)));
            stack.Children.Add(copy);
            row.Child = stack;
            _destinationPanel.Children.Add(row);
            _destinationRows.Add((destination, state, url));
        }
    }

    void SetDestinationEnabled(BroadcastDestination destination, bool enabled)
    {
        destination.Enabled = enabled;
        _venue.Save();
        if (!enabled) _headend.Unregister(destination.Id);
        else if (_headend.IsRunning) _headend.Register(destination.Id, destination.Name, destination.Kind, CurrentLayout().Name, DisplayNameFor(CurrentLayout()));
    }

    void ToggleRunning()
    {
        _running = !_running;
        if (_running)
        {
            var settings = _venue.State.Broadcast;
            settings.Normalize();
            if (!_headend.Start(settings.PreferredPort))
            {
                _running = false;
                _status.Text = "STREAM ERROR · " + (_headend.Error ?? "the local headend could not start");
                _runButton.Content = "Go live";
                return;
            }
            var stream = FirstStreamDestination();
            stream.Enabled = true;
            _venue.Save();
            BindDestinations();
            foreach (var destination in settings.Destinations.Where(item => item.Enabled))
                _headend.Register(destination.Id, destination.Name, destination.Kind, CurrentLayout().Name, DisplayNameFor(CurrentLayout()));
            _runButton.Content = "Stop stream";
            _status.Text = "STREAM STUDIO ON AIR · " + _headend.StreamUrl(stream.Id) + " · local encoder source";
        }
        else
        {
            _push.Stop();
            _pushButton.Content = "Push to provider";
            _runButton.Content = "Go live";
            _status.Text = "STREAM STUDIO STOPPED · the last delivered frame remains available until the next start";
        }
        _streamUrl.Text = _headend.StreamUrl(FirstStreamDestination().Id);
    }

    void Render()
    {
        if (_rendering) return;
        _rendering = true;
        var hideOverlayForStream = false;
        try
        {
            RefreshDestinationStates();
            RefreshPushState();
            if (_running)
            {
                var clock = Stopwatch.StartNew();
                var layout = CurrentLayout();
                var raster = FirstStreamDestination().Raster;
                var width = raster.Width;
                var height = raster.Height;
                _stage.Measure(new Size(width, height));
                _stage.Arrange(new Rect(0, 0, width, height));
                _stage.UpdateLayout();
                // The preview keeps the scorebug visible, but a switched-off scorebug must not
                // reach the delivered stream frame.
                if (_overlay is not null)
                {
                    _overlay.Visibility = _venue.State.Broadcast.StreamScorebug ? Visibility.Visible : Visibility.Collapsed;
                    hideOverlayForStream = true;
                    _stage.UpdateLayout();
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(_stage);
                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                var jpeg = stream.ToArray();
                foreach (var destination in _venue.State.Broadcast.Destinations
                             .Where(item => item.Enabled && item.Kind == BroadcastDestinationKind.Stream))
                    _headend.SetFrames(destination.Id, new BroadcastRenderResult(jpeg, [], [], width, height, _renderMs));
                _frames++;
                var now = DateTime.UtcNow;
                var interval = Math.Max(0.001, (now - _lastRender).TotalSeconds);
                if (interval > 0.45) _dropped += Math.Max(0, (long)(interval / 0.2) - 1);
                _lastRender = now;
                _renderMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
                _headend.NoteRender(_renderMs);
                _stats.Text = $"{width}×{height} · {_renderMs:0.0} ms render\n"
                    + $"{_frames} display frames · {_dropped} dropped\n"
                    + $"display {_catalog.FindDisplayOfLayout(CurrentLayout().Id)?.Name ?? _catalog.ActiveDisplay.Name}\n"
                    + $"genlock {_venue.State.Broadcast.GenlockSource}\n"
                    + $"frame interval {interval * 1000:0} ms";
            }
            else
            {
                _stats.Text = "stream idle\n"
                    + $"display {_catalog.FindDisplayOfLayout(CurrentLayout().Id)?.Name ?? _catalog.ActiveDisplay.Name}\n"
                    + $"genlock {_venue.State.Broadcast.GenlockSource}";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "STREAM ERROR · " + ex.Message;
        }
        finally
        {
            if (hideOverlayForStream && _overlay is not null) _overlay.Visibility = Visibility.Visible;
            _rendering = false;
        }
    }

    void RefreshDestinationStates()
    {
        var settings = _venue.State.Broadcast;
        settings.Normalize();
        foreach (var (destination, state, url) in _destinationRows)
        {
            _headend.RefreshDestination(destination);
            state.Text = destination.StateLabel + " · " + destination.KindLabel;
            url.Text = _headend.UrlsFor(destination).FirstOrDefault() ?? "";
        }
    }

    static void Copy(string text)
    {
        try { Clipboard.SetText(string.IsNullOrWhiteSpace(text) ? "" : text); } catch { }
    }

    void EngineChanged()
    {
        // ScorebugCanvasView listens to the engine directly; this hook exists so the stream timer
        // never has to reach across threads to decide when the next frame is due.
    }

    void CatalogChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CatalogChanged); return; }
        if (_layoutBox.SelectedItem is null) RefreshLayouts();
        // A layout edit or a new on-air layout must reach the compositor immediately, not only after
        // the window is reopened.
        _overlay?.Rebuild();
    }
}
