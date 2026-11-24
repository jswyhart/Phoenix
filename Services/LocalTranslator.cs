using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Halcyon.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Halcyon.Services;

/// <summary>
/// Translation done on this PC with nothing sent anywhere: Mozilla's Bergamot engine (the one Firefox uses for
/// offline translation), running in a hidden browser view, with language packs you download once. Packs come from
/// Mozilla's Firefox Translations models and are kept in a folder you can choose.
/// </summary>
public static class LocalTranslator
{
    const string Records = "https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models/records";
    const string Attachments = "https://firefox-settings-attachments.cdn.mozilla.net/";
    const string HostName = "phoenix-translate.example";

    public record PackFile(string Part, string Name, long Size, string Hash, string Location);
    public record Pack(string From, string To, string Version, List<PackFile> Files)
    {
        public string Key => From + To;
        public long Size => Files.Sum(f => f.Size);
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static List<Pack>? _catalogue;
    static WebView2? _view;
    static Task? _starting;
    static int _serial;
    static readonly Dictionary<int, TaskCompletionSource<string>> Pending = new();

    /// <summary>The folder the engine and the language packs are kept in: the one chosen in settings, or "translation" in the data folder.</summary>
    public static string Folder => App.Settings.TranslationFolder.Trim() is { Length: > 0 } chosen ? chosen : Path.Combine(AppPaths.DataDir, "translation");

    // ---------- which language is this? ----------

    /// <summary>
    /// The language a name is written in, told from its script. Names in Latin letters cannot be told apart this way
    /// (Spanish, French, German all look alike to it), so they give "".
    /// </summary>
    public static string Detect(string text)
    {
        int kana = 0, han = 0, hangul = 0, cyrillic = 0, arabic = 0, hebrew = 0, greek = 0, thai = 0, devanagari = 0;
        var ukrainian = false;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ') kana++;
            else if (c is >= '一' and <= '鿿') han++;
            else if (c is >= '가' and <= '힯') hangul++;
            else if (c is >= 'Ѐ' and <= 'ӿ') { cyrillic++; if ("іїєґІЇЄҐ".Contains(c)) ukrainian = true; }
            else if (c is >= '؀' and <= 'ۿ') arabic++;
            else if (c is >= '֐' and <= '׿') hebrew++;
            else if (c is >= 'Ͱ' and <= 'Ͽ') greek++;
            else if (c is >= '฀' and <= '๿') thai++;
            else if (c is >= 'ऀ' and <= 'ॿ') devanagari++;
        }
        if (kana > 0) return "ja";
        if (hangul > 0) return "ko";
        if (han > 0) return "zh";
        if (cyrillic > 0) return ukrainian ? "uk" : "ru";
        if (arabic > 0) return "ar";
        if (hebrew > 0) return "he";
        if (greek > 0) return "el";
        if (thai > 0) return "th";
        if (devanagari > 0) return "hi";
        return "";
    }

