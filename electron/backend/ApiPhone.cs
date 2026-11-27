using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Halcyon.Core;
using Halcyon.Services;

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

    static object PhoneState()
    {
        var url = _syncEnabled && Sync.IsRunning ? PhonePairing.QrUrl(Sync.Port) : null;
        return new
        {
            enabled = _syncEnabled, running = Sync.IsRunning, error = Sync.LastError ?? "", code = S.SyncCode,
            addresses = SyncServer.LocalAddresses(), name = Environment.MachineName, port = Sync.Port,
            qr = url != null ? "data:image/png;base64," + Convert.ToBase64String(PhonePairing.QrPng(url)) : "",
            qrUrl = url != null ? url[..url.IndexOf('#')] : "",
            phones = PhonePairing.Phones.OrderByDescending(p => p.LastSeen).Select(p => new
            {
                id = p.Id, name = p.Name,
                when = p.LastSync > 0 ? $"last synced {Fmt.Ago(p.LastSync)}" + (p.LastSummary != "" ? $" ({p.LastSummary})" : "")
                     : $"paired {Fmt.Ago(p.Paired)}, last seen {Fmt.Ago(p.LastSeen)}"
            })
        };
    }

    /// <summary>For the self-test: answers a phone's request to pair instead of asking.</summary>
    static bool? _pairAnswerForTest;

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

        if (Program.SmokeMode) PhonePairing.InMemory = true;
        // a phone asks to pair: the window comes forward and asks; a phone syncing shows in the status line
        PhonePairing.Asked += r =>
        {
            if (_pairAnswerForTest is { } answer) { PhonePairing.Decide(r.Id, answer); return; }
            Rpc.Emit("phone.ask", new { id = r.Id, name = r.Name, address = r.Address });
        };
        PhonePairing.Activity += (phone, text) => Rpc.Emit("status", text.StartsWith("done", StringComparison.OrdinalIgnoreCase)
            ? $"{phone.Name} synced: {text[4..].Trim(' ', ':')}" : $"{phone.Name}: {text}");
        PhonePairing.Changed += () => Rpc.Emit("phone", PhoneState());

        Rpc.On("phone.state", _ => PhoneState());
        Rpc.On("phone.decide", p =>
        {
            PhonePairing.Decide(p.Str("id"), p.Bool("allow"));
            Rpc.Emit("status", p.Bool("allow") ? $"{p.Str("name")} is paired with this computer" : $"{p.Str("name")} was not allowed to pair");
            return (object?)null;
        });
        Rpc.On("phone.forget", p => { PhonePairing.Forget(p.Str("id")); return PhoneState(); });
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
        if (Program.SmokeMode) Rpc.On("smoke.pair", async _ =>
        {
            PhonePairing.Reset();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Sync.Port}"), Timeout = TimeSpan.FromSeconds(20) };
            async Task<(int status, string body)> Pair(string token, string name)
            {
                using var res = await http.PostAsync("/sync/pair", new StringContent(System.Text.Json.JsonSerializer.Serialize(new { name, device = name, token })));
                return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
            }
            _pairAnswerForTest = false;
            var denied = await Pair("", "Test phone");
            _pairAnswerForTest = true;
            var allowed = await Pair("", "Test phone");
            _pairAnswerForTest = null;
            var key = JsonDocument.Parse(allowed.body).RootElement.GetProperty("key").GetString() ?? "";
            async Task<int> Library(string k)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/sync/library");
                req.Headers.Add("X-Halcyon-Code", k);
                req.Headers.Add("X-Halcyon-Progress", "done: 3 songs on the phone");
                using var res = await http.SendAsync(req);
                return (int)res.StatusCode;
            }
            var withKey = await Library(key);
            var listed = PhonePairing.Phones.FirstOrDefault();
            var badToken = await Pair("not-the-token", "QR phone");
            var qr = await Pair(PhonePairing.QrToken(), "QR phone");
            var page = await http.GetStringAsync("/pair");
            var info = await http.GetStringAsync("/sync/info");
            PhonePairing.Forget(listed?.Id ?? "");
            var afterForget = await Library(key);
            PhonePairing.Reset();
            return new
            {
                denied = denied.status, allowed = allowed.status, keyLength = key.Length, withKey, listedName = listed?.Name ?? "", lastSummary = listed?.LastSummary ?? "",
                badToken = badToken.status, qr = qr.status, pagePairs = page.Contains("intent://") && page.Contains("phoenix"), infoHasId = info.Contains("\"id\"") && info.Contains("\"pairing\":2"),
                afterForget, qrPng = PhonePairing.QrPng("http://192.168.1.2:47800/pair#t=x").Length
            };
        });
        // the phone remote: its page, then what is playing and a button press, with the pairing code
        if (Program.SmokeMode) Rpc.On("smoke.remote", async remoteParams =>
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Sync.Port}"), Timeout = TimeSpan.FromSeconds(10) };
            var page = await http.GetAsync("/remote");
            var pageText = await page.Content.ReadAsStringAsync();
            var refused = (int)(await http.GetAsync("/remote/state")).StatusCode;
            http.DefaultRequestHeaders.Add("X-Halcyon-Code", App.Settings.SyncCode);
            var state = await http.GetStringAsync("/remote/state");
            var press = await http.PostAsync("/remote/command", new StringContent("{\"cmd\":\"toggle\"}"));
            var after = await http.GetStringAsync("/remote/state");
            return new { pageStatus = (int)page.StatusCode, isPage = pageText.Contains("Phoenix remote"), refused, state, pressStatus = (int)press.StatusCode, after };
        });
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
