using System.Net.Http;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

public record PlexServer(string Name, string Uri, string Token)
{
    public override string ToString() => $"{Name}  —  {Uri}";
}

/// <summary>Reads music and video libraries from a Plex Media Server and builds stream URLs.</summary>
public class PlexClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static Settings S => App.Settings;

    public bool IsConnected => S.PlexUrl != "" && S.PlexToken != "";

    HttpRequestMessage Request(HttpMethod method, string url, string? token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", S.DeviceId);
        req.Headers.TryAddWithoutValidation("X-Plex-Product", "Phoenix");
        req.Headers.TryAddWithoutValidation("X-Plex-Version", "1.0.0");
        if (token is { Length: > 0 }) req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        return req;
    }

    async Task<JsonElement> SendAsync(HttpRequestMessage req)
    {
        using var res = await _http.SendAsync(req);
        if ((int)res.StatusCode == 401) throw new Exception("Plex rejected the token. Sign in again in settings.");
        res.EnsureSuccessStatusCode();
        return J.Parse(await res.Content.ReadAsStringAsync());
    }

    Task<JsonElement> GetAsync(string pathAndQuery) => SendAsync(Request(HttpMethod.Get, S.PlexUrl + pathAndQuery, S.PlexToken));

    // ---- plex.tv PIN sign-in: the user approves this app in their browser, no password is typed here ----

    public async Task<(long id, string code, string url)> CreatePinAsync()
    {
        var json = await SendAsync(Request(HttpMethod.Post, "https://plex.tv/api/v2/pins?strong=true", null));
        var code = json.S("code");
        var url = $"https://app.plex.tv/auth#?clientID={S.DeviceId}&code={code}&context%5Bdevice%5D%5Bproduct%5D=Phoenix";
        return (json.L("id"), code, url);
    }

    public async Task<string> CheckPinAsync(long id) =>
        (await SendAsync(Request(HttpMethod.Get, $"https://plex.tv/api/v2/pins/{id}", null))).S("authToken");

    public async Task<List<PlexServer>> GetServersAsync(string accountToken)
    {
        var json = await SendAsync(Request(HttpMethod.Get, "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1", accountToken));
        var list = new List<PlexServer>();
        if (json.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in json.EnumerateArray().Where(r => r.S("provides").Contains("server")))
            foreach (var c in r.A("connections").OrderByDescending(c => c.O("local").ValueKind == JsonValueKind.True))
                list.Add(new PlexServer(r.S("name"), c.S("uri"), r.S("accessToken") != "" ? r.S("accessToken") : accountToken));
        return list;
    }

    public async Task TestAsync() => await GetAsync("/identity");

    // ---- library ----

    async Task<List<JsonElement>> GetAllAsync(string sectionKey, int type, string what, Action<string> status)
    {
        var all = new List<JsonElement>();
        const int page = 500;
        while (true)
        {
            var mc = (await GetAsync($"/library/sections/{sectionKey}/all?type={type}&X-Plex-Container-Start={all.Count}&X-Plex-Container-Size={page}")).O("MediaContainer");
            var items = mc.A("Metadata");
            all.AddRange(items);
            var total = mc.L("totalSize");
            status($"plex: {all.Count}{(total > 0 ? " of " + total : "")} {what}");
            if (items.Length < page || (total > 0 && all.Count >= total)) break;
        }
        return all;
    }

    string ArtUrl(string thumb) => thumb == "" ? "" :
        $"{S.PlexUrl}/photo/:/transcode?width=500&height=500&minSize=1&upscale=1&url={Uri.EscapeDataString(thumb)}&X-Plex-Token={S.PlexToken}";

    static string PartKey(JsonElement item)
    {
        var media = item.A("Media");
        if (media.Length == 0) return "";
        var parts = media[0].A("Part");
        return parts.Length == 0 ? "" : parts[0].S("key");
    }

    public async Task<(List<Track> tracks, List<VideoItem> videos)> FetchAsync(Action<string> status)
    {
        if (!IsConnected) throw new Exception("Connect to a Plex server first.");
        var tracks = new List<Track>();
        var videos = new List<VideoItem>();
        var sections = (await GetAsync("/library/sections")).O("MediaContainer").A("Directory");

        foreach (var sec in sections)
        {
            var key = sec.S("key");
            switch (sec.S("type"))
            {
                case "artist":
                    // Genre and year live on the album, not the track.
                    var albums = (await GetAllAsync(key, 9, "albums", status)).ToDictionary(a => a.S("ratingKey"), a => a);
                    foreach (var i in await GetAllAsync(key, 10, "songs", status))
                    {
                        var part = PartKey(i);
                        if (part == "") continue;
                        albums.TryGetValue(i.S("parentRatingKey"), out var album);
                        var albumArtist = i.S("grandparentTitle") == "" ? "Unknown Artist" : i.S("grandparentTitle");
                        var genres = album.A("Genre");
                        tracks.Add(new Track
                        {
                            Source = "plex", SourceId = i.S("ratingKey"), Location = part, GroupId = i.S("parentRatingKey"),
                            Title = i.S("title"), AlbumArtist = albumArtist,
                            Artist = i.S("originalTitle") != "" ? i.S("originalTitle") : albumArtist,
                            Album = i.S("parentTitle") == "" ? "Unknown Album" : i.S("parentTitle"),
                            Genre = genres.Length > 0 ? genres[0].S("tag") : "",
                            Year = (int)(i.L("parentYear") > 0 ? i.L("parentYear") : album.L("year")),
                            TrackNo = (int)i.L("index"), DiscNo = (int)i.L("parentIndex"), DurationMs = i.L("duration"),
                            ArtPath = ArtUrl(i.S("parentThumb") != "" ? i.S("parentThumb") : i.S("thumb")),
                            DateAdded = i.L("addedAt") > 0 ? i.L("addedAt") : Fmt.Now
                        });
                    }
                    break;
                case "movie":
                    foreach (var i in await GetAllAsync(key, 1, "movies", status))
                    {
                        var collections = i.A("Collection");
                        AddVideo(i, new VideoItem { Kind = "movie", Year = (int)i.L("year"), Collection = collections.Length > 0 ? collections[0].S("tag") : "" });
                    }
                    break;
                case "show":
                    foreach (var i in await GetAllAsync(key, 4, "episodes", status))
                        AddVideo(i, new VideoItem
                        {
                            Kind = "episode", Series = i.S("grandparentTitle"), Season = (int)i.L("parentIndex"), Episode = (int)i.L("index"),
                            Year = (int)i.L("year"), GroupArt = ArtUrl(i.S("grandparentThumb"))
                        });
                    break;
            }
        }
        return (tracks, videos);

        void AddVideo(JsonElement i, VideoItem v)
        {
            var part = PartKey(i);
            if (part == "") return;
            v.Source = "plex"; v.SourceId = i.S("ratingKey"); v.Location = part; v.Title = i.S("title"); v.DurationMs = i.L("duration");
            v.ArtPath = ArtUrl(i.S("thumb")); v.DateAdded = i.L("addedAt") > 0 ? i.L("addedAt") : Fmt.Now;
            videos.Add(v);
        }
    }

    public async Task<List<RemotePhoto>> FetchPhotosAsync()
    {
        var photos = new List<RemotePhoto>();
        if (!IsConnected) return photos;
        foreach (var sec in (await GetAsync("/library/sections")).O("MediaContainer").A("Directory").Where(d => d.S("type") == "photo"))
            foreach (var i in await GetAllAsync(sec.S("key"), 13, "photos", _ => { }))
            {
                var part = PartKey(i);
                if (part != "") photos.Add(new RemotePhoto(i.S("parentTitle") != "" ? i.S("parentTitle") : sec.S("title"), i.S("title"), ArtUrl(i.S("thumb")), PartUrl(part)));
            }
        return photos;
    }

    /// <summary>Direct URL to the original media file on the server.</summary>
    public string PartUrl(string partKey) => $"{S.PlexUrl}{partKey}?X-Plex-Token={S.PlexToken}";
}
