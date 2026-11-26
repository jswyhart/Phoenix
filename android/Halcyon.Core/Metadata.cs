// Shared with the Windows app (Services\Metadata.cs): where album details and covers come from, and identifying songs by sound.
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>One album found by a search, from any source.</summary>
public record MetaHit(string Source, string Key, string Title, string Artist, string Year, string Country, string Label, string Format, string Thumb)
{
    public string Details => string.Join("  ·  ", new[] { Artist, Year, Country, Format, Label }.Where(s => s != ""));
}

/// <summary>A place album details and covers can come from.</summary>
public interface IMetaSource
{
    string Id { get; }
    string Name { get; }
    /// <summary>What this source gives, in a few words (for settings).</summary>
    string Gives { get; }
    /// <summary>Where to get the key this source needs, or null if it needs none.</summary>
    string? KeyHelp { get; }
    /// <summary>Whether it can be used right now (its key, if any, is filled in).</summary>
    bool Ready { get; }
    Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title);
    Task<DiscogsRelease> GetAsync(MetaHit hit);
}

/// <summary>
/// Every metadata source, the order they are trusted in, and the automatic "fill in what is missing" matching.
/// An album can be tied to one source (or to none) from its right-click menu; otherwise sources are tried by weight.
/// </summary>
public static class Metadata
{
    public const string Auto = "", Never = "none";

    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (desktop music player)");
        return c;
    }

    public static readonly IMetaSource[] Sources =
    {
        new FolderSource(), new MusicBrainzSource(), new DiscogsSource(), new ITunesSource(), new DeezerSource(),
        new LastFmSource(), new AudioDbSource(), new FanartSource()
    };

    static readonly Dictionary<string, int> DefaultWeights = new()
    {
        ["folder"] = 10, ["musicbrainz"] = 8, ["discogs"] = 7, ["itunes"] = 6, ["deezer"] = 5, ["lastfm"] = 4, ["theaudiodb"] = 3, ["fanart"] = 3
    };

    public static IMetaSource? Find(string id) => Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>The setting for a source, created with its default the first time it is asked for.</summary>
    public static MetaSourceSetting Setting(string id)
    {
        var s = App.Settings.MetaSources.FirstOrDefault(x => x.Id == id);
        if (s == null) App.Settings.MetaSources.Add(s = new MetaSourceSetting { Id = id, Weight = DefaultWeights.GetValueOrDefault(id, 5) });
        return s;
    }

    public static string Key(string id) => App.Settings.Keys.GetValueOrDefault(id, "");

    /// <summary>Sources that are switched on and usable, most trusted first.</summary>
    public static List<IMetaSource> Ranked() =>
        Sources.Where(s => Setting(s.Id).Enabled && s.Ready).OrderByDescending(s => Setting(s.Id).Weight).ThenBy(s => Array.IndexOf(Sources, s)).ToList();

    /// <summary>The source an album is tied to: "" for automatic, "none" for never, or a source id.</summary>
    public static string SourceOf(Album a) => App.Settings.AlbumSources.GetValueOrDefault(a.Key, Auto);

    public static void SetSource(Album a, string id)
    {
        if (id == Auto) App.Settings.AlbumSources.Remove(a.Key); else App.Settings.AlbumSources[a.Key] = id;
        if (!App.Testing) App.Settings.Save();
    }

    /// <summary>The sources to try for an album, in order: just its own if it is tied to one.</summary>
    public static List<IMetaSource> For(Album a)
    {
        var tied = SourceOf(a);
        if (tied == Never) return new();
        if (tied != Auto && Find(tied) is { Ready: true } one) return new() { one };
        return Ranked();
    }

    public static async Task<byte[]> GetBytesAsync(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return await File.ReadAllBytesAsync(url);
        if (url.Contains("discogs.com", StringComparison.OrdinalIgnoreCase)) return await App.Discogs.GetBytesAsync(url);
        return await Http.GetByteArrayAsync(url);
    }

    internal static async Task<JsonElement> JsonAsync(string url) => J.Parse(await Http.GetStringAsync(url));

    // ---------- matching ----------

    public static string Norm(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"\((deluxe|remaster|expanded|bonus|special|anniversary|explicit)[^)]*\)|\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\b(the|a|an)\b", " ");
        return Regex.Replace(s, @"[^\p{L}\p{N}]+", "");
    }

    /// <summary>How well a search result fits the album asked for: 0 (no) to 1 (the same names).</summary>
    public static double Fit(MetaHit hit, string artist, string title)
    {
        string ht = Norm(hit.Title), wt = Norm(title), ha = Norm(hit.Artist), wa = Norm(artist);
        // Discogs titles read "Artist - Album"
        if (hit.Artist == "" && hit.Title.Contains(" - ")) { ha = Norm(hit.Title[..hit.Title.IndexOf(" - ")]); ht = Norm(hit.Title[(hit.Title.IndexOf(" - ") + 3)..]); }
        if (wt == "" || ht == "") return 0;
        var t = ht == wt ? 1.0 : ht.StartsWith(wt) || wt.StartsWith(ht) ? 0.8 : ht.Contains(wt) || wt.Contains(ht) ? 0.6 : 0;
        if (t == 0) return 0;
        var a = wa == "" || ha == "" ? 0.7 : ha == wa ? 1.0 : ha.Contains(wa) || wa.Contains(ha) ? 0.8 : 0;
        return a == 0 ? 0 : t * a;
    }

    public record Filled(int Year, string Genre, byte[]? Cover, string CoverUrl, List<string> From)
    {
        public bool Any => Year > 0 || Genre != "" || Cover != null;
    }

    /// <summary>
    /// Looks for whatever the album lacks (cover, year, genre), asking its sources in order of trust and taking each
    /// missing piece from the first source that has it for a confidently matching album.
    /// </summary>
    public static async Task<Filled> FillAsync(Album album, CancellationToken stop = default)
    {
        bool needCover = album.ArtPath == "", needYear = album.Year == 0, needGenre = album.Genre == "";
        var year = 0; var genre = ""; byte[]? cover = null; var coverUrl = "";
        var from = new List<string>();
        foreach (var source in For(album))
        {
            if (stop.IsCancellationRequested || !(needCover || needYear || needGenre)) break;
            try
            {
                var hits = await source.SearchAsync(album, album.Artist, album.Title);
                var best = hits.Select(h => (h, fit: Fit(h, album.Artist, album.Title))).Where(x => x.fit >= 0.6).OrderByDescending(x => x.fit).FirstOrDefault().h;
                if (best == null) continue;
                var rel = await source.GetAsync(best);
                var used = false;
                if (needYear && rel.Year > 0) { year = rel.Year; needYear = false; used = true; }
                if (needGenre && rel.Genre != "") { genre = rel.Genre; needGenre = false; used = true; }
                if (needCover && rel.CoverUrl != "")
                {
                    try
                    {
                        var bytes = await GetBytesAsync(rel.CoverUrl);
                        if (bytes.Length > 500) { cover = bytes; coverUrl = rel.CoverUrl; needCover = false; used = true; }
                    }
                    catch { }
                }
                if (used) from.Add(source.Name);
            }
            catch (Exception ex) when (ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) && For(album).Count == 1) { throw; }
            catch { }
        }
        return new Filled(year, genre, cover, coverUrl, from);
    }

    // ---------- identifying a song by its sound (AcoustID) ----------

    public record Identified(string Title, string Artist, string Album, double Score);

    /// <summary>Chromaprint's fpcalc: the one chosen in settings, one beside Phoenix, or one installed on the system.</summary>
    public static string FpcalcPath()
    {
        var set = App.Settings.FpcalcPath;
        if (set != "" && File.Exists(set)) return set;
        var name = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        var places = new List<string> { Path.Combine(AppContext.BaseDirectory, name) };
        if (!OperatingSystem.IsWindows()) places.AddRange(new[] { "/opt/homebrew/bin/fpcalc", "/usr/local/bin/fpcalc", "/usr/bin/fpcalc" });
        places.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, name)));
        return places.FirstOrDefault(File.Exists) ?? "";
    }

    public static bool CanIdentify => Key("acoustid") != "" && FpcalcPath() != "";

    /// <summary>Fingerprints the file with Chromaprint's fpcalc and asks AcoustID what it is. Null if it is not known.</summary>
    public static async Task<Identified?> IdentifyAsync(string file)
    {
        var info = new ProcessStartInfo(FpcalcPath()) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-json");
        info.ArgumentList.Add(file);
        using var p = Process.Start(info) ?? throw new Exception("fpcalc could not be started.");
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var fp = J.Parse(text);
        var url = $"https://api.acoustid.org/v2/lookup?client={Uri.EscapeDataString(Key("acoustid"))}&meta=recordings+releasegroups+compress" +
                  $"&duration={(int)fp.O("duration").GetDouble()}&fingerprint={fp.S("fingerprint")}";
        return ParseAcoustId(await JsonAsync(url));
    }

    public static Identified? ParseAcoustId(JsonElement r)
    {
        foreach (var result in r.A("results").OrderByDescending(x => x.O("score").ValueKind == JsonValueKind.Number ? x.O("score").GetDouble() : 0))
        {
            var rec = result.A("recordings").FirstOrDefault(x => x.S("title") != "");
            if (rec.ValueKind != JsonValueKind.Object) continue;
            var group = rec.A("releasegroups").OrderBy(g => g.S("type") == "Album" ? 0 : 1).FirstOrDefault();
            return new Identified(rec.S("title"), string.Join(" & ", rec.A("artists").Select(a => a.S("name")).Where(n => n != "")),
                group.ValueKind == JsonValueKind.Object ? group.S("title") : "", result.O("score").ValueKind == JsonValueKind.Number ? result.O("score").GetDouble() : 0);
        }
        return null;
    }

    internal static int YearOf(string date) => date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : 0;
    internal static string Time(long ms) => ms > 0 ? Fmt.Time(ms) : "";
}

