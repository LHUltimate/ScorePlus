namespace Scoreboard.Core;

/// <summary>
/// One block of a practice: a name the coach wants on the board and a length in seconds. Practice
/// segments are ordinary game data, saved with the show and reusable week to week.
/// </summary>
public sealed class PracticeSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Drill";
    public int Seconds { get; set; } = 300;

    public PracticeSegment Copy() => new() { Id = Id, Name = Name, Seconds = Seconds };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = (Name ?? "").Trim();
        if (Name.Length == 0) Name = "Drill";
        if (Name.Length > 40) Name = Name[..40];
        Seconds = Math.Clamp(Seconds, 5, 3600);
    }
}

public static class PracticePlans
{
    /// <summary>A short starter practice, so the panel is usable the first time it is opened.</summary>
    public static List<PracticeSegment> Defaults() =>
    [
        new() { Name = "Warm-up", Seconds = 600 },
        new() { Name = "Drills", Seconds = 900 },
        new() { Name = "Team period", Seconds = 1200 },
        new() { Name = "Cool-down", Seconds = 300 }
    ];

    public static void Normalize(GameState? state)
    {
        if (state is null) return;
        state.PracticeSegments ??= [];
        state.PracticeSegments = state.PracticeSegments.Where(segment => segment is not null).Take(40).ToList();
        foreach (var segment in state.PracticeSegments) segment.Normalize();
        state.PracticeIndex = Math.Clamp(state.PracticeIndex, 0, Math.Max(0, state.PracticeSegments.Count - 1));
        state.MacroPageNames ??= [];
        state.MacroPageNames = state.MacroPageNames.Select(name => (name ?? "").Trim()).Take(99).ToList();
    }
}
