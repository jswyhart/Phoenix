using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Halcyon.Core;

// Features brought over from the Windows app: internet radio, podcast directories, the play log with stats and
// scrobbling, the update check, and backup and restore. Kept free of anything tied to one platform.

namespace Halcyon.Core
{
    /// <summary>A radio station you saved.</summary>
    public class Station
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Art { get; set; } = "";
        public string Tags { get; set; } = "";
        public string Country { get; set; } = "";
        public string Subtitle => Fmt.Join(Country, Tags);
    }

    /// <summary>A list of podcasts you added yourself to browse from: a name and the address of an OPML list.</summary>
    public class PodcastSource
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>One listen, as kept in the play log.</summary>
    public record PlayRow(long At, long TrackId, string Title, string Artist, string Album, long Ms, bool Counted);

    public partial class Database
    {
        bool _extrasReady;

        void Extras()
        {
            if (_extrasReady) return;
            _extrasReady = true;
            Exec("""
                CREATE TABLE IF NOT EXISTS plays(At INT, TrackId INT, Title TEXT, Artist TEXT, Album TEXT, Ms INT, Counted INT DEFAULT 1);
                CREATE INDEX IF NOT EXISTS plays_at ON plays(At);
                CREATE TABLE IF NOT EXISTS stations(Id INTEGER PRIMARY KEY, Name TEXT, Url TEXT UNIQUE, Art TEXT, Tags TEXT, Country TEXT);
                CREATE TABLE IF NOT EXISTS scrobbles(Id INTEGER PRIMARY KEY, Service TEXT, At INT, Artist TEXT, Title TEXT, Album TEXT, Ms INT);
                """);
        }

        static string Str(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        static long Num(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

        public void LogPlay(PlayRow p)
        {
            Extras();
            Exec("INSERT INTO plays(At,TrackId,Title,Artist,Album,Ms,Counted) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", p.At, p.TrackId, p.Title, p.Artist, p.Album, p.Ms, p.Counted ? 1 : 0);
        }

        public List<PlayRow> Plays(long from, long to)
        {
            Extras();
            return Query("SELECT At,TrackId,Title,Artist,Album,Ms,Counted FROM plays WHERE At>=$p0 AND At<$p1 ORDER BY At",
                r => new PlayRow(Num(r, 0), Num(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Num(r, 5), Num(r, 6) != 0), from, to);
        }

        public long FirstPlayAt()
        {
            Extras();
            return Query("SELECT MIN(At) FROM plays", r => Num(r, 0)).FirstOrDefault();
        }

        public List<Station> LoadStations()
        {
            Extras();
            return Query("SELECT Id,Name,Url,Art,Tags,Country FROM stations ORDER BY Name COLLATE NOCASE",
                r => new Station { Id = Num(r, 0), Name = Str(r, 1), Url = Str(r, 2), Art = Str(r, 3), Tags = Str(r, 4), Country = Str(r, 5) });
        }

        public void SaveStation(Station s)
        {
            Extras();
            s.Id = Query("INSERT INTO stations(Name,Url,Art,Tags,Country) VALUES($p0,$p1,$p2,$p3,$p4) " +
                         "ON CONFLICT(Url) DO UPDATE SET Name=excluded.Name,Art=excluded.Art,Tags=excluded.Tags,Country=excluded.Country RETURNING Id",
                r => Num(r, 0), s.Name, s.Url, s.Art, s.Tags, s.Country)[0];
        }

        public void DeleteStation(long id) { Extras(); Exec("DELETE FROM stations WHERE Id=$p0", id); }

        public record Pending(long Id, string Service, long At, string Artist, string Title, string Album, long Ms);

        public void QueueScrobble(string service, long at, string artist, string title, string album, long ms)
        {
            Extras();
            Exec("INSERT INTO scrobbles(Service,At,Artist,Title,Album,Ms) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", service, at, artist, title, album, ms);
        }

        public List<Pending> PendingScrobbles(string service)
        {
            Extras();
            return Query("SELECT Id,Service,At,Artist,Title,Album,Ms FROM scrobbles WHERE Service=$p0 ORDER BY At LIMIT 50",
                r => new Pending(Num(r, 0), Str(r, 1), Num(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Num(r, 6)), service);
        }

        public void DropScrobbles(IEnumerable<long> ids)
        {
            Extras();
            Batch(() => { foreach (var id in ids) Exec("DELETE FROM scrobbles WHERE Id=$p0", id); });
        }

        public void DropScrobbles(string service) { Extras(); Exec("DELETE FROM scrobbles WHERE Service=$p0", service); }

        /// <summary>Writes a clean, complete copy of the library to another file (works while the library is open).</summary>
        public void CopyTo(string file) => Exec($"VACUUM INTO '{file.Replace("'", "''")}'");
    }

    public partial class Library
    {
        List<Station>? _stations;
        public List<Station> Stations => _stations ??= Db.LoadStations();

        public void SaveStation(Station s)
        {
            Db.SaveStation(s);
            _stations = null;
            NotifyChanged();
        }

        public void RemoveStation(Station s)
        {
            Db.DeleteStation(s.Id);
            _stations = null;
            var pin = Pins.FirstOrDefault(p => p.Kind == "radio" && p.Key == s.Url);
            if (pin != null) { Pins.Remove(pin); Db.RemovePin(pin); }
            NotifyChanged();
        }
    }

    /// <summary>
    /// How a page is sorted and filtered: what to sort by, which way, what to show, and a typed filter. The first
    /// three are remembered per page in the settings.
    /// </summary>
    public class ViewChoice
    {
        readonly string _id;
        public string Sort { get; private set; } = "";
        public bool Descending { get; private set; }
        public string Show { get; private set; } = "";
        public string Text { get; set; } = "";

        public ViewChoice(string id)
        {
            _id = id;
            if (App.Settings.Views.TryGetValue(id, out var saved) && saved.Split('|') is { Length: 3 } parts)
            {
                Sort = parts[0]; Descending = parts[1] == "1"; Show = parts[2];
            }
        }

        public bool Active => Sort != "" || Descending || Show != "" || Text != "";

        /// <summary>Choosing the sort that is already chosen turns it round; another one starts forwards.</summary>
        public void SortBy(string sort)
        {
            if (sort == Sort) Descending = !Descending; else { Sort = sort; Descending = false; }
            Remember();
        }

        public void ShowOnly(string show) { Show = show; Remember(); }

        public void Clear() { Sort = Show = Text = ""; Descending = false; Remember(); }

        void Remember() => App.Settings.Views[_id] = $"{Sort}|{(Descending ? "1" : "0")}|{Show}";

        /// <summary>Whether the typed filter fits: every word has to be in one of the fields.</summary>
        public bool Matches(params string?[] fields)
        {
            if (Text.Trim() == "") return true;
            foreach (var word in Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!fields.Any(f => f != null && f.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        /// <summary>Puts a list in the chosen order; <paramref name="keys"/> says how to order for each sort.</summary>
        public List<T> Order<T>(IEnumerable<T> items, Func<string, Func<IEnumerable<T>, IEnumerable<T>>?> keys)
        {
            var list = (keys(Sort)?.Invoke(items) ?? items).ToList();
            if (Descending) list.Reverse();
            return list;
        }
    }
}

namespace Halcyon.Services
{
    /// <summary>
    /// Internet radio: looking stations up in Radio Browser (radio-browser.info), a free, community-kept directory.
    /// Only what you search for, or the genre or country you open, is sent to it.
    /// </summary>
    public static class Radio
    {
        static readonly string[] Hosts = { "de1.api.radio-browser.info", "nl1.api.radio-browser.info", "at1.api.radio-browser.info" };
        static readonly HttpClient Http = Create();
        static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (media player)");
            return c;
        }

        public record Group(string Name, string Key, int Count);

        static async Task<JsonElement> AskAsync(string path)
        {
            Exception? last = null;
            foreach (var host in Hosts)
            {
                try { return JsonDocument.Parse(await Http.GetStringAsync($"https://{host}/json/{path}")).RootElement; }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new Exception("the station directory can't be reached");
        }

        public static async Task<List<Station>> SearchAsync(string text) =>
            Parse(await AskAsync($"stations/search?name={Uri.EscapeDataString(text.Trim())}&limit=60&hidebroken=true&order=clickcount&reverse=true"));
        public static async Task<List<Station>> PopularAsync() => Parse(await AskAsync("stations/topclick/40?hidebroken=true"));
        public static async Task<List<Group>> GenresAsync() => ParseGenres(await AskAsync("tags?order=stationcount&reverse=true&hidebroken=true&limit=150"));
        public static async Task<List<Group>> CountriesAsync() => ParseCountries(await AskAsync("countries?order=name&hidebroken=true"));
        public static async Task<List<Station>> ByGenreAsync(string genre) =>
            Parse(await AskAsync($"stations/bytagexact/{Uri.EscapeDataString(genre)}?limit=100&hidebroken=true&order=clickcount&reverse=true"));
        public static async Task<List<Station>> ByCountryAsync(string code) =>
            Parse(await AskAsync($"stations/bycountrycodeexact/{Uri.EscapeDataString(code)}?limit=100&hidebroken=true&order=clickcount&reverse=true"));

        public static List<Station> Parse(JsonElement r)
        {
            if (r.ValueKind != JsonValueKind.Array) return new();
            return r.EnumerateArray().Select(x => new Station
            {
                Name = x.S("name").Trim(),
                Url = x.S("url_resolved") is { Length: > 0 } resolved ? resolved : x.S("url"),
                Art = x.S("favicon"),
                Tags = string.Join(", ", x.S("tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)),
                Country = x.S("countrycode")
            }).Where(s => s.Name != "" && s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).GroupBy(s => s.Url).Select(g => g.First()).ToList();
        }

        public static List<Group> ParseGenres(JsonElement r) => r.ValueKind != JsonValueKind.Array ? new()
            : r.EnumerateArray().Select(x => new Group(x.S("name").Trim(), x.S("name").Trim(), (int)x.L("stationcount"))).Where(g => g.Name.Length is > 1 and < 30 && g.Count >= 20).ToList();

        public static List<Group> ParseCountries(JsonElement r) => r.ValueKind != JsonValueKind.Array ? new()
            : r.EnumerateArray().Select(x => new Group(x.S("name").Trim(), x.S("iso_3166_1").Trim(), (int)x.L("stationcount")))
                .Where(g => g.Name != "" && g.Key.Length == 2 && g.Count > 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static Station? FromAddress(string name, string url)
        {
            url = url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
            return new Station { Name = name.Trim() == "" ? uri.Host : name.Trim(), Url = url };
        }
    }

    /// <summary>A podcast found in a directory, before it is subscribed to.</summary>
    public record PodcastHit(string Title, string Author, string Art, string FeedUrl, string Detail, string AppleId = "")
    {
        public string Subtitle => Fmt.Join(Author, Detail);
    }

    /// <summary>
    /// Places to find podcasts: Apple Podcasts (charts by genre, and search), fyyd (an open podcast index), and
    /// lists you add yourself (an OPML address). Google Podcasts is not here because Google closed it in 2024.
    /// </summary>
    public static class PodcastDirectory
    {
        public record Source(string Id, string Name, string Note, bool HasGenres, bool Yours = false);

        static readonly HttpClient Http = Create();
        static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (media player)");
            return c;
        }

        public static readonly (string Name, int Id)[] AppleGenres =
        {
            ("Arts", 1301), ("Business", 1321), ("Comedy", 1303), ("Education", 1304), ("Fiction", 1483), ("Government", 1511), ("Health & Fitness", 1512),
            ("History", 1487), ("Kids & Family", 1305), ("Leisure", 1502), ("Music", 1310), ("News", 1489), ("Religion & Spirituality", 1314), ("Science", 1533),
            ("Society & Culture", 1324), ("Sports", 1545), ("Technology", 1318), ("True Crime", 1488), ("TV & Film", 1309)
        };

        public static List<Source> Sources()
        {
            var list = new List<Source>
            {
                new("apple", "Apple Podcasts", "Apple's charts for your country, by genre, and its search", true),
                new("fyyd", "fyyd", "an open podcast index (fyyd.de): podcasts being noticed right now, and search", false)
            };
            for (var i = 0; i < App.Settings.PodcastSources.Count; i++)
                list.Add(new Source("own:" + i, App.Settings.PodcastSources[i].Name, App.Settings.PodcastSources[i].Url, false, true));
            return list;
        }

        public static string Country
        {
            get
            {
                try { var c = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant(); return c.Length == 2 && c.All(char.IsLetter) ? c : "us"; }
                catch { return "us"; }
            }
        }

        static async Task<JsonElement> JsonAsync(string url) => JsonDocument.Parse(await Http.GetStringAsync(url)).RootElement;

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

        public static List<PodcastHit> ParseAppleSearch(JsonElement r) => r.A("results").Where(x => x.S("collectionName") != "")
            .Select(x => new PodcastHit(x.S("collectionName"), x.S("artistName"), x.S("artworkUrl600") is { Length: > 0 } big ? big : x.S("artworkUrl100"), x.S("feedUrl"),
                Fmt.Join(x.S("primaryGenreName"), x.L("trackCount") > 0 ? $"{x.L("trackCount")} episodes" : ""), x.S("collectionId"))).ToList();

        public static List<PodcastHit> ParseAppleTop(JsonElement r) => r.O("feed").A("entry").Select(x => new PodcastHit(
                x.O("im:name").S("label"), x.O("im:artist").S("label"), x.A("im:image").Select(i => i.S("label")).LastOrDefault(u => u != "") ?? "", "",
                x.O("category").O("attributes").S("label"), x.O("id").O("attributes").S("im:id")))
            .Where(h => h.Title != "" && h.AppleId != "").ToList();

        public static List<PodcastHit> ParseFyyd(JsonElement r) => r.A("data").Where(x => x.S("xmlURL").StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Select(x => new PodcastHit(x.S("title"), x.S("author"), x.S("smallImageURL") is { Length: > 0 } small ? small : x.S("imgURL"), x.S("xmlURL"),
                Fmt.Join(x.S("language").ToUpperInvariant(), x.L("episode_count") > 0 ? $"{x.L("episode_count")} episodes" : ""))).ToList();

        public static List<PodcastHit> ParseOpml(string xml)
        {
            try
            {
                return XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "outline").Select(e =>
                    {
                        string At(string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
                        return new PodcastHit(At("title") is { Length: > 0 } t ? t : At("text"), "", At("imageUrl"), At("xmlUrl"), "");
                    })
                    .Where(h => h.FeedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)).GroupBy(h => h.FeedUrl).Select(g => g.First())
                    .Select(h => h.Title == "" ? h with { Title = new Uri(h.FeedUrl).Host } : h).ToList();
            }
            catch { throw new Exception("That is not an OPML list of podcasts."); }
        }

        public static string ToOpml(IEnumerable<Podcast> podcasts) => new XDocument(new XElement("opml", new XAttribute("version", "2.0"),
            new XElement("head", new XElement("title", "Phoenix podcasts")),
            new XElement("body", podcasts.Select(p => new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", p.Title), new XAttribute("title", p.Title), new XAttribute("xmlUrl", p.FeedUrl)))))).ToString();
    }

    /// <summary>
    /// What you listened to: every listen goes into the library's play log (which the stats page reads) and, if you
    /// switched it on, to Last.fm and ListenBrainz. Listens that could not be sent wait and go out later.
    /// </summary>
    public static class Listening
    {
        static readonly HttpClient Http = Create();
        static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (media player)");
            return c;
        }

        static Settings S => App.Settings;
        static string Key(string id) => S.Keys.GetValueOrDefault(id, "");

        public static bool LastFmReady => Key("lastfm") != "" && Key("lastfm_secret") != "" && Key("lastfm_session") != "";
        public static bool ListenBrainzReady => Key("listenbrainz") != "";
        public static string LastResult { get; private set; } = "";
        /// <summary>Set by tests so that nothing is sent anywhere.</summary>
        public static bool Offline;

        public static void Started(PlayItem item)
        {
            if (item.Track is not { } t || Offline) return;
            if (S.ScrobbleLastFm && LastFmReady) _ = Quiet(() => LastFmAsync("track.updateNowPlaying", Describe(t.Artist, t.Title, t.Album, t.DurationMs)));
            if (S.ScrobbleListenBrainz && ListenBrainzReady) _ = Quiet(() => ListenBrainzAsync("playing_now", new[] { (0L, t.Artist, t.Title, t.Album, t.DurationMs) }));
        }

        /// <summary>A song was left after some listening: logged after half a minute; a play (and scrobbled) if it finished or half of it or four minutes were heard.</summary>
        public static void Left(PlayItem item, long ms, bool finished)
        {
            if (item.Track is not { } t || item.Logged) return;
            var length = t.DurationMs > 0 ? t.DurationMs : item.DurationMs;
            if (finished && length > 0) ms = Math.Max(ms, length);
            if (length > 0) ms = Math.Min(ms, length);
            if (ms < 30_000 && !(finished && length > 0)) return;
            item.Logged = true;
            var counted = finished || ms >= Math.Min(length > 0 ? length / 2 : long.MaxValue, 240_000);
            var at = Fmt.Now - ms / 1000;
            App.Lib.Db.LogPlay(new PlayRow(at, t.Id, t.Title, t.Artist, t.Album, ms, counted));
            if (!counted || length is > 0 and <= 30_000) return;
            if (S.ScrobbleLastFm && LastFmReady) App.Lib.Db.QueueScrobble("lastfm", at, t.Artist, t.Title, t.Album, length);
            if (S.ScrobbleListenBrainz && ListenBrainzReady) App.Lib.Db.QueueScrobble("listenbrainz", at, t.Artist, t.Title, t.Album, length);
            if (!Offline) _ = FlushAsync();
        }

        static readonly SemaphoreSlim Gate = new(1, 1);

        public static async Task FlushAsync()
        {
            if (!await Gate.WaitAsync(0)) return;
            try
            {
                if (S.ScrobbleLastFm && LastFmReady)
                    foreach (var batch in App.Lib.Db.PendingScrobbles("lastfm").Chunk(40))
                    {
                        var p = new SortedDictionary<string, string>(StringComparer.Ordinal);
                        for (var i = 0; i < batch.Length; i++)
                        {
                            p[$"artist[{i}]"] = batch[i].Artist; p[$"track[{i}]"] = batch[i].Title; p[$"timestamp[{i}]"] = batch[i].At.ToString();
                            if (batch[i].Album != "") p[$"album[{i}]"] = batch[i].Album;
                            if (batch[i].Ms > 0) p[$"duration[{i}]"] = (batch[i].Ms / 1000).ToString();
                        }
                        await LastFmAsync("track.scrobble", p);
                        App.Lib.Db.DropScrobbles(batch.Select(b => b.Id));
                        LastResult = $"sent {batch.Length} listen{(batch.Length == 1 ? "" : "s")} to Last.fm at {DateTime.Now:t}";
                    }
                if (S.ScrobbleListenBrainz && ListenBrainzReady)
                    foreach (var batch in App.Lib.Db.PendingScrobbles("listenbrainz").Chunk(40))
                    {
                        await ListenBrainzAsync(batch.Length == 1 ? "single" : "import", batch.Select(b => (b.At, b.Artist, b.Title, b.Album, b.Ms)));
                        App.Lib.Db.DropScrobbles(batch.Select(b => b.Id));
                        LastResult = $"sent {batch.Length} listen{(batch.Length == 1 ? "" : "s")} to ListenBrainz at {DateTime.Now:t}";
                    }
            }
            catch (Exception ex) { LastResult = "couldn't send: " + ex.Message + " (will try again)"; }
            finally { Gate.Release(); }
        }

        static async Task Quiet(Func<Task> send) { try { await send(); } catch { } }

        static SortedDictionary<string, string> Describe(string artist, string title, string album, long ms)
        {
            var p = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["artist"] = artist, ["track"] = title };
            if (album != "") p["album"] = album;
            if (ms > 0) p["duration"] = (ms / 1000).ToString();
            return p;
        }

        public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
        {
            var text = string.Concat(parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + p.Value)) + secret;
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        static async Task<JsonElement> LastFmAsync(string method, SortedDictionary<string, string> p, bool session = true)
        {
            p["method"] = method;
            p["api_key"] = Key("lastfm");
            if (session) p["sk"] = Key("lastfm_session");
            p["api_sig"] = Sign(p, Key("lastfm_secret"));
            p["format"] = "json";
            using var response = await Http.PostAsync("https://ws.audioscrobbler.com/2.0/", new FormUrlEncodedContent(p));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            if (json.L("error") != 0) throw new Exception("Last.fm: " + json.S("message"));
            return json;
        }

        /// <summary>First half of signing in: returns (token, the Last.fm page to open so you can allow Phoenix).</summary>
        public static async Task<(string Token, string Url)> LastFmBeginAsync()
        {
            var json = await LastFmAsync("auth.getToken", new SortedDictionary<string, string>(StringComparer.Ordinal), session: false);
            var token = json.S("token");
            if (token == "") throw new Exception("Last.fm gave no token.");
            return (token, $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(Key("lastfm"))}&token={Uri.EscapeDataString(token)}");
        }

        /// <summary>Second half: once you allowed it, fetches the session. Returns your Last.fm name, or "" if not allowed yet.</summary>
        public static async Task<string> LastFmFinishAsync(string token)
        {
            JsonElement json;
            try { json = await LastFmAsync("auth.getSession", new SortedDictionary<string, string>(StringComparer.Ordinal) { ["token"] = token }, session: false); }
            catch (Exception ex) when (ex.Message.Contains("not been authorized", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)) { return ""; }
            var key = json.O("session").S("key");
            if (key == "") return "";
            S.Keys["lastfm_session"] = key;
            S.LastFmUser = json.O("session").S("name");
            S.Save();
            return S.LastFmUser;
        }

        public static void LastFmSignOut()
        {
            S.Keys.Remove("lastfm_session");
            S.LastFmUser = "";
            S.ScrobbleLastFm = false;
            App.Lib.Db.DropScrobbles("lastfm");
            S.Save();
        }

        public static string ListenBrainzBody(string type, IEnumerable<(long At, string Artist, string Title, string Album, long Ms)> listens) =>
            JsonSerializer.Serialize(new
            {
                listen_type = type,
                payload = listens.Select(l =>
                {
                    var info = new Dictionary<string, object> { ["media_player"] = "Phoenix", ["submission_client"] = "Phoenix" };
                    if (l.Ms > 0) info["duration_ms"] = l.Ms;
                    var meta = new Dictionary<string, object> { ["artist_name"] = l.Artist, ["track_name"] = l.Title, ["additional_info"] = info };
                    if (l.Album != "") meta["release_name"] = l.Album;
                    var entry = new Dictionary<string, object> { ["track_metadata"] = meta };
                    if (type != "playing_now") entry["listened_at"] = l.At;
                    return entry;
                }).ToList()
            });

        static async Task ListenBrainzAsync(string type, IEnumerable<(long, string, string, string, long)> listens)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.listenbrainz.org/1/submit-listens")
            {
                Content = new StringContent(ListenBrainzBody(type, listens), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Token " + Key("listenbrainz"));
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new Exception($"ListenBrainz answered {(int)response.StatusCode}");
        }

        public static async Task<string> ListenBrainzCheckAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
            request.Headers.TryAddWithoutValidation("Authorization", "Token " + token);
            using var response = await Http.SendAsync(request);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            return json.O("valid").ValueKind == JsonValueKind.True ? json.S("user_name") : "";
        }
    }

    /// <summary>Totals and top lists for the stats page.</summary>
    public class ListenStats
    {
        public record Top(string Name, string Detail, long Ms, int Plays, string Art, object? Item);

        public string Title = "";
        public long Ms;
        public int Plays, Artists, Albums, Songs;
        public List<Top> TopArtists = new(), TopAlbums = new(), TopSongs = new();
        public string Note = "";

        static string ArtistOf(Track t) => t.Artist != "" ? t.Artist : t.AlbumArtist;

        public static ListenStats FromLog(string title, long from, long to)
        {
            var lib = App.Lib;
            var byId = lib.Tracks.ToDictionary(t => t.Id);
            var rows = lib.Db.Plays(from, to);
            var s = new ListenStats { Title = title, Ms = rows.Sum(r => r.Ms), Plays = rows.Count(r => r.Counted) };
            string Art(PlayRow r) => byId.TryGetValue(r.TrackId, out var t) ? t.ArtPath : "";
            s.Artists = rows.Select(r => r.Artist.ToLowerInvariant()).Distinct().Count();
            s.Albums = rows.Select(r => (r.Artist + "\u001f" + r.Album).ToLowerInvariant()).Distinct().Count();
            s.Songs = rows.Select(r => (r.Artist + "\u001f" + r.Title).ToLowerInvariant()).Distinct().Count();
            s.TopArtists = rows.GroupBy(r => r.Artist, StringComparer.OrdinalIgnoreCase).Select(g => new Top(g.Key, "", g.Sum(r => r.Ms), g.Count(r => r.Counted), g.Select(Art).FirstOrDefault(a => a != "") ?? "", g.Key))
                .OrderByDescending(x => x.Ms).Take(10).ToList();
            s.TopAlbums = rows.Where(r => r.Album != "").GroupBy(r => (r.Artist + "\u001f" + r.Album).ToLowerInvariant()).Select(g =>
                {
                    var first = g.First();
                    var album = byId.TryGetValue(first.TrackId, out var t) ? lib.Albums.FirstOrDefault(a => a.Key == t.AlbumKey) : null;
                    return new Top(first.Album, first.Artist, g.Sum(r => r.Ms), g.Count(r => r.Counted), g.Select(Art).FirstOrDefault(a => a != "") ?? "", album);
                }).OrderByDescending(x => x.Ms).Take(10).ToList();
            s.TopSongs = rows.GroupBy(r => (r.Artist + "\u001f" + r.Title).ToLowerInvariant()).Select(g =>
                {
                    var first = g.First();
                    return new Top(first.Title, first.Artist, g.Sum(r => r.Ms), g.Count(r => r.Counted), g.Select(Art).FirstOrDefault(a => a != "") ?? "", byId.GetValueOrDefault(first.TrackId));
                }).OrderByDescending(x => x.Plays).ThenByDescending(x => x.Ms).Take(10).ToList();
            var firstAt = lib.Db.FirstPlayAt();
            if (firstAt == 0) s.Note = "Nothing has been logged yet. Listens are logged from now on; “all time” uses the play counts you already have.";
            else if (firstAt > from) s.Note = $"Listens have been logged since {DateTimeOffset.FromUnixTimeSeconds(firstAt).LocalDateTime:MMMM d, yyyy}; earlier ones only show under “all time”.";
            return s;
        }

        public static ListenStats AllTime()
        {
            var lib = App.Lib;
            var played = lib.Tracks.Where(t => t.PlayCount > 0).ToList();
            var s = new ListenStats
            {
                Title = "all time", Ms = played.Sum(t => t.PlayCount * t.DurationMs), Plays = played.Sum(t => t.PlayCount),
                Artists = played.Select(t => ArtistOf(t).ToLowerInvariant()).Distinct().Count(), Albums = played.Select(t => t.AlbumKey).Distinct().Count(), Songs = played.Count
            };
            s.TopArtists = played.GroupBy(ArtistOf, StringComparer.OrdinalIgnoreCase).Select(g => new Top(g.Key, "", g.Sum(t => t.PlayCount * t.DurationMs), g.Sum(t => t.PlayCount), g.Select(t => t.ArtPath).FirstOrDefault(a => a != "") ?? "", g.Key))
                .OrderByDescending(x => x.Ms).Take(10).ToList();
            s.TopAlbums = played.GroupBy(t => t.AlbumKey).Select(g => new Top(g.First().Album, g.First().AlbumArtist, g.Sum(t => t.PlayCount * t.DurationMs), g.Sum(t => t.PlayCount),
                g.Select(t => t.ArtPath).FirstOrDefault(a => a != "") ?? "", lib.Albums.FirstOrDefault(a => a.Key == g.Key))).OrderByDescending(x => x.Ms).Take(10).ToList();
            s.TopSongs = played.OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayed).Take(10).Select(t => new Top(t.Title, t.Artist, t.PlayCount * t.DurationMs, t.PlayCount, t.ArtPath, t)).ToList();
            s.Note = "Counted from the play count kept on each song (full plays only).";
            return s;
        }

        public static string Hours(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 10 ? $"{t.TotalHours:0} hours" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hours" : $"{Math.Max(0, (int)t.TotalMinutes)} minutes";
        }
    }

    /// <summary>
    /// The update check: asks GitHub for Phoenix's newest release and whether it is newer than this copy. Nothing
    /// about you or your library is sent.
    /// </summary>
    public static class Updates
    {
        public const string Repo = "jswyhart/Phoenix";
        static string ReleasePrefix => $"https://github.com/{Repo}/releases/";
        public static string ReleasesPage => ReleasePrefix + "latest";

        public record Release(Version Version, string Name, string Notes, string Page, string FileUrl, string FileName, long FileSize)
        {
            public string VersionText => Version.ToString(3);
        }

        public static Version? ParseVersion(string tag)
        {
            var digits = new string(tag.TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            if (digits == "") return null;
            var parts = digits.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
            return new Version(parts[0], parts[1], parts[2]);
        }

        /// <summary>Reads a release; <paramref name="fileEnding"/> picks its file for this platform (".apk" on Android).</summary>
        public static Release? Parse(JsonElement r, string fileEnding)
        {
            if (r.ValueKind != JsonValueKind.Object || r.O("draft").ValueKind == JsonValueKind.True) return null;
            var version = ParseVersion(r.S("tag_name"));
            if (version == null) return null;
            var asset = r.A("assets").FirstOrDefault(a => a.S("name").EndsWith(fileEnding, StringComparison.OrdinalIgnoreCase)
                                                          && a.S("browser_download_url").StartsWith(ReleasePrefix + "download/", StringComparison.OrdinalIgnoreCase));
            var page = r.S("html_url").StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase) ? r.S("html_url") : ReleasesPage;
            return asset.ValueKind == JsonValueKind.Object
                ? new Release(version, r.S("name") is { Length: > 0 } n ? n : r.S("tag_name"), r.S("body"), page, asset.S("browser_download_url"), asset.S("name"), asset.L("size"))
                : new Release(version, r.S("name") is { Length: > 0 } n2 ? n2 : r.S("tag_name"), r.S("body"), page, "", "", 0);
        }

        static readonly HttpClient Http = Create();
        static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>The newest release, or null when none has been published yet.</summary>
        public static async Task<Release?> CheckAsync(string fileEnding)
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return Parse(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement, fileEnding);
        }
    }

    /// <summary>
    /// Backup and restore: one file holding the library (ratings, play counts, the play log, playlists, bookmarks,
    /// reading progress, podcasts, stations, pins, tags) and your settings. Your music and other files are not in it.
    /// </summary>
    public static class Backup
    {
        public const string Extension = ".phoenixbackup";
        public record Manifest(string App, string Created, string Device, int Songs, int Playlists);

        static string PendingDir => Path.Combine(AppPaths.DataDir, "restore-pending");
        public static string SuggestedName => $"Phoenix backup {DateTime.Now:yyyy-MM-dd}{Extension}";

        /// <summary>Writes a backup to a stream (wherever the platform lets the user put it).</summary>
        public static void Write(Stream to)
        {
            App.Settings.Save();
            var work = Path.Combine(Path.GetTempPath(), "phoenix-backup-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                App.Lib.Db.CopyTo(work);
                using var zip = new ZipArchive(to, ZipArchiveMode.Create, leaveOpen: true);
                using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                    w.Write(JsonSerializer.Serialize(new Manifest("Phoenix", DateTime.Now.ToString("s"), App.DeviceName, App.Lib.Tracks.Count, App.Lib.Playlists.Count)));
                zip.CreateEntryFromFile(work, "library.db", CompressionLevel.Optimal);
                if (File.Exists(AppPaths.SettingsPath)) zip.CreateEntryFromFile(AppPaths.SettingsPath, "settings.json", CompressionLevel.Optimal);
            }
            finally { try { File.Delete(work); } catch { } }
        }

        /// <summary>
        /// Unpacks a backup beside the library, to be put in place the next time the app starts (the library cannot be
        /// swapped while it is open). Returns what the backup says about itself.
        /// </summary>
        public static Manifest Stage(Stream from)
        {
            using var zip = new ZipArchive(from, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("library.db") == null || zip.GetEntry("manifest.json") is not { } entry) throw new Exception("That file is not a Phoenix backup.");
            Manifest? manifest;
            using (var r = new StreamReader(entry.Open())) manifest = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd());
            if (manifest is not { App: "Phoenix" }) throw new Exception("That file is not a Phoenix backup.");
            if (Directory.Exists(PendingDir)) Directory.Delete(PendingDir, true);
            Directory.CreateDirectory(PendingDir);
            foreach (var name in new[] { "library.db", "settings.json", "manifest.json" })
                zip.GetEntry(name)?.ExtractToFile(Path.Combine(PendingDir, name), true);
            return manifest;
        }

        /// <summary>Throws a staged restore away (you changed your mind).</summary>
        public static void Discard() { try { if (Directory.Exists(PendingDir)) Directory.Delete(PendingDir, true); } catch { } }

        /// <summary>At start, before the library is opened: puts a staged restore in place. True if one was applied.</summary>
        public static bool ApplyPending()
        {
            var db = Path.Combine(PendingDir, "library.db");
            if (!File.Exists(db)) return false;
            try
            {
                if (File.Exists(AppPaths.DbPath)) File.Copy(AppPaths.DbPath, Path.Combine(AppPaths.DataDir, "library.before-restore.db"), true);
                foreach (var extra in new[] { "-wal", "-shm" }) try { File.Delete(AppPaths.DbPath + extra); } catch { }
                File.Copy(db, AppPaths.DbPath, true);
                var settings = Path.Combine(PendingDir, "settings.json");
                if (File.Exists(settings)) File.Copy(settings, AppPaths.SettingsPath, true);
                return true;
            }
            catch { return false; }
            finally { try { Directory.Delete(PendingDir, true); } catch { } }
        }
    }
}
