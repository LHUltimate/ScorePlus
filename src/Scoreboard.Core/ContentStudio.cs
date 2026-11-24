using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scoreboard.Core;

/// <summary>
/// Where a template element takes its appearance from. A role is saved with the artwork, so a
/// brand change repaints exactly the parts the designer marked instead of guessing from colours.
/// </summary>
public enum BrandRole
{
    None,
    /// <summary>The main brand colour: accents, rules and the wordmark.</summary>
    Primary,
    /// <summary>The darker supporting colour for plates and panels.</summary>
    Surface,
    /// <summary>A highlight colour for one emphatic item.</summary>
    Accent,
    /// <summary>The canvas colour behind a full-frame board.</summary>
    Background,
    /// <summary>Ordinary brand body text.</summary>
    Text,
    /// <summary>A heading: brand heading font and text colour.</summary>
    Heading,
    /// <summary>The show wordmark; the content comes from the brand kit.</summary>
    Wordmark,
    /// <summary>A logo box; the picture comes from the brand kit.</summary>
    Logo,
    /// <summary>A sponsor placeholder, filled from the sponsor inventory.</summary>
    Sponsor
}

public enum LayerEffectKind { None, Blur, Glow }

/// <summary>
/// The show's visual identity. One document is shared by every template and every layout, so a
/// venue can be branded once instead of editing colours into each scorebug by hand.
/// </summary>
public sealed class BrandKit
{
    public string Name { get; set; } = "ScorePlus";
    public string Primary { get; set; } = "#FF38D7A3";
    public string Surface { get; set; } = "#FF0B1118";
    public string Accent { get; set; } = "#FF38D7A3";
    public string Background { get; set; } = "#FF091018";
    public string Text { get; set; } = "#FFF4F7FB";
    public string HeadingFont { get; set; } = "Segoe UI";
    public string BodyFont { get; set; } = "Segoe UI";
    public string LogoPath { get; set; } = "";
    public string Wordmark { get; set; } = "SCOREPLUS";
    public double SafeMargin { get; set; } = 48;

    public BrandKit Copy() => new()
    {
        Name = Name, Primary = Primary, Surface = Surface, Accent = Accent, Background = Background,
        Text = Text, HeadingFont = HeadingFont, BodyFont = BodyFont, LogoPath = LogoPath,
        Wordmark = Wordmark, SafeMargin = SafeMargin
    };

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "ScorePlus" : Name.Trim();
        Primary = Hex(Primary, "#FF38D7A3");
        Surface = Hex(Surface, "#FF0B1118");
        Accent = Hex(Accent, "#FF38D7A3");
        Background = Hex(Background, "#FF091018");
        Text = Hex(Text, "#FFF4F7FB");
        HeadingFont = Font(HeadingFont, "Segoe UI");
        BodyFont = Font(BodyFont, "Segoe UI");
        LogoPath = (LogoPath ?? "").Trim();
        Wordmark = string.IsNullOrWhiteSpace(Wordmark) ? Name.ToUpperInvariant() : Wordmark.Trim();
        SafeMargin = Math.Clamp(double.IsFinite(SafeMargin) ? SafeMargin : 48, 0, 400);
    }

    static string Hex(string? value, string fallback)
    {
        var text = (value ?? "").Trim();
        return text.Length is 4 or 7 or 9 && text[0] == '#' ? text : fallback;
    }

    static string Font(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>Applies the brand to every element that declares a role, and to the layout canvas.</summary>
    public void ApplyTo(ScorebugLayout layout)
    {
        if (layout.UseBrandBackground) layout.Background = Background;
        foreach (var element in layout.Elements) ApplyTo(element);
    }

    public void ApplyTo(ScorebugElement element)
    {
        switch (element.BrandRole)
        {
            case BrandRole.Primary:
                element.Foreground = Primary;
                element.ForegroundSource = ScorebugColorSource.Custom;
                // A shape marked Primary is a brand bar, not a text glyph: give it the fill too.
                if (element.Kind == ScorebugElementKind.Shape)
                {
                    element.Background = Primary;
                    element.BackgroundSource = ScorebugColorSource.Custom;
                }
                break;
            case BrandRole.Surface:
                element.Background = Surface;
                element.BackgroundSource = ScorebugColorSource.Custom;
                break;
            case BrandRole.Accent:
                element.BorderColor = Accent;
                element.BorderSource = ScorebugColorSource.Custom;
                if (element.BorderThickness <= 0) element.BorderThickness = 2;
                break;
            case BrandRole.Background:
                element.Background = Background;
                element.BackgroundSource = ScorebugColorSource.Custom;
                element.TransparentBackground = false;
                break;
            case BrandRole.Text:
                element.Foreground = Text;
                element.ForegroundSource = ScorebugColorSource.Custom;
                element.FontFamily = BodyFont;
                break;
            case BrandRole.Heading:
                element.Foreground = Text;
                element.ForegroundSource = ScorebugColorSource.Custom;
                element.FontFamily = HeadingFont;
                break;
            case BrandRole.Wordmark:
                element.Content = Wordmark;
                element.Foreground = Primary;
                element.ForegroundSource = ScorebugColorSource.Custom;
                element.FontFamily = HeadingFont;
                break;
            case BrandRole.Logo:
                if (!string.IsNullOrWhiteSpace(LogoPath) && File.Exists(LogoPath)) element.ImagePath = LogoPath;
                break;
            case BrandRole.Sponsor:
                element.Background = Surface;
                element.BackgroundSource = ScorebugColorSource.Custom;
                element.BorderColor = Accent;
                element.BorderSource = ScorebugColorSource.Custom;
                element.TransparentBackground = false;
                break;
        }
    }
}

