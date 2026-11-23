using System.Text.Json;

namespace Scoreboard.Core;

/// <summary>One row of the preflight diff shown before a team identity switch.</summary>
public sealed record IdentityChange(string Group, string Field, string Before, string After);

/// <summary>
/// Swaps the two team identities across the whole show. Colours, logos and every palette-bound
/// element follow the slots, so one switch flips the graphic without editing a single layout.
/// </summary>
public static class TeamIdentities
{
    public static void Swap(GameState state)
    {
        (state.HomeTeam, state.AwayTeam) = (state.AwayTeam, state.HomeTeam);
        (state.HomeAbbreviation, state.AwayAbbreviation) = (state.AwayAbbreviation, state.HomeAbbreviation);
        (state.HomeMascot, state.AwayMascot) = (state.AwayMascot, state.HomeMascot);
        (state.HomeRecord, state.AwayRecord) = (state.AwayRecord, state.HomeRecord);
        (state.HomeDivisionRecord, state.AwayDivisionRecord) = (state.AwayDivisionRecord, state.HomeDivisionRecord);
        (state.HomeRanking, state.AwayRanking) = (state.AwayRanking, state.HomeRanking);
        (state.HomeLogoPath, state.AwayLogoPath) = (state.AwayLogoPath, state.HomeLogoPath);
        (state.HomePrimaryColor, state.AwayPrimaryColor) = (state.AwayPrimaryColor, state.HomePrimaryColor);
        (state.HomeSecondaryColor, state.AwaySecondaryColor) = (state.AwaySecondaryColor, state.HomeSecondaryColor);
    }

    /// <summary>Exactly what the switch would change, for the dialog's preflight list.</summary>
    public static List<IdentityChange> Diff(GameState state)
    {
        var before = state;
        var after = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(state)) ?? new GameState();
        Swap(after);
        var rows = new List<IdentityChange>();
        void Add(string group, string field, string? from, string? to)
        {
            from ??= ""; to ??= "";
            if (string.Equals(from, to, StringComparison.Ordinal)) return;
            rows.Add(new IdentityChange(group, field, from.Length == 0 ? "—" : from, to.Length == 0 ? "—" : to));
        }
        Add("Home", "Name", before.HomeTeam, after.HomeTeam);
        Add("Home", "Abbreviation", before.HomeAbbreviation, after.HomeAbbreviation);
        Add("Home", "Mascot", before.HomeMascot, after.HomeMascot);
        Add("Home", "Primary colour", before.HomePrimaryColor, after.HomePrimaryColor);
        Add("Home", "Secondary colour", before.HomeSecondaryColor, after.HomeSecondaryColor);
        Add("Home", "Logo", before.HomeLogoPath, after.HomeLogoPath);
        Add("Home", "Record", before.HomeRecord, after.HomeRecord);
        Add("Visitors", "Name", before.AwayTeam, after.AwayTeam);
        Add("Visitors", "Abbreviation", before.AwayAbbreviation, after.AwayAbbreviation);
        Add("Visitors", "Mascot", before.AwayMascot, after.AwayMascot);
        Add("Visitors", "Primary colour", before.AwayPrimaryColor, after.AwayPrimaryColor);
        Add("Visitors", "Secondary colour", before.AwaySecondaryColor, after.AwaySecondaryColor);
        Add("Visitors", "Logo", before.AwayLogoPath, after.AwayLogoPath);
        Add("Visitors", "Record", before.AwayRecord, after.AwayRecord);
        return rows;
    }
}
