using System.Windows;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class ControlRoomWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly MediaPlayback? _playback;
    readonly AutomationRuntime? _automation;
    readonly string _theme;
    ScorebugEditorWindow? _displayStudio;
    ContentStudioWindow? _contentStudio;
    LiveSwitchWindow? _liveSwitch;
    LiveChannelWindow? _liveChannel;
    Window? _ownerWindow;
    WindowState? _ownerState;

    public ControlRoomWindow(ScorebugCatalog catalog, GameEngine engine, MediaPlayback? playback = null,
        AutomationRuntime? automation = null, string theme = "Dark")
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _playback = playback;
        _automation = automation;
        _theme = theme;
        Loaded += (_, _) =>
        {
            _ownerWindow = Owner;
            _ownerState = _ownerWindow?.WindowState;
        };
        Closing += (_, e) => { if (!e.Cancel) Owner = null; };
        ThemeManager.Apply(this, theme);
        _catalog.Changed += Changed;
        _engine.Changed += Changed;
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= Changed;
            _engine.Changed -= Changed;
            _displayStudio?.Close();
            _contentStudio?.Close();
            _liveSwitch?.Close();
            _liveChannel?.Close();
            Dispatcher.BeginInvoke(new Action(RestoreOwnerIfMinimized));
        };
    }

    /// <summary>Closing the control map must leave the console visible, not minimized behind it.</summary>
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
        ProgramName.Text = program?.Name ?? "No layout";
        ProgramState.Text = $"{display.Name} · {program?.Width ?? 0} × {program?.Height ?? 0}";
        DisplayCount.Text = $"{_catalog.Displays.Count} display{(_catalog.Displays.Count == 1 ? "" : "s")}";
        DisplayNames.Text = string.Join(" · ", _catalog.Displays.Select(item => item.Name));
        ContentCount.Text = $"{ContentTemplateCatalog.All.Count} templates";
        ContentNames.Text = $"{_catalog.Storyboard.Scenes.Count} scenes · {_catalog.Sponsors.Count} sponsor slots · brand {_catalog.Brand.Name}";
        SpineCount.Text = $"{_engine.State.Events.Count} events";
        SpineState.Text = $"{_catalog.Transitions.Count + _catalog.Animations.Count} motion clips · {_engine.State.SportId}";
        OnAirBadge.Text = $"{_catalog.Displays.Count} LIVE";
        EventBadge.Text = $"{_engine.State.Events.Count} EVENTS";
        DisplayStatus.Text = $"{display.Layouts.Count} layouts · editing {display.Name}";
        ContentStatus.Text = $"{_catalog.Brand.Name} · {_catalog.Storyboard.Scenes.Count} storyboard scenes";
        SwitchStatus.Text = $"{program?.Name ?? "nothing"} · {display.Layouts.Count} sources";
        ChannelStatus.Text = $"{_engine.State.Events.Count} events · data live";
        StatusText.Text = $"SHOW CONTROL MAP READY · {display.Name} · {program?.Name ?? "no layout"}";
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
}