/// <summary>One entry in the CONTENT gallery. The same catalog feeds "new from template".</summary>
public sealed record ContentTemplate(
    string Id, string Name, string Sport, string Slot, string Description, bool BroadcastStandard = false)
{
    /// <summary>The sports this template is written for. A template can serve more than one sport.</summary>
    public IReadOnlyList<string> Sports => Sport.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The sports as one readable line, used by the preview and gallery tooltips.</summary>
    public string SportLabel => string.Join(" · ", Sports);

    public override string ToString() => Name;
}

/// <summary>
/// The gallery catalog. Built-in scorebugs keep their existing builders; the additional broadcast
/// slots are ordinary layouts with brand roles, so every template brands the same way.
/// </summary>
public static class ContentTemplateCatalog
{
    public static IReadOnlyList<ContentTemplate> All { get; } =
    [
        new(ScorebugPresets.Classic, "Classic Scorebug", "American Football|Basketball|Baseball|Hockey|Soccer", "Scorebug", "Centred clock, visitors left, home right.", true),
        new(ScorebugPresets.CornerBug, "Corner Scorebug", "Basketball|Hockey", "Scorebug", "A compact bug in the bottom-right corner.", true),
        new(ScorebugPresets.TopBar, "Top Score Bar", "American Football|Hockey", "Ribbon", "A full-width running score across the top.", true),
        new(ScorebugPresets.Stadium, "Stadium Display", "American Football|Basketball", "Full screen", "A full-frame board with the score strip at the bottom.", true),
        new(ScorebugPresets.Jumbotron, "Stadium Jumbotron", "Basketball|Baseball", "Full screen", "Giant team panels under a full-frame video.", true),
        new(ScorebugPresets.LowerThird, "Broadcast Lower Third", "Soccer|American Football|Basketball", "Lower third", "A slim bottom-screen strip for name reads.", true),
        new("comparison", "Player Comparison", "Basketball|Baseball", "Comparison", "Two players, their numbers and three matched stats.", true),
        new("lineup", "Starting Lineup", "Baseball|Soccer", "Lineup", "Five player rows with number, name and position.", true),
        new("stat-panel", "Team Stat Panel", "American Football|Basketball", "Stat panel", "A side-by-side team stat block for halftime and post-game.", true),
        new("sponsor", "Sponsor Full Screen", "Any sport", "Sponsor", "A taggable sponsor board with a logo and call to action."),
        new("ribbon-ticker", "Sponsor Ribbon", "Any sport", "Ribbon", "A slim sponsor ribbon with a scrolling message area.")
    ];

