using System.Net.Http;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Translates song, artist and album names so they can be shown beside the originals. Off until switched on in
/// settings. Each name is asked for once and remembered in the library; names are sent to the chosen service.
/// </summary>
public static class Translator
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("sv", "Swedish"),
        ("pl", "Polish"), ("ru", "Russian"), ("uk", "Ukrainian"), ("tr", "Turkish"), ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"), ("ar", "Arabic"), ("hi", "Hindi")
    };
    public static readonly (string Id, string Name, string? KeyHelp)[] Providers =
    {
        ("local", "On this PC (offline; needs a language pack for each language)", null),
        ("mymemory", "MyMemory (free, no account, a few thousand letters a day)", null),
        ("deepl", "DeepL", "API key from deepl.com/pro-api (the free plan works)"),
        ("libre", "LibreTranslate", "your server's address goes in the box below; key only if the server asks for one")
    };
    static readonly HashSet<string> LatinTargets = new() { "en", "es", "fr", "de", "it", "pt", "nl", "sv", "pl", "tr" };

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Dictionary<string, string> Known = new();              // text -> translation ("" = nothing useful)
    static readonly Dictionary<string, TaskCompletionSource<string>> Waiting = new();
    static readonly LinkedList<string> Queue = new();
    static string _loadedFor = "";
    static bool _working, _stopped;
    static Database? _db;

    public static bool On => App.Settings.TranslateOn;
    /// <summary>Why translating stopped for now (a daily limit, a rejected key), or "".</summary>
    public static string Trouble { get; private set; } = "";
    public static event Action? TroubleChanged;

    public static void Attach(Database db) { _db = db; _loadedFor = ""; }

    static void EnsureLoaded()
    {
        var lang = App.Settings.TranslateTo;
        if (_loadedFor == lang || _db == null) return;
        lock (Known)
        {
            Known.Clear();
            foreach (var (source, result) in _db.LoadTranslations(lang)) Known[source] = result;
            _loadedFor = lang;
        }
    }

    /// <summary>Whether a name is worth sending: it has letters, and is not plainly in the target language's own script already.</summary>
    public static bool Needs(string text)
    {
        if (!On || string.IsNullOrWhiteSpace(text) || text.Length > 200 || text is "Unknown Artist" or "Unknown Album") return false;
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return false;
        var foreignScript = letters.Any(c => c > 'ɏ');   // beyond the Latin alphabets
        if (App.Settings.TranslateProvider == "local") return foreignScript && LocalTranslator.Detect(text) != App.Settings.TranslateTo;
        if (LatinTargets.Contains(App.Settings.TranslateTo)) return foreignScript || App.Settings.TranslateLatin;
        return true;
    }

    /// <summary>The translation if it is already known ("" when there is none or it is not known yet).</summary>
    public static string Lookup(string text)
    {
        if (!On) return "";
        EnsureLoaded();
        lock (Known) return Known.GetValueOrDefault(text, "");
    }

    /// <summary>The translation of a name, asking the service if it is not known yet. "" when there is nothing useful to show.</summary>
    public static Task<string> GetAsync(string text)
    {
        if (!Needs(text)) return Task.FromResult("");
        EnsureLoaded();
        lock (Known)
        {
            if (Known.TryGetValue(text, out var hit)) return Task.FromResult(hit);
            if (_stopped) return Task.FromResult("");
            if (Waiting.TryGetValue(text, out var pending)) return pending.Task;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiting[text] = tcs;
            Queue.AddFirst(text);   // what was asked for last is on screen now: do it first
            while (Queue.Count > 400) { var old = Queue.Last!.Value; Queue.RemoveLast(); if (Waiting.Remove(old, out var dropped)) dropped.TrySetResult(""); }
            if (!_working) { _working = true; _ = Task.Run(WorkAsync); }
            return tcs.Task;
        }
    }

    static async Task WorkAsync()
    {
        while (true)
        {
            string text; string lang;
            lock (Known)
            {
                if (Queue.Count == 0 || _stopped)
                {
                    _working = false;
                    foreach (var w in Waiting.Values) w.TrySetResult("");
                    Waiting.Clear(); Queue.Clear();
                    return;
                }
                text = Queue.First!.Value; Queue.RemoveFirst();
                lang = App.Settings.TranslateTo;
            }
            var result = "";
            var remember = true;
            try
            {
                result = Useful(text, await AskAsync(text, lang));
            }
            catch (NoAnswerException) { remember = false; }
            catch (Exception ex)
            {
                remember = false;
                // a limit or a bad key will not get better by asking again right away
                if (ex is LimitException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or (System.Net.HttpStatusCode)429 or (System.Net.HttpStatusCode)456 })
                {
                    lock (Known) _stopped = true;
                    Trouble = ex is LimitException ? ex.Message : "the translation service refused the request (check its key, or its limit has been reached)";
                    App.Current?.Dispatcher.BeginInvoke(() => TroubleChanged?.Invoke());
                }
            }
            lock (Known)
            {
                if (remember && lang == _loadedFor) { Known[text] = result; try { _db?.SaveTranslation(lang, text, result); } catch { } }
                if (Waiting.Remove(text, out var tcs)) tcs.TrySetResult(result);
            }
            await Task.Delay(App.Settings.TranslateProvider switch { "mymemory" => 400, "local" => 0, _ => 120 });
        }
    }

    /// <summary>A translation worth showing: not empty, and not just the same words back.</summary>
    public static string Useful(string source, string translated)
    {
        translated = translated.Trim();
        static string Bare(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return translated == "" || Bare(translated) == Bare(source) ? "" : translated;
    }

    sealed class LimitException(string message) : Exception(message);
    /// <summary>Nothing can be said about this name now (no language pack for it yet): do not remember that as an answer.</summary>
    sealed class NoAnswerException() : Exception("no answer");

    static async Task<string> AskAsync(string text, string lang)
    {
        switch (App.Settings.TranslateProvider)
        {
            case "local":
            {
                var from = LocalTranslator.Detect(text);
                if (!LocalTranslator.CanTranslate(from, lang)) throw new NoAnswerException();
                return await LocalTranslator.TranslateAsync(text, from, lang);
            }
            case "deepl":
            {
                var key = Metadata.Key("deepl");
                if (key == "") throw new LimitException("DeepL needs its key (settings → translation)");
                var host = key.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";
                using var req = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text, ["target_lang"] = lang.ToUpperInvariant() })
                };
                req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
                using var res = await Http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                return ParseDeepL(J.Parse(await res.Content.ReadAsStringAsync()));
            }
            case "libre":
            {
                var url = App.Settings.LibreTranslateUrl.Trim().TrimEnd('/');
                if (url == "") throw new LimitException("LibreTranslate needs its server address (settings → translation)");
                var body = JsonSerializer.Serialize(new { q = text, source = "auto", target = lang, format = "text", api_key = Metadata.Key("libre") });
                using var res = await Http.PostAsync(url + "/translate", new StringContent(body, Encoding.UTF8, "application/json"));
                res.EnsureSuccessStatusCode();
                return J.Parse(await res.Content.ReadAsStringAsync()).S("translatedText");
            }
            default:
            {
                var r = J.Parse(await Http.GetStringAsync($"https://api.mymemory.translated.net/get?langpair=autodetect%7C{lang}&q={Uri.EscapeDataString(text)}"));
                return ParseMyMemory(r);
            }
        }
    }

    public static string ParseDeepL(JsonElement r) => r.A("translations").Select(t => t.S("text")).FirstOrDefault() ?? "";

    public static string ParseMyMemory(JsonElement r)
    {
        var text = r.O("responseData").S("translatedText");
        if (r.S("responseStatus") is "429" or "403" || text.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            throw new LimitException("MyMemory's free daily limit has been reached; translating carries on tomorrow, or choose another service");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    /// <summary>Settings changed: forget what is waiting and allow asking again.</summary>
    public static void Reset()
    {
        lock (Known) { _stopped = false; _loadedFor = ""; }
        Trouble = "";
        TroubleChanged?.Invoke();
    }
}
