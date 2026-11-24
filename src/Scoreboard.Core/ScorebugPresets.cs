namespace Scoreboard.Core;

/// <summary>One starting design offered when a new game is created.</summary>
public sealed record ScorebugPreset(string Id, string Name, string Description)
{
    /// <summary>ComboBox templates print the selected item directly, so never show the record type name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The starting scorebug and stadium designs. A new game picks one, so an operator can begin with
/// a compact broadcast bug, a full-frame stadium board or a clean lower third instead of editing
/// the factory layout from scratch. Every preset is ordinary elements, so it can be edited freely.
/// </summary>
public static class ScorebugPresets
{
    public const string Classic = "classic-scorebug";
    public const string CornerBug = "corner-scorebug";
    public const string TopBar = "top-score-bar";
    public const string Stadium = "stadium-display";
    public const string Jumbotron = "stadium-jumbotron";
    public const string LowerThird = "broadcast-lower-third";

    public static IReadOnlyList<ScorebugPreset> All { get; } =
    [
        new(Classic, "Classic Scorebug", "A compact centred clock with the home team on the right and visitors on the left."),
        new(CornerBug, "Corner Scorebug", "A small bug in the bottom-right corner, leaving the rest of the picture clear."),
        new(TopBar, "Top Score Bar", "A full-width bar across the top, like a highlight show's running score."),
        new(Stadium, "Stadium Display", "A full-frame video board with the score strip across the bottom, like the screen at a game."),
        new(Jumbotron, "Stadium Jumbotron", "A big arena board: full-frame video above giant team panels and the clock."),
        new(LowerThird, "Broadcast Lower Third", "A slim bottom-screen strip for name-and-score reads over live pictures.")
    ];

    public static string Normalize(string? id) => All.Any(preset =>
        string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
        ? All.First(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)).Id
        : Classic;

    public static ScorebugLayout Build(string? id)
    {
        // The CONTENT gallery's newer templates are not in this file's legacy list. Their builders
        // live in one catalog, so a new game can start from any gallery template instead of only
        // the six originals.
        var key = (id ?? "").Trim();
        if (ContentTemplateCatalog.All.Any(template =>
                string.Equals(template.Id, key, StringComparison.OrdinalIgnoreCase))
            && All.All(preset => !string.Equals(preset.Id, key, StringComparison.OrdinalIgnoreCase)))
            return ContentTemplateCatalog.Build(key);
        return Normalize(id) switch
        {
            CornerBug => CornerBugLayout(),
            TopBar => TopBarLayout(),
            Stadium => StadiumLayout(),
            Jumbotron => JumbotronLayout(),
            LowerThird => LowerThirdLayout(),
            _ => ScorebugLayout.Default()
        };
    }

    static ScorebugLayout CornerBugLayout() => new()
    {
        Name = "Corner Scorebug",
        Width = 1280,
        Height = 720,
        Background = "#00000000",
        Elements =
        [
            new() { Name = "Bug plate", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 760, Y = 552, Width = 470, Height = 122, CornerRadius = 10, Background = "#E60B1118", BorderThickness = 2, BorderColor = "#FF38D7A3" },
            new() { Name = "Away team", Content = "{{AWAY_TEAM}}", X = 782, Y = 566, Width = 120, Height = 30, FontSize = 20 },
            new() { Name = "Away score", Content = "{{AWAY_SCORE}}", X = 782, Y = 598, Width = 120, Height = 62, FontSize = 50, ForegroundSource = ScorebugColorSource.AwayPrimary },
            new() { Name = "Period", Content = "{{PERIOD}}", X = 902, Y = 564, Width = 186, Height = 24, FontSize = 14 },
            new() { Name = "Game Clock", Content = "{{GAME_CLOCK}}", X = 902, Y = 588, Width = 186, Height = 44, FontSize = 34, Transition = ScorebugTransition.Cut },
            new() { Name = "Down and distance", Content = "{{DOWNS}}", X = 902, Y = 632, Width = 186, Height = 24, FontSize = 13 },
            new() { Name = "Home team", Content = "{{HOME_TEAM}}", X = 1088, Y = 566, Width = 120, Height = 30, FontSize = 20, TextAlign = ScorebugTextAlign.Right },
            new() { Name = "Home score", Content = "{{HOME_SCORE}}", X = 1088, Y = 598, Width = 120, Height = 62, FontSize = 50, ForegroundSource = ScorebugColorSource.HomePrimary, TextAlign = ScorebugTextAlign.Right }
        ]
    };