// ---------- the sources ----------

/// <summary>A picture saved beside the songs (cover.jpg, folder.jpg and the like). Nothing leaves this computer.</summary>
public class FolderSource : IMetaSource
{
    static readonly string[] Names = { "cover", "folder", "front", "album", "albumart" };
    static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
    public string Id => "folder";
    public string Name => "Pictures in the album's folder";
    public string Gives => "cover art (cover.jpg, folder.jpg…); nothing is sent anywhere";
    public string? KeyHelp => null;
    public bool Ready => true;

    public Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title)
    {
        var hits = new List<MetaHit>();
        try
        {
            foreach (var dir in (album?.Tracks ?? new()).Where(t => t.IsLocal).Select(t => Path.GetDirectoryName(t.Location) ?? "").Where(d => d != "" && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
                foreach (var file in Directory.EnumerateFiles(dir).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .OrderBy(f => Array.IndexOf(Names, Path.GetFileNameWithoutExtension(f).ToLowerInvariant()) is var i && i < 0 ? 99 : i).Take(8))
                    hits.Add(new MetaHit(Id, file, album!.Title, album.Artist, "", "", Path.GetFileName(file), "picture", file));
        }
        catch { }
        return Task.FromResult(hits);
    }

    public Task<DiscogsRelease> GetAsync(MetaHit hit) => Task.FromResult(new DiscogsRelease(0, "", "", 0, "", "", hit.Key, new()));
}

/// <summary>The existing Discogs lookup (personal access token).</summary>
public class DiscogsSource : IMetaSource
{
    public string Id => "discogs";
    public string Name => "Discogs";
    public string Gives => "album details, track names, covers";
    public string? KeyHelp => "personal access token from discogs.com → Settings → Developers";
    public bool Ready => App.Discogs.HasToken;

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title) =>
        (await App.Discogs.SearchAsync(artist, title)).Select(r => new MetaHit(Id, r.Id.ToString(), r.Title, "", r.Year, r.Country, r.Label, r.Format, r.Thumb)).ToList();

    public Task<DiscogsRelease> GetAsync(MetaHit hit) => App.Discogs.GetReleaseAsync(int.Parse(hit.Key));
}

