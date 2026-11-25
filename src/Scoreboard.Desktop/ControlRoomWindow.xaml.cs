using System.Windows;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The room behind the operator console: a map of the surfaces that make up a show. Display
/// Studio, Content Studio, Live Switch, Live Clips, Live Channel, the Replay Booth and the
/// Multiview open here; Show and Venue are launched from the same map instead of living as tabs,
/// so every surface keeps its own window and chrome.
/// </summary>
public partial class ControlRoomWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly VenueEngine _venue;
    readonly MediaPlayback? _playback;
    readonly AutomationRuntime? _automation;
    readonly string _theme;
    readonly Action<Window>? _openShow;
    readonly Action<Window>? _openVenue;
    readonly NodeService? _nodes;
    readonly MediaSession? _session;
    readonly ReplayBoothController? _booth;
    readonly Action<Window>? _openMultiview;
    readonly BroadcastHeadend _headend = BroadcastHeadend.Shared;
    ScorebugEditorWindow? _displayStudio;
    ContentStudioWindow? _contentStudio;
    LiveSwitchWindow? _liveSwitch;
    LiveClipsWindow? _liveClips;
    LiveChannelWindow? _liveChannel;
    LiveBookWindow? _liveBook;
    GraphicsOutputWindow? _graphics;
    CameraSwitchWindow? _cameraSwitch;
    Window? _ownerWindow;
    WindowState? _ownerState;

    public ControlRoomWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback = null,
        AutomationRuntime? automation = null, string theme = "Dark", VenueEngine? venue = null,
        Action<Window>? openShow = null, Action<Window>? openVenue = null, NodeService? nodes = null,
        MediaSession? session = null, ReplayBoothController? booth = null, Action<Window>? openMultiview = null)
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _automation = automation;
        _theme = theme;
        _venue = venue ?? new VenueEngine(catalog, engine);
        _openShow = openShow;
        _openVenue = openVenue;
        _nodes = nodes;
        _session = session;
        _booth = booth;
        _openMultiview = openMultiview;

        Loaded += (_, _) =>
        {
            _ownerWindow = Owner;
            _ownerState = _ownerWindow?.WindowState;
        };
        Closing += (_, e) => { if (!e.Cancel) Owner = null; };
        ThemeManager.Apply(this, theme);
        _catalog.Changed += Changed;
        _engine.Changed += Changed;
        _venue.Changed += Changed;
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= Changed;
            _engine.Changed -= Changed;
            _venue.Changed -= Changed;
            _displayStudio?.Close();
            _contentStudio?.Close();
            _liveSwitch?.Close();
            _liveClips?.Close();
            _liveChannel?.Close();
            _liveBook?.Close();
            _graphics?.Close();
            _cameraSwitch?.Close();
            Dispatcher.BeginInvoke(new Action(RestoreOwnerIfMinimized));
        };
    }

    /// <summary>Closing the room must leave the console visible, not minimized behind it.</summary>
    void RestoreOwnerIfMinimized()
    {
        if (_ownerWindow is not { WindowState: WindowState.Minimized }) return;
        if (_ownerState is null or WindowState.Minimized) return;
        _ownerWindow.WindowState = _ownerState.Value;
        _ownerWindow.Activate();
    }

    void Changed()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Changed); return; }
        Refresh();
    }

    void Refresh()
    {
        var display = _catalog.ActiveDisplay;
        var program = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, display.Layouts.Count - 1));
        var enabledChannels = _venue.State.Channels.Count(channel => channel.Enabled);
        EventBadge.Text = $"{_engine.State.Events.Count} EVENTS";
        OnAirBadge.Text = $"{enabledChannels} CHANNELS";
        DisplayStatus.Text = $"{display.Layouts.Count} layouts · editing {display.Name}";
        ContentStatus.Text = $"{_catalog.Brand.Name} · {_catalog.Storyboard.Scenes.Count} storyboard scenes";
        ShowStatus.Text = $"{_engine.State.Macros.Count} macros · {_engine.State.Cues.Count} cues";
        var channel = display.Broadcast ?? new BroadcastProfile();
        SwitchStatus.Text = $"{program?.Name ?? "nothing"} · {channel.Standard} channel";
        var fill = _venue.State.Broadcast.Destinations.FirstOrDefault(destination => destination.Kind == BroadcastDestinationKind.FillKey);
        var clips = _session?.Library.Count(entry => entry.IsVideo && !entry.IsCamera) ?? 0;
        var cameras = _session?.Library.Count(entry => entry.IsCamera) ?? 0;
        ClipsStatus.Text = $"{clips} clips · {cameras} cameras · fill+key {(fill is null ? "not configured" : _headend.ViewerCount(fill.Id) + " viewers")}";
        ReplayStatus.Text = _booth is null ? "no replay controller" : $"{_booth.Cameras.Count} replay cameras · mark in/out";
        ChannelStatus.Text = $"{enabledChannels} channels · {_engine.State.Events.Count} events";
        VenueStatus.Text = $"{_catalog.Displays.Count} displays · {_venue.State.Playlists.Count} playlists · {_venue.State.Schedules.Count} schedules";
        var stream = _venue.State.Broadcast.Destinations.FirstOrDefault(destination => destination.Kind == BroadcastDestinationKind.Stream);
        GraphicsStatus.Text = fill is null ? "no fill + key destination" : $"{fill.Raster.Label} · {(fill.IsLive ? "live" : fill.StateLabel.ToLowerInvariant())}";
        StudioStatus.Text = stream is null ? "no stream destination"
            : $"{stream.Provider} · {(stream.DisplayId.Length > 0 ? _catalog.FindDisplay(stream.DisplayId)?.Name ?? "assigned display" : "venue programme")}";
        CameraSwitchStatus.Text = $"{cameras} cameras · Fill A / Fill B";
        MultiviewStatus.Text = $"{cameras} cameras · preview, audience, replay";
        StatusText.Text = $"CONTROL ROOM READY · {display.Name} · {program?.Name ?? "no layout"}";
    }

    void DisplayStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_displayStudio is { IsVisible: true }) { _displayStudio.Activate(); return; }
        _displayStudio = new ScorebugEditorWindow(_catalog, _engine, _playback, _theme, _automation, _nodes) { Owner = this };
        _displayStudio.Closed += (_, _) => { _displayStudio = null; Refresh(); };
        _displayStudio.Show();
        StatusText.Text = "DISPLAY STUDIO opened in its own window.";
    }

    void ContentStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_contentStudio is { IsVisible: true }) { _contentStudio.Activate(); return; }
        _contentStudio = new ContentStudioWindow(_catalog, _engine, _theme, _nodes) { Owner = this };
        _contentStudio.Closed += (_, _) => { _contentStudio = null; Refresh(); };
        _contentStudio.Show();
        StatusText.Text = "CONTENT STUDIO opened in its own window.";
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_openShow is null) { StatusText.Text = "Open SHOW from the main console."; return; }
        _openShow(this);
        StatusText.Text = "SHOW opened in its own live-book window.";
    }

    void LiveSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_liveSwitch is { IsVisible: true }) { _liveSwitch.Activate(); return; }
        _liveSwitch = new LiveSwitchWindow(_catalog, _engine, _theme, _venue.Store, _session, _booth) { Owner = this };
        _liveSwitch.Closed += (_, _) => { _liveSwitch = null; Refresh(); };
        _liveSwitch.Show();
        StatusText.Text = "LIVE SWITCH opened in its own window.";
    }

    void LiveClips_Click(object sender, RoutedEventArgs e)
    {
        if (_liveClips is { IsVisible: true }) { _liveClips.Activate(); return; }
        _liveClips = new LiveClipsWindow(_session, _playback, _venue.Store, _catalog, _engine) { Owner = this };
        _liveClips.Closed += (_, _) => { _liveClips = null; Refresh(); };
        _liveClips.Show();
        StatusText.Text = "LIVE CLIPS opened in its own window.";
    }

    void ReplayBooth_Click(object sender, RoutedEventArgs e)
    {
        if (_booth is null)
        {
            StatusText.Text = "REPLAY BOOTH · no replay controller is attached";
            return;
        }
        var booth = new ReplayBoothWindow(_booth, PreferencesStore.Load()) { Owner = this };
        booth.Show();
        StatusText.Text = "REPLAY BOOTH opened · mark in/out, rate and save";
    }

    void LiveBook_Click(object sender, RoutedEventArgs e)
    {
        if (_liveBook is { IsVisible: true }) { _liveBook.Activate(); return; }
        _liveBook = new LiveBookWindow(_catalog, _engine, _playback, _session, _venue.Store, _theme, OpenGraphicsWindow) { Owner = this };
        _liveBook.Closed += (_, _) => { _liveBook = null; Refresh(); };
        _liveBook.Show();
        StatusText.Text = "STREAM STUDIO opened · channels, displays and provider output";
    }

    void CameraSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraSwitch is { IsVisible: true }) { _cameraSwitch.Activate(); return; }
        _cameraSwitch = new CameraSwitchWindow(_session, _playback, _catalog, _venue.Store) { Owner = this };
        _cameraSwitch.Closed += (_, _) => { _cameraSwitch = null; Refresh(); };
        _cameraSwitch.Show();
        StatusText.Text = "CAMERA SWITCH opened · camera-only Fill A / Fill B";
    }

    void Multiview_Click(object sender, RoutedEventArgs e)
    {
        if (_openMultiview is null) { StatusText.Text = "Open MULTIVIEW from the main console."; return; }
        _openMultiview(this);
        StatusText.Text = "MULTIVIEW opened · every source on one wall";
    }

    void GraphicsOutput_Click(object sender, RoutedEventArgs e) => OpenGraphicsWindow();

    void OpenGraphicsWindow()
    {
        if (_graphics is { IsVisible: true }) { _graphics.Activate(); return; }
        _graphics = new GraphicsOutputWindow(_catalog, _engine, _playback, _venue.Store, _theme) { Owner = this };
        _graphics.Closed += (_, _) => { _graphics = null; Refresh(); };
        _graphics.Show();
        StatusText.Text = "GRAPHICS OUTPUT opened · fill + key";
    }

    void LiveChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_liveChannel is { IsVisible: true }) { _liveChannel.Activate(); return; }
        _liveChannel = new LiveChannelWindow(_catalog, _engine, _playback, _automation, _theme, _venue.Store) { Owner = this };
        _liveChannel.Closed += (_, _) => { _liveChannel = null; Refresh(); };
        _liveChannel.Show();
        StatusText.Text = "LIVE CHANNEL opened in its own window.";
    }

    void Venue_Click(object sender, RoutedEventArgs e)
    {
        if (_openVenue is null) { StatusText.Text = "Open VENUE from the main console."; return; }
        _openVenue(this);
        StatusText.Text = "VENUE opened in its own window.";
    }
}
