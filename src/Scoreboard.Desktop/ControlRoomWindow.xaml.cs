using System.Windows;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The room behind the operator console: a map of the six surfaces that make up a show. Display
/// Studio, Content Studio, Live Switch and Live Channel open here; Show and Venue are launched from
/// the same map instead of living as tabs, so every surface keeps its own window and chrome.
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
    ScorebugEditorWindow? _displayStudio;
    ContentStudioWindow? _contentStudio;
    LiveSwitchWindow? _liveSwitch;
    LiveChannelWindow? _liveChannel;
    Window? _ownerWindow;
    WindowState? _ownerState;

    public ControlRoomWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback = null,
        AutomationRuntime? automation = null, string theme = "Dark", VenueEngine? venue = null,
        Action<Window>? openShow = null, Action<Window>? openVenue = null)
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
            _liveChannel?.Close();
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
        SwitchStatus.Text = $"{program?.Name ?? "nothing"} · {display.Layouts.Count} sources";
        ChannelStatus.Text = $"{enabledChannels} channels · {_engine.State.Events.Count} events";
        VenueStatus.Text = $"{_catalog.Displays.Count} displays · {_venue.State.Playlists.Count} playlists · {_venue.State.Schedules.Count} schedules";
        StatusText.Text = $"CONTROL ROOM READY · {display.Name} · {program?.Name ?? "no layout"}";
    }

    void DisplayStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_displayStudio is { IsVisible: true }) { _displayStudio.Activate(); return; }
        _displayStudio = new ScorebugEditorWindow(_catalog, _engine, _playback, _theme, _automation) { Owner = this };
        _displayStudio.Closed += (_, _) => { _displayStudio = null; Refresh(); };
        _displayStudio.Show();
        StatusText.Text = "DISPLAY STUDIO opened in its own window.";
    }

    void ContentStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_contentStudio is { IsVisible: true }) { _contentStudio.Activate(); return; }
        _contentStudio = new ContentStudioWindow(_catalog, _engine, _theme) { Owner = this };
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
        _liveSwitch = new LiveSwitchWindow(_catalog, _engine, _theme) { Owner = this };
        _liveSwitch.Closed += (_, _) => { _liveSwitch = null; Refresh(); };
        _liveSwitch.Show();
        StatusText.Text = "LIVE SWITCH opened in its own window.";
    }

    void LiveChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_liveChannel is { IsVisible: true }) { _liveChannel.Activate(); return; }
        _liveChannel = new LiveChannelWindow(_catalog, _engine, _playback, _automation, _theme) { Owner = this };
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