/// <summary>MusicBrainz for the details, the Cover Art Archive for the picture. Free, no account; one request a second.</summary>
public class MusicBrainzSource : IMetaSource
{
    const string Api = "https://musicbrainz.org/ws/2";
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _last = DateTime.MinValue;
    public string Id => "musicbrainz";
    public string Name => "MusicBrainz + Cover Art Archive";
    public string Gives => "album details, track names, covers; free, no account";
    public string? KeyHelp => null;
    public bool Ready => true;

    internal static async Task<JsonElement> AskAsync(string url)
    {
        await Gate.WaitAsync();
        try
        {
            // MusicBrainz answers "503, busy" quite often, even to polite callers: ask again a few times, a little
            // later each time, before giving up
            for (var attempt = 0; ; attempt++)
            {
                var wait = _last.AddMilliseconds(1100) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                try { return await Ask(url); }
                catch (System.Net.Http.HttpRequestException ex) when (Busy(ex.StatusCode))
                {
                    if (attempt >= Retries) throw new Exception("MusicBrainz is busy right now and did not answer after several tries. Try again in a minute, or choose another source.");
                    Retried++;
                    await Task.Delay(RetryDelay * (attempt + 1));
                }
                finally { _last = DateTime.UtcNow; }
            }
        }
        finally { Gate.Release(); }
    }

