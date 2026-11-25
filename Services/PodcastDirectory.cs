using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>A podcast found in a directory, before it is subscribed to.</summary>
public record PodcastHit(string Title, string Author, string Art, string FeedUrl, string Detail, string AppleId = "")
{
    public string Subtitle => string.Join("  ·  ", new[] { Author, Detail }.Where(s => s != ""));
}

/// <summary>
/// Places to find podcasts: Apple Podcasts (charts by genre, and search), fyyd (an open podcast index: what is new
/// and noticed, and search), and lists you add yourself (an OPML file or address, the format podcast apps export
/// their subscriptions in). Google Podcasts is not here because Google closed it in 2024. Only what you search
/// for, or the genre you open, is sent.
/// </summary>
public static class PodcastDirectory
{
    public record Source(string Id, string Name, string Note, bool HasGenres, bool Yours = false);

    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (desktop media player)");
        return c;
    }

    public static readonly (string Name, int Id)[] AppleGenres =
    {
        ("Arts", 1301), ("Business", 1321), ("Comedy", 1303), ("Education", 1304), ("Fiction", 1483), ("Government", 1511), ("Health & Fitness", 1512),
        ("History", 1487), ("Kids & Family", 1305), ("Leisure", 1502), ("Music", 1310), ("News", 1489), ("Religion & Spirituality", 1314), ("Science", 1533),
        ("Society & Culture", 1324), ("Sports", 1545), ("Technology", 1318), ("True Crime", 1488), ("TV & Film", 1309)
    };

    /// <summary>The built-in sources, then the ones you added.</summary>
    public static List<Source> Sources()
    {
        var list = new List<Source>
        {
            new("apple", "Apple Podcasts", "Apple's charts for your country, by genre, and its search.", true),
            new("fyyd", "fyyd", "An open podcast index (fyyd.de): podcasts being noticed right now, and search.", false)
        };
        for (var i = 0; i < App.Settings.PodcastSources.Count; i++)
            list.Add(new Source("own:" + i, App.Settings.PodcastSources[i].Name, App.Settings.PodcastSources[i].Url, false, true));
        return list;
    }

    /// <summary>Your country as Apple's charts name it ("us", "gb", "de"…).</summary>
    public static string Country
    {
        get
        {
            try { var c = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant(); return c.Length == 2 ? c : "us"; }
            catch { return "us"; }
        }
    }

    static async Task<JsonElement> JsonAsync(string url) => J.Parse(await Http.GetStringAsync(url));

    /// <summary>What a source shows when it is opened: its chart or list (for Apple, optionally one genre's chart).</summary>
    public static async Task<List<PodcastHit>> TopAsync(string source, int genre = 0)
    {
        if (source == "apple")
            return ParseAppleTop(await JsonAsync($"https://itunes.apple.com/{Country}/rss/toppodcasts/limit=60{(genre > 0 ? $"/genre={genre}" : "")}/json"));
        if (source == "fyyd") return ParseFyyd(await JsonAsync("https://api.fyyd.de/0.2/feature/podcast/hot?count=60"));
        return await OwnAsync(source);
    }

    public static async Task<List<PodcastHit>> SearchAsync(string source, string text)
    {
        text = text.Trim();
        if (source == "apple") return ParseAppleSearch(await JsonAsync($"https://itunes.apple.com/search?media=podcast&limit=60&country={Country}&term={Uri.EscapeDataString(text)}"));
        if (source == "fyyd") return ParseFyyd(await JsonAsync($"https://api.fyyd.de/0.2/search/podcast?count=50&title={Uri.EscapeDataString(text)}"));
        return (await OwnAsync(source)).Where(h => h.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || h.Author.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The feed address of a hit. Apple's charts only give an id, which is looked up here.</summary>
    public static async Task<string> FeedAsync(PodcastHit hit)
    {
        if (hit.FeedUrl != "") return hit.FeedUrl;
        if (hit.AppleId == "") throw new Exception("This entry has no feed address.");
        var found = ParseAppleSearch(await JsonAsync($"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(hit.AppleId)}"));
        return found.FirstOrDefault(h => h.FeedUrl != "")?.FeedUrl ?? throw new Exception("Apple gives no feed address for this podcast (it may only be available in Apple's own app).");
    }

    static async Task<List<PodcastHit>> OwnAsync(string source)
    {
        if (!source.StartsWith("own:") || !int.TryParse(source[4..], out var i) || i < 0 || i >= App.Settings.PodcastSources.Count) return new();
        var url = App.Settings.PodcastSources[i].Url.Trim();
        var text = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await Http.GetStringAsync(url) : await File.ReadAllTextAsync(url);
        return ParseOpml(text);
    }

    // ---------- reading what the sources send ----------

    public static List<PodcastHit> ParseAppleSearch(JsonElement r) => r.A("results").Where(x => x.S("collectionName") != "")
        .Select(x => new PodcastHit(x.S("collectionName"), x.S("artistName"), x.S("artworkUrl600") is { Length: > 0 } big ? big : x.S("artworkUrl100"), x.S("feedUrl"),
            string.Join("  ·  ", new[] { x.S("primaryGenreName"), x.L("trackCount") > 0 ? $"{x.L("trackCount")} episodes" : "" }.Where(s => s != "")), x.S("collectionId"))).ToList();

    public static List<PodcastHit> ParseAppleTop(JsonElement r) => r.O("feed").A("entry").Select(x => new PodcastHit(
            x.O("im:name").S("label"), x.O("im:artist").S("label"), x.A("im:image").Select(i => i.S("label")).LastOrDefault(u => u != "") ?? "", "",
            x.O("category").O("attributes").S("label"), x.O("id").O("attributes").S("im:id")))
        .Where(h => h.Title != "" && h.AppleId != "").ToList();

    public static List<PodcastHit> ParseFyyd(JsonElement r) => r.A("data").Where(x => x.S("xmlURL").StartsWith("http", StringComparison.OrdinalIgnoreCase))
        .Select(x => new PodcastHit(x.S("title"), x.S("author"), x.S("smallImageURL") is { Length: > 0 } small ? small : x.S("imgURL"), x.S("xmlURL"),
            string.Join("  ·  ", new[] { x.S("language").ToUpperInvariant(), x.L("episode_count") > 0 ? $"{x.L("episode_count")} episodes" : "" }.Where(s => s != "")))).ToList();

    /// <summary>The podcasts in an OPML list (what "export subscriptions" gives in most podcast apps).</summary>
    public static List<PodcastHit> ParseOpml(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "outline").Select(e =>
                {
                    string At(string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
                    var title = At("title") is { Length: > 0 } t ? t : At("text");
                    return new PodcastHit(title, "", At("imageUrl"), At("xmlUrl"), At("description").Length > 90 ? At("description")[..90] + "…" : At("description"));
                })
                .Where(h => h.FeedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)).GroupBy(h => h.FeedUrl).Select(g => g.First())
                .Select(h => h.Title == "" ? h with { Title = new Uri(h.FeedUrl).Host } : h).ToList();
        }
        catch { throw new Exception("That is not an OPML list of podcasts."); }
    }

    /// <summary>Your subscriptions as an OPML list, for another app or another PC.</summary>
    public static string ToOpml(IEnumerable<Podcast> podcasts) => new XDocument(new XElement("opml", new XAttribute("version", "2.0"),
        new XElement("head", new XElement("title", "Phoenix podcasts")),
        new XElement("body", podcasts.Select(p => new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", p.Title), new XAttribute("title", p.Title), new XAttribute("xmlUrl", p.FeedUrl)))))).ToString();
}

/// <summary>
/// Windows' own player (which plays podcasts and videos here) refuses https addresses, and nearly every podcast is
/// https now. This hands it the same stream over plain http from this PC itself: a request to 127.0.0.1 is passed
/// on to the real address, part by part, so an episode starts at once and can be skipped around in without being
/// downloaded whole first. Nothing outside this PC can reach it.
/// </summary>
public static class LocalProxy
{
    static System.Net.HttpListener? _listener;
    static int _port;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Targets = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Tokens = new();
    static readonly object Gate = new();
    static readonly HttpClient Upstream = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 20 }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Requests passed on since the app started (for the self-test).</summary>
    public static int Served;

    static bool Start()
    {
        lock (Gate)
        {
            if (_listener != null) return true;
            for (var tries = 0; tries < 30; tries++)
            {
                var port = 43000 + Random.Shared.Next(6000);
                var l = new System.Net.HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { l.Start(); } catch { continue; }
                _listener = l; _port = port;
                _ = Task.Run(async () =>
                {
                    while (l.IsListening)
                    {
                        System.Net.HttpListenerContext c;
                        try { c = await l.GetContextAsync(); } catch { return; }
                        _ = Task.Run(() => HandleAsync(c));
                    }
                });
                return true;
            }
            return false;
        }
    }

    /// <summary>The address on this PC that stands for a stream elsewhere; the address itself if the proxy cannot start.</summary>
    public static string Url(string target)
    {
        if (!Start()) return target;
        var token = Tokens.GetOrAdd(target, _ => Guid.NewGuid().ToString("N"));
        Targets[token] = target;
        var ext = "";
        try { ext = Path.GetExtension(new Uri(target).AbsolutePath).ToLowerInvariant(); } catch { }
        return $"http://127.0.0.1:{_port}/p/{token}/media{(ext.Length is > 1 and <= 5 ? ext : "")}";
    }

    static async Task HandleAsync(System.Net.HttpListenerContext c)
    {
        try
        {
            var parts = c.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "p" || !Targets.TryGetValue(parts[1], out var target)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
            Interlocked.Increment(ref Served);
            using var ask = new HttpRequestMessage(c.Request.HttpMethod == "HEAD" ? HttpMethod.Head : HttpMethod.Get, target);
            ask.Headers.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");
            if (c.Request.Headers["Range"] is { Length: > 0 } range) ask.Headers.TryAddWithoutValidation("Range", range);
            using var answer = await Upstream.SendAsync(ask, HttpCompletionOption.ResponseHeadersRead);
            c.Response.StatusCode = (int)answer.StatusCode;
            if (answer.Content.Headers.ContentType is { } type) c.Response.ContentType = type.ToString();
            if (answer.Content.Headers.ContentLength is { } length) c.Response.ContentLength64 = length; else c.Response.SendChunked = true;
            if (answer.Content.Headers.ContentRange is { } contentRange) c.Response.AddHeader("Content-Range", contentRange.ToString());
            c.Response.AddHeader("Accept-Ranges", answer.Headers.AcceptRanges.Count > 0 ? string.Join(", ", answer.Headers.AcceptRanges) : "bytes");
            if (c.Request.HttpMethod != "HEAD")
            {
                await using var from = await answer.Content.ReadAsStreamAsync();
                await from.CopyToAsync(c.Response.OutputStream, 1 << 16);
            }
            c.Response.Close();
        }
        catch { try { c.Response.Abort(); } catch { } }   // the player stopped reading (it does when you skip): nothing to do
    }
}