    static ScorebugLayout TopBarLayout() => new()
    {
        Name = "Top Score Bar",
        Width = 1920,
        Height = 1080,
        Background = "#00000000",
        Elements =
        [
            new() { Name = "Bar plate", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 0, Y = 0, Width = 1920, Height = 132, CornerRadius = 0, Background = "#F00B1118" },
            new() { Name = "Bar accent", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 0, Y = 126, Width = 1920, Height = 6, CornerRadius = 0, Background = "#FF38D7A3" },
            new() { Name = "Away team", Content = "{{AWAY_TEAM}}", X = 70, Y = 18, Width = 340, Height = 46, FontSize = 32 },
            new() { Name = "Away score", Content = "{{AWAY_SCORE}}", X = 70, Y = 60, Width = 340, Height = 58, FontSize = 48, ForegroundSource = ScorebugColorSource.AwayPrimary },
            new() { Name = "Period", Content = "{{PERIOD}}", X = 640, Y = 12, Width = 640, Height = 26, FontSize = 18 },
            new() { Name = "Game Clock", Content = "{{GAME_CLOCK}}", X = 640, Y = 36, Width = 640, Height = 56, FontSize = 44, Transition = ScorebugTransition.Cut },
            new() { Name = "Down and distance", Content = "{{DOWNS}}", X = 640, Y = 94, Width = 640, Height = 26, FontSize = 16 },
            new() { Name = "Home team", Content = "{{HOME_TEAM}}", X = 1510, Y = 18, Width = 340, Height = 46, FontSize = 32, TextAlign = ScorebugTextAlign.Right },
            new() { Name = "Home score", Content = "{{HOME_SCORE}}", X = 1510, Y = 60, Width = 340, Height = 58, FontSize = 48, ForegroundSource = ScorebugColorSource.HomePrimary, TextAlign = ScorebugTextAlign.Right }
        ]
    };

    static ScorebugLayout JumbotronLayout() => new()
    {
        Name = "Stadium Jumbotron",
        Width = 1920,
        Height = 1080,
        Background = "#FF05070A",
        Elements =
        [
            new() { Name = "Board backdrop", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 0, Y = 0, Width = 1920, Height = 1080, CornerRadius = 0, Background = "#FF05070A" },
            new() { Name = "Board video", Kind = ScorebugElementKind.Video, X = 160, Y = 90, Width = 1600, Height = 620, Background = "#FF000000", MediaTag = "GLOBAL", PreserveVideoAspect = false },
            new() { Name = "Away panel", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 80, Y = 740, Width = 600, Height = 300, CornerRadius = 14, Background = "#FF0E1A24", BorderThickness = 2, BorderColor = "#FF1F2A37" },
            new() { Name = "Home panel", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 1240, Y = 740, Width = 600, Height = 300, CornerRadius = 14, Background = "#FF0E1A24", BorderThickness = 2, BorderColor = "#FF1F2A37" },
            new() { Name = "Away team", Content = "{{AWAY_TEAM}}", X = 110, Y = 762, Width = 540, Height = 50, FontSize = 36 },
            new() { Name = "Away score", Content = "{{AWAY_SCORE}}", X = 110, Y = 820, Width = 540, Height = 170, FontSize = 140, ForegroundSource = ScorebugColorSource.AwayPrimary },
            new() { Name = "Home team", Content = "{{HOME_TEAM}}", X = 1270, Y = 762, Width = 540, Height = 50, FontSize = 36 },
            new() { Name = "Home score", Content = "{{HOME_SCORE}}", X = 1270, Y = 820, Width = 540, Height = 170, FontSize = 140, ForegroundSource = ScorebugColorSource.HomePrimary },
            new() { Name = "Game Clock", Content = "{{GAME_CLOCK}}", X = 700, Y = 762, Width = 520, Height = 120, FontSize = 96, Transition = ScorebugTransition.Cut },
            new() { Name = "Period", Content = "{{PERIOD}}", X = 700, Y = 890, Width = 520, Height = 40, FontSize = 28 },
            new() { Name = "Down and distance", Content = "{{DOWNS}}", X = 700, Y = 940, Width = 520, Height = 34, FontSize = 20 }
        ]
    };