    const int Retries = 4;
    /// <summary>How long to wait before asking again, times the number of the attempt (shortened by the self-test).</summary>
    public static int RetryDelay = 1500;
    /// <summary>Times MusicBrainz was asked again after saying it was busy (for the self-test).</summary>
    public static int Retried;
    /// <summary>What fetches an address (the self-test stands in for MusicBrainz here).</summary>
    public static Func<string, Task<JsonElement>> Ask = Metadata.JsonAsync;

    static bool Busy(System.Net.HttpStatusCode? code) => code is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests
        or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout;

    static string Q(string s) => Uri.EscapeDataString("\"" + s.Replace("\"", "") + "\"");

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title)
    {
        var query = "release:" + Q(title) + (artist.Trim() != "" ? "%20AND%20artist:" + Q(artist) : "");
        return ParseSearch(await AskAsync($"{Api}/release/?query={query}&fmt=json&limit=15"));
    }

    public static List<MetaHit> ParseSearch(JsonElement r) => r.A("releases").Select(x => new MetaHit("musicbrainz", x.S("id"), x.S("title"),
        string.Join(" & ", x.A("artist-credit").Select(a => a.S("name")).Where(n => n != "")), x.S("date").Length >= 4 ? x.S("date")[..4] : "", x.S("country"),
        x.A("label-info").Select(l => l.O("label").S("name")).FirstOrDefault(n => n != "") ?? "",
        string.Join(" + ", x.A("media").Select(m => m.S("format")).Where(f => f != "").Distinct()),
        $"https://coverartarchive.org/release/{x.S("id")}/front-250")).ToList();

    public async Task<DiscogsRelease> GetAsync(MetaHit hit) =>
        ParseRelease(await AskAsync($"{Api}/release/{hit.Key}?inc=recordings+artist-credits+genres+release-groups&fmt=json"));

    public static DiscogsRelease ParseRelease(JsonElement r)
    {
        var tracks = r.A("media").SelectMany(m => m.A("tracks")).Select((t, i) => new DiscogsTrack((i + 1).ToString(), t.S("title"), Metadata.Time(t.L("length")))).ToList();
        var genres = r.A("genres").Concat(r.O("release-group").A("genres")).OrderByDescending(g => g.L("count")).Select(g => g.S("name")).Where(n => n != "").Distinct().ToList();
        string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
        return new DiscogsRelease(0, r.S("title"), string.Join(" & ", r.A("artist-credit").Select(a => a.S("name")).Where(n => n != "")),
            Metadata.YearOf(r.O("release-group").S("first-release-date") is { Length: >= 4 } first ? first : r.S("date")),
            genres.Count > 0 ? Cap(genres[0]) : "", genres.Count > 1 ? Cap(genres[1]) : "", $"https://coverartarchive.org/release/{r.S("id")}/front", tracks);
    }
}

/// <summary>Apple's public catalogue search: large covers, genre and year. No account.</summary>
public class ITunesSource : IMetaSource
{
    public string Id => "itunes";
    public string Name => "iTunes";
    public string Gives => "large covers, genre, year, track names; no account";
    public string? KeyHelp => null;
    public bool Ready => true;

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title) =>
        ParseSearch(await Metadata.JsonAsync($"https://itunes.apple.com/search?entity=album&limit=15&term={Uri.EscapeDataString((artist + " " + title).Trim())}"));

    public static List<MetaHit> ParseSearch(JsonElement r) => r.A("results").Where(x => x.S("collectionType") != "" || x.S("collectionName") != "")
        .Select(x => new MetaHit("itunes", x.S("collectionId"), x.S("collectionName"), x.S("artistName"), x.S("releaseDate").Length >= 4 ? x.S("releaseDate")[..4] : "",
            x.S("country"), x.S("primaryGenreName"), x.L("trackCount") > 0 ? $"{x.L("trackCount")} songs" : "", x.S("artworkUrl100"))).ToList();

    public async Task<DiscogsRelease> GetAsync(MetaHit hit) => ParseRelease(await Metadata.JsonAsync($"https://itunes.apple.com/lookup?entity=song&id={hit.Key}"));

    public static DiscogsRelease ParseRelease(JsonElement r)
    {
        var items = r.A("results");
        var c = items.FirstOrDefault(x => x.S("wrapperType") == "collection");
        var tracks = items.Where(x => x.S("wrapperType") == "track").OrderBy(x => x.L("discNumber")).ThenBy(x => x.L("trackNumber"))
            .Select((t, i) => new DiscogsTrack((i + 1).ToString(), t.S("trackName"), Metadata.Time(t.L("trackTimeMillis")))).ToList();
        // the artwork address carries its size: ask for a big one
        var art = Regex.Replace(c.S("artworkUrl100"), @"/\d+x\d+bb\.", "/1200x1200bb.");
        return new DiscogsRelease(0, c.S("collectionName"), c.S("artistName"), Metadata.YearOf(c.S("releaseDate")), c.S("primaryGenreName"), "", art, tracks);
    }
}

