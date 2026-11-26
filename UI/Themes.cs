using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// A look for Phoenix: its colours and fonts. The two built in (light and dark) are made in code; your own are small
/// JSON files in the "themes" folder of the data folder. A theme names a base (light or dark) and only has to give
/// the colours it changes: everything it leaves out comes from that base.
/// </summary>
public sealed class Theme
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"light" or "dark": the colours a theme leaves out, the title bars Windows draws, and the book reader.</summary>
    [JsonPropertyName("base")] public string Base { get; set; } = "light";
    [JsonPropertyName("colours")] public Dictionary<string, string> Colours { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The font for everyday text; blank = Segoe UI.</summary>
    [JsonPropertyName("textFont")] public string TextFont { get; set; } = "";
    /// <summary>The font for big headings and the song title in now playing; blank = Segoe UI Light.</summary>
    [JsonPropertyName("headFont")] public string HeadFont { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>"light", "dark", or the theme file's name without ".json".</summary>
    [JsonIgnore] public string Id { get; set; } = "";
    [JsonIgnore] public bool BuiltIn => Id is "light" or "dark" or "system";
    [JsonIgnore] public bool IsDark => string.Equals(Base, "dark", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string TextFontName => string.IsNullOrWhiteSpace(TextFont) ? Themes.DefaultTextFont : TextFont.Trim();
    [JsonIgnore] public string HeadFontName => string.IsNullOrWhiteSpace(HeadFont) ? Themes.DefaultHeadFont : HeadFont.Trim();

    /// <summary>A colour as this theme shows it: its own if it gives one, otherwise its base's.</summary>
    public Color Get(string key) =>
        Colours.TryGetValue(key, out var hex) && Themes.TryParse(hex, out var c) ? c : Themes.Default(key, IsDark);

    public void Set(string key, Color c)
    {
        if (c == Themes.Default(key, IsDark)) Colours.Remove(key);   // the same as the base: nothing to keep
        else Colours[key] = Themes.Hex(c);
    }

    public Theme Copy() => new()
    {
        Name = Name, Base = Base, TextFont = TextFont, HeadFont = HeadFont, Version = Version, Id = Id,
        Colours = new Dictionary<string, string>(Colours, StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>Every colour written out (for exporting, so the file reads the same whatever the base becomes).</summary>
    public Theme Filled()
    {
        var t = Copy();
        foreach (var k in Themes.Keys) t.Colours[k.Key] = Themes.Hex(Get(k.Key));
        return t;
    }
}

/// <summary>One colour a theme sets, for the designer.</summary>
public sealed record ThemeKey(string Key, string Group, string Label, string Tip);

public static class Themes
{
    public const string DefaultTextFont = "Segoe UI", DefaultHeadFont = "Segoe UI Light";

    public static readonly ThemeKey[] Keys =
    {
        new("Bg", "background", "page", "The window and every page"),
        new("BgAlt", "background", "panels", "The player bar, menus, boxes and the grey behind covers"),
        new("Line", "background", "lines", "Borders and dividing lines"),
        new("Hover", "background", "pointing at", "Under the pointer (usually see-through)"),
        new("Selected", "background", "selected", "Behind what is selected (usually see-through)"),
        new("Fg", "text", "text", "Titles and everyday text"),
        new("FgDim", "text", "quieter text", "Artists, times and other second lines"),
        new("FgFaint", "text", "faint text", "Hints, labels and icons that stay in the background"),
        new("Accent", "accent", "accent", "On switches, the heart, shuffle and repeat, and highlighted words"),
        new("AccentStart", "accent", "bar, left end", "Progress bars and the tab at the window's edge start in this colour"),
        new("AccentEnd", "accent", "bar, right end", "...and end in this one"),
        new("NowBg", "now playing", "background", "The now playing screen behind the art and visualizer"),
        new("NowBgAlt", "now playing", "panels", "Boxes and buttons on the now playing screen"),
        new("NowFg", "now playing", "text", "The song, artist and album, and the queue"),
        new("NowFgDim", "now playing", "quieter text", "Second lines on the now playing screen"),
        new("NowFgFaint", "now playing", "faint text", "Hints on the now playing screen"),
        new("NowLine", "now playing", "lines", "Lines on the now playing screen"),
        new("NowHover", "now playing", "pointing at", "Under the pointer on the now playing screen"),
        new("NowSelected", "now playing", "selected", "Behind what is selected on the now playing screen"),
        new("NowHighlight", "now playing", "playing song", "The song playing now, in the queue"),
    };

    static readonly Dictionary<string, (string Light, string Dark)> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bg"] = ("#FFFFFF", "#141414"),
        ["BgAlt"] = ("#F3F3F3", "#1E1E1E"),
        ["Fg"] = ("#1A1A1A", "#F2F2F2"),
        ["FgDim"] = ("#666666", "#A6A6A6"),
        ["FgFaint"] = ("#A9A9A9", "#6A6A6A"),
        ["Line"] = ("#E2E2E2", "#2E2E2E"),
        ["Hover"] = ("#0E000000", "#16FFFFFF"),
        ["Selected"] = ("#1C000000", "#26FFFFFF"),
        ["Accent"] = ("#E0447C", "#E0447C"),
        ["AccentStart"] = ("#F0603A", "#F0603A"),
        ["AccentEnd"] = ("#D5358F", "#D5358F"),
        ["NowBg"] = ("#0A0A0A", "#0A0A0A"),
        ["NowBgAlt"] = ("#1C1C1C", "#1C1C1C"),
        ["NowFg"] = ("#FFFFFF", "#FFFFFF"),
        ["NowFgDim"] = ("#B8B8B8", "#B8B8B8"),
        ["NowFgFaint"] = ("#7A7A7A", "#7A7A7A"),
        ["NowLine"] = ("#333333", "#333333"),
        ["NowHover"] = ("#1EFFFFFF", "#1EFFFFFF"),
        ["NowSelected"] = ("#30FFFFFF", "#30FFFFFF"),
        ["NowHighlight"] = ("#FF6FA5", "#FF6FA5"),
    };

    public static Color Default(string key, bool dark) =>
        Defaults.TryGetValue(key, out var d) && TryParse(dark ? d.Dark : d.Light, out var c) ? c : Colors.Gray;

    public static bool TryParse(string? hex, out Color colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim();
        if (!h.StartsWith('#')) h = "#" + h;
        if (h.Length is not (4 or 5 or 7 or 9) || !h.Skip(1).All(Uri.IsHexDigit)) return false;
        try { colour = (Color)ColorConverter.ConvertFromString(h); return true; }
        catch { return false; }
    }

    public static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Theme Light => new() { Id = "light", Name = "light", Base = "light" };
    public static Theme Dark => new() { Id = "dark", Name = "dark", Base = "dark" };
    /// <summary>
    /// Following Windows: the theme chosen for light (settings → display) while Windows is set to light for apps, the
    /// one chosen for dark while it is dark; it switches when Windows does.
    /// </summary>
    public static Theme System
    {
        get
        {
            var id = SystemIsDark ? App.Settings.ThemeDark : App.Settings.ThemeLight;
            var t = (string.Equals(id, "system", StringComparison.OrdinalIgnoreCase) ? (SystemIsDark ? Dark : Light) : Find(id)).Copy();
            t.Id = "system";
            t.Name = "follow the system";
            return t;
        }
    }

    /// <summary>Which of the two the system theme shows now (for settings).</summary>
    public static string SystemSide => SystemIsDark ? App.Settings.ThemeDark : App.Settings.ThemeLight;

    /// <summary>Windows is set to dark for apps (settings → personalisation → colours).</summary>
    public static bool SystemIsDark
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }
    }

    static bool _following;

    /// <summary>Windows switching between light and dark redraws Phoenix when the system theme is chosen.</summary>
    static void FollowSystem()
    {
        if (_following) return;
        _following = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Current.Id == "system") Apply(System);
            });
        };
    }

    /// <summary>The theme showing now (in the designer, the one being designed).</summary>
    public static Theme Current { get; private set; } = Light;
    public static string TextFontName => Current.TextFontName;

    /// <summary>The theme changed (the designer changes it many times a second while a colour is dragged).</summary>
    public static event Action? Changed;

    public static string Folder => Path.Combine(AppPaths.DataDir, "themes");
    static string FileOf(string id) => Path.Combine(Folder, id + ".json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Reads a theme file; null if it is not one.</summary>
    public static Theme? Read(string file)
    {
        try
        {
            var t = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file), Json);
            if (t == null) return null;
            t.Colours = new Dictionary<string, string>(t.Colours ?? new(), StringComparer.OrdinalIgnoreCase);
            t.Base = string.Equals(t.Base, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
            t.Id = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = t.Id;
            return t;
        }
        catch { return null; }
    }

    /// <summary>The built-in themes, then yours by name.</summary>
    public static List<Theme> All()
    {
        var list = new List<Theme> { Light, Dark, System };
        try
        {
            if (Directory.Exists(Folder))
                list.AddRange(Directory.GetFiles(Folder, "*.json").Select(Read).OfType<Theme>()
                    .Where(t => !t.BuiltIn).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        catch { }
        return list;
    }

    public static Theme Find(string? id)
    {
        if (string.Equals(id, "dark", StringComparison.OrdinalIgnoreCase)) return Dark;
        if (string.Equals(id, "system", StringComparison.OrdinalIgnoreCase)) return System;
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, "light", StringComparison.OrdinalIgnoreCase)) return Light;
        return File.Exists(FileOf(id)) && Read(FileOf(id)) is { } t ? t : Light;
    }

    /// <summary>A file name for a theme called <paramref name="name"/> that no other theme has.</summary>
    public static string NewId(string name, string? keep = null)
    {
        var bad = Path.GetInvalidFileNameChars();
        var stem = new string(name.Trim().Select(ch => bad.Contains(ch) ? '-' : ch).ToArray()).Trim(' ', '.', '-');
        if (stem == "" || stem.Equals("light", StringComparison.OrdinalIgnoreCase) || stem.Equals("dark", StringComparison.OrdinalIgnoreCase) || stem.Equals("system", StringComparison.OrdinalIgnoreCase)) stem = "my " + (stem == "" ? "theme" : stem);
        if (stem.Length > 60) stem = stem[..60];
        var id = stem;
        for (var n = 2; File.Exists(FileOf(id)) && !string.Equals(id, keep, StringComparison.OrdinalIgnoreCase); n++) id = $"{stem} {n}";
        return id;
    }

    /// <summary>Writes one of your themes to its file (a new one gets a file of its own); returns its id.</summary>
    public static string Save(Theme t)
    {
        Directory.CreateDirectory(Folder);
        if (t.BuiltIn || string.IsNullOrEmpty(t.Id)) t.Id = NewId(t.Name);
        File.WriteAllText(FileOf(t.Id), JsonSerializer.Serialize(t, Json));
        return t.Id;
    }

    public static void Export(Theme t, string file) => File.WriteAllText(file, JsonSerializer.Serialize(t.Filled(), Json));

    /// <summary>Copies a theme file someone gave you into the themes folder; null if the file is not a theme.</summary>
    public static Theme? Import(string file)
    {
        var t = Read(file);
        if (t == null) return null;
        // any JSON file reads as an empty theme: say no unless it at least looks like one
        var text = File.ReadAllText(file);
        if (!text.Contains("\"colours\"", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"base\"", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var bad in t.Colours.Where(kv => !TryParse(kv.Value, out _) || !Defaults.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList()) t.Colours.Remove(bad);
        t.Id = "";
        Save(t);
        return t;
    }

    public static void Delete(string id)
    {
        if (id is "light" or "dark" or "system") return;
        try { File.Delete(FileOf(id)); } catch { }
    }

    /// <summary>Shows a theme (and keeps watching its file, so editing it by hand shows at once).</summary>
    public static void Apply(Theme t)
    {
        if (t.Id == "system") FollowSystem();
        Current = t;
        var res = Application.Current.Resources;
        foreach (var k in Keys)
        {
            var b = new SolidColorBrush(t.Get(k.Key));
            b.Freeze();
            res[k.Key] = b;
        }
        var grad = new LinearGradientBrush(t.Get("AccentStart"), t.Get("AccentEnd"), new Point(0, 0), new Point(1, 0));
        grad.Freeze();
        res["AccentGrad"] = grad;
        // behind your font, Segoe UI and the icon font, for the letters and symbols it doesn't have
        res["TextFont"] = new FontFamily(WithFallback(t.TextFontName));
        res["HeadFont"] = new FontFamily(WithFallback(t.HeadFontName));
        WindowTheme.ApplyAll();
        Changed?.Invoke();
        Repaint();
        Watch(t);
    }

    static string WithFallback(string font) =>
        font.Equals(DefaultTextFont, StringComparison.OrdinalIgnoreCase) || font.Equals(DefaultHeadFont, StringComparison.OrdinalIgnoreCase)
            ? font : $"{font}, {DefaultTextFont}, Segoe MDL2 Assets";

    /// <summary>The parts of the window that draw themselves look the colours up as they draw: ask them to draw again.</summary>
    static void Repaint()
    {
        if (Application.Current == null) return;
        foreach (Window w in Application.Current.Windows) Walk(w);
        static void Walk(DependencyObject o)
        {
            if (o is IThemed themed) themed.ThemeChanged();
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(o); i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    // ---------- your theme's file, edited outside Phoenix ----------

    static FileSystemWatcher? _watcher;
    static DispatcherTimer? _reload;
    /// <summary>While the designer is open it owns the theme, so a change on disk is not shown over it.</summary>
    public static bool Designing;

    static void Watch(Theme t)
    {
        if (t.BuiltIn) { StopWatching(); return; }
        try
        {
            Directory.CreateDirectory(Folder);
            if (_watcher == null)
            {
                _watcher = new FileSystemWatcher(Folder, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                _watcher.Changed += OnDisk;
                _watcher.Created += OnDisk;
                _watcher.Renamed += OnDisk;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch { StopWatching(); }
    }

    static void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    static void OnDisk(object sender, FileSystemEventArgs e)
    {
        if (Designing) return;   // (a save of our own reloads the same colours: harmless)
        if (!string.Equals(Path.GetFileNameWithoutExtension(e.FullPath), Current.Id, StringComparison.OrdinalIgnoreCase)) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // editors save in a few steps: wait for the file to settle before reading it
            _reload ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _reload.Stop();
            _reload.Tick -= Reload;
            _reload.Tick += Reload;
            _reload.Start();
        });
    }

    static void Reload(object? sender, EventArgs e)
    {
        _reload?.Stop();
        if (Designing || Current.BuiltIn) return;
        if (Read(FileOf(Current.Id)) is { } t) Apply(t);
    }

    // ---------- contrast ----------

    /// <summary>A see-through colour as it looks over <paramref name="under"/>.</summary>
    public static Color Over(Color c, Color under)
    {
        var a = c.A / 255.0;
        byte Mix(byte top, byte bottom) => (byte)Math.Round(top * a + bottom * (1 - a));
        return Color.FromRgb(Mix(c.R, under.R), Mix(c.G, under.G), Mix(c.B, under.B));
    }

    static double Luminance(Color c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>How far apart two colours are to the eye, from 1 (the same) to 21 (black on white); text wants 4.5 or more.</summary>
    public static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The pairs a theme should keep readable, with the least each needs.</summary>
    public static readonly (string Fore, string Back, double Need, string What)[] Pairs =
    {
        ("Fg", "Bg", 4.5, "text on the page"),
        ("Fg", "BgAlt", 4.5, "text on panels"),
        ("FgDim", "Bg", 3.0, "quieter text on the page"),
        ("FgFaint", "Bg", 1.8, "faint text on the page"),
        ("Accent", "Bg", 2.5, "the accent on the page"),
        ("Accent", "BgAlt", 2.5, "the accent on panels"),
        ("NowFg", "NowBg", 4.5, "now playing text"),
        ("NowFgDim", "NowBg", 3.0, "now playing quieter text"),
        ("NowHighlight", "NowBg", 2.5, "the playing song in the queue"),
    };

    /// <summary>Pairs in a theme that are hard to read, worst first.</summary>
    public static List<(string What, double Ratio, double Need)> Problems(Theme t)
    {
        var found = new List<(string, double, double)>();
        foreach (var (fore, back, need, what) in Pairs)
        {
            var bg = t.Get(back);
            if (back == "BgAlt") bg = Over(bg, t.Get("Bg"));
            if (back == "NowBgAlt") bg = Over(bg, t.Get("NowBg"));
            var ratio = Contrast(Over(t.Get(fore), bg), Over(bg, Colors.Black));
            if (ratio < need) found.Add((what, ratio, need));
        }
        return found.OrderBy(p => p.Item2 / p.Item3).ToList();
    }
}

/// <summary>Something that draws itself with the theme's colours and needs telling when they change.</summary>
public interface IThemed
{
    void ThemeChanged();
}
