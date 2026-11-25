using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// What you listened to: every listen goes into the library's play log (which the stats page reads), and, if you
/// switched it on, to Last.fm and ListenBrainz. Listens that could not be sent wait in the library and go out later.
/// </summary>
public static class Listening
{
    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (desktop music player)");
        return c;
    }

    static Settings S => App.Settings;
    static string Key(string id) => S.ApiKeys.GetValueOrDefault(id, "");

    public static bool LastFmReady => Key("lastfm") != "" && Key("lastfm_secret") != "" && Key("lastfm_session") != "";
    public static bool ListenBrainzReady => Key("listenbrainz") != "";
    /// <summary>What happened the last time something was sent (shown in settings).</summary>
    public static string LastResult { get; private set; } = "";
    public static event Action? Sent;

    /// <summary>A song has started: tell the services what is playing now.</summary>
    public static void Started(PlayItem item)
    {
        if (item.Track is not { } t || App.SmokeMode) return;
        if (S.ScrobbleLastFm && LastFmReady) _ = Quiet(() => LastFmAsync("track.updateNowPlaying", Describe(t.Artist, t.Title, t.Album, t.DurationMs)));
        if (S.ScrobbleListenBrainz && ListenBrainzReady) _ = Quiet(() => ListenBrainzAsync("playing_now", new[] { (0L, t.Artist, t.Title, t.Album, t.DurationMs) }));
    }

    /// <summary>
    /// A song was left after <paramref name="ms"/> of listening. It is written to the play log if you heard at least
    /// half a minute, and counts as a play (and is scrobbled) if it finished or you heard half of it or four minutes.
    /// </summary>
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
        if (!App.SmokeMode) _ = FlushAsync();
    }

    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Sends the listens that are waiting. Whatever fails stays in the queue for next time.</summary>
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
        finally { Gate.Release(); Sent?.Invoke(); }
    }

    static async Task Quiet(Func<Task> send) { try { await send(); } catch { } }

    static SortedDictionary<string, string> Describe(string artist, string title, string album, long ms)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["artist"] = artist, ["track"] = title };
        if (album != "") p["album"] = album;
        if (ms > 0) p["duration"] = (ms / 1000).ToString();
        return p;
    }

    // ---------- Last.fm ----------

    const string LastFmApi = "https://ws.audioscrobbler.com/2.0/";

    /// <summary>The signature Last.fm asks for: every parameter in name order, then the shared secret, hashed.</summary>
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
        using var response = await Http.PostAsync(LastFmApi, new FormUrlEncodedContent(p));
        var json = J.Parse(await response.Content.ReadAsStringAsync());
        if (json.L("error") != 0) throw new Exception("Last.fm: " + json.S("message"));
        return json;
    }

    /// <summary>First half of signing in: opens Last.fm in the browser so you can allow Phoenix; returns the token to finish with.</summary>
    public static async Task<string> LastFmBeginAsync()
    {
        var json = await LastFmAsync("auth.getToken", new SortedDictionary<string, string>(StringComparer.Ordinal), session: false);
        var token = json.S("token");
        if (token == "") throw new Exception("Last.fm gave no token.");
        Process.Start(new ProcessStartInfo($"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(Key("lastfm"))}&token={Uri.EscapeDataString(token)}") { UseShellExecute = true });
        return token;
    }

    /// <summary>Second half: once you allowed it in the browser, fetches the session. Returns your Last.fm name, or "" if not allowed yet.</summary>
    public static async Task<string> LastFmFinishAsync(string token)
    {
        JsonElement json;
        try { json = await LastFmAsync("auth.getSession", new SortedDictionary<string, string>(StringComparer.Ordinal) { ["token"] = token }, session: false); }
        catch (Exception ex) when (ex.Message.Contains("not been authorized", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)) { return ""; }
        var key = json.O("session").S("key");
        if (key == "") return "";
        S.ApiKeys["lastfm_session"] = key;
        S.LastFmUser = json.O("session").S("name");
        if (!App.SmokeMode) S.Save();
        return S.LastFmUser;
    }

    public static void LastFmSignOut()
    {
        S.ApiKeys.Remove("lastfm_session");
        S.LastFmUser = "";
        App.Lib.Db.DropScrobbles("lastfm");
        if (!App.SmokeMode) S.Save();
    }

    // ---------- ListenBrainz ----------

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

    /// <summary>Checks a ListenBrainz token; returns the user name it belongs to, or "" if it is not valid.</summary>
    public static async Task<string> ListenBrainzCheckAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
        request.Headers.TryAddWithoutValidation("Authorization", "Token " + token);
        using var response = await Http.SendAsync(request);
        var json = J.Parse(await response.Content.ReadAsStringAsync());
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
    /// <summary>Listening time per bucket (months of a year, or days), oldest first.</summary>
    public List<(string Label, long Ms)> Over = new();
    public string Note = "";

    static string ArtistOf(Track t) => t.Artist != "" ? t.Artist : t.AlbumArtist;

    /// <summary>From the play log, for listens between two moments (seconds since 1970).</summary>
    public static ListenStats FromLog(string title, long from, long to, bool byMonth)
    {
        var lib = App.Lib;
        var rows = lib.Db.Plays(from, to);
        var s = new ListenStats { Title = title, Ms = rows.Sum(r => r.Ms), Plays = rows.Count(r => r.Counted) };
        string Art(PlayRow r) => lib.ById.TryGetValue(r.TrackId, out var t) ? t.ArtPath : "";
        s.Artists = rows.Select(r => r.Artist.ToLowerInvariant()).Distinct().Count();
        s.Albums = rows.Select(r => (r.Artist + "\u001f" + r.Album).ToLowerInvariant()).Distinct().Count();
        s.Songs = rows.Select(r => (r.Artist + "\u001f" + r.Title).ToLowerInvariant()).Distinct().Count();
        s.TopArtists = rows.GroupBy(r => r.Artist, StringComparer.OrdinalIgnoreCase).Select(g => new Top(g.Key, "", g.Sum(r => r.Ms), g.Count(r => r.Counted), g.Select(Art).FirstOrDefault(a => a != "") ?? "", g.Key))
            .OrderByDescending(x => x.Ms).Take(10).ToList();
        s.TopAlbums = rows.Where(r => r.Album != "").GroupBy(r => (r.Artist + "\u001f" + r.Album).ToLowerInvariant()).Select(g =>
            {
                var first = g.First();
                var album = lib.ById.TryGetValue(first.TrackId, out var t) ? lib.Albums.FirstOrDefault(a => a.Key == t.AlbumKey) : null;
                return new Top(first.Album, first.Artist, g.Sum(r => r.Ms), g.Count(r => r.Counted), g.Select(Art).FirstOrDefault(a => a != "") ?? "", album);
            }).OrderByDescending(x => x.Ms).Take(10).ToList();
        s.TopSongs = rows.GroupBy(r => (r.Artist + "\u001f" + r.Title).ToLowerInvariant()).Select(g =>
            {
                var first = g.First();
                return new Top(first.Title, first.Artist, g.Sum(r => r.Ms), g.Count(r => r.Counted), g.Select(Art).FirstOrDefault(a => a != "") ?? "", lib.ById.GetValueOrDefault(first.TrackId));
            }).OrderByDescending(x => x.Plays).ThenByDescending(x => x.Ms).Take(10).ToList();
        var start = DateTimeOffset.FromUnixTimeSeconds(from).LocalDateTime;
        var end = DateTimeOffset.FromUnixTimeSeconds(Math.Min(to, Fmt.Now)).LocalDateTime;
        if (byMonth)
            for (var m = new DateTime(start.Year, start.Month, 1); m <= end; m = m.AddMonths(1))
            {
                long a = new DateTimeOffset(m).ToUnixTimeSeconds(), b = new DateTimeOffset(m.AddMonths(1)).ToUnixTimeSeconds();
                s.Over.Add((m.ToString("MMM"), rows.Where(r => r.At >= a && r.At < b).Sum(r => r.Ms)));
            }
        else
            for (var d = start.Date; d <= end; d = d.AddDays(1))
            {
                long a = new DateTimeOffset(d).ToUnixTimeSeconds(), b = new DateTimeOffset(d.AddDays(1)).ToUnixTimeSeconds();
                s.Over.Add((d.Day.ToString(), rows.Where(r => r.At >= a && r.At < b).Sum(r => r.Ms)));
            }
        var first = lib.Db.FirstPlayAt();
        if (first == 0) s.Note = "Nothing has been logged yet. Listens are logged from now on; “all time” uses the play counts you already have.";
        else if (first > from) s.Note = $"Listens have been logged since {DateTimeOffset.FromUnixTimeSeconds(first).LocalDateTime:MMMM d, yyyy}; earlier ones only show under “all time”.";
        return s;
    }

    /// <summary>From the play counts kept on every song (these go back further than the log, but have no dates).</summary>
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