/// <summary>Deezer's public catalogue: covers, genre, year. No account.</summary>
public class DeezerSource : IMetaSource
{
    public string Id => "deezer";
    public string Name => "Deezer";
    public string Gives => "large covers, genre, year, track names; no account";
    public string? KeyHelp => null;
    public bool Ready => true;

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title)
    {
        var q = (artist.Trim() != "" ? $"artist:\"{artist.Replace("\"", "")}\" " : "") + $"album:\"{title.Replace("\"", "")}\"";
        return ParseSearch(await Metadata.JsonAsync($"https://api.deezer.com/search/album?limit=15&q={Uri.EscapeDataString(q)}"));
    }

    public static List<MetaHit> ParseSearch(JsonElement r) => r.A("data").Select(x => new MetaHit("deezer", x.S("id"), x.S("title"), x.O("artist").S("name"), "", "",
        x.S("record_type"), x.L("nb_tracks") > 0 ? $"{x.L("nb_tracks")} songs" : "", x.S("cover_medium"))).ToList();

    public async Task<DiscogsRelease> GetAsync(MetaHit hit) => ParseRelease(await Metadata.JsonAsync($"https://api.deezer.com/album/{hit.Key}"));

    public static DiscogsRelease ParseRelease(JsonElement r) => new(0, r.S("title"), r.O("artist").S("name"), Metadata.YearOf(r.S("release_date")),
        r.O("genres").A("data").Select(g => g.S("name")).FirstOrDefault(n => n != "") ?? "", "", r.S("cover_xl") is { Length: > 0 } xl ? xl : r.S("cover_big"),
        r.O("tracks").A("data").Select((t, i) => new DiscogsTrack((i + 1).ToString(), t.S("title"), Metadata.Time(t.L("duration") * 1000))).ToList());
}

/// <summary>Last.fm: covers, listener tags (used as the genre) and track names. Free API key.</summary>
public class LastFmSource : IMetaSource
{
    const string Api = "https://ws.audioscrobbler.com/2.0/?format=json";
    public string Id => "lastfm";
    public string Name => "Last.fm";
    public string Gives => "covers, genre from listener tags, track names";
    public string? KeyHelp => "free API key from last.fm/api/account/create";
    public bool Ready => Metadata.Key(Id) != "";

    static string Image(JsonElement x) => x.A("image").Select(i => i.S("#text")).LastOrDefault(u => u != "") ?? "";

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title) =>
        ParseSearch(await Metadata.JsonAsync($"{Api}&method=album.search&limit=20&album={Uri.EscapeDataString(title)}&api_key={Uri.EscapeDataString(Metadata.Key(Id))}"));

    public static List<MetaHit> ParseSearch(JsonElement r) => r.O("results").O("albummatches").A("album")
        .Select(x => new MetaHit("lastfm", x.S("artist") + "\u001f" + x.S("name"), x.S("name"), x.S("artist"), "", "", "", "", Image(x))).ToList();

    public async Task<DiscogsRelease> GetAsync(MetaHit hit)
    {
        var parts = hit.Key.Split('\u001f');
        return ParseRelease(await Metadata.JsonAsync($"{Api}&method=album.getinfo&artist={Uri.EscapeDataString(parts[0])}&album={Uri.EscapeDataString(parts[^1])}&api_key={Uri.EscapeDataString(Metadata.Key(Id))}"));
    }

    public static DiscogsRelease ParseRelease(JsonElement r)
    {
        var a = r.O("album");
        var tags = a.O("tags").A("tag").Select(t => t.S("name")).Where(n => n != "" && !Regex.IsMatch(n, @"^\d{4}$|albums i own|favou?rite|seen live", RegexOptions.IgnoreCase)).ToList();
        string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
        return new DiscogsRelease(0, a.S("name"), a.S("artist"), 0, tags.Count > 0 ? Cap(tags[0]) : "", tags.Count > 1 ? Cap(tags[1]) : "", Image(a),
            a.O("tracks").A("track").Select((t, i) => new DiscogsTrack((i + 1).ToString(), t.S("name"), Metadata.Time(t.L("duration") * 1000))).ToList());
    }
}

