using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class LiveSwitchWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly VenueStore _venue;
    readonly MediaSession? _session;
    readonly ReplayBoothController? _booth;
    readonly MediaPlayback? _playback;
    readonly BroadcastHeadend _headend = BroadcastHeadend.Shared;
    readonly DispatcherTimer _colourWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _visionWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly List<ScorebugCanvasView> _views = [];
    bool _refreshing;
    int _previewIndex;
    int _multiviewPage;
    string? _displayId;
    string _colourSignature = "";
    bool _refreshingProfile;
    TextBlock? _broadcastStats;
    TextBlock? _profileSummary;
    TextBlock? _fillUrlText;
    TextBlock? _keyUrlText;
    TextBlock? _streamUrlText;
    ComboBox? _profileDisplayBox;
    ComboBox? _profileStandardBox;
    TextBox? _safeActionInput;
    TextBox? _safeTitleInput;
    CheckBox? _fillKeyCheck;
    CheckBox? _lowerThirdCheck;
    CheckBox? _tickerCheck;
    Slider? _masterSlider;
    ProgressBar? _masterMeter;
    TextBlock? _masterMeterText;
    LiveBookWindow? _liveBookWindow;
    CameraSwitchWindow? _cameraSwitchWindow;
    GraphicsOutputWindow? _graphicsWindow;

    sealed record DisplayChoice(string Id, string Label) { public override string ToString() => Label; }
    sealed record TransitionChoice(LayoutTransition Kind, string Label) { public override string ToString() => Label; }
    sealed record LayoutPick(string Id, string Label) { public override string ToString() => Label; }
    sealed record StandardPick(string Key, string Label) { public override string ToString() => Label; }
    sealed record DisplayPick(string Id, string Label) { public override string ToString() => Label; }

    static readonly TransitionChoice[] Transitions =
    [
        new(LayoutTransition.Cut, "Cut"),
        new(LayoutTransition.Fade, "Dissolve"),
        new(LayoutTransition.SlideLeft, "Slide left"),
        new(LayoutTransition.SlideRight, "Slide right"),
        new(LayoutTransition.SlideUp, "Slide up"),
        new(LayoutTransition.SlideDown, "Slide down"),
        new(LayoutTransition.Zoom, "Zoom"),
        new(LayoutTransition.Move, "Move items")
    ];

    static readonly (string Background, string Border, string Foreground)[] TileColours =
    [
        ("#1D3C50", "#3D8FB8", "#BFE7FA"), ("#2C2440", "#8A6BC8", "#D9C9FF"),
        ("#163C32", "#38D7A3", "#9BF2D7"), ("#3F2E18", "#D9A441", "#FFE0A3"),
        ("#3C1B28", "#D45C86", "#FFC1D4"), ("#23313F", "#6E8BA5", "#D2E1EE")
    ];

    static readonly (string Name, string Hex)[] LayoutColourChoices =
    [
        ("Red", "#B52B32"), ("Blue", "#2467B3"), ("Purple", "#6842A8"),
        ("Green", "#24754F"), ("Gold", "#A56B16"), ("Pink", "#A43A68"),
        ("Slate", "#34495E"), ("Charcoal", "#2B2F36")
    ];

    public LiveSwitchWindow(ScorebugCatalog catalog, GameEngine engine, string theme = "Dark")
        : this(catalog, engine, theme, null, null, null)
    {
    }

    public LiveSwitchWindow(ScorebugCatalog catalog, GameEngine engine, string theme, VenueStore? venueStore)
        : this(catalog, engine, theme, venueStore, null, null)
    {
    }

    public LiveSwitchWindow(ScorebugCatalog catalog, GameEngine engine, string theme, VenueStore? venueStore,
        MediaSession? session, ReplayBoothController? booth)
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _venue = venueStore ?? VenueStore.Load();
        _session = session;
        _booth = booth;
        _playback = session?.Playback;
        ThemeManager.Apply(this, theme);
        _displayId = _catalog.ActiveDisplay.Id;
        _previewIndex = _catalog.ActiveDisplay.PublishedIndex;
        TransitionBox.ItemsSource = Transitions;
        TransitionBox.SelectedIndex = 1;
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        _colourWatch.Tick += (_, _) => RefreshColoursIfChanged();
        _colourWatch.Start();
        _visionWatch.Tick += (_, _) => RefreshProductionStatus();
        _visionWatch.Start();
        WindowOwnerGuard.Attach(this);
        BuildProductionTabs();
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
            _colourWatch.Stop();
            _visionWatch.Stop();
            DisposeViews();
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
        UpdateClock();
        RefreshGameData();
    }

    void UpdateClock()
    {
        var state = _engine.State;
        ClockStatus.Text = $"{state.HomeAbbreviation} {state.HomeScore} – {state.AwayScore} {state.AwayAbbreviation}   ·   " +
            $"{GameEngine.FormatClock(state.ClockSeconds, ClockStyle.Padded)}   ·   {PeriodDisplay.Format(state)}";
    }

    void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var display = _catalog.FindDisplay(_displayId) ?? _catalog.ActiveDisplay;
            _displayId = display.Id;
            var layouts = display.Layouts;
            if (layouts.Count == 0) return;
            _previewIndex = Math.Clamp(_previewIndex, 0, layouts.Count - 1);
            var publishedIndex = Math.Clamp(display.PublishedIndex, 0, layouts.Count - 1);
            var preview = layouts[_previewIndex];
            var program = layouts[publishedIndex];
            DisposeViews();

            DisplayBox.ItemsSource = _catalog.Displays.Select((item, index) => new DisplayChoice(item.Id, $"{index + 1} · {item.Name}")).ToArray();
            DisplayBox.SelectedItem = DisplayBox.Items.OfType<DisplayChoice>().FirstOrDefault(choice => choice.Id == display.Id);
            MultiviewDisplayName.Text = $"   {display.Name}";
            ProgramLabel.Text = $"PROGRAM · {program.Name}";
            PreviewLabel.Text = $"PREVIEW · {preview.Name}";

            SetHost(ProgramHost, program);
            SetHost(PreviewHost, preview, showAllLayers: true);
            BuildMultiview(layouts, publishedIndex);
            BuildSources(layouts, publishedIndex);
            BuildLayers(preview);
            RefreshProfile();
            RefreshProductionStatus();
            UpdateClock();
            RefreshGameData();
            SwitchStatus.Text = publishedIndex == _previewIndex
                ? $"{program.Name} is on air. Choose another source to preview."
                : $"Next: {preview.Name}";
            StatusText.Text = $"LIVE SWITCH · {display.Name} · {layouts.Count} layouts · program {publishedIndex + 1} · preview {_previewIndex + 1}";
            _colourSignature = ColourSignature();
        }
        finally { _refreshing = false; }
    }

    string ColourSignature() => string.Join("|", _venue.State.LayoutColors
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .Select(pair => $"{pair.Key}={pair.Value}"));

    void RefreshColoursIfChanged()
    {
        if (_refreshing) return;
        var signature = ColourSignature();
        if (string.Equals(signature, _colourSignature, StringComparison.Ordinal)) return;
        _colourSignature = signature;
        Refresh();
    }

    void SetHost(ContentControl host, ScorebugLayout layout, bool showAllLayers = false)
    {
        host.Content = null;
        var view = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
            playback: _playback, layoutProvider: () => layout, liveValues: true)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _views.Add(view);
        if (showAllLayers) view.ShowAllLayers();
        host.Content = view;
    }

    void DisposeViews()
    {
        foreach (var view in _views) view.Dispose();
        _views.Clear();
        ProgramHost.Content = null;
        PreviewHost.Content = null;
    }

    // ---------- Vision production strip ----------

    void BuildProductionTabs()
    {
        VisionTabs.Items.Clear();
        // Clips and cameras moved to their own Live Clips window. Vision keeps the two surfaces an
        // operator needs while switching: the audio bus and this display's broadcast channel.
        var audioItem = new TabItem { Header = "AUDIO", Content = AudioTab() };
        var broadcastItem = new TabItem { Header = "BROADCAST", Content = BroadcastTab() };
        foreach (var item in new[] { audioItem, broadcastItem })
            item.SetResourceReference(StyleProperty, "VisionTabItem");
        VisionTabs.Items.Add(audioItem);
        VisionTabs.Items.Add(broadcastItem);
    }

    UIElement AudioTab()
    {
        var panel = new StackPanel { Margin = new Thickness(2) };
        var meterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 5), LastChildFill = true };
        var meterLabel = new TextBlock { Text = "MASTER MIX", Width = 68, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(meterLabel, Dock.Left);
        meterRow.Children.Add(meterLabel);
        _masterMeterText = new TextBlock { Text = "—", Width = 74, FontFamily = new FontFamily("Consolas"), FontSize = 9, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x66)) };
        DockPanel.SetDock(_masterMeterText, Dock.Right);
        meterRow.Children.Add(_masterMeterText);
        _masterMeter = new ProgressBar { Minimum = 0, Maximum = 100, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xD7, 0xA3)) };
        meterRow.Children.Add(_masterMeter);
        panel.Children.Add(meterRow);
        panel.Children.Add(AudioRow("MASTER", _session?.MasterVolume ?? 1, value => _session?.SetMasterVolume(value), null));
        panel.Children.Add(new TextBlock
        {
            Text = "Master drives the programme/cue bus. Clip A and B trims live with their channels in the Live Clips window; the venue mixer keeps its own camera and microphone buses.",
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 3, 0, 0)
        });
        // No ScrollViewer: the master fader, its meter and the explanation fit in the strip at
        // once. Audio used to be the one tab that scrolled, which hid the bottom of the fader.
        return panel;
    }

    UIElement AudioRow(string label, double initial, Action<double> setValue, Action<bool>? muteAction)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
        var name = new TextBlock { Text = label, Width = 68, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        // The fader stretches to the row instead of a fixed 260px that could fall outside a
        // narrow strip and look invisible. Its rail and thumb are drawn by VisionSlider.
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = Math.Clamp(initial * 100, 0, 100),
            MinWidth = 180, Height = 26, IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x48)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8D, 0xEB, 0xCB)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x50, 0x64))
        };
        // Use the same proven track as the Replay Booth scrubber. The hand-drawn VisionSlider
        // template collapsed to nothing under the tab's measured height, which left the master
        // fader invisible on some DPI scales.
        slider.SetResourceReference(StyleProperty, "ReplayTransportSlider");
        slider.MinWidth = 160;
        slider.Height = 28;
        slider.ToolTip = "Master programme / cue bus level";
        var valueText = new TextBlock { Width = 42, FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x66)), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        valueText.Text = $"{slider.Value:0}%";
        slider.ValueChanged += (_, e) => { valueText.Text = $"{e.NewValue:0}%"; setValue(e.NewValue / 100); };
        DockPanel.SetDock(valueText, Dock.Right);
        row.Children.Add(valueText);
        if (muteAction is not null)
        {
            var mute = new CheckBox { Content = "MUTE", FontSize = 9, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            mute.Checked += (_, _) => muteAction(true);
            mute.Unchecked += (_, _) => muteAction(false);
            DockPanel.SetDock(mute, Dock.Right);
            row.Children.Add(mute);
        }
        row.Children.Add(slider);
        if (label == "MASTER") _masterSlider = slider;
        return row;
    }

    UIElement BroadcastTab()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var left = new StackPanel { Margin = new Thickness(2) };
        left.Children.Add(FieldLabel("DISPLAY CHANNEL"));
        _profileDisplayBox = new ComboBox { MinHeight = 26, Margin = new Thickness(0, 0, 0, 3) };
        _profileDisplayBox.SelectionChanged += (_, _) => SyncProfile();
        left.Children.Add(_profileDisplayBox);
        left.Children.Add(new TextBlock
        {
            Text = "The channel follows this display's published layout. Switching artwork never re-points the output.",
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5)
        });
        var standardRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        _profileStandardBox = new ComboBox { Width = 188, MinHeight = 26, Margin = new Thickness(0, 0, 8, 0) };
        _profileStandardBox.ItemsSource = BroadcastStandards.All.Select(item => new StandardPick(item.Key, item.Label)).ToArray();
        standardRow.Children.Add(_profileStandardBox);
        _safeActionInput = new TextBox { Width = 52, MinHeight = 26, Text = "90", Margin = new Thickness(0, 0, 4, 0) };
        _safeTitleInput = new TextBox { Width = 52, MinHeight = 26, Text = "80", Margin = new Thickness(0, 0, 8, 0) };
        standardRow.Children.Add(new TextBlock { Text = "SAFE", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), Margin = new Thickness(0, 0, 4, 0) });
        standardRow.Children.Add(_safeActionInput);
        standardRow.Children.Add(_safeTitleInput);
        left.Children.Add(standardRow);
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        _fillKeyCheck = new CheckBox { Content = "Fill + key", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
        _lowerThirdCheck = new CheckBox { Content = "Lower third stack", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
        _tickerCheck = new CheckBox { Content = "Ticker stack", IsChecked = true };
        checks.Children.Add(_fillKeyCheck);
        checks.Children.Add(_lowerThirdCheck);
        checks.Children.Add(_tickerCheck);
        left.Children.Add(checks);
        var apply = new Button { Content = "Apply profile", MinHeight = 28, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Save this display's standard, safe areas and graphic stacks" };
        apply.Click += (_, _) => ApplyProfile();
        var applyRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(apply, Dock.Right);
        applyRow.Children.Add(apply);
        _profileSummary = new TextBlock
        {
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap
        };
        applyRow.Children.Add(_profileSummary);
        left.Children.Add(applyRow);
        grid.Children.Add(left);
        var right = new StackPanel { Margin = new Thickness(2) };
        right.Children.Add(FieldLabel("LOCAL OUTPUTS"));
        _fillUrlText = UrlLine();
        _keyUrlText = UrlLine();
        _streamUrlText = UrlLine();
        right.Children.Add(UrlRow("FILL", _fillUrlText));
        right.Children.Add(UrlRow("KEY", _keyUrlText));
        right.Children.Add(UrlRow("STREAM", _streamUrlText));
        _broadcastStats = new TextBlock { FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        right.Children.Add(_broadcastStats);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        // The profile and every local output URL fit in the strip together. Keeping this out of a
        // ScrollViewer is what makes the broadcast tab readable without scrolling.
        return grid;
    }

    static TextBlock FieldLabel(string text) => new()
    {
        Text = text, FontSize = 9, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2)), Margin = new Thickness(0, 0, 0, 3)
    };

    static TextBlock UrlLine() => new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 9, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)), Margin = new Thickness(0, 0, 0, 2)
    };

    static UIElement UrlRow(string label, TextBlock text)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        host.Children.Add(new TextBlock { Text = label, FontSize = 8, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)) });
        host.Children.Add(text);
        return host;
    }

    void ReturnProgram_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        StatusText.Text = "PROGRAM · layout returned";
    }

    void RefreshProfile()
    {
        if (_profileDisplayBox is null) return;
        var displays = _catalog.Displays.Select(display => new DisplayPick(display.Id, display.Name)).ToArray();
        var current = (_profileDisplayBox.SelectedItem as DisplayPick)?.Id;
        _refreshingProfile = true;
        try
        {
            _profileDisplayBox.ItemsSource = displays;
            _profileDisplayBox.SelectedItem = displays.FirstOrDefault(item => item.Id == current)
                ?? displays.FirstOrDefault(item => item.Id == _displayId)
                ?? displays.FirstOrDefault();
        }
        finally { _refreshingProfile = false; }
        SyncProfile();
    }

    void SyncProfile()
    {
        if (_refreshingProfile || _profileDisplayBox?.SelectedItem is not DisplayPick pick) return;
        var display = _catalog.FindDisplay(pick.Id);
        if (display is null) return;
        var profile = display.Broadcast ?? new BroadcastProfile();
        profile.Normalize();
        _profileStandardBox!.SelectedItem = (_profileStandardBox.ItemsSource as IEnumerable<StandardPick>)?
            .FirstOrDefault(item => item.Key == profile.Standard);
        if (_safeActionInput is not null && !_safeActionInput.IsKeyboardFocusWithin) _safeActionInput.Text = profile.SafeActionPercent.ToString("0");
        if (_safeTitleInput is not null && !_safeTitleInput.IsKeyboardFocusWithin) _safeTitleInput.Text = profile.SafeTitlePercent.ToString("0");
        if (_fillKeyCheck is not null) _fillKeyCheck.IsChecked = profile.FillKey;
        if (_lowerThirdCheck is not null) _lowerThirdCheck.IsChecked = profile.LowerThirdStack;
        if (_tickerCheck is not null) _tickerCheck.IsChecked = profile.TickerStack;
        if (_profileSummary is not null) _profileSummary.Text = profile.Summary;
    }

    void ApplyProfile()
    {
        if (_profileDisplayBox?.SelectedItem is not DisplayPick pick) return;
        var display = _catalog.FindDisplay(pick.Id);
        if (display is null) return;
        var action = double.TryParse(_safeActionInput?.Text, out var actionValue) ? actionValue : 90;
        var title = double.TryParse(_safeTitleInput?.Text, out var titleValue) ? titleValue : 80;
        var standard = (_profileStandardBox?.SelectedItem as StandardPick)?.Key ?? "1080p60";
        _catalog.UpdateDisplay(pick.Id, edit =>
        {
            edit.Broadcast = new BroadcastProfile
            {
                Standard = standard,
                SafeActionPercent = action,
                SafeTitlePercent = title,
                FillKey = _fillKeyCheck?.IsChecked != false,
                LowerThirdStack = _lowerThirdCheck?.IsChecked != false,
                TickerStack = _tickerCheck?.IsChecked != false
            };
            edit.Broadcast.Normalize();
        });
        // The destinations are channels of this display, not of whichever layout was on air when
        // they were created. Linking them here is what makes the display the unit that goes live.
        var settings = _venue.State.Broadcast;
        settings.Normalize();
        foreach (var destination in settings.Destinations) destination.DisplayId = pick.Id;
        _venue.Save();
        StatusText.Text = $"BROADCAST CHANNEL · {display.Name} · {standard}";
        RefreshProfile();
        RefreshProductionStatus();
    }

    void RefreshProductionStatus()
    {
        var settings = _venue.State.Broadcast;
        settings.Normalize();
        var fill = settings.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.FillKey);
        var stream = settings.Destinations.FirstOrDefault(item => item.Kind == BroadcastDestinationKind.Stream);
        if (_fillUrlText is not null) _fillUrlText.Text = fill is null ? "not configured" : _headend.FillUrl(fill.Id);
        if (_keyUrlText is not null) _keyUrlText.Text = fill is null ? "not configured" : _headend.KeyUrl(fill.Id);
        if (_streamUrlText is not null) _streamUrlText.Text = stream is null ? "not configured" : _headend.StreamUrl(stream.Id);
        if (_masterMeter is not null && VisionTabs.SelectedIndex == 0)
        {
            // This is the machine's real output meter, not a synthetic bar driven by the fader.
            var level = SoundLevelMeter.ReadMaster();
            _masterMeter.Value = Math.Clamp(level * 100, 0, 100);
            if (_masterMeterText is not null)
                _masterMeterText.Text = SoundLevelMeter.Available
                    ? (level <= 0 ? "−∞ dBFS" : $"{SoundLevelMeter.Decibels(level):0.0} dBFS")
                    : "NO DEVICE";
        }
        if (_broadcastStats is not null)
        {
            var display = _catalog.FindDisplay(_displayId) ?? _catalog.ActiveDisplay;
            var profile = display.Broadcast ?? new BroadcastProfile();
            profile.Normalize();
            var channels = string.Join("\n", settings.Destinations.Select(destination =>
            {
                _headend.RefreshDestination(destination);
                var linked = string.IsNullOrWhiteSpace(destination.DisplayId)
                    ? display.Name
                    : _catalog.FindDisplay(destination.DisplayId)?.Name ?? "missing display";
                return $"  {destination.Name} · {linked} · {destination.KindLabel} · {destination.StateLabel}";
            }));
            _broadcastStats.Text = $"{profile.Standard} · fill+key {(profile.FillKey ? "on" : "off")} · safe {profile.SafeActionPercent:0}/{profile.SafeTitlePercent:0}%\n"
                + $"headend {(_headend.IsRunning ? $"online :{_headend.Port}" : "idle — open Live Book")}\n"
                + $"channel {display.Name} · measured latency {_headend.LastRenderMilliseconds:0.0} ms · genlock {settings.GenlockSource}\n"
                + (channels.Length == 0 ? "  no destinations configured\n" : channels + "\n")
                + $"fill viewers {_headend.ViewerCount(fill?.Id ?? "router-fill-key")} · stream viewers {_headend.ViewerCount(stream?.Id ?? "stream-main")}";
        }
    }

    void LiveBook_Click(object sender, RoutedEventArgs e)
    {
        if (_liveBookWindow is { IsVisible: true }) { _liveBookWindow.Activate(); return; }
        _liveBookWindow = new LiveBookWindow(_catalog, _engine, _playback, _session, _venue, "Dark", OpenGraphicsWindow) { Owner = this };
        _liveBookWindow.Closed += (_, _) => _liveBookWindow = null;
        _liveBookWindow.Show();
        StatusText.Text = "STREAM STUDIO opened · camera, channels and provider output";
    }

    void CameraSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraSwitchWindow is { IsVisible: true }) { _cameraSwitchWindow.Activate(); return; }
        _cameraSwitchWindow = new CameraSwitchWindow(_session, _playback, _catalog, _venue) { Owner = this };
        _cameraSwitchWindow.Closed += (_, _) => _cameraSwitchWindow = null;
        _cameraSwitchWindow.Show();
        StatusText.Text = "CAMERA SWITCH opened · camera-only Fill A / Fill B with take";
    }

    void GraphicsOutput_Click(object sender, RoutedEventArgs e) => OpenGraphicsWindow();

    void OpenGraphicsWindow()
    {
        if (_graphicsWindow is { IsVisible: true }) { _graphicsWindow.Activate(); return; }
        _graphicsWindow = new GraphicsOutputWindow(_catalog, _engine, _playback, _venue, "Dark") { Owner = this };
        _graphicsWindow.Closed += (_, _) => _graphicsWindow = null;
        _graphicsWindow.Show();
        StatusText.Text = "GRAPHICS OUTPUT opened · fill + key";
    }

    void BuildMultiview(IReadOnlyList<ScorebugLayout> layouts, int programIndex)
    {
        const int pageSize = 8;
        var pages = Math.Max(1, (layouts.Count + pageSize - 1) / pageSize);
        _multiviewPage = Math.Clamp(_multiviewPage, 0, pages - 1);
        MultiviewPageLabel.Text = $"{_multiviewPage + 1} / {pages}";
        MultiviewGrid.Children.Clear();
        for (var offset = 0; offset < pageSize; offset++)
        {
            var index = _multiviewPage * pageSize + offset;
            if (index >= layouts.Count)
            {
                // A fixed 16:9 cell, scaled into the grid cell. Keeping the tile geometry fixed and
                // letting the Viewbox do the scaling is what keeps all eight boxes the same shape
                // when the window grows or shrinks; a stretched outer Border read as a long strip.
                var emptyGrid = new Grid { Width = 320, Height = 180, Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x15)) };
                emptyGrid.Children.Add(new TextBlock
                {
                    Text = $"{index + 1}  ·  NO SOURCE", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x4C, 0x5C)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                });
                var empty = new Border
                {
                    Background = Brushes.Transparent, Margin = new Thickness(3),
                    Child = new Viewbox
                    {
                        Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
                        Child = new Border
                        {
                            Width = 320, Height = 180, BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x3D)),
                            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = emptyGrid
                        }
                    }
                };
                ThemeManager.SetSkipTheme(empty, true);
                MultiviewGrid.Children.Add(empty);
                continue;
            }
            var layout = layouts[index];
            var palette = PaletteFor(layout, index);
            var onProgram = index == programIndex;
            var onPreview = index == _previewIndex;
            var picture = new Grid { Width = 320, Height = 180, Background = new SolidColorBrush(Color.FromRgb(5, 8, 12)), ClipToBounds = true };
            var image = new Image { Source = Thumbnail(layout, 640, 360), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            picture.Children.Add(image);
            var strip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x05, 0x08, 0x0C)),
                VerticalAlignment = VerticalAlignment.Bottom, Height = 23, Padding = new Thickness(5, 0, 5, 0)
            };
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            var tally = new TextBlock
            {
                Text = onProgram ? "PGM" : onPreview ? "PVW" : $"{index + 1}",
                FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(onProgram ? Color.FromRgb(0xFF, 0x9B, 0x9B)
                    : onPreview ? Color.FromRgb(0x8D, 0xEB, 0xCB) : Color.FromRgb(0x7C, 0x8A, 0x9C)),
                Margin = new Thickness(0, 0, 6, 0)
            };
            DockPanel.SetDock(tally, Dock.Left);
            row.Children.Add(tally);
            row.Children.Add(new TextBlock { Text = layout.Name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
            strip.Child = row;
            picture.Children.Add(strip);
            var framed = new Border
            {
                Width = 320, Height = 180,
                Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x15)),
                BorderThickness = new Thickness(onProgram || onPreview ? 3 : 1),
                BorderBrush = new SolidColorBrush(onProgram ? Color.FromRgb(0xE6, 0x3B, 0x3B)
                    : onPreview ? Color.FromRgb(0x38, 0xD7, 0xA3) : ParseColour(palette.Border)),
                CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = picture
            };
            var tile = new Button
            {
                Margin = new Thickness(3), Padding = new Thickness(0), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Tag = layout.Id,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = framed },
                ToolTip = $"{index + 1}. {layout.Name}{(onProgram ? " · on air" : onPreview ? " · preview" : "")} · right-click for colour"
            };
            ThemeManager.SetSkipTheme(tile, true);
            tile.ContextMenu = BuildLayoutColourMenu(layout);
            var target = index;
            tile.Click += (_, _) => { _previewIndex = target; Refresh(); };
            tile.MouseDoubleClick += (_, _) => Take(target);
            MultiviewGrid.Children.Add(tile);
        }
    }

    void BuildSources(IReadOnlyList<ScorebugLayout> layouts, int programIndex)
    {
        SourcePanel.Children.Clear();
        for (var index = 0; index < layouts.Count; index++)
        {
            var layout = layouts[index];
            var palette = PaletteFor(layout, index);
            var onProgram = index == programIndex;
            var onPreview = index == _previewIndex;
            var button = new Button
            {
                Width = 154, Height = 36, Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(6, 2, 6, 2),
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Background)),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Foreground)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(onProgram ? "#E63B3B" : onPreview ? "#38D7A3" : palette.Border)),
                BorderThickness = new Thickness(onProgram || onPreview ? 2 : 1),
                Tag = layout.Id,
                ToolTip = $"{layout.Name}\nClick to preview · double-click to take · right-click for colour"
            };
            ThemeManager.SetSkipTheme(button, true);
            button.ContextMenu = BuildLayoutColourMenu(layout);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = $"{index + 1}  {layout.Name}", TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = onProgram ? "ON AIR" : onPreview ? "PREVIEW" : $"{layout.Width} × {layout.Height}",
                FontSize = 8, Opacity = 0.75
            });
            button.Content = stack;
            var target = index;
            button.Click += (_, _) => { _previewIndex = target; Refresh(); };
            button.MouseDoubleClick += (_, _) => Take(target);
            SourcePanel.Children.Add(button);
        }
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

    static Color ParseColour(string text) => (Color)ColorConverter.ConvertFromString(text);

    static bool TryColour(string? text, out Color colour)
    {
        colour = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            colour = ParseColour(text);
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
                    Background = new SolidColorBrush(ParseColour(hex)),
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
        _colourSignature = "";
        Refresh();
    }

    void BuildLayers(ScorebugLayout layout)
    {
        LayerPanel.Children.Clear();
        foreach (var element in Enumerable.Reverse(layout.Elements).Take(14))
        {
            var item = element;
            var button = new Button
            {
                Content = item.Name, Opacity = item.Visible ? 1 : 0.45,
                ToolTip = item.Visible ? $"{item.Name} is visible · click to hide" : $"{item.Name} is hidden · click to show"
            };
            button.SetResourceReference(StyleProperty, "LayerChip");
            button.Click += (_, _) =>
            {
                var id = item.Id;
                _catalog.UpdateLayout(layout.Id, edit =>
                {
                    var target = edit.Elements.FirstOrDefault(candidate => candidate.Id == id);
                    if (target is not null) target.Visible = !target.Visible;
                });
                Refresh();
            };
            LayerPanel.Children.Add(button);
        }
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

    void MultiviewPrev_Click(object sender, RoutedEventArgs e)
    {
        _multiviewPage = Math.Max(0, _multiviewPage - 1);
        Refresh();
    }

    void MultiviewNext_Click(object sender, RoutedEventArgs e)
    {
        _multiviewPage++;
        Refresh();
    }

    void MoveLayoutUp_Click(object sender, RoutedEventArgs e) => MovePreview(-1);
    void MoveLayoutDown_Click(object sender, RoutedEventArgs e) => MovePreview(1);

    void MovePreview(int delta)
    {
        var display = _catalog.FindDisplay(_displayId) ?? _catalog.ActiveDisplay;
        var target = _previewIndex + delta;
        if (target < 0 || target >= display.Layouts.Count) return;
        var name = display.Layouts[_previewIndex].Name;
        if (!_catalog.MoveLayout(_previewIndex, target, display.Id)) return;
        _previewIndex = target;
        StatusText.Text = $"SWITCH ORDER · {name} moved to {target + 1} of {display.Layouts.Count}";
        Refresh();
    }

    void AddDisplay_Click(object sender, RoutedEventArgs e)
    {
        var index = _catalog.AddDisplay($"Display {_catalog.Displays.Count + 1}");
        _displayId = _catalog.Displays[index].Id;
        _previewIndex = 0;
        _multiviewPage = 0;
        Refresh();
        StatusText.Text = $"LIVE SWITCH · added {_catalog.Displays[index].Name} · choose it from the display list to preview";
    }

    void RefreshGameData()
    {
        GameDataPanel.Children.Clear();
        var state = _engine.State;
        void Add(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var name = new TextBlock { Text = label, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x8A, 0x9C)) };
            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value, FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF7)), TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            DockPanel.SetDock(text, Dock.Right);
            row.Children.Add(text);
            row.Children.Add(name);
            GameDataPanel.Children.Add(row);
        }
        Add("Game", string.IsNullOrWhiteSpace(state.GameName) ? _engine.Sport.Name : state.GameName);
        Add("Matchup", $"{state.AwayTeam} @ {state.HomeTeam}");
        Add("Score", $"{state.AwayAbbreviation} {state.AwayScore} · {state.HomeAbbreviation} {state.HomeScore}");
        Add("Clock", $"{GameEngine.FormatClock(state.ClockSeconds, ClockStyle.Padded)} · {PeriodDisplay.Format(state)}");
        Add("Play clock", GameEngine.FormatClock(state.PlayClockSeconds));
        Add("Timeout clock", GameEngine.FormatClock(state.TimeoutClockSeconds));
        Add("Possession", state.HomePossession ? state.HomeTeam : state.AwayTeam);
        Add("Timeouts", $"{state.AwayAbbreviation} {state.AwayTimeouts} · {state.HomeAbbreviation} {state.HomeTimeouts}");
        Add("Records", $"{state.AwayAbbreviation} {state.AwayRecord} · {state.HomeAbbreviation} {state.HomeRecord}");
        Add("Rankings", $"{state.AwayAbbreviation} {state.AwayRanking} · {state.HomeAbbreviation} {state.HomeRanking}");
        if (state.SportId == "football")
        {
            Add("Down & distance", $"Down {state.Down} · {state.YardsToGo} to go");
            Add("Ball spot", $"{(state.BallOnHomeSide ? state.HomeAbbreviation : state.AwayAbbreviation)} {state.YardsToGoal}");
            Add("Yards", $"{state.AwayAbbreviation} {state.AwayYards} · {state.HomeAbbreviation} {state.HomeYards}");
            Add("Turnovers", $"{state.AwayAbbreviation} {state.AwayTurnovers} · {state.HomeAbbreviation} {state.HomeTurnovers}");
        }
        if (state.SportId == "basketball")
            Add("Fouls", $"{state.AwayAbbreviation} {state.AwayFouls} · {state.HomeAbbreviation} {state.HomeFouls}");
        if (state.SportId == "baseball")
            Add("Count", $"{state.Balls} balls · {state.Strikes} strikes · {state.Outs} outs");
        if (state.SportId == "baseball")
            Add("Hits", $"{state.AwayAbbreviation} {state.AwayHits} · {state.HomeAbbreviation} {state.HomeHits}");
        if (state.SportId == "soccer")
        {
            Add("Shots", $"{state.AwayAbbreviation} {state.AwayShots} · {state.HomeAbbreviation} {state.HomeShots}");
            Add("Corners", $"{state.AwayAbbreviation} {state.AwayCorners} · {state.HomeAbbreviation} {state.HomeCorners}");
            Add("Cards", $"Y {state.AwayYellowCards + state.HomeYellowCards} · R {state.AwayRedCards + state.HomeRedCards}");
        }
        Add("Penalty", state.PenaltyActive ? $"{state.PenaltyType} · {(state.PenaltyHome ? state.HomeAbbreviation : state.AwayAbbreviation)}" : "None");
        if (state.HomePlayer is { } home)
            Add("Home player", $"#{home.Number} {home.Name}" + (string.IsNullOrWhiteSpace(home.Position) ? "" : $" · {home.Position}"));
        if (state.AwayPlayer is { } away)
            Add("Away player", $"#{away.Number} {away.Name}" + (string.IsNullOrWhiteSpace(away.Position) ? "" : $" · {away.Position}"));
        foreach (var stat in state.Stats)
            Add(stat.Name, stat.Display);
        foreach (var control in state.Controls)
            Add(control.Label, control.Display(SportValues.Read(state, control.Id)));
    }

    void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || DisplayBox.SelectedItem is not DisplayChoice choice) return;
        var index = _catalog.Displays.FindIndex(display => string.Equals(display.Id, choice.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _displayId = choice.Id;
        _previewIndex = _catalog.Displays[index].PublishedIndex;
        _multiviewPage = 0;
        Refresh();
    }

    void Transition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing) SwitchStatus.Text = "Transition selected. TAKE publishes the preview.";
    }

    void Duration_Changed(object sender, TextChangedEventArgs e)
    {
        // Half-typed values are ignored; the button reads the field when it fires.
    }

    void Take_Click(object sender, RoutedEventArgs e) => Take(_previewIndex);
    void Cut_Click(object sender, RoutedEventArgs e) => Publish(_previewIndex, cut: true);
    void UpdateOutput_Click(object sender, RoutedEventArgs e) => Take(_previewIndex);
    void Clear_Click(object sender, RoutedEventArgs e) => Publish(0, cut: true);

    void Take(int index) => Publish(index, cut: false);

    void Publish(int index, bool cut)
    {
        var display = _catalog.FindDisplay(_displayId) ?? _catalog.ActiveDisplay;
        if (index < 0 || index >= display.Layouts.Count) return;
        var transition = cut
            ? new TransitionChoice(LayoutTransition.Cut, "Cut")
            : TransitionBox.SelectedItem as TransitionChoice ?? Transitions[1];
        var seconds = cut ? 0 : double.TryParse(DurationInput.Text, out var value) ? Math.Clamp(value, 0, 5) : 0.5;
        var scene = new StoryboardScene
        {
            Name = display.Layouts[index].Name, LayoutId = display.Layouts[index].Id,
            Transition = transition.Kind, TransitionSeconds = seconds
        };
        if (!_catalog.PublishStoryboardScene(scene)) return;
        _previewIndex = index;
        SwitchStatus.Text = $"{display.Layouts[index].Name} is on air ({transition.Label}{(cut ? "" : $" · {seconds:0.##}s")}).";
        StatusText.Text = $"TAKEN · {display.Name} · {display.Layouts[index].Name}";
        Refresh();
    }
}