    public static string LanguageName(string code)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; } catch { return code; }
    }

    // ---------- the catalogue of packs ----------

    /// <summary>Every pack Mozilla publishes, newest version of each.</summary>
    public static async Task<List<Pack>> CatalogueAsync()
    {
        if (_catalogue != null) return _catalogue;
        return _catalogue = ParseCatalogue(J.Parse(await Http.GetStringAsync(Records)));
    }

    public static List<Pack> ParseCatalogue(JsonElement root)
    {
        static Version Ver(string v) => Version.TryParse(v.Contains('.') ? v : v + ".0", out var parsed) ? parsed : new Version(0, 0);
        return root.A("data")
            .Select(r => (from: r.S("fromLang"), to: r.S("toLang"), version: r.S("version"), type: r.S("fileType"), file: new PackFile(r.S("fileType"), r.O("attachment").S("filename"), r.O("attachment").L("size"), r.O("attachment").S("hash"), r.O("attachment").S("location"))))
            .Where(x => x.from.Length >= 2 && x.to.Length >= 2 && x.file.Location != "")
            .GroupBy(x => (from: x.from[..2], to: x.to[..2]))
            .Select(g =>
            {
                // the newest version that has everything the engine needs
                foreach (var version in g.GroupBy(x => x.version).OrderByDescending(v => Ver(v.Key)))
                {
                    var files = version.GroupBy(x => x.type).Select(t => t.First().file).ToList();
                    bool Has(string part) => files.Any(f => f.Part == part);
                    if (Has("model") && Has("lex") && (Has("vocab") || (Has("srcvocab") && Has("trgvocab"))))
                        return new Pack(g.Key.from, g.Key.to, version.Key, files.Where(f => f.Part is "model" or "lex" or "vocab" or "srcvocab" or "trgvocab").ToList());
                }
                return null;
            })
            .Where(p => p != null).Select(p => p!).OrderBy(p => p.From).ThenBy(p => p.To).ToList();
    }

    /// <summary>The packs needed to go from one language to another: a direct one, or two through English.</summary>
    public static List<Pack> Route(List<Pack> catalogue, string from, string to)
    {
        if (from == to) return new();
        if (catalogue.FirstOrDefault(p => p.From == from && p.To == to) is { } direct) return new() { direct };
        var first = catalogue.FirstOrDefault(p => p.From == from && p.To == "en");
        var second = catalogue.FirstOrDefault(p => p.From == "en" && p.To == to);
        return first != null && second != null ? new() { first, second } : new();
    }

    public static bool Installed(Pack p) => File.Exists(Path.Combine(Folder, p.Key, "pack.json")) && p.Files.All(f => File.Exists(Path.Combine(Folder, p.Key, f.Name)));
    public static bool Installed(string from, string to) => File.Exists(Path.Combine(Folder, from + to, "pack.json"));

    /// <summary>Pairs that are installed, read from the folder (so it works without the catalogue, offline).</summary>
    public static List<(string From, string To)> InstalledPairs()
    {
        try
        {
            return Directory.EnumerateDirectories(Folder).Where(d => File.Exists(Path.Combine(d, "pack.json")))
                .Select(d => Path.GetFileName(d)!).Where(k => k.Length == 4).Select(k => (k[..2], k[2..])).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Whether text in one language can be translated to another with what is installed.</summary>
    public static bool CanTranslate(string from, string to) =>
        from != "" && from != to && (Installed(from, to) || (Installed(from, "en") && Installed("en", to)));

    // ---------- downloading and removing ----------

    public static async Task DownloadAsync(Pack p, Action<string>? progress, CancellationToken stop = default)
    {
        var dir = Path.Combine(Folder, p.Key);
        Directory.CreateDirectory(dir);
        long done = 0;
        foreach (var f in p.Files)
        {
            var file = Path.Combine(dir, f.Name);
            if (!(File.Exists(file) && new FileInfo(file).Length == f.Size))
            {
                var part = file + ".part";
                using (var res = await Http.GetAsync(Attachments + f.Location, HttpCompletionOption.ResponseHeadersRead, stop))
                {
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync(stop);
                    await using var dst = File.Create(part);
                    var buffer = new byte[1 << 16];
                    int n;
                    long sofar = 0, reported = 0;
                    while ((n = await src.ReadAsync(buffer, stop)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), stop);
                        sofar += n;
                        if (sofar - reported > 2 << 20) { reported = sofar; progress?.Invoke($"downloading {LanguageName(p.From)} → {LanguageName(p.To)}: {(done + sofar) >> 20} of {p.Size >> 20} MB"); }
                    }
                }
                // the file must be exactly what Mozilla published
                await using (var check = File.OpenRead(part))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, stop));
                    if (f.Hash != "" && !string.Equals(hash, f.Hash, StringComparison.OrdinalIgnoreCase)) { check.Close(); File.Delete(part); throw new Exception($"{f.Name} did not arrive intact; try again."); }
                }
                File.Move(part, file, true);
            }
            done += f.Size;
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "pack.json"), JsonSerializer.Serialize(p), stop);
        WriteRegistry();
        await RestartAsync();
    }

    public static async Task RemoveAsync(string from, string to)
    {
        await StopAsync();   // the engine may have the files open
        try { Directory.Delete(Path.Combine(Folder, from + to), true); } catch { }
        WriteRegistry();
    }

    /// <summary>Writes the list of installed packs in the form the engine reads.</summary>
    public static void WriteRegistry()
    {
        var registry = new Dictionary<string, Dictionary<string, object>>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Folder))
            {
                var manifest = Path.Combine(dir, "pack.json");
                if (!File.Exists(manifest)) continue;
                var pack = JsonSerializer.Deserialize<Pack>(File.ReadAllText(manifest));
                if (pack == null) continue;
                registry[pack.Key] = pack.Files.ToDictionary(f => f.Part, f => (object)new { name = $"/{pack.Key}/{f.Name}", size = f.Size, expectedSha256Hash = "" });
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "registry.json"), JsonSerializer.Serialize(registry));
        }
        catch { }
    }

    /// <summary>How much room the installed packs take.</summary>
    public static long Size()
    {
        try { return InstalledPairs().Sum(p => Directory.EnumerateFiles(Path.Combine(Folder, p.From + p.To)).Sum(f => new FileInfo(f).Length)); } catch { return 0; }
    }

    /// <summary>Moves the engine and every language pack to another folder ("" = back inside the data folder).</summary>
    public static async Task MoveToAsync(string target)
    {
        var from = Folder;
        var to = target.Trim() == "" ? Path.Combine(AppPaths.DataDir, "translation") : Path.GetFullPath(target.Trim());
        if (string.Equals(from.TrimEnd('\\'), to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        if (to.StartsWith(from.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Choose a folder that is not inside the current one.");
        await StopAsync();
        await Task.Run(() =>
        {
            Directory.CreateDirectory(to);
            if (!Directory.Exists(from)) return;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
            }
            try { Directory.Delete(from, true); } catch { }
        });
        App.Settings.TranslationFolder = target.Trim() == "" ? "" : to;
        if (!App.SmokeMode) App.Settings.Save();
        WriteRegistry();
    }

    // ---------- the engine ----------

    static void CopyEngine()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "translate-engine");
        if (!Directory.Exists(source)) throw new Exception("The offline translation engine is missing from this copy of Phoenix.");
        Directory.CreateDirectory(Path.Combine(Folder, "engine", "worker"));
        void Put(string from, string to)
        {
            var dest = Path.Combine(Folder, to);
            if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(from).Length) File.Copy(from, dest, true);
        }
        Put(Path.Combine(source, "host.html"), "host.html");
        Put(Path.Combine(source, "translator.js"), Path.Combine("engine", "translator.js"));
        foreach (var f in Directory.GetFiles(Path.Combine(source, "worker"))) Put(f, Path.Combine("engine", "worker", Path.GetFileName(f)));
    }

    static Task EnsureStartedAsync() => _starting ??= StartAsync();

    static async Task StartAsync()
    {
        CopyEngine();
        if (!File.Exists(Path.Combine(Folder, "registry.json"))) WriteRegistry();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        App.Win.AddHidden(view);
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "webview"));
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(HostName, Folder, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var m = doc.RootElement;
                if (m.TryGetProperty("ready", out JsonElement _)) { ready.TrySetResult(); return; }
                var id = m.GetProperty("id").GetInt32();
                TaskCompletionSource<string>? tcs;
                lock (Pending) { if (!Pending.Remove(id, out tcs)) return; }
                if (m.TryGetProperty("error", out var err)) tcs.TrySetException(new Exception(err.GetString()));
                else tcs.TrySetResult(m.GetProperty("text").GetString() ?? "");
            }
            catch { }
        };
        core.Navigate($"https://{HostName}/host.html");
        _view = view;
        if (await Task.WhenAny(ready.Task, Task.Delay(30000)) != ready.Task) throw new Exception("The offline translation engine did not start.");
    }

    static async Task StopAsync()
    {
        var view = _view;
        _view = null;
        _starting = null;
        lock (Pending) { foreach (var p in Pending.Values) p.TrySetException(new Exception("stopped")); Pending.Clear(); }
        if (view == null) return;
        await App.Current.Dispatcher.InvokeAsync(() => { App.Win.RemoveHidden(view); view.Dispose(); });
        await Task.Delay(300);   // let the browser process let go of the pack files
    }

    /// <summary>After packs change: the engine reads the list of packs when it starts, so start it afresh next time.</summary>
    static Task RestartAsync() => StopAsync();

    /// <summary>Translates text between two languages with the installed packs. Runs entirely on this PC.</summary>
    public static async Task<string> TranslateAsync(string text, string from, string to)
    {
        if (!CanTranslate(from, to)) throw new InvalidOperationException($"no language pack for {LanguageName(from)}");
        return await await App.Current.Dispatcher.InvokeAsync(async () =>
        {
            await EnsureStartedAsync();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (Pending) { id = ++_serial; Pending[id] = tcs; }
            _view!.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, from, to, text }));
            // the first sentence after a start loads the pack, which takes a few seconds
            if (await Task.WhenAny(tcs.Task, Task.Delay(90000)) != tcs.Task) { lock (Pending) Pending.Remove(id); throw new TimeoutException("the offline translator took too long"); }
            return await tcs.Task;
        });
    }
}
