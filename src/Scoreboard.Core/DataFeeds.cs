using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Scoreboard.Core;

/// <summary>
/// Where a feed gets its values. Manual, CSV, JSON and XML files need no server at all; the HTTP
/// kinds use the same schema so a small league service can feed a professional venue without a
/// second application to install.
/// </summary>
public enum DataFeedKind
{
    Manual,
    Csv,
    Json,
    Xml,
    HttpJson,
    Weather
}

/// <summary>
/// The game values a feed can write. Beyond the scoreboard basics a source can fill team identity,
/// the player on the graphic, one named player's stat, or any custom value — so a league feed can
/// carry jersey numbers and per-player yardage, not just the score.
/// </summary>
public enum DataFeedTarget
{
    HomeScore,
    AwayScore,
    GameClock,
    PlayClock,
    Period,
    HomeTimeouts,
    AwayTimeouts,
    HomeFouls,
    AwayFouls,
    CustomValue,
    // New targets are appended: a saved feed stores the number, so inserting one above would
    // silently turn an old "visitors fouls" mapping into something else.
    HomeTeamName,
    AwayTeamName,
    HomeAbbreviation,
    AwayAbbreviation,
    HomePlayerName,
    AwayPlayerName,
    HomePlayerNumber,
    AwayPlayerNumber,
    HomePlayerPosition,
    AwayPlayerPosition,
    HomePlayerStat,
    AwayPlayerStat
}

/// <summary>One source value to one game value. Key is a JSON path, CSV header, XML leaf name or
/// manual key; a numeric key selects a row by position for sources without names.</summary>
public sealed class DataFeedMapping
{
    public string Key { get; set; } = "";
    public DataFeedTarget Target { get; set; } = DataFeedTarget.CustomValue;
    /// <summary>A custom value name, or the stat name for a player-stat mapping.</summary>
    public string CustomName { get; set; } = "";
    /// <summary>
    /// Which roster player a player mapping writes. Empty means the player currently on the graphic
    /// for that side, which is what most feeds want: a live box score follows the selected player.
    /// </summary>
    public string PlayerId { get; set; } = "";
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }

    public string Label => Target switch
    {
        DataFeedTarget.CustomValue when !string.IsNullOrWhiteSpace(CustomName) => $"{Key} → {CustomName}",
        DataFeedTarget.HomePlayerStat or DataFeedTarget.AwayPlayerStat when !string.IsNullOrWhiteSpace(CustomName)
            => $"{Key} → {(Target == DataFeedTarget.HomePlayerStat ? "Home" : "Away")} player {CustomName}",
        _ => $"{Key} → {DataFeedLabels.Target(Target)}"
    };

    /// <summary>True for the targets that name a player stat rather than a whole-game value.</summary>
    public bool IsPlayerStat => Target is DataFeedTarget.HomePlayerStat or DataFeedTarget.AwayPlayerStat;
}

/// <summary>A value read from a source, already flattened to a key and printable text.</summary>
public sealed record DataFeedValue(string Key, string Value);

/// <summary>
/// One configured feed. Runtime health is deliberately outside the saved document: a saved feed
/// opens as Waiting until its first refresh, rather than pretending yesterday's error is still live.
/// </summary>
public sealed class DataFeed
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New source";
    public DataFeedKind Kind { get; set; } = DataFeedKind.Manual;
    public string Location { get; set; } = "";
    public int RefreshSeconds { get; set; } = 30;
    public bool AutoRefresh { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public List<DataFeedMapping> Mappings { get; set; } = [];

    [JsonIgnore] public List<DataFeedValue> Values { get; set; } = [];
    [JsonIgnore] public string Status { get; set; } = "Waiting";
    [JsonIgnore] public string LastError { get; set; } = "";
    [JsonIgnore] public DateTimeOffset? LastUpdated { get; set; }
    [JsonIgnore] public string LastValue => Values.Count == 0 ? "—" : Values[0].Value;

    public string Health
    {
        get
        {
            if (!Enabled) return "Paused";
            if (!string.IsNullOrWhiteSpace(LastError)) return $"Error · {LastError}";
            if (LastUpdated is null) return "Waiting";
            return $"OK · {LastUpdated.Value.LocalDateTime:HH:mm:ss} · {LastValue}";
        }
    }

    public DataFeed Copy() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Location = Location, RefreshSeconds = RefreshSeconds,
        AutoRefresh = AutoRefresh, Enabled = Enabled,
        Mappings = Mappings.Select(mapping => new DataFeedMapping
        {
            Key = mapping.Key, Target = mapping.Target, CustomName = mapping.CustomName,
            PlayerId = mapping.PlayerId, Scale = mapping.Scale, Offset = mapping.Offset
        }).ToList()
    };
}

