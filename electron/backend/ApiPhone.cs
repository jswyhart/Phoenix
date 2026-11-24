using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// Phone sync: the on/off switch and pairing code, and the "Phone sync" list (an ordinary playlist with that name, as on
/// Windows) that the phone page shows. The pairing code lives in the shared settings (encrypted there); whether the
/// server is on is kept in phone-sync.json beside them, so the shared core needs no change.
/// </summary>
public static partial class Api
{
    public const string PhoneListName = "Phone sync";
    public static readonly SyncServer Sync = new();
    static bool _syncEnabled;
    static string SyncFile => Path.Combine(AppPaths.DataDir, "phone-sync.json");

    static Playlist? PhoneList => Lib.Playlists.FirstOrDefault(p => p.Name == PhoneListName);

    static object PhoneState() => new
    {
        enabled = _syncEnabled, running = Sync.IsRunning, error = Sync.LastError ?? "", code = S.SyncCode,
        addresses = SyncServer.LocalAddresses(), name = Environment.MachineName, port = Sync.Port
    };

    static void SaveSync()
    {
        if (Program.SmokeMode) return;
        try { File.WriteAllText(SyncFile, JsonSerializer.Serialize(new { enabled = _syncEnabled })); } catch { }
        Save();
    }

    static void RegisterPhone()
    {
        try { _syncEnabled = JsonDocument.Parse(File.ReadAllText(SyncFile)).RootElement.GetProperty("enabled").GetBoolean(); } catch { _syncEnabled = false; }
        if (_syncEnabled && !Program.SmokeMode) Sync.Start();

        Rpc.On("phone.state", _ => PhoneState());
        Rpc.On("phone.enable", p =>
        {
            _syncEnabled = p.Bool("on");
            // the self-test listens on this machine only, on a port the system picks, so it cannot meet a running copy
            if (_syncEnabled) Sync.Start(Program.SmokeMode ? 0 : SyncServer.DefaultPort, loopbackOnly: Program.SmokeMode); else Sync.Stop();
            SaveSync();
            return PhoneState();
        });
        Rpc.On("phone.newCode", _ =>
        {
            S.SyncCode = SyncServer.NewCode();
            SaveSync();
            return PhoneState();
        });
        Rpc.On("phone.view", p =>
        {
            var sort = new SongSort();
            if (p.Str("sortKey") != "") sort.Set(p.Str("sortKey"), p.Bool("sortDesc"));
            var songs = sort.Apply(PhoneList is { } list ? Lib.TracksOf(list) : new List<Track>());
            return new { state = PhoneState(), songs = songs.Select(Song), total = Fmt.Time(songs.Sum(t => t.DurationMs)) };
        });
        // adds songs to the phone sync list (creating it on first use); only music on this computer can be copied
        Rpc.On("phone.add", p =>
        {
            var tracks = Tracks(p);
            var list = PhoneList ?? Lib.CreatePlaylist(PhoneListName);
            var have = list.TrackIds.ToHashSet();
            var fresh = tracks.Where(t => t.Source == "local" && have.Add(t.Id)).ToList();
            if (fresh.Count > 0) Lib.AddToPlaylist(list, fresh);
            return new { added = fresh.Count, skipped = tracks.Count(t => t.Source != "local") };
        });
        Rpc.On("phone.remove", p =>
        {
            if (PhoneList is not { } list) return;
            var gone = p.Longs("ids").ToHashSet();
            if (gone.Count == 0) return;
            list.TrackIds = list.TrackIds.Where(id => !gone.Contains(id)).ToList();
            Lib.SavePlaylist(list);
        });
        Rpc.On("phone.clear", _ =>
        {
            if (PhoneList is not { } list || list.TrackIds.Count == 0) return;
            list.TrackIds = new List<long>();
            Lib.SavePlaylist(list);
        });

        if (Program.SmokeMode) Rpc.On("smoke.sync", async _ => await SmokeSyncAsync());
    }

    /// <summary>
    /// Self-test: plays the phone's part against the running server over the loopback interface, with real sockets, and
    /// reports what came back.
    /// </summary>
    static async Task<object?> SmokeSyncAsync()
    {
        var port = Sync.Port;
        var code = S.SyncCode;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int status, byte[] body)> Get(string path, string? withCode)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (withCode != null) req.Headers.Add("X-Halcyon-Code", withCode);
            using var res = await http.SendAsync(req);
            return ((int)res.StatusCode, await res.Content.ReadAsByteArrayAsync());
        }
        async Task<int> PostStats(object items)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/sync/stats") { Content = new StringContent(JsonSerializer.Serialize(items), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Halcyon-Code", code);
            using var res = await http.SendAsync(req);
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("applied").GetInt32();
        }

        var info = await Get("/sync/info", null);
        var noCode = await Get("/sync/library", null);
        var wrongCode = await Get("/sync/library", code == "000000" ? "111111" : "000000");
        var library = await Get("/sync/library", code);
        using var doc = JsonDocument.Parse(library.body);
        var tracks = doc.RootElement.GetProperty("tracks").EnumerateArray().ToList();
        var first = tracks[0];
        var id = first.GetProperty("id").GetInt64();
        var file = await Get($"/sync/file/{id}", code);
        var art = await Get($"/sync/art/{id}", code);
        var missing = await Get("/sync/file/999999999", code);
        Track track = null!;
        MainLoop.Invoke(() => track = Lib.ById[id]);
        var onDisk = await File.ReadAllBytesAsync(track.SourceId);

        // a like made on the phone just now is taken; one older than what is here is not
        var before = (track.Rating, track.RatedAt);
        var newer = await PostStats(new[] { new { id, rating = 1, ratedAt = Fmt.Now + 100, playCount = track.PlayCount + 2, lastPlayed = Fmt.Now } });
        var afterNewer = (track.Rating, track.PlayCount);
        var older = await PostStats(new[] { new { id, rating = -1, ratedAt = 5L, playCount = 0, lastPlayed = 0L } });

        string discovery;
        using (var udp = new UdpClient())
        {
            var ask = Encoding.UTF8.GetBytes("HALCYON_DISCOVER");
            await udp.SendAsync(ask, ask.Length, "127.0.0.1", port);
            var wait = udp.ReceiveAsync();
            discovery = await Task.WhenAny(wait, Task.Delay(3000)) == wait ? Encoding.UTF8.GetString(wait.Result.Buffer) : "(no answer)";
        }

        return new
        {
            port,
            info = Encoding.UTF8.GetString(info.body), infoStatus = info.status,
            noCodeStatus = noCode.status, wrongCodeStatus = wrongCode.status, wrongCodeBody = Encoding.UTF8.GetString(wrongCode.body),
            libraryStatus = library.status, libraryName = doc.RootElement.GetProperty("name").GetString(),
            tracks = tracks.Count,
            firstTrack = first.GetRawText(),
            playlists = doc.RootElement.GetProperty("playlists").GetRawText(),
            fileStatus = file.status, fileMatches = file.body.AsSpan().SequenceEqual(onDisk), fileSize = file.body.Length, listedSize = first.GetProperty("size").GetInt64(),
            artStatus = art.status, artIsPng = art.body.Length > 8 && art.body[1] == 'P' && art.body[2] == 'N' && art.body[3] == 'G',
            missingStatus = missing.status,
            ratingBefore = before.Rating, appliedNewer = newer, ratingAfter = afterNewer.Rating, playCountAfter = afterNewer.PlayCount,
            appliedOlder = older, ratingAfterOlder = track.Rating,
            discovery, machine = Environment.MachineName
        };
    }
}
