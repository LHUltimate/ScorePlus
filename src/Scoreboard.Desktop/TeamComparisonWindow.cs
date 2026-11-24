using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>Sport-data comparison: both teams and the players on the graphic, side by side.</summary>
public sealed class TeamComparisonWindow : Window
{
    readonly GameEngine _engine;
    readonly StackPanel _body = new();
    readonly TextBlock _headline = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB2));
    static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
    static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x25));
    static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0x2E, 0x3B, 0x4A));

    public TeamComparisonWindow(GameEngine engine, string theme = "Dark")
    {
        _engine = engine;
        Title = "ScorePlus — Team Comparison";
        Width = 860; Height = 720; MinWidth = 680; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x18));
        Foreground = Text; FontFamily = new FontFamily("Segoe UI");
        ThemeManager.Apply(this, theme);

        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(new TextBlock { Text = "TEAM COMPARISON", FontSize = 18, FontWeight = FontWeights.Bold });
        top.Children.Add(_headline);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var footer = new TextBlock
        {
            Text = "Live values refresh every second. Player rows use the players currently on the graphic.",
            FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 8, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    void Refresh()
    {
        var state = _engine.State;
        _headline.Text = $"{state.AwayTeam} at {state.HomeTeam}  ·  {state.AwayScore}–{state.HomeScore}  ·  "
            + $"{PeriodDisplay.Format(state)} {state.GameClockText}";
        _body.Children.Clear();
        _body.Children.Add(Section("TEAM"));
        _body.Children.Add(HeaderRow());
        AddRow("Score", state.HomeScore.ToString(), state.AwayScore.ToString(), bold: true);
        AddRow("Record", state.HomeRecordDisplay, state.AwayRecordDisplay);
        if (!string.IsNullOrWhiteSpace(state.HomeRanking) || !string.IsNullOrWhiteSpace(state.AwayRanking))
            AddRow("Ranking", state.HomeRanking, state.AwayRanking);
        AddRow("Timeouts", state.HomeTimeouts.ToString(), state.AwayTimeouts.ToString());
        AddRow("Fouls", state.HomeFouls.ToString(), state.AwayFouls.ToString());
        if (state.SportId == "football")
        {
            AddRow("Yards", state.HomeYards.ToString(), state.AwayYards.ToString());
            AddRow("Turnovers", state.HomeTurnovers.ToString(), state.AwayTurnovers.ToString());
            AddRow("Down & distance", $"Down {state.Down} · {state.YardsToGo} to go", "");
        }
        if (state.SportId is "baseball")
        {
            AddRow("Hits", state.HomeHits.ToString(), state.AwayHits.ToString());
            AddRow("Count", $"{state.Balls} balls · {state.Strikes} strikes · {state.Outs} outs", "");
        }
        if (state.SportId is "soccer")
        {
            AddRow("Shots", state.HomeShots.ToString(), state.AwayShots.ToString());
            AddRow("Corners", state.HomeCorners.ToString(), state.AwayCorners.ToString());
            AddRow("Yellow cards", state.HomeYellowCards.ToString(), state.AwayYellowCards.ToString());
            AddRow("Red cards", state.HomeRedCards.ToString(), state.AwayRedCards.ToString());
        }
        foreach (var stat in state.Stats)
            AddRow(stat.Name, stat.Display, "");
        if (state.HomePlayer is not null || state.AwayPlayer is not null)
        {
            _body.Children.Add(Section("PLAYERS ON GRAPHIC"));
            _body.Children.Add(HeaderRow(state.HomePlayer?.Name ?? "Home player", state.AwayPlayer?.Name ?? "Visitors player"));
            var names = (state.HomePlayer?.Stats ?? []).Select(stat => stat.Name)
                .Concat((state.AwayPlayer?.Stats ?? []).Select(stat => stat.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(18).ToArray();
            foreach (var name in names)
                AddRow(name, Number(state.HomePlayer, name), Number(state.AwayPlayer, name));
        }
    }

    static string Number(SelectedPlayer? player, string stat) => player is null ? "—" : player.Stat(stat).ToString();

    Border Section(string text) => new()
    {
        Background = Surface, BorderBrush = Border, BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(8, 7, 8, 5), Margin = new Thickness(0, 10, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Muted }
    };

    Grid HeaderRow(string? home = null, string? away = null)
    {
        var grid = RowGrid();
        AddCell(grid, 0, home ?? _engine.State.HomeTeam, Muted, TextAlignment.Left, true);
        AddCell(grid, 1, "", Muted, TextAlignment.Center);
        AddCell(grid, 2, away ?? _engine.State.AwayTeam, Muted, TextAlignment.Right, true);
        return grid;
    }

    void AddRow(string label, string home, string away, bool bold = false)
    {
        var grid = RowGrid();
        AddCell(grid, 0, home, Text, TextAlignment.Left, bold);
        AddCell(grid, 1, label, Muted, TextAlignment.Center, false);
        AddCell(grid, 2, away, Text, TextAlignment.Right, bold);
        _body.Children.Add(grid);
    }

    static Grid RowGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    static void AddCell(Grid grid, int column, string text, Brush colour, TextAlignment align, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = 11, Foreground = colour, TextAlignment = align,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 3, 7, 3)
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}