using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class LiveChannelWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly MediaPlayback? _playback;
    readonly AutomationRuntime? _automation;
    readonly string _theme;
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
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
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
                Width = 158, Height = 80, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 0),
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