public static class DataFeedLabels
{
    public static string Kind(DataFeedKind kind) => kind switch
    {
        DataFeedKind.Manual => "Manual / typed key=value",
        DataFeedKind.Csv => "CSV file",
        DataFeedKind.Json => "JSON file",
        DataFeedKind.Xml => "XML file",
        DataFeedKind.HttpJson => "HTTP JSON poll",
        DataFeedKind.Weather => "Weather JSON poll",
        _ => kind.ToString()
    };

    public static string Target(DataFeedTarget target) => target switch
    {
        DataFeedTarget.HomeScore => "Home score",
        DataFeedTarget.AwayScore => "Visitors score",
        DataFeedTarget.GameClock => "Game clock",
        DataFeedTarget.PlayClock => "Play clock",
        DataFeedTarget.Period => "Period",
        DataFeedTarget.HomeTimeouts => "Home timeouts",
        DataFeedTarget.AwayTimeouts => "Visitors timeouts",
        DataFeedTarget.HomeFouls => "Home fouls",
        DataFeedTarget.AwayFouls => "Visitors fouls",
        DataFeedTarget.HomeTeamName => "Home team name",
        DataFeedTarget.AwayTeamName => "Visitors team name",
        DataFeedTarget.HomeAbbreviation => "Home abbreviation",
        DataFeedTarget.AwayAbbreviation => "Visitors abbreviation",
        DataFeedTarget.HomePlayerName => "Home player name",
        DataFeedTarget.AwayPlayerName => "Visitors player name",
        DataFeedTarget.HomePlayerNumber => "Home player number",
        DataFeedTarget.AwayPlayerNumber => "Visitors player number",
        DataFeedTarget.HomePlayerPosition => "Home player position",
        DataFeedTarget.AwayPlayerPosition => "Visitors player position",
        DataFeedTarget.HomePlayerStat => "Home player stat",
        DataFeedTarget.AwayPlayerStat => "Visitors player stat",
        DataFeedTarget.CustomValue => "Custom value",
        _ => target.ToString()
    };

    public static IReadOnlyList<DataFeedKind> Kinds => Enum.GetValues<DataFeedKind>();
    public static IReadOnlyList<DataFeedTarget> Targets => Enum.GetValues<DataFeedTarget>();

    /// <summary>True when the target writes the player on the graphic, or a named player.</summary>
    public static bool TouchesPlayer(DataFeedTarget target) => target is
        DataFeedTarget.HomePlayerName or DataFeedTarget.AwayPlayerName or
        DataFeedTarget.HomePlayerNumber or DataFeedTarget.AwayPlayerNumber or
        DataFeedTarget.HomePlayerPosition or DataFeedTarget.AwayPlayerPosition or
        DataFeedTarget.HomePlayerStat or DataFeedTarget.AwayPlayerStat;
}

/// <summary>Reads a manual block, CSV, JSON or XML into flat key/value rows. JSON paths use dots
/// (game.home.score); arrays use bracket indexes. XML uses element names, also joined by dots.</summary>
public static class DataFeedParser
{
    public static IReadOnlyList<DataFeedValue> Parse(DataFeedKind kind, string? text)
    {
        var body = text ?? "";
        return kind switch
        {
            DataFeedKind.Manual => ParseManual(body),
            DataFeedKind.Csv => ParseCsv(body),
            DataFeedKind.Json or DataFeedKind.HttpJson or DataFeedKind.Weather => ParseJson(body),
            DataFeedKind.Xml => ParseXml(body),
            _ => []
        };
    }