    static ScorebugLayout StadiumLayout() => new()
    {
        Name = "Stadium Display",
        Width = 1920,
        Height = 1080,
        Background = "#00000000",
        Elements =
        [
            // The board is a video first: everything above the score strip is one large picture,
            // so a clean feed or highlight reel fills the screen like the venue's own display.
            new() { Name = "Video backdrop", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 0, Y = 0, Width = 1920, Height = 820, CornerRadius = 0, Background = "#FF000000" },
            new() { Name = "Stadium video", Kind = ScorebugElementKind.Video, X = 0, Y = 0, Width = 1920, Height = 820, Background = "#FF000000", MediaTag = "GLOBAL", PreserveVideoAspect = false },

            // The score is a single readable strip along the bottom, away on the left and home on
            // the right, with the clock and period in the middle.
            new() { Name = "Score strip", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 0, Y = 820, Width = 1920, Height = 260, CornerRadius = 0, Background = "#FF0B1118" },
            new() { Name = "Away accent", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 0, Y = 820, Width = 560, Height = 260, CornerRadius = 0, Background = "#33141D28" },
            new() { Name = "Home accent", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 1360, Y = 820, Width = 560, Height = 260, CornerRadius = 0, Background = "#33141D28" },
            new() { Name = "Away team", Content = "{{AWAY_TEAM}}", X = 60, Y = 840, Width = 500, Height = 54, FontSize = 36 },
            new() { Name = "Away score", Content = "{{AWAY_SCORE}}", X = 60, Y = 890, Width = 500, Height = 150, FontSize = 120, ForegroundSource = ScorebugColorSource.AwayPrimary },
            new() { Name = "Home team", Content = "{{HOME_TEAM}}", X = 1360, Y = 840, Width = 500, Height = 54, FontSize = 36 },
            new() { Name = "Home score", Content = "{{HOME_SCORE}}", X = 1360, Y = 890, Width = 500, Height = 150, FontSize = 120, ForegroundSource = ScorebugColorSource.HomePrimary },
            new() { Name = "Period", Content = "{{PERIOD}}", X = 760, Y = 840, Width = 400, Height = 48, FontSize = 30 },
            new() { Name = "Game Clock", Content = "{{GAME_CLOCK}}", X = 660, Y = 885, Width = 600, Height = 120, FontSize = 96, Transition = ScorebugTransition.Cut },
            new() { Name = "Down and distance", Content = "{{DOWNS}}", X = 700, Y = 1010, Width = 520, Height = 48, FontSize = 28 }
        ]
    };

    static ScorebugLayout LowerThirdLayout() => new()
    {
        Name = "Broadcast Lower Third",
        Width = 1920,
        Height = 1080,
        Background = "#00000000",
        Elements =
        [
            new() { Name = "Lower third plate", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 100, Y = 850, Width = 1720, Height = 170, CornerRadius = 12, Background = "#E60B1118", BorderThickness = 2, BorderColor = "#FF38D7A3" },
            new() { Name = "Away accent", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 100, Y = 850, Width = 510, Height = 170, CornerRadius = 12, Background = "#B3141D28" },
            new() { Name = "Home accent", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle, X = 1310, Y = 850, Width = 510, Height = 170, CornerRadius = 12, Background = "#B3141D28" },
            new() { Name = "Away team", Content = "{{AWAY_TEAM}}", X = 135, Y = 875, Width = 420, Height = 56, FontSize = 38 },
            new() { Name = "Away score", Content = "{{AWAY_SCORE}}", X = 135, Y = 925, Width = 420, Height = 72, FontSize = 62, ForegroundSource = ScorebugColorSource.AwayPrimary },
            new() { Name = "Home team", Content = "{{HOME_TEAM}}", X = 1365, Y = 875, Width = 420, Height = 56, FontSize = 38 },
            new() { Name = "Home score", Content = "{{HOME_SCORE}}", X = 1365, Y = 925, Width = 420, Height = 72, FontSize = 62, ForegroundSource = ScorebugColorSource.HomePrimary },
            new() { Name = "Period", Content = "{{PERIOD}}", X = 780, Y = 875, Width = 360, Height = 48, FontSize = 30 },
            new() { Name = "Game Clock", Content = "{{GAME_CLOCK}}", X = 760, Y = 925, Width = 400, Height = 78, FontSize = 64, Transition = ScorebugTransition.Cut }
        ]
    };
}
