using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

public record RemotePhoto(string Folder, string Name, string ThumbUrl, string FullUrl);

/// <summary>Reads music, video and photo libraries from a Jellyfin server and builds stream URLs.</summary>
public class JellyfinClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };
    static Settings S => App.Settings;

    // Audio the Windows media pipeline can open straight from the original file; the server converts everything else.
    static readonly string[] DirectAudio = { "mp3", "flac", "aac", "m4a", "mp4", "wav", "wma", "asf" };

    public bool IsConnected => S.JellyfinUrl != "" && S.JellyfinToken != "";

    static string AuthHeader(string? token) =>
        $"MediaBrowser Client=\"Phoenix\", Device=\"Windows PC\", DeviceId=\"{S.DeviceId}\", Version=\"1.0.0\"" + (token is { Length: > 0 } ? $", Token=\"{token}\"" : "");

    public async Task LoginAsync(string url, string user, string password)
    {
        url = NormalizeUrl(url);
        var req = new HttpRequestMessage(HttpMethod.Post, url + "/Users/AuthenticateByName") { Content = JsonContent.Create(new { Username = user, Pw = password }) };
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader(null));
        using var res = await _http.SendAsync(req);
        if ((int)res.StatusCode == 401) throw new Exception("Jellyfin rejected that user name or password.");
        res.EnsureSuccessStatusCode();
        var json = J.Parse(await res.Content.ReadAsStringAsync());
        S.JellyfinUrl = url;
        S.JellyfinUser = user;
        S.JellyfinToken = json.S("AccessToken");
        S.JellyfinUserId = json.O("User").S("Id");
        if (S.JellyfinToken == "") throw new Exception("Jellyfin did not return an access token.");
        S.Save();
    }

    public static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }

    public void Disconnect()
    {
        S.JellyfinToken = ""; S.JellyfinUserId = "";
        S.Save();
    }

    async Task<JsonElement> GetAsync(string pathAndQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, S.JellyfinUrl + pathAndQuery);
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader(S.JellyfinToken));
        using var res = await _http.SendAsync(req);
        if ((int)res.StatusCode == 401) throw new Exception("Jellyfin sign-in expired. Connect again in settings.");
        res.EnsureSuccessStatusCode();
        return J.Parse(await res.Content.ReadAsStringAsync());
    }

    async Task<List<JsonElement>> GetItemsAsync(string query, string what, Action<string> status, int page = 400)
    {
        var all = new List<JsonElement>();
        while (true)
        {
            var json = await GetAsync($"/Items?userId={S.JellyfinUserId}&{query}&SortBy=SortName&StartIndex={all.Count}&Limit={page}&EnableTotalRecordCount=true");
            var items = json.A("Items");
            all.AddRange(items);
            var total = json.L("TotalRecordCount");
            status($"jellyfin: {all.Count}{(total > 0 ? " of " + total : "")} {what}");
            if (items.Length < page || (total > 0 && all.Count >= total)) break;
        }
        return all;
    }

    string Image(string itemId, string tag, int width = 500) =>
        itemId == "" || tag == "" ? "" : $"{S.JellyfinUrl}/Items/{itemId}/Images/Primary?maxWidth={width}&tag={tag}";

    static bool ContainerIs(JsonElement item, string[] allowed) =>
        item.S("Container").ToLowerInvariant().Split(',').Any(allowed.Contains);

    public async Task<(List<Track> tracks, List<VideoItem> videos)> FetchAsync(Action<string> status)
    {
        if (!IsConnected) throw new Exception("Connect to a Jellyfin server first.");

        // ---- music: grouped by the server's own albums ----
        var albums = (await GetItemsAsync("IncludeItemTypes=MusicAlbum&Recursive=true", "albums", status)).ToDictionary(a => a.S("Id"), a => a);
        var tracks = (await GetItemsAsync("IncludeItemTypes=Audio&Recursive=true&Fields=Genres,DateCreated", "songs", status)).Select(i =>
        {
            albums.TryGetValue(i.S("AlbumId"), out var album);
            var artist = i.First("Artists");
            var albumArtist = album.S("AlbumArtist") != "" ? album.S("AlbumArtist") : i.S("AlbumArtist");
            if (artist == "") artist = albumArtist != "" ? albumArtist : "Unknown Artist";
            if (albumArtist == "") albumArtist = artist;
            var id = i.S("Id");
            var art = Image(i.S("AlbumId"), i.S("AlbumPrimaryImageTag"));
            if (art == "") art = Image(id, i.O("ImageTags").S("Primary"));
            return new Track
            {
                Source = "jellyfin", SourceId = id, GroupId = i.S("AlbumId"),
                Location = id + (ContainerIs(i, DirectAudio) ? "|direct" : "|transcode"),
                Title = i.S("Name"), Artist = artist, AlbumArtist = albumArtist,
                Album = album.S("Name") != "" ? album.S("Name") : i.S("Album") != "" ? i.S("Album") : "Unknown Album",
                Genre = i.First("Genres"), Year = (int)(i.L("ProductionYear") > 0 ? i.L("ProductionYear") : album.L("ProductionYear")),
                TrackNo = (int)i.L("IndexNumber"), DiscNo = (int)i.L("ParentIndexNumber"),
                DurationMs = i.L("RunTimeTicks") / 10000, ArtPath = art, DateAdded = ParseDate(i.S("DateCreated"))
            };
        }).ToList();

        // ---- collections (box sets): which movie belongs where ----
        var collectionOf = new Dictionary<string, string>();
        foreach (var box in await GetItemsAsync("IncludeItemTypes=BoxSet&Recursive=true", "collections", status))
            foreach (var child in (await GetAsync($"/Items?userId={S.JellyfinUserId}&ParentId={box.S("Id")}")).A("Items"))
                collectionOf.TryAdd(child.S("Id"), box.S("Name"));

        // ---- videos: movies, and episodes with their series and season ----
        var videos = (await GetItemsAsync("IncludeItemTypes=Movie,Episode,MusicVideo,Video&Recursive=true&Fields=DateCreated", "videos", status)).Select(i =>
        {
            var id = i.S("Id");
            var episode = i.S("Type") == "Episode";
            var seriesArt = Image(i.S("SeriesId"), i.S("SeriesPrimaryImageTag"), 400);
            var art = Image(id, i.O("ImageTags").S("Primary"));
            collectionOf.TryGetValue(id, out var collection);
            // Video always goes through the server's stream endpoint (it copies H.264 untouched and converts the rest):
            // opening the original file over HTTP took 40+ seconds to start in testing.
            var video = new VideoItem
            {
                Source = "jellyfin", SourceId = id, Location = id + "|transcode",
                Title = i.S("Name"), DurationMs = i.L("RunTimeTicks") / 10000,
                Kind = episode ? "episode" : i.S("Type") == "Movie" ? "movie" : "video",
                Series = episode ? i.S("SeriesName") : "", Season = episode ? (int)i.L("ParentIndexNumber") : 0, Episode = episode ? (int)i.L("IndexNumber") : 0,
                Year = (int)i.L("ProductionYear"), Collection = collection ?? "",
                ArtPath = art != "" ? art : seriesArt, GroupArt = seriesArt,
                DateAdded = ParseDate(i.S("DateCreated"))
            };
            // Episodes the server couldn't match to a series still usually carry "Show.S01E02.Title" style names.
            if (episode && video.Series == "")
            {
                var guess = new VideoItem();
                Library.Classify(guess, video.Title + ".x");
                if (guess.Kind == "episode" && guess.Series != "")
                {
                    video.Series = guess.Series; video.Title = guess.Title;
                    if (video.Season == 0) video.Season = guess.Season;
                    if (video.Episode == 0) video.Episode = guess.Episode;
                }
            }
            return video;
        }).ToList();
        return (tracks, videos);
    }

    public async Task<List<RemotePhoto>> FetchPhotosAsync()
    {
        if (!IsConnected) return new();
        var photos = await GetItemsAsync("IncludeItemTypes=Photo&Recursive=true&Fields=ParentId", "photos", _ => { });
        // Photos only carry their parent's id; look the album/folder names up in batches.
        var names = new Dictionary<string, string>();
        foreach (var chunk in photos.Select(p => p.S("ParentId")).Where(p => p != "").Distinct().Chunk(60))
            foreach (var parent in (await GetAsync($"/Items?userId={S.JellyfinUserId}&Ids={string.Join(",", chunk)}")).A("Items"))
                names[parent.S("Id")] = parent.S("Name");
        return photos.Select(p =>
        {
            var id = p.S("Id");
            return new RemotePhoto(names.GetValueOrDefault(p.S("ParentId"), "Photos"), p.S("Name"),
                $"{S.JellyfinUrl}/Items/{id}/Images/Primary?maxWidth=400", $"{S.JellyfinUrl}/Items/{id}/Images/Primary?maxWidth=2400");
        }).ToList();
    }

    /// <summary>Ebooks (EPUB/PDF) and audiobooks from the server's book libraries.</summary>
    public async Task<List<Book>> FetchBooksAsync(Action<string> status)
    {
        var books = new List<Book>();
        if (!IsConnected) return books;
        static string Author(JsonElement i) => i.First("Artists") is { Length: > 0 } a ? a : i.S("AlbumArtist");

        foreach (var i in await GetItemsAsync("IncludeItemTypes=Book&Recursive=true&Fields=Path,DateCreated", "books", status))
        {
            var ext = System.IO.Path.GetExtension(i.S("Path")).ToLowerInvariant();
            if (ext is not (".epub" or ".pdf")) continue;
            books.Add(new Book
            {
                Source = "jellyfin", SourceId = i.S("Id"), Kind = "ebook", Format = ext[1..], Title = i.S("Name"), Author = Author(i), Series = i.S("SeriesName"),
                ArtPath = Image(i.S("Id"), i.O("ImageTags").S("Primary"), 400), DateAdded = ParseDate(i.S("DateCreated"))
            });
        }

        // The server lists every audiobook file separately; files that share an album are one book.
        var files = await GetItemsAsync("IncludeItemTypes=AudioBook&Recursive=true&Fields=DateCreated", "audiobook files", status);
        foreach (var g in files.GroupBy(i => i.S("AlbumId") != "" ? i.S("AlbumId") : i.S("Id")))
        {
            var parts = g.OrderBy(i => i.L("ParentIndexNumber")).ThenBy(i => i.L("IndexNumber")).ThenBy(i => i.S("Name"), StringComparer.OrdinalIgnoreCase).ToList();
            var f = parts[0];
            var art = Image(f.S("AlbumId"), f.S("AlbumPrimaryImageTag"), 400);
            var book = new Book
            {
                Source = "jellyfin", SourceId = "audio:" + g.Key, Kind = "audio", Format = "audio",
                Title = f.S("Album") != "" ? f.S("Album") : f.S("Name"), Author = Author(f),
                ArtPath = art != "" ? art : Image(f.S("Id"), f.O("ImageTags").S("Primary"), 400), DateAdded = ParseDate(f.S("DateCreated")),
                Parts = parts.Select(i => new BookPart
                {
                    Location = i.S("Id") + (ContainerIs(i, DirectAudio) ? "|direct" : "|transcode"), Title = i.S("Name"), DurationMs = i.L("RunTimeTicks") / 10000
                }).ToList()
            };
            book.DurationMs = book.Parts.Sum(p => p.DurationMs);
            books.Add(book);
        }
        return books;
    }

    /// <summary>Fetches an ebook's file from the server (once) so the reader can open it.</summary>
    public async Task<string> DownloadBookAsync(Book b)
    {
        var dir = System.IO.Path.Combine(AppPaths.DataDir, "books", "downloads");
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, $"{b.SourceId}.{b.Format}");
        if (System.IO.File.Exists(file)) return file;
        var req = new HttpRequestMessage(HttpMethod.Get, $"{S.JellyfinUrl}/Items/{b.SourceId}/Download");
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader(S.JellyfinToken));
        using var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var temp = file + ".part";
        await using (var fs = System.IO.File.Create(temp)) await res.Content.CopyToAsync(fs);
        System.IO.File.Move(temp, file, true);
        return file;
    }

    static long ParseDate(string s) => DateTimeOffset.TryParse(s, out var d) ? d.ToUnixTimeSeconds() : Fmt.Now;

    // ---- streaming. Location is "<itemId>|direct" or "<itemId>|transcode" ----

    public static bool IsDirect(string location) => location.EndsWith("|direct");
    static string ItemId(string location) => location.Split('|')[0];
    static string Start(long startMs) => startMs > 0 ? $"&StartTimeTicks={startMs * 10000}" : "";

    string? _session;

    /// <summary>
    /// Jellyfin keys a running conversion by device + play session and hands the SAME stream back to a repeat request,
    /// ignoring a new start time. So each request gets a fresh session id, and the previous conversion is stopped.
    /// </summary>
    string NewSession()
    {
        var old = _session;
        _session = Guid.NewGuid().ToString("N");
        if (old != null) _ = StopEncodingAsync(old);
        return $"&PlaySessionId={_session}";
    }

    async Task StopEncodingAsync(string session)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, $"{S.JellyfinUrl}/Videos/ActiveEncodings?deviceId={S.DeviceId}&playSessionId={session}");
            req.Headers.TryAddWithoutValidation("Authorization", AuthHeader(S.JellyfinToken));
            using var res = await _http.SendAsync(req);
        }
        catch { /* best effort: the server also cleans up idle conversions itself */ }
    }

    public string AudioUrl(string location, long startMs = 0)
    {
        var id = ItemId(location);
        return IsDirect(location)
            ? $"{S.JellyfinUrl}/Audio/{id}/stream?static=true&api_key={S.JellyfinToken}"
            : $"{S.JellyfinUrl}/Audio/{id}/stream.mp3?DeviceId={S.DeviceId}&AudioCodec=mp3&AudioBitRate=320000&api_key={S.JellyfinToken}{Start(startMs)}{NewSession()}";
    }

    public string VideoUrl(string location, long startMs = 0)
    {
        var id = ItemId(location);
        return IsDirect(location)
            ? $"{S.JellyfinUrl}/Videos/{id}/stream?static=true&api_key={S.JellyfinToken}"
            : $"{S.JellyfinUrl}/Videos/{id}/stream.mp4?MediaSourceId={id}&DeviceId={S.DeviceId}&VideoCodec=h264&AudioCodec=aac&MaxAudioChannels=2" +
              $"&VideoBitrate=10000000&AudioBitrate=192000&api_key={S.JellyfinToken}{Start(startMs)}{NewSession()}";
    }
}