    static IReadOnlyList<DataFeedValue> ParseManual(string text)
    {
        var values = new List<DataFeedValue>();
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.IndexOf('=');
            if (split < 0) split = line.IndexOf(':');
            if (split <= 0)
            {
                values.Add(new DataFeedValue(values.Count.ToString(CultureInfo.InvariantCulture), line));
                continue;
            }
            values.Add(new DataFeedValue(line[..split].Trim(), line[(split + 1)..].Trim()));
        }
        return values;
    }

    static IReadOnlyList<DataFeedValue> ParseCsv(string text)
    {
        var rows = SplitCsv(text);
        if (rows.Count == 0) return [];
        var header = rows[0].Select(cell => cell.Trim()).ToArray();
        var values = new List<DataFeedValue>();
        // The first data row is the live row: a scoreboard should not silently average or print a
        // whole table. Extra rows remain available to the data viewer, keyed row.column.
        for (var row = 1; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Count; column++)
            {
                var key = column < header.Length && header[column].Length > 0
                    ? header[column]
                    : $"column{column + 1}";
                var value = rows[row][column];
                if (row == 1) values.Add(new DataFeedValue(key, value));
                values.Add(new DataFeedValue($"{row - 1}.{key}", value));
            }
        }
        return values;
    }

    static List<List<string>> SplitCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (ch == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            if (ch == '"') { quoted = true; continue; }
            if (ch == ',') { row.Add(field.ToString()); field.Clear(); continue; }
            if (ch is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(cell => cell.Length > 0)) rows.Add(row);
                row = [];
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                continue;
            }
            field.Append(ch);
        }
        row.Add(field.ToString());
        if (row.Any(cell => cell.Length > 0)) rows.Add(row);
        return rows;
    }

    static IReadOnlyList<DataFeedValue> ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
        var values = new List<DataFeedValue>();
        Walk(document.RootElement, "", values);
        return values;
    }

    static void Walk(JsonElement element, string path, List<DataFeedValue> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", values);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, $"{path}[{index}]", values);
                    index++;
                }
                break;
            case JsonValueKind.String:
                values.Add(new DataFeedValue(path, element.GetString() ?? ""));
                break;
            case JsonValueKind.Number:
                values.Add(new DataFeedValue(path, element.GetRawText()));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                values.Add(new DataFeedValue(path, element.GetBoolean() ? "true" : "false"));
                break;
        }
    }

    static IReadOnlyList<DataFeedValue> ParseXml(string text)
    {
        var document = XDocument.Parse(text);
        var values = new List<DataFeedValue>();
        WalkXml(document.Root, "", values);
        return values;
    }

    static void WalkXml(XElement? element, string path, List<DataFeedValue> values)
    {
        if (element is null) return;
        var name = path.Length == 0 ? element.Name.LocalName : $"{path}.{element.Name.LocalName}";
        if (!element.HasElements)
        {
            values.Add(new DataFeedValue(name, element.Value.Trim()));
            return;
        }
        foreach (var child in element.Elements()) WalkXml(child, name, values);
    }
}

