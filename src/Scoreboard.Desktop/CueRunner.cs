using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Runs the show's cues. GO executes the current armed cue through the same macro runner every
/// button uses, then moves the marker to the next armed cue; PREV and NEXT move the marker without
/// running anything, and SKIP moves past a cue the operator does not want. The marker lives in the
/// game state, so a saved show reopens exactly where the operator left it.
/// </summary>
public sealed class CueRunner
{
    readonly GameEngine _engine;
    readonly MacroRunner _runner;

    public CueRunner(GameEngine engine, MacroRunner runner)
    {
        _engine = engine;
        _runner = runner;
    }

    public IReadOnlyList<ShowCue> Cues => _engine.State.Cues;

    public int Index => Math.Clamp(_engine.State.CueIndex, 0, Math.Max(0, Cues.Count - 1));

    public ShowCue? Current => Cues.Count == 0 ? null : Cues[Index];

    /// <summary>The cue GO would run next, which is the current one while it is armed.</summary>
    public ShowCue? NextArmed => Cues.Count == 0
        ? null
        : Cues[Cues[Index].Armed ? Index : CueLists.NextArmed(Cues, Index)];

    public string Summary
    {
        get
        {
            if (Cues.Count == 0) return "No cues yet — add one from the macro palette.";
            var cue = Current!;
            return $"CUE {Index + 1}/{Cues.Count} · {cue.DisplayName}{(cue.Armed ? "" : " · disarmed")}";
        }
    }

    /// <summary>Runs the current cue, then moves the marker to the next armed cue.</summary>
    public async Task<string> GoAsync()
    {
        if (Cues.Count == 0) return "CUE · nothing to run";
        var cue = Cues[Index];
        if (!cue.Armed)
        {
            MoveTo(CueLists.NextArmed(Cues, Index));
            _engine.RecordEvent("Cue", $"Skipped disarmed cue {Index + 1} · {cue.DisplayName}");
            return $"CUE · {cue.DisplayName} is disarmed · moved on";
        }
        var macro = _engine.State.Macros.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, cue.MacroId, StringComparison.OrdinalIgnoreCase));
        if (macro is null)
        {
            _engine.RecordEvent("Cue", $"Cue {Index + 1} · {cue.DisplayName} · missing macro");
            return $"CUE · {cue.DisplayName} has no macro";
        }
        var result = await _runner.RunAsync(macro);
        _engine.RecordEvent("Cue", $"GO {Index + 1} · {cue.DisplayName} · {result}");
        MoveTo(CueLists.NextArmed(Cues, Index));
        return $"GO · {cue.DisplayName}";
    }

    public string Previous()
    {
        if (Cues.Count == 0) return "CUE · nothing to step back to";
        var from = Index;
        MoveTo(CueLists.Previous(Index, Cues.Count));
        return $"CUE · back to {Index + 1} · {Cues[Index].DisplayName}{(from == Index ? " (only cue)" : "")}";
    }

    public string Next()
    {
        if (Cues.Count == 0) return "CUE · nothing to step to";
        MoveTo(CueLists.Next(Index, Cues.Count));
        return $"CUE · at {Index + 1} · {Cues[Index].DisplayName}";
    }

    public string Skip()
    {
        if (Cues.Count == 0) return "CUE · nothing to skip";
        var skipped = Cues[Index].DisplayName;
        MoveTo(CueLists.NextArmed(Cues, Index));
        _engine.RecordEvent("Cue", $"Skipped cue · {skipped}");
        return $"CUE · skipped {skipped}";
    }

    public bool Jump(int index)
    {
        if (index < 0 || index >= Cues.Count) return false;
        MoveTo(index);
        return true;
    }

    public bool Move(int delta)
    {
        if (!CueLists.Move(_engine.State.Cues, Index, delta)) return false;
        MoveTo(Index + delta);
        _engine.RecordEvent("Cue", $"Moved cue {Index + 1}");
        return true;
    }

    public bool ToggleArmed(int index)
    {
        if (index < 0 || index >= Cues.Count) return false;
        Cues[index].Armed = !Cues[index].Armed;
        _engine.RecordEvent("Cue", $"{Cues[index].DisplayName} · {(Cues[index].Armed ? "armed" : "disarmed")}");
        return true;
    }

    void MoveTo(int index)
    {
        _engine.State.CueIndex = Math.Clamp(index, 0, Math.Max(0, Cues.Count - 1));
        _engine.RecordEvent("Cue", Summary);
    }
}