    public static ContentTemplate? Find(string? id) => All.FirstOrDefault(template =>
        string.Equals(template.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Sports { get; } =
        All.SelectMany(template => template.Sports)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(sport => sport, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> Slots { get; } =
        All.Select(template => template.Slot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Builds a fresh layout with new element ids, branded with the current kit.</summary>
    public static ScorebugLayout Build(string? id, BrandKit? brand = null)
    {
        var template = Find(id) ?? All[0];
        var layout = template.Id switch
        {
            "comparison" => ComparisonLayout(),
            "lineup" => LineupLayout(),
            "stat-panel" => StatPanelLayout(),
            "sponsor" => SponsorLayout(),
            "ribbon-ticker" => SponsorRibbonLayout(),
            _ => ScorebugPresets.Build(template.Id)
        };
        layout.Name = template.Name;
        layout.UseBrandBackground = template.Slot is "Full screen" or "Sponsor" or "Comparison" or "Lineup" or "Stat panel";
        if (layout.Elements.All(element => element.BrandRole == BrandRole.None)) DecorateExisting(layout);
        (brand ?? new BrandKit()).ApplyTo(layout);
        return layout;
    }

    /// <summary>
    /// The original built-in scorebugs predate brand roles. They keep their artwork, but gain one
    /// thin brand rule and have their clock and period marked, so opening them from the gallery
    /// still publishes in the show's colours instead of looking untouched.
    /// </summary>
    static void DecorateExisting(ScorebugLayout layout)
    {
        foreach (var element in layout.Elements)
        {
            if (element.Content.Contains("{{GAME_CLOCK}}", StringComparison.OrdinalIgnoreCase)) element.BrandRole = BrandRole.Heading;
            else if (element.Content.Contains("{{PERIOD}}", StringComparison.OrdinalIgnoreCase)) element.BrandRole = BrandRole.Text;
        }
        layout.Elements.Insert(0, new ScorebugElement
        {
            Name = "Brand rule", Kind = ScorebugElementKind.Shape, Shape = ScorebugShape.Rectangle,
            X = 0, Y = Math.Max(0, layout.Height - 6), Width = layout.Width, Height = 6, CornerRadius = 0,
            BrandRole = BrandRole.Primary, Locked = true
        });
    }

    static ScorebugElement Branded(ScorebugElement element, BrandRole role)
    {
        element.BrandRole = role;
        return element;
    }

    static ScorebugLayout ComparisonLayout()
    {
        var layout = new ScorebugLayout
        {
            Name = "Player Comparison", Width = 1920, Height = 1080, Background = "#FF091018",
            UseBrandBackground = true,
            Elements =
            [
                Branded(new() { Name = "Backdrop", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 1080, CornerRadius = 0 }, BrandRole.Background),
                Branded(new() { Name = "Header", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 96, CornerRadius = 0 }, BrandRole.Surface),
                Branded(new() { Name = "Title", Content = "PLAYER COMPARISON", X = 70, Y = 20, Width = 900, Height = 56, FontSize = 40, TextAlign = ScorebugTextAlign.Left }, BrandRole.Heading),
                Branded(new() { Name = "Brand rule", Kind = ScorebugElementKind.Shape, X = 0, Y = 90, Width = 1920, Height = 6, CornerRadius = 0 }, BrandRole.Accent),
                Branded(new() { Name = "Left card", Kind = ScorebugElementKind.Shape, X = 70, Y = 150, Width = 870, Height = 820, CornerRadius = 14 }, BrandRole.Surface),
                Branded(new() { Name = "Right card", Kind = ScorebugElementKind.Shape, X = 980, Y = 150, Width = 870, Height = 820, CornerRadius = 14 }, BrandRole.Surface),
                Branded(new() { Name = "Left number", Content = "{{PLAYER:HOME:Number}}", X = 120, Y = 200, Width = 240, Height = 170, FontSize = 130 }, BrandRole.Primary),
                Branded(new() { Name = "Left name", Content = "{{PLAYER:HOME:Name}}", X = 120, Y = 380, Width = 720, Height = 80, FontSize = 56, TextAlign = ScorebugTextAlign.Left }, BrandRole.Heading),
                Branded(new() { Name = "Left position", Content = "{{PLAYER:HOME:Position}}", X = 120, Y = 466, Width = 720, Height = 46, FontSize = 30, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Right number", Content = "{{PLAYER:AWAY:Number}}", X = 1560, Y = 200, Width = 240, Height = 170, FontSize = 130, TextAlign = ScorebugTextAlign.Right }, BrandRole.Primary),
                Branded(new() { Name = "Right name", Content = "{{PLAYER:AWAY:Name}}", X = 1080, Y = 380, Width = 720, Height = 80, FontSize = 56, TextAlign = ScorebugTextAlign.Right }, BrandRole.Heading),
                Branded(new() { Name = "Right position", Content = "{{PLAYER:AWAY:Position}}", X = 1080, Y = 466, Width = 720, Height = 46, FontSize = 30, TextAlign = ScorebugTextAlign.Right }, BrandRole.Text),
                Branded(new() { Name = "Points label", Content = "POINTS", X = 820, Y = 620, Width = 280, Height = 34, FontSize = 20 }, BrandRole.Text),
                Branded(new() { Name = "Left points", Content = "{{PLAYER:HOME:Points}}", X = 120, Y = 660, Width = 720, Height = 80, FontSize = 56, TextAlign = ScorebugTextAlign.Left }, BrandRole.Primary),
                Branded(new() { Name = "Right points", Content = "{{PLAYER:AWAY:Points}}", X = 1080, Y = 660, Width = 720, Height = 80, FontSize = 56, TextAlign = ScorebugTextAlign.Right }, BrandRole.Primary)
            ]
        };
        return layout;
    }

    static ScorebugLayout LineupLayout()
    {
        var layout = new ScorebugLayout
        {
            Name = "Starting Lineup", Width = 1920, Height = 1080, Background = "#FF091018", UseBrandBackground = true,
            Elements =
            [
                Branded(new() { Name = "Backdrop", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 1080, CornerRadius = 0 }, BrandRole.Background),
                Branded(new() { Name = "Header", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 112, CornerRadius = 0 }, BrandRole.Surface),
                Branded(new() { Name = "Title", Content = "STARTING LINEUP", X = 80, Y = 24, Width = 900, Height = 60, FontSize = 44, TextAlign = ScorebugTextAlign.Left }, BrandRole.Heading),
                Branded(new() { Name = "Team", Content = "{{HOME_TEAM}}", X = 1120, Y = 24, Width = 720, Height = 60, FontSize = 38, TextAlign = ScorebugTextAlign.Right }, BrandRole.Primary),
                Branded(new() { Name = "Brand rule", Kind = ScorebugElementKind.Shape, X = 0, Y = 106, Width = 1920, Height = 6 }, BrandRole.Accent),
                Branded(new() { Name = "List plate", Kind = ScorebugElementKind.Shape, X = 80, Y = 154, Width = 1760, Height = 760, CornerRadius = 14 }, BrandRole.Surface),
                Branded(new() { Name = "Player 1", Content = "#{{PLAYER:HOME:Number}}  {{PLAYER:HOME:Name}}  ·  {{PLAYER:HOME:Position}}", X = 130, Y = 200, Width = 1660, Height = 86, FontSize = 38, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Player 2", Content = "STARTING LINEUP · PLAYER 2", X = 130, Y = 300, Width = 1660, Height = 86, FontSize = 38, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Player 3", Content = "STARTING LINEUP · PLAYER 3", X = 130, Y = 400, Width = 1660, Height = 86, FontSize = 38, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Player 4", Content = "STARTING LINEUP · PLAYER 4", X = 130, Y = 500, Width = 1660, Height = 86, FontSize = 38, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Player 5", Content = "STARTING LINEUP · PLAYER 5", X = 130, Y = 600, Width = 1660, Height = 86, FontSize = 38, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Footer", Content = "{{HOME_RECORD}}   ·   {{PERIOD}}", X = 130, Y = 790, Width = 1660, Height = 54, FontSize = 28 }, BrandRole.Text)
            ]
        };
        return layout;
    }

    static ScorebugLayout StatPanelLayout()
    {
        var layout = new ScorebugLayout
        {
            Name = "Team Stat Panel", Width = 1920, Height = 1080, Background = "#FF091018", UseBrandBackground = true,
            Elements =
            [
                Branded(new() { Name = "Backdrop", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 1080, CornerRadius = 0 }, BrandRole.Background),
                Branded(new() { Name = "Title plate", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 120, CornerRadius = 0 }, BrandRole.Surface),
                Branded(new() { Name = "Title", Content = "TEAM STATISTICS", X = 80, Y = 28, Width = 900, Height = 62, FontSize = 44, TextAlign = ScorebugTextAlign.Left }, BrandRole.Heading),
                Branded(new() { Name = "Brand rule", Kind = ScorebugElementKind.Shape, X = 0, Y = 114, Width = 1920, Height = 6 }, BrandRole.Accent),
                Branded(new() { Name = "Away team", Content = "{{AWAY_TEAM}}", X = 100, Y = 160, Width = 760, Height = 60, FontSize = 36 }, BrandRole.Text),
                Branded(new() { Name = "Home team", Content = "{{HOME_TEAM}}", X = 1060, Y = 160, Width = 760, Height = 60, FontSize = 36 }, BrandRole.Text),
                Branded(new() { Name = "Away score", Content = "{{AWAY_SCORE}}", X = 100, Y = 230, Width = 760, Height = 150, FontSize = 120 }, BrandRole.Primary),
                Branded(new() { Name = "Home score", Content = "{{HOME_SCORE}}", X = 1060, Y = 230, Width = 760, Height = 150, FontSize = 120 }, BrandRole.Primary),
                Branded(new() { Name = "Divider", Kind = ScorebugElementKind.Shape, X = 950, Y = 170, Width = 4, Height = 760 }, BrandRole.Accent),
                Branded(new() { Name = "Stat 1", Content = "SHOTS\n{{SHOTS}}", X = 100, Y = 430, Width = 760, Height = 120, FontSize = 34 }, BrandRole.Text),
                Branded(new() { Name = "Stat 1 home", Content = "{{SHOTS}}", X = 1060, Y = 430, Width = 760, Height = 120, FontSize = 34 }, BrandRole.Text),
                Branded(new() { Name = "Stat 2", Content = "YARDS\n{{YARDS}}", X = 100, Y = 580, Width = 760, Height = 120, FontSize = 34 }, BrandRole.Text),
                Branded(new() { Name = "Stat 2 home", Content = "{{YARDS}}", X = 1060, Y = 580, Width = 760, Height = 120, FontSize = 34 }, BrandRole.Text),
                Branded(new() { Name = "Stat 3", Content = "TURNOVERS\n{{TURNOVERS}}", X = 100, Y = 730, Width = 760, Height = 120, FontSize = 34 }, BrandRole.Text),
                Branded(new() { Name = "Stat 3 home", Content = "{{TURNOVERS}}", X = 1060, Y = 730, Width = 760, Height = 120, FontSize = 34 }, BrandRole.Text)
            ]
        };
        return layout;
    }

    static ScorebugLayout SponsorLayout()
    {
        var layout = new ScorebugLayout
        {
            Name = "Sponsor Full Screen", Width = 1920, Height = 1080, Background = "#FF091018", UseBrandBackground = true,
            Elements =
            [
                Branded(new() { Name = "Backdrop", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 1080, CornerRadius = 0 }, BrandRole.Background),
                Branded(new() { Name = "Sponsor plate", Kind = ScorebugElementKind.Shape, X = 160, Y = 150, Width = 1600, Height = 780, CornerRadius = 18, SponsorTag = "Presenting sponsor" }, BrandRole.Sponsor),
                Branded(new() { Name = "Sponsor logo", Kind = ScorebugElementKind.Image, X = 560, Y = 260, Width = 800, Height = 300, SponsorTag = "Presenting sponsor" }, BrandRole.Logo),
                Branded(new() { Name = "Presented by", Content = "PRESENTED BY", X = 560, Y = 620, Width = 800, Height = 44, FontSize = 26 }, BrandRole.Text),
                Branded(new() { Name = "Sponsor name", Content = "SPONSOR NAME", X = 560, Y = 680, Width = 800, Height = 90, FontSize = 56, SponsorTag = "Presenting sponsor" }, BrandRole.Wordmark),
                Branded(new() { Name = "Brand rule", Kind = ScorebugElementKind.Shape, X = 160, Y = 900, Width = 1600, Height = 8 }, BrandRole.Accent),
                Branded(new() { Name = "Wordmark", Content = "SCOREPLUS", X = 160, Y = 940, Width = 700, Height = 50, FontSize = 30, TextAlign = ScorebugTextAlign.Left }, BrandRole.Wordmark)
            ]
        };
        return layout;
    }

    static ScorebugLayout SponsorRibbonLayout()
    {
        var layout = new ScorebugLayout
        {
            Name = "Sponsor Ribbon", Width = 1920, Height = 180, Background = "#00000000",
            Elements =
            [
                Branded(new() { Name = "Ribbon plate", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 180, CornerRadius = 0, SponsorTag = "Ribbon sponsor" }, BrandRole.Surface),
                Branded(new() { Name = "Brand rule", Kind = ScorebugElementKind.Shape, X = 0, Y = 0, Width = 1920, Height = 8 }, BrandRole.Accent),
                Branded(new() { Name = "Sponsor logo", Kind = ScorebugElementKind.Image, X = 50, Y = 36, Width = 270, Height = 108, SponsorTag = "Ribbon sponsor" }, BrandRole.Logo),
                Branded(new() { Name = "Sponsor line", Content = "SPONSOR NAME  ·  TONIGHT'S PRESENTING PARTNER", X = 350, Y = 54, Width = 1320, Height = 72, FontSize = 42, TextAlign = ScorebugTextAlign.Left }, BrandRole.Text),
                Branded(new() { Name = "Wordmark", Content = "SCOREPLUS", X = 1700, Y = 54, Width = 190, Height = 72, FontSize = 28, TextAlign = ScorebugTextAlign.Right }, BrandRole.Wordmark)
            ]
        };
        return layout;
    }
}

/// <summary>One layout in a storyboard sequence.</summary>
public sealed class StoryboardScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scene";
    public string LayoutId { get; set; } = "";
    public LayoutTransition Transition { get; set; } = LayoutTransition.Fade;
    public double TransitionSeconds { get; set; } = 0.5;
    public double HoldSeconds { get; set; } = 5;

    public StoryboardScene Copy() => new()
    {
        Id = Id, Name = Name, LayoutId = LayoutId, Transition = Transition,
        TransitionSeconds = TransitionSeconds, HoldSeconds = HoldSeconds
    };
}

/// <summary>
/// The sequence of layouts a show plays through, with the hand-over between scenes. It edits the
/// same saved show as the layouts, so publishing a scene is publishing a real layout.
/// </summary>
public sealed class Storyboard
{
    public string Name { get; set; } = "Show open";
    public List<StoryboardScene> Scenes { get; set; } = [];
    public bool Loop { get; set; }

    public Storyboard Copy() => new()
    {
        Name = Name, Loop = Loop, Scenes = Scenes.Select(scene => scene.Copy()).ToList()
    };

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Storyboard" : Name.Trim();
        Scenes ??= [];
        foreach (var scene in Scenes)
        {
            if (string.IsNullOrWhiteSpace(scene.Id)) scene.Id = Guid.NewGuid().ToString("N");
            scene.Name = string.IsNullOrWhiteSpace(scene.Name) ? "Scene" : scene.Name.Trim();
            scene.LayoutId = (scene.LayoutId ?? "").Trim();
            scene.TransitionSeconds = Math.Clamp(double.IsFinite(scene.TransitionSeconds) ? scene.TransitionSeconds : 0.5, 0, 5);
            scene.HoldSeconds = Math.Clamp(double.IsFinite(scene.HoldSeconds) ? scene.HoldSeconds : 5, 0.25, 3600);
        }
    }

    public void RemoveMissing(IReadOnlySet<string> layoutIds) =>
        Scenes.RemoveAll(scene => !layoutIds.Contains(scene.LayoutId));
}

/// <summary>A named ad region that a sponsor package can fill.</summary>
public sealed class SponsorSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sponsor";
    public string Tag { get; set; } = "";
    public string AssetPath { get; set; } = "";
    public string Notes { get; set; } = "";

    public SponsorSlot Copy() => new()
    {
        Id = Id, Name = Name, Tag = Tag, AssetPath = AssetPath, Notes = Notes
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Sponsor" : Name.Trim();
        Tag = (Tag ?? "").Trim();
        AssetPath = (AssetPath ?? "").Trim();
        Notes = (Notes ?? "").Trim();
    }
}

/// <summary>
/// A portable bundle of show content. Layouts, the brand and the storyboard travel together, and
/// every image the layouts point at is embedded so the pack opens on another machine.
/// </summary>
public sealed class ContentPack
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Content pack";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public BrandKit Brand { get; set; } = new();
    public Storyboard Storyboard { get; set; } = new();
    public List<ScorebugDisplay> Displays { get; set; } = [];
    public List<SponsorSlot> Sponsors { get; set; } = [];
    public List<string> Fonts { get; set; } = [];
    /// <summary>Imported font families and the file each one came from, so a pack travels with them.</summary>
    public Dictionary<string, string> FontFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public List<string> MissingAssets { get; } = [];
}

public sealed class ContentPackImport
{
    public ContentPack Pack { get; init; } = new();
    /// <summary>Assets unpacked into this machine's content folder, keyed by their original path.</summary>
    public Dictionary<string, string> RestoredAssets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RestoredFonts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
}

public static class ContentPacks
{
    const int CurrentVersion = 1;
    const string ManifestName = "manifest.json";

    sealed class Manifest
    {
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public BrandKit Brand { get; set; } = new();
        public Storyboard Storyboard { get; set; } = new();
        public List<ScorebugDisplay> Displays { get; set; } = [];
        public List<SponsorSlot> Sponsors { get; set; } = [];
        public List<string> Fonts { get; set; } = [];
        public Dictionary<string, string> FontFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Writes a .spk pack. Every existing image is embedded; missing files are reported, not hidden.</summary>
    public static ContentPack Export(string path, ContentPack pack)
    {
        pack.Brand.Normalize();
        pack.Storyboard.Normalize();
        foreach (var sponsor in pack.Sponsors) sponsor.Normalize();
        var manifest = new Manifest
        {
            Version = CurrentVersion, Name = pack.Name, CreatedAt = DateTimeOffset.Now,
            Brand = pack.Brand.Copy(), Storyboard = pack.Storyboard.Copy(),
            Displays = pack.Displays, Sponsors = pack.Sponsors.Select(sponsor => sponsor.Copy()).ToList(),
            Fonts = pack.Fonts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var source in AssetPaths(pack))
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (assets.ContainsKey(source)) continue;
            if (!File.Exists(source)) { pack.MissingAssets.Add(source); continue; }
            var name = $"{index++:D3}_{SafeName(Path.GetFileName(source))}";
            assets[source] = name;
        }
        manifest.Assets = assets;
        var fontFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fontIndex = 0;
        foreach (var (family, source) in pack.FontFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
            var name = $"{fontIndex++:D3}_{SafeName(Path.GetFileName(source))}";
            fontFiles[family] = name;
        }
        manifest.FontFiles = fontFiles;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, ManifestName, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (source, name) in assets)
            WriteFile(archive, "assets/" + name, source);
        foreach (var (family, name) in fontFiles)
            WriteFile(archive, "fonts/" + name, pack.FontFiles[family]);
        return pack;
    }

    /// <summary>Reads a pack, validates it and unpacks its images next to this installation.</summary>
    public static ContentPackImport Import(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifestEntry = archive.GetEntry(ManifestName)
            ?? throw new InvalidDataException("This is not a ScorePlus content pack: manifest.json is missing.");
        Manifest manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd())
                ?? throw new InvalidDataException("The content pack manifest is empty.");
        if (manifest.Version > CurrentVersion)
            throw new InvalidDataException($"This pack was written by a newer build (version {manifest.Version}).");
        if (manifest.Displays.Count == 0 || manifest.Displays.All(display => display.Layouts.Count == 0))
            throw new InvalidDataException("The content pack contains no layouts.");
        manifest.Brand ??= new BrandKit();
        manifest.Brand.Normalize();
        manifest.Storyboard ??= new Storyboard();
        manifest.Storyboard.Normalize();
        var result = new ContentPackImport
        {
            Pack = new ContentPack
            {
                Version = manifest.Version, Name = manifest.Name, CreatedAt = manifest.CreatedAt,
                Brand = manifest.Brand, Storyboard = manifest.Storyboard,
                Displays = manifest.Displays, Sponsors = manifest.Sponsors ?? [], Fonts = manifest.Fonts ?? []
            }
        };
        var folder = AppPaths.Combine("ContentPacks", SafeName(Path.GetFileNameWithoutExtension(path)));
        Directory.CreateDirectory(folder);
        var fontFolder = AppPaths.Combine("Fonts");
        Directory.CreateDirectory(fontFolder);
        foreach (var (original, archiveName) in manifest.Assets ?? [])
        {
            var entry = archive.GetEntry("assets/" + archiveName);
            if (entry is null) { result.Warnings.Add($"Missing from the pack: {Path.GetFileName(original)}"); continue; }
            var target = Path.Combine(folder, SafeName(Path.GetFileName(archiveName)));
            using (var input = entry.Open())
            using (var output = File.Create(target))
                input.CopyTo(output);
            result.RestoredAssets[original] = target;
        }
        foreach (var (family, archiveName) in manifest.FontFiles ?? [])
        {
            var entry = archive.GetEntry("fonts/" + archiveName);
            if (entry is null) { result.Warnings.Add($"Missing font: {family}"); continue; }
            var target = Path.Combine(fontFolder, $"{Path.GetFileNameWithoutExtension(archiveName)}-{Guid.NewGuid():N}{Path.GetExtension(archiveName)}");
            using (var input = entry.Open())
            using (var output = File.Create(target))
                input.CopyTo(output);
            result.RestoredFonts[family] = target;
        }
        foreach (var layout in manifest.Displays.SelectMany(display => display.Layouts))
            foreach (var element in layout.Elements)
                if (!string.IsNullOrWhiteSpace(element.ImagePath)
                    && result.RestoredAssets.TryGetValue(element.ImagePath, out var restored))
                    element.ImagePath = restored;
        if (result.RestoredAssets.TryGetValue(manifest.Brand.LogoPath, out var logo))
            manifest.Brand.LogoPath = logo;
        if (result.RestoredAssets.Count < (manifest.Assets?.Count ?? 0))
            result.Warnings.Add("Some assets could not be restored; affected layouts show their placeholder.");
        if (result.RestoredFonts.Count < (manifest.FontFiles?.Count ?? 0))
            result.Warnings.Add("Some imported fonts could not be restored; affected text falls back to the system font.");
        return result;
    }

    static IEnumerable<string> AssetPaths(ContentPack pack)
    {
        if (!string.IsNullOrWhiteSpace(pack.Brand.LogoPath)) yield return pack.Brand.LogoPath;
        foreach (var display in pack.Displays)
            foreach (var layout in display.Layouts)
                foreach (var element in layout.Elements)
                    if (!string.IsNullOrWhiteSpace(element.ImagePath)) yield return element.ImagePath;
        foreach (var sponsor in pack.Sponsors)
            if (!string.IsNullOrWhiteSpace(sponsor.AssetPath)) yield return sponsor.AssetPath;
    }

    static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    static void WriteFile(ZipArchive archive, string name, string source)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var input = File.OpenRead(source);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    static string SafeName(string name)
    {
        var cleaned = new string((name ?? "").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
        return cleaned.Length == 0 ? "asset" : cleaned;
    }
}