/// <summary>Fetches and applies feed values. No background thread is owned here; the desktop
/// window decides when to poll, which keeps a headless test deterministic.</summary>
public static class DataFeedReader
{
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<(IReadOnlyList<DataFeedValue> Values, string? Error)> ReadAsync(
        DataFeed feed, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var location = (feed.Location ?? "").Trim();
            if (feed.Kind == DataFeedKind.Manual)
                return (DataFeedParser.Parse(feed.Kind, location), null);

            if (feed.Kind is DataFeedKind.HttpJson or DataFeedKind.Weather)
            {
                if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https"))
                    return ([], "Enter an HTTP or HTTPS address.");
                var client = http ?? SharedHttp;
                HttpResponseMessage? response = null;
                // A venue poller should survive one dropped Wi-Fi packet without showing a red
                // health line. Retry once with a short backoff, then report the real error.
                for (var attempt = 0; attempt < 2 && response is null; attempt++)
                {
                    try
                    {
                        response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException) when (attempt == 0)
                    {
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (response is null) return ([], "The source could not be reached after a retry.");
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return ([], $"HTTP {(int)response.StatusCode}: {Short(body)}");
                    return (DataFeedParser.Parse(DataFeedKind.HttpJson, body), null);
                }
            }

            if (location.Length == 0) return ([], "Choose a source file.");
            if (!File.Exists(location)) return ([], $"File not found: {Path.GetFileName(location)}");
            var text = await File.ReadAllTextAsync(location, cancellationToken).ConfigureAwait(false);
            return (DataFeedParser.Parse(feed.Kind, text), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or System.Xml.XmlException or HttpRequestException or TaskCanceledException)
        {
            return ([], ex.Message);
        }
    }

    static string Short(string value) => value.Length <= 140 ? value : value[..140] + "…";
}

/// <summary>Writes mapped source values into the live game through the same public engine methods
/// a console button uses, so a feed update is undoable and appears in the event log.</summary>
public static class DataFeedApplier
{
    public static int Apply(GameEngine engine, DataFeed feed)
    {
        var values = feed.Values ?? [];
        if (values.Count == 0) return 0;
        var applied = 0;
        foreach (var mapping in feed.Mappings ?? [])
        {
            if (mapping is null) continue;
            var match = Find(values, mapping.Key);
            if (match is null) continue;
            var raw = match.Value.Trim();
            var number = ParseNumber(raw) * (double.IsFinite(mapping.Scale) ? mapping.Scale : 1) + mapping.Offset;
            switch (mapping.Target)
            {
                case DataFeedTarget.HomeScore:
                    if (TryInt(number, out var home)) { engine.SetScore(home: true, home); applied++; }
                    break;
                case DataFeedTarget.AwayScore:
                    if (TryInt(number, out var away)) { engine.SetScore(home: false, away); applied++; }
                    break;
                case DataFeedTarget.GameClock:
                    if (LeagueCloudImport.TryParseClock(raw, out var clock)) { engine.SetClock(clock); applied++; }
                    break;
                case DataFeedTarget.PlayClock:
                    if (TryInt(number, out var playClock)) { engine.SetPlayClock(playClock); applied++; }
                    break;
                case DataFeedTarget.Period:
                    if (TryInt(number, out var period)) { engine.SetPeriod(period); applied++; }
                    break;
                case DataFeedTarget.HomeTimeouts:
                    if (TryInt(number, out var homeTimeouts)) { engine.SetTimeoutCount(home: true, homeTimeouts); applied++; }
                    break;
                case DataFeedTarget.AwayTimeouts:
                    if (TryInt(number, out var awayTimeouts)) { engine.SetTimeoutCount(home: false, awayTimeouts); applied++; }
                    break;
                case DataFeedTarget.HomeFouls:
                    if (TryInt(number, out var homeFouls)) { SetFouls(engine, home: true, homeFouls); applied++; }
                    break;
                case DataFeedTarget.AwayFouls:
                    if (TryInt(number, out var awayFouls)) { SetFouls(engine, home: false, awayFouls); applied++; }
                    break;
                case DataFeedTarget.HomeTeamName:
                    if (raw.Length > 0) { SetTeamName(engine, home: true, raw); applied++; }
                    break;
                case DataFeedTarget.AwayTeamName:
                    if (raw.Length > 0) { SetTeamName(engine, home: false, raw); applied++; }
                    break;
                case DataFeedTarget.HomeAbbreviation:
                    if (raw.Length > 0) { SetTeamAbbreviation(engine, home: true, raw); applied++; }
                    break;
                case DataFeedTarget.AwayAbbreviation:
                    if (raw.Length > 0) { SetTeamAbbreviation(engine, home: false, raw); applied++; }
                    break;
                case DataFeedTarget.HomePlayerName:
                    if (SetPlayerField(engine, home: true, mapping, raw, PlayerField.Name)) applied++;
                    break;
                case DataFeedTarget.AwayPlayerName:
                    if (SetPlayerField(engine, home: false, mapping, raw, PlayerField.Name)) applied++;
                    break;
                case DataFeedTarget.HomePlayerNumber:
                    if (TryInt(number, out var homeNumber) && SetPlayerField(engine, home: true, mapping, homeNumber, PlayerField.Number)) applied++;
                    break;
                case DataFeedTarget.AwayPlayerNumber:
                    if (TryInt(number, out var awayNumber) && SetPlayerField(engine, home: false, mapping, awayNumber, PlayerField.Number)) applied++;
                    break;
                case DataFeedTarget.HomePlayerPosition:
                    if (SetPlayerField(engine, home: true, mapping, raw, PlayerField.Position)) applied++;
                    break;
                case DataFeedTarget.AwayPlayerPosition:
                    if (SetPlayerField(engine, home: false, mapping, raw, PlayerField.Position)) applied++;
                    break;
                case DataFeedTarget.HomePlayerStat:
                    if (TryInt(number, out var homeStat) && SetPlayerField(engine, home: true, mapping, homeStat, PlayerField.Stat)) applied++;
                    break;
                case DataFeedTarget.AwayPlayerStat:
                    if (TryInt(number, out var awayStat) && SetPlayerField(engine, home: false, mapping, awayStat, PlayerField.Stat)) applied++;
                    break;
                case DataFeedTarget.CustomValue:
                    var name = string.IsNullOrWhiteSpace(mapping.CustomName) ? mapping.Key : mapping.CustomName.Trim();
                    if (name.Length == 0) break;
                    var stat = engine.State.Stats.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (stat is null)
                    {
                        stat = engine.State.Stats.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, $"Feed {name}", StringComparison.OrdinalIgnoreCase));
                        if (stat is null) stat = engine.AddStat(name);
                    }
                    // Weather text such as "Cloudy 18°" is valuable as text; a numeric-only
                    // stat keeps its number and a clock can still take MM:SS.
                    engine.SetStatText(stat.Id, raw);
                    applied++;
                    break;
            }
        }
        return applied;
    }

    /// <summary>Writes one team's name, keeping its abbreviation, colours and records.</summary>
    static void SetTeamName(GameEngine engine, bool home, string name)
    {
        var state = engine.State;
        engine.SetTeamIdentity(home, name.Trim(),
            (home ? state.HomeAbbreviation : state.AwayAbbreviation).Trim(),
            home ? state.HomeLogoPath : state.AwayLogoPath,
            home ? state.HomePrimaryColor : state.AwayPrimaryColor,
            home ? state.HomeSecondaryColor : state.AwaySecondaryColor,
            home ? state.HomeMascot : state.AwayMascot,
            home ? state.HomeRecordDisplay : state.AwayRecordDisplay,
            home ? state.HomeRanking : state.AwayRanking,
            home ? state.HomeDivisionRecord : state.AwayDivisionRecord);
    }

    /// <summary>Writes one team's abbreviation, keeping everything else about the team.</summary>
    static void SetTeamAbbreviation(GameEngine engine, bool home, string abbreviation)
    {
        var state = engine.State;
        engine.SetTeamIdentity(home,
            (home ? state.HomeTeam : state.AwayTeam).Trim(), abbreviation.Trim(),
            home ? state.HomeLogoPath : state.AwayLogoPath,
            home ? state.HomePrimaryColor : state.AwayPrimaryColor,
            home ? state.HomeSecondaryColor : state.AwaySecondaryColor,
            home ? state.HomeMascot : state.AwayMascot,
            home ? state.HomeRecordDisplay : state.AwayRecordDisplay,
            home ? state.HomeRanking : state.AwayRanking,
            home ? state.HomeDivisionRecord : state.AwayDivisionRecord);
    }

    enum PlayerField { Name, Number, Position, Stat }

    /// <summary>
    /// Writes a field or a stat on a roster player and re-resolves the player on the graphic. The
    /// mapping may name a player; when it does not, the player currently selected for that side is
    /// the one a live feed follows.
    /// </summary>
    static bool SetPlayerField(GameEngine engine, bool home, DataFeedMapping mapping, object value, PlayerField field)
    {
        var roster = RosterStore.Book.Find(home ? engine.State.HomeRosterId : engine.State.AwayRosterId);
        if (roster is null) return false;
        var selected = home ? engine.State.HomePlayer : engine.State.AwayPlayer;
        var player = !string.IsNullOrWhiteSpace(mapping.PlayerId)
            ? roster.Find(mapping.PlayerId)
            : roster.Find(selected?.PlayerId);
        if (player is null) return false;
        switch (field)
        {
            case PlayerField.Name:
                var name = Convert.ToString(value)?.Trim() ?? "";
                if (name.Length == 0 || string.Equals(name, player.Name, StringComparison.Ordinal)) return false;
                player.Name = name;
                break;
            case PlayerField.Number:
                var number = Math.Clamp(Convert.ToInt32(value), 0, 999);
                if (number == player.Number) return false;
                player.Number = number;
                break;
            case PlayerField.Position:
                var position = Convert.ToString(value)?.Trim() ?? "";
                if (string.Equals(position, player.Position, StringComparison.Ordinal)) return false;
                player.Position = position;
                break;
            case PlayerField.Stat:
                var statName = (mapping.CustomName ?? "").Trim();
                if (statName.Length == 0) return false;
                player.SetStat(statName, Math.Clamp(Convert.ToInt32(value), -99999, 99999));
                break;
        }
        RosterStore.Save();
        engine.RecordEvent("Data", $"{(home ? "Home" : "Visitors")} · {player.Record} · "
            + (field == PlayerField.Stat ? $"{mapping.CustomName} = {value}" : $"{field} = {value}"));
        engine.ReloadSelectedPlayer(home, publish: engine.PlayerStatsLive);
        return true;
    }

    static void SetFouls(GameEngine engine, bool home, int value)
    {
        var current = home ? engine.State.HomeFouls : engine.State.AwayFouls;
        var delta = Math.Clamp(value - current, -9999, 9999);
        for (var index = 0; index < Math.Abs(delta); index++) engine.AddFoul(home);
    }

    static DataFeedValue? Find(IReadOnlyList<DataFeedValue> values, string? key)
    {
        var wanted = (key ?? "").Trim();
        if (wanted.Length == 0) return values.FirstOrDefault();
        var exact = values.FirstOrDefault(value => string.Equals(value.Key, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        // A JSON path may be configured without its root (score.home); accept the leaf too when
        // only one source column carries that name.
        var leaf = values.Where(value => value.Key.EndsWith($".{wanted}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Key.Split('.').Last(), wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (leaf.Length == 1) return leaf[0];
        return int.TryParse(wanted, out var index) && index >= 0 && index < values.Count ? values[index] : null;
    }

    static double ParseNumber(string text)
    {
        var cleaned = new string(text.Where(ch => char.IsDigit(ch) || ch is '-' or '.' or ',').ToArray()).Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    static bool TryInt(double value, out int result)
    {
        result = (int)Math.Round(value);
        return double.IsFinite(value) && result >= 0;
    }
}

/// <summary>The feed document lives beside the rest of the operator's settings.</summary>
public sealed class DataFeedStore
{
    public string FilePath { get; }
    public List<DataFeed> Feeds { get; private set; } = [];

    public DataFeedStore(string? filePath = null) => FilePath = filePath ?? AppPaths.Combine("data-feeds.json");

    public static DataFeedStore Load(string? filePath = null)
    {
        var store = new DataFeedStore(filePath);
        try
        {
            if (File.Exists(store.FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<DataFeed>>(File.ReadAllText(store.FilePath));
                if (loaded is not null) store.Feeds = loaded.Where(feed => feed is not null).ToList();
            }
        }
        catch
        {
            // A damaged feed file must never stop a show.
        }
        store.Normalize();
        return store;
    }

    public void Normalize()
    {
        foreach (var feed in Feeds)
        {
            feed.Id = string.IsNullOrWhiteSpace(feed.Id) ? Guid.NewGuid().ToString("N") : feed.Id.Trim();
            feed.Name = string.IsNullOrWhiteSpace(feed.Name) ? "Untitled source" : feed.Name.Trim();
            feed.Location ??= "";
            feed.RefreshSeconds = Math.Clamp(feed.RefreshSeconds, 5, 3600);
            feed.Mappings ??= [];
            feed.Mappings = feed.Mappings.Where(mapping => mapping is not null).Take(64).ToList();
            foreach (var mapping in feed.Mappings)
            {
                mapping.Key = (mapping.Key ?? "").Trim();
                mapping.CustomName = (mapping.CustomName ?? "").Trim();
                mapping.PlayerId = (mapping.PlayerId ?? "").Trim();
                mapping.Scale = double.IsFinite(mapping.Scale) ? mapping.Scale : 1;
                mapping.Offset = double.IsFinite(mapping.Offset) ? mapping.Offset : 0;
            }
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Feeds, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // Storage trouble is reported by the window; live data carries on.
        }
    }

    public DataFeed Add(string? name = null)
    {
        var feed = new DataFeed { Name = string.IsNullOrWhiteSpace(name) ? $"Source {Feeds.Count + 1}" : name.Trim() };
        Feeds.Add(feed);
        return feed;
    }

    public void Remove(DataFeed feed)
    {
        Feeds.Remove(feed);
    }
}
