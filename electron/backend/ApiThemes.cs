using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// Themes, as on Windows and in the same files: the two built in (light and dark), and your own as small JSON files
/// in the "themes" folder of the data folder. A theme names a base (light or dark) and only has to give the colours it
/// changes. A theme file made on Windows works here and the other way round. Editing the file by hand shows at once.
/// </summary>
public static partial class Api
{
    public sealed class ThemeFile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("base")] public string Base { get; set; } = "light";
        [JsonPropertyName("colours")] public Dictionary<string, string> Colours { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("textFont")] public string TextFont { get; set; } = "";
        [JsonPropertyName("headFont")] public string HeadFont { get; set; } = "";
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonIgnore] public string Id { get; set; } = "";
    }

    /// <summary>The colours a theme sets: (key, group, label, tip) — the same keys as the Windows app's.</summary>
    static readonly (string Key, string Group, string Label, string Tip)[] ThemeKeys =
    {
        ("Bg", "background", "page", "The window and every page"),
        ("BgAlt", "background", "panels", "The player bar, menus, boxes and the grey behind covers"),
        ("Line", "background", "lines", "Borders and dividing lines"),
        ("Hover", "background", "pointing at", "Under the pointer (usually see-through)"),
        ("Selected", "background", "selected", "Behind what is selected (usually see-through)"),
        ("Fg", "text", "text", "Titles and everyday text"),
        ("FgDim", "text", "quieter text", "Artists, times and other second lines"),
        ("FgFaint", "text", "faint text", "Hints, labels and icons that stay in the background"),
        ("Accent", "accent", "accent", "On switches, the heart, shuffle and repeat, and highlighted words"),
        ("AccentStart", "accent", "bar, left end", "Progress bars and the tab at the window's edge start in this colour"),
        ("AccentEnd", "accent", "bar, right end", "...and end in this one"),
        ("NowBg", "now playing", "background", "The now playing screen behind the art and visualizer"),
        ("NowBgAlt", "now playing", "panels", "Boxes and buttons on the now playing screen"),
        ("NowFg", "now playing", "text", "The song, artist and album, and the queue"),
        ("NowFgDim", "now playing", "quieter text", "Second lines on the now playing screen"),
        ("NowFgFaint", "now playing", "faint text", "Hints on the now playing screen"),
        ("NowLine", "now playing", "lines", "Lines on the now playing screen"),
        ("NowHover", "now playing", "pointing at", "Under the pointer on the now playing screen"),
        ("NowSelected", "now playing", "selected", "Behind what is selected on the now playing screen"),
        ("NowHighlight", "now playing", "playing song", "The song playing now, in the queue"),
    };

    static readonly Dictionary<string, (string Light, string Dark)> ThemeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bg"] = ("#FFFFFF", "#141414"), ["BgAlt"] = ("#F3F3F3", "#1E1E1E"), ["Fg"] = ("#1A1A1A", "#F2F2F2"), ["FgDim"] = ("#666666", "#A6A6A6"),
        ["FgFaint"] = ("#A9A9A9", "#6A6A6A"), ["Line"] = ("#E2E2E2", "#2E2E2E"), ["Hover"] = ("#0E000000", "#16FFFFFF"), ["Selected"] = ("#1C000000", "#26FFFFFF"),
        ["Accent"] = ("#E0447C", "#E0447C"), ["AccentStart"] = ("#F0603A", "#F0603A"), ["AccentEnd"] = ("#D5358F", "#D5358F"),
        ["NowBg"] = ("#0A0A0A", "#0A0A0A"), ["NowBgAlt"] = ("#1C1C1C", "#1C1C1C"), ["NowFg"] = ("#FFFFFF", "#FFFFFF"), ["NowFgDim"] = ("#B8B8B8", "#B8B8B8"),
        ["NowFgFaint"] = ("#7A7A7A", "#7A7A7A"), ["NowLine"] = ("#333333", "#333333"), ["NowHover"] = ("#1EFFFFFF", "#1EFFFFFF"), ["NowSelected"] = ("#30FFFFFF", "#30FFFFFF"),
        ["NowHighlight"] = ("#FF6FA5", "#FF6FA5"),
    };

    static readonly JsonSerializerOptions ThemeJson = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static string ThemesFolder => Path.Combine(AppPaths.DataDir, "themes");
    static string ThemeFileOf(string id) => Path.Combine(ThemesFolder, id + ".json");
    static bool IsHex(string? hex) => hex != null && hex.Trim().TrimStart('#') is var h && h.Length is 3 or 4 or 6 or 8 && h.All(Uri.IsHexDigit);
    static bool BuiltInTheme(string id) => id is "light" or "dark";

    static ThemeFile? ReadTheme(string file)
    {
        try
        {
            var t = JsonSerializer.Deserialize<ThemeFile>(File.ReadAllText(file), ThemeJson);
            if (t == null) return null;
            t.Colours = new Dictionary<string, string>(t.Colours ?? new(), StringComparer.OrdinalIgnoreCase);
            t.Base = string.Equals(t.Base, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
            t.Id = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = t.Id;
            return t;
        }
        catch { return null; }
    }

    static ThemeFile FindTheme(string? id)
    {
        if (string.Equals(id, "dark", StringComparison.OrdinalIgnoreCase)) return new ThemeFile { Id = "dark", Name = "dark", Base = "dark" };
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, "light", StringComparison.OrdinalIgnoreCase) || !File.Exists(ThemeFileOf(id)) || ReadTheme(ThemeFileOf(id)) is not { } t)
            return new ThemeFile { Id = "light", Name = "light", Base = "light" };
        return t;
    }

    /// <summary>Every colour a theme shows (its own, else its base's), as "#RRGGBB" or "#AARRGGBB".</summary>
    static Dictionary<string, string> Resolved(ThemeFile t) => ThemeKeys.ToDictionary(k => k.Key, k =>
        t.Colours.TryGetValue(k.Key, out var hex) && IsHex(hex) ? "#" + hex.Trim().TrimStart('#').ToUpperInvariant()
        : t.Base == "dark" ? ThemeDefaults[k.Key].Dark : ThemeDefaults[k.Key].Light);

    static object ThemeShape(ThemeFile t) => new
    {
        id = t.Id, name = t.Name, @base = t.Base, builtIn = BuiltInTheme(t.Id), textFont = t.TextFont, headFont = t.HeadFont,
        own = t.Colours.Where(kv => IsHex(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value), colours = Resolved(t)
    };

    static string NewThemeId(string name, string? keep = null)
    {
        var bad = Path.GetInvalidFileNameChars();
        var stem = new string(name.Trim().Select(ch => bad.Contains(ch) ? '-' : ch).ToArray()).Trim(' ', '.', '-');
        if (stem == "" || BuiltInTheme(stem.ToLowerInvariant())) stem = "my " + (stem == "" ? "theme" : stem);
        if (stem.Length > 60) stem = stem[..60];
        var id = stem;
        for (var n = 2; File.Exists(ThemeFileOf(id)) && !string.Equals(id, keep, StringComparison.OrdinalIgnoreCase); n++) id = $"{stem} {n}";
        return id;
    }

    static FileSystemWatcher? _themeWatcher;
    static Timer? _themeReload;

    /// <summary>Your theme's file edited outside Phoenix: shown as soon as it is saved.</summary>
    static void WatchThemes()
    {
        try
        {
            Directory.CreateDirectory(ThemesFolder);
            _themeWatcher = new FileSystemWatcher(ThemesFolder, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            void OnDisk(object _, FileSystemEventArgs e)
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(e.FullPath), S.Theme, StringComparison.OrdinalIgnoreCase)) return;
                // editors save in a few steps: wait for the file to settle before reading it
                _themeReload?.Dispose();
                _themeReload = new Timer(_ => MainLoop.Post(() => Rpc.Emit("theme", ThemeShape(FindTheme(S.Theme)))), null, 250, Timeout.Infinite);
            }
            _themeWatcher.Changed += OnDisk;
            _themeWatcher.Created += OnDisk;
            _themeWatcher.Renamed += OnDisk;
        }
        catch { }
    }

    static void RegisterThemes()
    {
        WatchThemes();
        Rpc.On("themes.list", _ =>
        {
            var list = new List<ThemeFile> { FindTheme("light"), FindTheme("dark") };
            try { if (Directory.Exists(ThemesFolder)) list.AddRange(Directory.GetFiles(ThemesFolder, "*.json").Select(ReadTheme).OfType<ThemeFile>().Where(t => !BuiltInTheme(t.Id)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)); }
            catch { }
            return new { current = FindTheme(S.Theme).Id, themes = list.Select(t => new { id = t.Id, name = t.Name, @base = t.Base }), folder = ThemesFolder };
        });
        Rpc.On("themes.keys", _ => ThemeKeys.Select(k => new { key = k.Key, group = k.Group, label = k.Label, tip = k.Tip }));
        Rpc.On("themes.current", _ => ThemeShape(FindTheme(S.Theme)));
        Rpc.On("themes.get", p => ThemeShape(FindTheme(p.Str("id"))));
        // what a theme would look like with these changes (the designer, before saving)
        Rpc.On("themes.preview", p =>
        {
            var t = new ThemeFile { Id = p.Str("id"), Name = p.Str("name"), Base = p.Str("base") == "dark" ? "dark" : "light", TextFont = p.Str("textFont"), HeadFont = p.Str("headFont") };
            foreach (var kv in p.Obj("own").EnumerateObject()) if (kv.Value.ValueKind == JsonValueKind.String && IsHex(kv.Value.GetString())) t.Colours[kv.Name] = kv.Value.GetString()!;
            return ThemeShape(t);
        });
        Rpc.On("themes.use", p =>
        {
            var t = FindTheme(p.Str("id"));
            S.Theme = t.Id;
            Save();
            return ThemeShape(t);
        });
        Rpc.On("themes.save", p =>
        {
            var name = p.Str("name").Trim();
            if (name == "") throw new InvalidOperationException("Give the theme a name.");
            var id = p.Str("id");
            var asNew = p.Bool("asNew") || BuiltInTheme(id) || id == "";
            var t = new ThemeFile { Name = name, Base = p.Str("base") == "dark" ? "dark" : "light", TextFont = p.Str("textFont").Trim(), HeadFont = p.Str("headFont").Trim() };
            foreach (var kv in p.Obj("own").EnumerateObject())
            {
                var hex = kv.Value.GetString();
                if (!IsHex(hex) || !ThemeDefaults.ContainsKey(kv.Name)) continue;
                // the same as the base: nothing to keep
                var basic = t.Base == "dark" ? ThemeDefaults[kv.Name].Dark : ThemeDefaults[kv.Name].Light;
                if (!string.Equals("#" + hex!.Trim().TrimStart('#'), basic, StringComparison.OrdinalIgnoreCase)) t.Colours[kv.Name] = "#" + hex.Trim().TrimStart('#').ToUpperInvariant();
            }
            t.Id = asNew ? NewThemeId(name) : id;
            Directory.CreateDirectory(ThemesFolder);
            File.WriteAllText(ThemeFileOf(t.Id), JsonSerializer.Serialize(t, ThemeJson));
            S.Theme = t.Id;
            Save();
            return ThemeShape(t);
        });
        Rpc.On("themes.delete", p =>
        {
            var id = p.Str("id");
            var back = FindTheme(id).Base;   // read before the file goes
            if (!BuiltInTheme(id)) try { File.Delete(ThemeFileOf(id)); } catch { }
            if (string.Equals(S.Theme, id, StringComparison.OrdinalIgnoreCase)) { S.Theme = back; Save(); }
            return ThemeShape(FindTheme(S.Theme));
        });
        Rpc.On("themes.export", p =>
        {
            var t = FindTheme(p.Str("id"));
            // every colour written out, so the file reads the same whatever its base
            var full = new ThemeFile { Name = t.Name, Base = t.Base, TextFont = t.TextFont, HeadFont = t.HeadFont, Colours = Resolved(t) };
            File.WriteAllText(p.Str("file"), JsonSerializer.Serialize(full, ThemeJson));
        });
        Rpc.On("themes.import", p =>
        {
            var file = p.Str("file");
            var text = File.ReadAllText(file);
            var t = ReadTheme(file);
            // any JSON file reads as an empty theme: say no unless it at least looks like one
            if (t == null || (!text.Contains("\"colours\"", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"base\"", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That file is not a Phoenix theme.");
            foreach (var bad in t.Colours.Where(kv => !IsHex(kv.Value) || !ThemeDefaults.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList()) t.Colours.Remove(bad);
            t.Id = NewThemeId(t.Name);
            Directory.CreateDirectory(ThemesFolder);
            File.WriteAllText(ThemeFileOf(t.Id), JsonSerializer.Serialize(t, ThemeJson));
            S.Theme = t.Id;
            Save();
            return ThemeShape(t);
        });
    }
}