/// <summary>TheAudioDB: album details and covers. API key from theaudiodb.com.</summary>
public class AudioDbSource : IMetaSource
{
    readonly Dictionary<string, JsonElement> _seen = new();
    public string Id => "theaudiodb";
    public string Name => "TheAudioDB";
    public string Gives => "album details, covers, track names";
    public string? KeyHelp => "API key from theaudiodb.com (free and supporter keys both work)";
    public bool Ready => Metadata.Key(Id) != "";
    string Api => $"https://www.theaudiodb.com/api/v1/json/{Uri.EscapeDataString(Metadata.Key(Id))}";

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title)
    {
        var r = await Metadata.JsonAsync($"{Api}/searchalbum.php?s={Uri.EscapeDataString(artist)}&a={Uri.EscapeDataString(title)}");
        foreach (var x in r.A("album")) _seen[x.S("idAlbum")] = x;
        return ParseSearch(r);
    }

    public static List<MetaHit> ParseSearch(JsonElement r) => r.A("album").Select(x => new MetaHit("theaudiodb", x.S("idAlbum"), x.S("strAlbum"), x.S("strArtist"),
        x.S("intYearReleased") is "0" ? "" : x.S("intYearReleased"), "", x.S("strLabel"), x.S("strReleaseFormat"), x.S("strAlbumThumb") is { Length: > 0 } t ? t + "/preview" : "")).ToList();

    public async Task<DiscogsRelease> GetAsync(MetaHit hit)
    {
        _seen.TryGetValue(hit.Key, out var a);
        return ParseRelease(a, await Metadata.JsonAsync($"{Api}/track.php?m={Uri.EscapeDataString(hit.Key)}"));
    }

    public static DiscogsRelease ParseRelease(JsonElement a, JsonElement tracks) => new(0, a.S("strAlbum"), a.S("strArtist"), (int)a.L("intYearReleased"), a.S("strGenre"), a.S("strStyle"), a.S("strAlbumThumb"),
        tracks.A("track").OrderBy(t => t.L("intTrackNumber")).Select((t, i) => new DiscogsTrack((i + 1).ToString(), t.S("strTrack"), Metadata.Time(t.L("intDuration")))).ToList());
}

/// <summary>fanart.tv: fan-made album covers, found through the album's MusicBrainz id. Personal API key.</summary>
public class FanartSource : IMetaSource
{
    public string Id => "fanart";
    public string Name => "fanart.tv";
    public string Gives => "cover art only";
    public string? KeyHelp => "personal API key from fanart.tv/get-an-api-key";
    public bool Ready => Metadata.Key(Id) != "";

    public async Task<List<MetaHit>> SearchAsync(Album? album, string artist, string title)
    {
        string Q(string s) => Uri.EscapeDataString("\"" + s.Replace("\"", "") + "\"");
        var groups = await MusicBrainzSource.AskAsync($"https://musicbrainz.org/ws/2/release-group/?query=releasegroup:{Q(title)}" + (artist.Trim() != "" ? "%20AND%20artist:" + Q(artist) : "") + "&fmt=json&limit=4");
        var hits = new List<MetaHit>();
        foreach (var g in groups.A("release-groups"))
        {
            JsonElement r;
            try { r = await Metadata.JsonAsync($"https://webservice.fanart.tv/v3/music/albums/{g.S("id")}?api_key={Uri.EscapeDataString(Metadata.Key(Id))}"); }
            catch (HttpRequestException) { continue; }   // nothing for that album
            hits.AddRange(ParseCovers(r, g.S("id"), g.S("title"), string.Join(" & ", g.A("artist-credit").Select(a => a.S("name")).Where(n => n != "")),
                g.S("first-release-date").Length >= 4 ? g.S("first-release-date")[..4] : ""));
        }
        return hits;
    }

    public static List<MetaHit> ParseCovers(JsonElement r, string groupId, string title, string artist, string year) =>
        r.O("albums").O(groupId).A("albumcover").Select(c => new MetaHit("fanart", c.S("url"), title, artist, year, "", "", c.L("likes") > 0 ? $"{c.L("likes")} likes" : "", c.S("url"))).ToList();

    public Task<DiscogsRelease> GetAsync(MetaHit hit) => Task.FromResult(new DiscogsRelease(0, "", "", 0, "", "", hit.Key, new()));
}
