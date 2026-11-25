using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The live-book page: a compact macro grid and its output strip in their own window. The board is
/// owned by the main console and handed in, so the phone remote, detached panels and this window
/// all stay on the same cue list.
/// </summary>
public partial class ShowWindow : Window
{
    static readonly Brush Panel = Frozen(Color.FromRgb(0x14, 0x16, 0x1A));
    static readonly Brush Border = Frozen(Color.FromRgb(0x2A, 0x2D, 0x33));
    static readonly Brush Ink = Frozen(Color.FromRgb(0xF4, 0xF7, 0xFB));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x93, 0xA0, 0xB2));
    static readonly Brush Accent = Frozen(Color.FromRgb(0xF1, 0xC4, 0x0F));

    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly string _theme;
    Window? _ownerWindow;
    WindowState? _ownerState;
    string _stripSignature = "";

    public ShowWindow(ScorebugCatalog catalog, GameEngine engine, MacroDock showBoard, string theme = "Dark")
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        _theme = theme;
        ShowBoard = showBoard;
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
        ThemeManager.Apply(this, theme);
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
        CueBadge.Text = $"{_engine.State.Cues.Count} CUES";
        StatusText.Text = $"SHOW READY · {_catalog.ActiveDisplay.Name} · {_engine.State.Macros.Count} macros";
        RefreshStrip();
    }

    void RefreshStrip()
    {
        var signature = $"{_catalog.ActiveDisplayIndex}|" + string.Join("|", _catalog.Displays.Select((display, index) =>
            $"{index}:{display.Id}:{display.Name}:{display.PublishedIndex}:{display.Layouts.Count}:"
            + $"{display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)))?.Name}"));
        if (string.Equals(signature, _stripSignature, StringComparison.Ordinal) && ShowDisplayStrip.Children.Count > 0) return;
        _stripSignature = signature;
        ShowDisplayStrip.Children.Clear();
        for (var index = 0; index < _catalog.Displays.Count; index++)
        {
            var display = _catalog.Displays[index];
            var layout = display.Layouts.ElementAtOrDefault(Math.Clamp(display.PublishedIndex, 0, Math.Max(0, display.Layouts.Count - 1)));
            var card = new Border
            {
                Width = 176, Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(9, 6, 9, 6),
                Background = Panel, BorderBrush = index == _catalog.ActiveDisplayIndex ? Accent : Border,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6)
            };
            var stack = new StackPanel();
            var head = new DockPanel();
            head.Children.Add(new TextBlock
            {
                Text = display.Name, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Ink,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var state = new TextBlock
            {
                Text = index == _catalog.ActiveDisplayIndex ? "PROGRAM" : "OUTPUT",
                FontSize = 8, FontWeight = FontWeights.Bold,
                Foreground = index == _catalog.ActiveDisplayIndex ? Accent : Muted,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(state, Dock.Right);
            head.Children.Add(state);
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock
            {
                Text = layout?.Name ?? "No layout", FontSize = 9, Foreground = Muted,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0)
            });
            card.Child = stack;
            ShowDisplayStrip.Children.Add(card);
        }
        if (ShowDisplayStrip.Children.Count == 0)
            ShowDisplayStrip.Children.Add(new TextBlock
            {
                Text = "No displays yet. Add one in the venue page.", FontSize = 10, Foreground = Muted,
                Margin = new Thickness(2, 4, 0, 4)
            });
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
