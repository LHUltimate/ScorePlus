namespace Scoreboard.Core;

/// <summary>
/// One entry in the show's run of show. A cue is a macro plus position in the running order, a
/// name, notes and an arm flag; the steps themselves stay in the macro board, so editing a macro
/// updates every cue that runs it and there is only one step editor.
/// </summary>
public sealed class ShowCue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The macro this cue runs. An empty id is a cue waiting to be pointed at a macro.</summary>
    public string MacroId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Free text for the operator: what happens, who moves, what to watch.</summary>
    public string Notes { get; set; } = "";

    /// <summary>
    /// A disarmed cue is skipped by GO. It keeps its place in the list, which is what a cue that
    /// only applies to some games needs.
    /// </summary>
    public bool Armed { get; set; } = true;

    /// <summary>An optional heading such as “Pre-game” or “Second half”, shown as a divider.</summary>
    public string Section { get; set; } = "";

    public ShowCue Copy() => new()
    {
        Id = Id,
        MacroId = MacroId,
        Name = Name,
        Notes = Notes,
        Armed = Armed,
        Section = Section
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        MacroId = (MacroId ?? "").Trim();
        Name = (Name ?? "").Trim();
        if (Name.Length > 80) Name = Name[..80];
        Notes = (Notes ?? "").Trim();
        if (Notes.Length > 400) Notes = Notes[..400];
        Section = (Section ?? "").Trim();
        if (Section.Length > 40) Section = Section[..40];
    }

    /// <summary>Shown in the cue list when the operator has not named the cue.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled cue" : Name;
}

/// <summary>
/// Editing and transport helpers for a run of show. Transport is deliberately simple: GO runs the
/// current armed cue and moves the marker to the next armed one; PREV moves back without running
/// anything; SKIP moves forward without running. The marker is saved with the game.
/// </summary>
public static class CueLists
{
    /// <summary>
    /// Builds a first cue list from the macro board, in board order (page, row, column). This is the
    /// migration an older game goes through, and the starting point for a new show.
    /// </summary>
    public static List<ShowCue> FromMacros(IEnumerable<SavedMacro>? macros)
    {
        var ordered = (macros ?? []).Where(macro => macro is not null)
            .OrderBy(macro => macro.Page)
            .ThenBy(macro => macro.Row < 0 ? int.MaxValue : macro.Row)
            .ThenBy(macro => macro.Column < 0 ? int.MaxValue : macro.Column)
            .ThenBy(macro => macro.Label, StringComparer.CurrentCultureIgnoreCase);
        return ordered.Select((macro, index) => new ShowCue
        {
            MacroId = macro.Id,
            Name = macro.Label,
            Section = macro.Page == 0 ? "" : $"Page {macro.Page + 1}",
            Armed = true
        }).ToList();
    }

    public static void Normalize(GameState? state)
    {
        if (state is null) return;
        state.Cues ??= [];
        state.Cues = state.Cues.Where(cue => cue is not null).Take(500).ToList();
        foreach (var cue in state.Cues) cue.Normalize();
        if (state.CueIndex < 0) state.CueIndex = 0;
        if (state.Cues.Count == 0) state.CueIndex = 0;
        else if (state.CueIndex >= state.Cues.Count) state.CueIndex = state.Cues.Count - 1;
    }

    /// <summary>The first armed cue after <paramref name="index"/>, wrapping to the top once.</summary>
    public static int NextArmed(IReadOnlyList<ShowCue>? cues, int index)
    {
        if (cues is null || cues.Count == 0) return 0;
        for (var step = 1; step <= cues.Count; step++)
        {
            var candidate = (index + step) % cues.Count;
            if (cues[candidate].Armed) return candidate;
        }
        return index;
    }

    public static int Previous(int index, int count) =>
        count <= 0 ? 0 : Math.Clamp((index - 1 + count) % count, 0, count - 1);

    public static int Next(int index, int count) =>
        count <= 0 ? 0 : Math.Clamp((index + 1) % count, 0, count - 1);

    public static bool Move(List<ShowCue>? cues, int from, int delta)
    {
        if (cues is null) return false;
        var to = from + delta;
        if (from < 0 || from >= cues.Count || to < 0 || to >= cues.Count) return false;
        (cues[from], cues[to]) = (cues[to], cues[from]);
        return true;
    }
}
