using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The live-book page: a compact macro grid and its output strip in their own window. The board is
/// owned by the main console and handed in, so the phone remote, detached panels and this window
/// all stay on the same macro list. The cue editor is deliberately hidden here; the console's own
/// cue tab remains the place for building a run of show.
/// </summary>
public partial class ShowWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly string _theme;
    Window? _ownerWindow;
    WindowState? _ownerState;


    public ShowWindow(ScorebugCatalog catalog, GameEngine engine, MacroDock showBoard, string theme = "Dark")
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _theme = theme;
        ShowBoard = showBoard;
        // The live book has its own fixed palette. Do not run the operator theme pass over it: it
        // repaints the designed dark panels to the generic window surface and makes the page look
        // like a different application.
        showBoard.SetCuePanelVisible(false);
        showBoard.Background = Frozen(Color.FromRgb(0x00, 0x00, 0x00));
        ShowHost.Content = showBoard;
        Loaded += (_, _) =>
        {
            _ownerWindow = Owner;
            _ownerState = _ownerWindow?.WindowState;
        };
        Closing += (_, e) =>
        {
            if (e.Cancel) return;
            Owner = null;
            showBoard.CommitPendingEdits();
            ShowHost.Content = null;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox or ComboBox) return;
            if (ShowBoard.HandleTransportKey(e.Key)) e.Handled = true;
        };
        Background = Frozen(Color.FromRgb(0x00, 0x00, 0x00));
        _catalog.Changed += Changed;
        _engine.Changed += Changed;
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= Changed;
            _engine.Changed -= Changed;
            Dispatcher.BeginInvoke(new Action(RestoreOwnerIfMinimized));
        };
    }

    public MacroDock ShowBoard { get; }

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
        MacroBadge.Text = $"{_engine.State.Macros.Count} MACROS";
        StatusText.Text = $"SHOW READY · {_catalog.ActiveDisplay.Name} · {_engine.State.Macros.Count} macros";
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
