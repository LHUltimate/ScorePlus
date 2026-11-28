using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Scoreboard.Core;

/// <summary>
/// The whole game as rows an operator can take elsewhere: the scoreboard itself, every sport value
/// the console holds, every custom value, both selected players, every player on both rosters with
/// their stats, and the event timeline. Written as flat rows so a CSV opens in a spreadsheet and the
/// same rows group cleanly into JSON for a league service. The printable match report stays a report;
/// this is the data behind it.
/// </summary>
public static class GameDataExport
{
    /// <summary>One exported value. <paramref name="Group"/> is the section (Game, Home roster…).</summary>
    public sealed record Row(string Group, string Key, string Value);

    public static IReadOnlyList<Row> Rows(GameState state, IEnumerable<Roster>? rosters = null)
    {
        var rows = new List<Row>();
        void Add(string group, string key, string value) => rows.Add(new Row(group, key, value));

        // ---------- the game ----------
        Add("Game", "Game name", state.GameName);
        Add("Game", "Sport", SportName(state));
        Add("Game", "Status", state.GameEnded ? "Final" : "In progress");
        Add("Game", "Home team", state.HomeTeam);
        Add("Game", "Home abbreviation", state.HomeAbbreviation);
        Add("Game", "Home mascot", state.HomeMascot);
        Add("Game", "Home score", state.HomeScore.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Home record", state.HomeRecordDisplay);
        Add("Game", "Home division record", state.HomeDivisionRecord);
        Add("Game", "Home ranking", state.HomeRanking);
        Add("Game", "Home primary colour", state.HomePrimaryColor);
        Add("Game", "Home secondary colour", state.HomeSecondaryColor);
        Add("Game", "Visitors team", state.AwayTeam);
        Add("Game", "Visitors abbreviation", state.AwayAbbreviation);
        Add("Game", "Visitors mascot", state.AwayMascot);
        Add("Game", "Visitors score", state.AwayScore.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Visitors record", state.AwayRecordDisplay);
        Add("Game", "Visitors division record", state.AwayDivisionRecord);
        Add("Game", "Visitors ranking", state.AwayRanking);
        Add("Game", "Visitors primary colour", state.AwayPrimaryColor);
        Add("Game", "Visitors secondary colour", state.AwaySecondaryColor);
        Add("Game", "Period", PeriodDisplay.Format(state));
        Add("Game", "Periods", state.TotalPeriods.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Game clock", GameEngine.FormatClock(state.ClockSeconds));
        Add("Game", "Game clock running", state.ClockRunning ? "Yes" : "No");
        Add("Game", "Clock counts up", state.ClockCountsUp ? "Yes" : "No");
        Add("Game", "Play clock", state.PlayClockRunning || state.PlayClockSeconds < 40 ? state.PlayClockText : "");
        Add("Game", "Home timeouts", state.HomeTimeouts.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Visitors timeouts", state.AwayTimeouts.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Home fouls", state.HomeFouls.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Visitors fouls", state.AwayFouls.ToString(CultureInfo.InvariantCulture));
        Add("Game", "Possession", state.HomePossession ? "Home" : "Visitors");
        Add("Game", "Penalty flag", state.PenaltyActive ? "On" : "Off");
        Add("Game", "Penalty type", state.PenaltyType);
        Add("Game", "Penalty team", state.PenaltyActive ? (state.PenaltyHome ? "Home" : "Visitors") : "");
        Add("Game", "Division game", state.DivisionGame ? "Yes" : "No");
        Add("Game", "Scoring values", string.Join(", ", state.ScoringOptions));
        Add("Game", "Saved", state.SavedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        // ---------- the sport's own values ----------
        foreach (var control in state.Controls)
        {
            var printed = SportValues.Display(state, control);
            Add("Sport values", control.Label, printed.Length == 0 ? "OFF" : printed);
        }

        // ---------- custom values ----------
        foreach (var stat in state.Stats)
        {
            Add("Custom values", stat.Name, stat.Display);
            if (stat.IsClock)
                Add("Custom values", $"{stat.Name} (seconds)", stat.ClockSeconds.ToString(CultureInfo.InvariantCulture));
            else if (stat.IsNumber)
                Add("Custom values", $"{stat.Name} (number)", stat.Value.ToString(CultureInfo.InvariantCulture));
            else
                Add("Custom values", $"{stat.Name} (text)", stat.Text ?? "");
        }

        // ---------- the players on the graphic ----------
        AddPlayer(rows, "Home player", state.HomePlayer);
        AddPlayer(rows, "Visitors player", state.AwayPlayer);

        // ---------- both rosters, every player, every stat ----------
        foreach (var roster in (rosters ?? RosterStore.All()).Where(roster => roster is not null))
        {
            foreach (var player in roster.Players)
            {
                var group = $"{roster.Name} · #{player.Number} {player.Name}".TrimEnd();
                Add(group, "Number", player.Number.ToString(CultureInfo.InvariantCulture));
                Add(group, "Name", player.Name);
                Add(group, "Position", player.Position);
                Add(group, "Group", player.Group);
                foreach (var stat in player.Stats)
                    Add(group, stat.Name, stat.Display);
            }
        }

        // ---------- the event timeline ----------
        foreach (var entry in state.Events)
            Add("Events", $"{entry.At:yyyy-MM-dd HH:mm:ss} · {entry.Type}", entry.Description);

        return rows;
    }

    static void AddPlayer(List<Row> rows, string group, SelectedPlayer? player)
    {
        if (player is null) return;
        rows.Add(new Row(group, "Number", player.Number.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new Row(group, "Name", player.Name));
        rows.Add(new Row(group, "Position", player.Position));
        foreach (var stat in player.Stats)
            rows.Add(new Row(group, stat.Name, stat.Display));
    }

    public static string Csv(GameState state, IEnumerable<Roster>? rosters = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Group,Key,Value");
        foreach (var row in Rows(state, rosters))
            text.AppendLine($"{Quote(row.Group)},{Quote(row.Key)},{Quote(row.Value)}");
        return text.ToString();
    }

    public static string Json(GameState state, IEnumerable<Roster>? rosters = null)
    {
        var document = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(state, rosters))
        {
            if (!document.TryGetValue(row.Group, out var section))
                document[row.Group] = section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            section[row.Key] = row.Value;
        }
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    static string Quote(string? value)
    {
        var text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    static string SportName(GameState state)
    {
        var builtIn = SportDefinition.BuiltIns.FirstOrDefault(sport =>
            string.Equals(sport.Id, state.SportId, StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null) return builtIn.Name;
        var text = (state.SportId ?? string.Empty).Replace('-', ' ').Replace('_', ' ').Trim();
        return text.Length == 0 ? "Custom" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
