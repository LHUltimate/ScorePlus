using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

public partial class LiveSwitchWindow : Window
{
    readonly ScorebugCatalog _catalog;
    readonly GameEngine _engine;
    readonly List<ScorebugCanvasView> _views = [];
    bool _refreshing;
    int _previewIndex;
    int _multiviewPage;
    string? _displayId;

    sealed record DisplayChoice(string Id, string Label) { public override string ToString() => Label; }
    sealed record TransitionChoice(LayoutTransition Kind, string Label) { public override string ToString() => Label; }

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

    public LiveSwitchWindow(ScorebugCatalog catalog, GameEngine engine, string theme = "Dark")
    {
        InitializeComponent();
        _catalog = catalog;
        _engine = engine;
        ThemeManager.Apply(this, theme);
        _displayId = _catalog.ActiveDisplay.Id;
        _previewIndex = _catalog.ActiveDisplay.PublishedIndex;
        TransitionBox.ItemsSource = Transitions;
        TransitionBox.SelectedIndex = 1;
        _catalog.Changed += CatalogChanged;
        _engine.Changed += EngineChanged;
        Refresh();
        Closed += (_, _) =>
        {
            _catalog.Changed -= CatalogChanged;
            _engine.Changed -= EngineChanged;
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
            if (_catalog.FindDisplay(display.Id) is not null && _catalog.ActiveDisplayIndex != _catalog.Displays.IndexOf(display))
                _catalog.SelectDisplay(_catalog.Displays.IndexOf(display));
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
            UpdateClock();
            RefreshGameData();
            SwitchStatus.Text = publishedIndex == _previewIndex
                ? $"{program.Name} is on air. Choose another source to preview."
                : $"Next: {preview.Name}";
            StatusText.Text = $"LIVE SWITCH · {display.Name} · {layouts.Count} layouts · program {publishedIndex + 1} · preview {_previewIndex + 1}";
        }
        finally { _refreshing = false; }
    }

    void SetHost(ContentControl host, ScorebugLayout layout, bool showAllLayers = false)
    {
        host.Content = null;
        var view = new ScorebugCanvasView(_catalog, _engine, stateProvider: () => _engine.State,
            playback: null, layoutProvider: () => layout, liveValues: true)
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

    void BuildMultiview(IReadOnlyList<ScorebugLayout> layouts, int programIndex)
    {
        const int pageSize = 6;
        var pages = Math.Max(1, (layouts.Count + pageSize - 1) / pageSize);
        _multiviewPage = Math.Clamp(_multiviewPage, 0, pages - 1);
        MultiviewPageLabel.Text = $"{_multiviewPage + 1} / {pages}";
        MultiviewGrid.Children.Clear();
        for (var offset = 0; offset < pageSize; offset++)
        {
            var index = _multiviewPage * pageSize + offset;
            if (index >= layouts.Count)
            {
                // The wall keeps its six cells even when the show has one layout, so it reads as a
                // multiview instead of a single oversized picture with empty space beside it.
                var empty = new Border
                {
                    Margin = new Thickness(3), Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x15)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x3D)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5)
                };
                var label = new TextBlock
                {
                    Text = $"{index + 1}  ·  NO SOURCE", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x4C, 0x5C)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                empty.Child = label;
                ThemeManager.SetSkipTheme(empty, true);
                MultiviewGrid.Children.Add(empty);
                continue;
            }
            var layout = layouts[index];
            var onProgram = index == programIndex;
            var onPreview = index == _previewIndex;
            var tile = new Button
            {
                Margin = new Thickness(3),
                Padding = new Thickness(2),
                Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x15)),
                BorderThickness = new Thickness(onProgram || onPreview ? 3 : 1),
                BorderBrush = new SolidColorBrush(onProgram ? Color.FromRgb(0xE6, 0x3B, 0x3B)
                    : onPreview ? Color.FromRgb(0x38, 0xD7, 0xA3) : Color.FromRgb(0x2E, 0x3B, 0x4A)),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                ToolTip = $"{index + 1}. {layout.Name}{(onProgram ? " · on air" : onPreview ? " · preview" : "")}"
            };
            // The tally border is a real border, so it survives the theme pass and is visible even
            // when the picture is dark.
            ThemeManager.SetSkipTheme(tile, true);
            var stack = new StackPanel();
            var image = new Image
            {
                Source = Thumbnail(layout, 420, 128), Height = 128, Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            stack.Children.Add(new Border
            {
                Height = 128, HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(5, 8, 12)), Child = image, ClipToBounds = true
            });
            var row = new DockPanel { Margin = new Thickness(2, 3, 2, 0) };
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
            stack.Children.Add(row);
            tile.Content = stack;
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
            var palette = TileColours[index % TileColours.Length];
            var onProgram = index == programIndex;
            var onPreview = index == _previewIndex;
            var button = new Button
            {
                Width = 170, Height = 44, Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(6, 3, 6, 3),
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(onProgram ? "#3A1717" : onPreview ? "#163C32" : palette.Background)),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(onProgram ? "#FF9B9B" : onPreview ? "#8DEBCB" : palette.Foreground)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(onProgram ? "#E63B3B" : onPreview ? "#38D7A3" : palette.Border)),
                BorderThickness = new Thickness(onProgram || onPreview ? 2 : 1),
                ToolTip = $"{layout.Name}\nClick to preview · double-click to take"
            };
            ThemeManager.SetSkipTheme(button, true);
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

    void BuildLayers(ScorebugLayout layout)
    {
        LayerPanel.Children.Clear();
        foreach (var element in Enumerable.Reverse(layout.Elements).Take(14))
        {
            var item = element;
            var button = new Button
            {
                Content = item.Name, FontSize = 9, MinHeight = 26, Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 4, 4), Opacity = item.Visible ? 1 : 0.45,
                ToolTip = item.Visible ? $"{item.Name} is visible · click to hide" : $"{item.Name} is hidden · click to show"
            };
            button.Click += (_, _) =>
            {
                var id = item.Id;
                _catalog.Update(edit =>
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
        _catalog.SelectDisplay(index);
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
