using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>One choice in the studio's font picker. <see cref="Source"/> is what an element stores.</summary>
public sealed record FontChoice(string Label, string Source, bool Imported = false, string? FilePath = null)
{
    public override string ToString() => Imported ? $"{Label}  ·  imported" : Label;
}

/// <summary>
/// Every font the studio can put on a graphic: the fonts that ship with Windows (well over the
/// twenty-five asked for), plus any family the operator imports from a .ttf/.otf file. Imported
/// fonts are copied into the app's Fonts folder so a saved layout still finds them.
/// </summary>
public static class FontCatalog
{
    public const string Fallback = "Segoe UI";

    // Ordered first in the picker: the faces a broadcast operator reaches for, all shipped with Windows.
    static readonly string[] Preferred =
    [
        "Segoe UI", "Segoe UI Semibold", "Segoe UI Black", "Arial", "Arial Black", "Arial Narrow",
        "Bahnschrift", "Calibri", "Cambria", "Candara", "Comic Sans MS", "Consolas", "Constantia",
        "Corbel", "Courier New", "Franklin Gothic Medium", "Gabriola", "Georgia", "Impact",
        "Lucida Console", "Lucida Sans Unicode", "Malgun Gothic", "Microsoft Sans Serif",
        "Palatino Linotype", "Segoe Print", "Segoe Script", "Sitka Text", "Sylfaen", "Tahoma",
        "Times New Roman", "Trebuchet MS", "Verdana"
    ];

    static List<FontChoice>? _cache;

    public static string Folder => AppPaths.Combine("Fonts");

    public static IReadOnlyList<FontChoice> All => _cache ??= Build();

    /// <summary>Rebuilds the list after a font file was imported.</summary>
    public static void Refresh() => _cache = null;

    static List<FontChoice> Build()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var family in Fonts.SystemFontFamilies) installed.Add(family.Source); }
        catch { /* a font subsystem failure must not empty the picker */ }

        var choices = new List<FontChoice>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Preferred)
            if (installed.Contains(name) && used.Add(name))
                choices.Add(new FontChoice(name, name));
        foreach (var name in installed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            if (used.Add(name))
                choices.Add(new FontChoice(name, name));
        if (choices.Count == 0) choices.Add(new FontChoice(Fallback, Fallback));

        choices.AddRange(ImportedFonts());
        return choices;
    }

    /// <summary>The font files this app owns, newest naming scheme or the GUID copies older builds made.</summary>
    static IEnumerable<string> FontFiles()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, "*.*")
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".ttf" or ".otf")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the family straight from the font file. <see cref="Fonts.GetFontFamilies(Uri)"/> was the
    /// old route, but a file URI gives back the whole installed collection with relative sources such
    /// as <c>./#Arial</c> — a value that cannot be resolved again in another session, which is why
    /// imported fonts silently fell back to Segoe UI.
    /// </summary>
    static bool TryReadFace(string path, out string family, out string face)
    {
        family = "";
        face = "";
        try
        {
            var glyph = new GlyphTypeface(new Uri(path));
            family = glyph.FamilyNames.Values.FirstOrDefault()?.Trim() ?? "";
            face = glyph.FaceNames.Values.FirstOrDefault()?.Trim() ?? "";
            return family.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>A file-backed source WPF and the web export can both read back.</summary>
    static string SourceFor(string path, string family) => new Uri(path).AbsoluteUri + "#" + family;

    /// <summary>The font families found in the app's Fonts folder, as file-backed choices.</summary>
    static IEnumerable<FontChoice> ImportedFonts()
    {
        // A family is one picker row, however many files or earlier imports carry it. This is what
        // stops the same font appearing twice after it has been imported twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in FontFiles())
        {
            if (!TryReadFace(path, out var family, out _)) continue;
            if (!seen.Add(family)) continue;
            yield return new FontChoice(family, SourceFor(path, family), Imported: true, path);
        }
    }

    /// <summary>Copies a font file in and returns the value an element should store.</summary>
    public static string Import(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf"))
            throw new InvalidOperationException("Choose a .ttf or .otf font file.");
        if (!File.Exists(path)) throw new InvalidOperationException("That font file is not there any more.");
        if (!TryReadFace(path, out var family, out _))
            throw new InvalidOperationException("That file does not contain a font Windows can read.");

        Directory.CreateDirectory(Folder);
        // The family is already here: reuse its canonical file instead of copying the same font a
        // second time under a new name. This also keeps one layout pointing at one stable source.
        foreach (var existing in FontFiles())
            if (TryReadFace(existing, out var existingFamily, out _)
                && string.Equals(existingFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Refresh();
                return SourceFor(existing, existingFamily);
            }

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..8].ToLowerInvariant();
        var safe = new string(family.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch).ToArray()).Trim();
        if (safe.Length == 0) safe = "font";
        var target = Path.Combine(Folder, $"{safe}-{hash}{extension}");
        if (!File.Exists(target)) File.Copy(path, target);
        Refresh();
        return SourceFor(target, family);
    }

    /// <summary>The family name stored inside a font source: after the # of a file URI, or the name itself.</summary>
    public static string FamilyName(string? source)
    {
        var text = (source ?? "").Trim();
        var hash = text.LastIndexOf('#');
        if (hash >= 0 && hash < text.Length - 1) return text[(hash + 1)..].Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Path.GetFileNameWithoutExtension(new Uri(text).LocalPath); }
            catch { }
        }
        return text;
    }

    public static FontFamily Resolve(string? source)
    {
        var text = (source ?? "").Trim();
        if (text.Length == 0) return new FontFamily(Fallback);
        // Layouts saved by an older build stored "./#Family" or a bare file URI. Re-point those at
        // the file-backed source of the imported family, so the artwork heals instead of reverting.
        if (text.StartsWith("./#", StringComparison.Ordinal) || text.StartsWith('#'))
            text = text[(text.IndexOf('#') + 1)..].Trim();
        var family = FamilyName(text);
        var imported = All.FirstOrDefault(choice => choice.Imported
            && string.Equals(choice.Label, family, StringComparison.OrdinalIgnoreCase));
        if (imported is not null && !text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            text = imported.Source;
        try { return new FontFamily(text); }
        catch { return new FontFamily(Fallback); }
    }

    /// <summary>The picker entry for a stored source, even when the font was never in the list.</summary>
    public static FontChoice ChoiceFor(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return All[0];
        var family = FamilyName(source);
        return All.FirstOrDefault(c => string.Equals(c.Source, source, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(c => c.Imported && string.Equals(c.Label, family, StringComparison.OrdinalIgnoreCase))
            ?? new FontChoice(family.Length > 0 ? family : Fallback, source, true);
    }
}
