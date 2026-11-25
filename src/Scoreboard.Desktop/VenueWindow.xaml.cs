using System.Windows;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The Venue surface in its own window. The fleet, playlist, schedule, sponsor and channel desk
/// lives in <see cref="VenueDock"/> so it can be hosted by the map without owning a window.
/// </summary>
public sealed class VenueWindow : Window
{
    readonly VenueDock _desk;
    readonly NodeService _nodes;
    readonly bool _ownsNodes;
    Window? _ownerWindow;
    WindowState? _ownerState;

    public VenueWindow(VenueEngine venue, ScorebugCatalog catalog, GameEngine engine,
        MediaPlayback? playback = null, AutomationRuntime? automation = null, string theme = "Dark",
        NodeService? nodes = null)
    {
        Title = "ScorePlus — Venue";
        Width = 1480;
        Height = 920;
        MinWidth = 1120;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0D));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        FontFamily = new FontFamily("Segoe UI");
        _nodes = nodes ?? new NodeService(venue, engine, catalog, Dispatcher);
        _ownsNodes = nodes is null;
        _desk = new VenueDock(venue, catalog, engine, _nodes, playback, automation, theme);
        Content = _desk;
        Loaded += (_, _) =>
        {
            _ownerWindow = Owner;
            _ownerState = _ownerWindow?.WindowState;
        };
        Closing += (_, e) => { if (!e.Cancel) Owner = null; };
        ThemeManager.Apply(this, theme);
        Closed += (_, _) =>
        {
            if (_ownsNodes) _nodes.Dispose();
            Dispatcher.BeginInvoke(new Action(RestoreOwnerIfMinimized));
        };
    }

    /// <summary>The venue desk, exposed for the checks.</summary>
    public VenueDock Desk => _desk;

    /// <summary>The node service, exposed for the Stage 5 checks and node-package tests.</summary>
    public NodeService Nodes => _nodes;

    void RestoreOwnerIfMinimized()
    {
        if (_ownerWindow is not { WindowState: WindowState.Minimized }) return;
        if (_ownerState is null or WindowState.Minimized) return;
        _ownerWindow.WindowState = _ownerState.Value;
        _ownerWindow.Activate();
    }
}
