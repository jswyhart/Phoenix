using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

public record PcTrack(long Id, string Title, string Artist, string AlbumArtist, string Album, long Size, long Modified, string Path,
    bool HasArt, int Rating, long RatedAt, int PlayCount, long LastPlayed)
{
    public string AlbumKey => SyncClient.AlbumKey(AlbumArtist, Album);
}
public record PcPlaylist(string Name, List<long> TrackIds);
public record PcLibrary(string Name, List<PcTrack> Tracks, List<PcPlaylist> Playlists);

/// <summary>What the phone keeps a copy of, and which files it has copied so far (stored as sync.json).</summary>
public class SyncState
{
    public List<string> Artists { get; set; } = new();
    public List<string> Albums { get; set; } = new();      // SyncClient.AlbumKey values
    public List<string> Playlists { get; set; } = new();
    public List<SyncedFile> Files { get; set; } = new();
    public long LastSync { get; set; }
}
public class SyncedFile
{
    public long PcId { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long Modified { get; set; }
}

/// <summary>
/// Copies chosen music from the Halcyon Windows app over the local network (the PC is the master collection),
/// brings its playlists along, and merges likes and play counts both ways: the most recent change wins.
/// </summary>
public class SyncClient
{
    public const int Port = 47800;
    /// <summary>
    /// The playlist the PC fills when music is dragged onto its phone icon. It is always synced when the PC has it,
    /// whether or not it is ticked on the phone.
    /// </summary>
    public const string PhoneListName = "Phone sync";

    /// <summary>True for a playlist that is synced: ticked here, or the PC's own "Phone sync" list.</summary>
    public bool IsChosen(string playlistName) =>
        string.Equals(playlistName, PhoneListName, StringComparison.OrdinalIgnoreCase) || State.Playlists.Contains(playlistName, StringComparer.OrdinalIgnoreCase);
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static Settings S => App.Settings;
    static string StatePath => Path.Combine(AppPaths.DataDir, "sync.json");

    public SyncState State { get; private set; } = new();
    public bool IsBusy { get; private set; }
    public bool IsPaired => S.SyncHost != "" && S.SyncCode != "";

    public SyncClient()
    {
        try { State = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath)) ?? new(); } catch { State = new(); }
    }

    public void SaveState() => File.WriteAllText(StatePath, JsonSerializer.Serialize(State));

    public static string AlbumKey(string albumArtist, string album) => $"{albumArtist}\u001f{album}".ToLowerInvariant();

    static string Base => "http://" + (S.SyncHost.Contains(':') ? S.SyncHost : $"{S.SyncHost}:{Port}");

    /// <summary>
    /// The PC's remote control page (play, pause, skip, volume and the queue of what is playing there). The pairing
    /// code rides in the part after "#", which the page reads and which is never sent over the network as part of the address.
    /// </summary>
    public static string RemoteUrl => $"{Base}/remote#code={Uri.EscapeDataString(S.SyncCode)}";

    // ---------- finding and talking to the PC ----------

    /// <summary>
    /// The broadcast addresses of the networks this phone is on (192.168.1.255 for 192.168.1.x): some routers pass
    /// these on when they drop the all-networks broadcast.
    /// </summary>
    static List<System.Net.IPAddress> Broadcasts()
    {
        var list = new List<System.Net.IPAddress> { System.Net.IPAddress.Broadcast };
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a.Address) || a.IPv4Mask == null) continue;
                    var ip = a.Address.GetAddressBytes();
                    var mask = a.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var b2 = new byte[4];
                    for (var i = 0; i < 4; i++) b2[i] = (byte)(ip[i] | ~mask[i]);
                    var cast = new System.Net.IPAddress(b2);
                    if (!list.Contains(cast)) list.Add(cast);
                }
            }
        }
        catch { /* the all-networks broadcast alone */ }
        return list;
    }

    /// <summary>Asks the local network who is running Halcyon with phone sync on. Returns (name, address).</summary>
    public static async Task<List<(string name, string address)>> DiscoverAsync(int waitMs = 1500)
    {
        var found = new List<(string, string)>();
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var ask = Encoding.UTF8.GetBytes("HALCYON_DISCOVER");
            foreach (var cast in Broadcasts())
                try { await udp.SendAsync(ask, ask.Length, new System.Net.IPEndPoint(cast, Port)); } catch { }
            using var cts = new CancellationTokenSource(waitMs);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token);
                var parts = Encoding.UTF8.GetString(reply.Buffer).Split('|');
                if (parts.Length == 3 && parts[0] == "HALCYON_HERE") found.Add((parts[1], $"{reply.RemoteEndPoint.Address}:{parts[2]}"));
            }
        }
        catch { /* timeout ends the search; no network means nothing found */ }
        return found.Distinct().ToList();
    }

    // ---------- pairing ----------

    static readonly HttpClient Quick = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>What a computer says about itself: its name, lasting id, and whether it can be paired with by asking (2) or only by code (1).</summary>
    public record PcInfo(string Name, string Id, int Pairing);

    public static async Task<PcInfo?> InfoAsync(string host)
    {
        try
        {
            var j = J.Parse(await Quick.GetStringAsync($"http://{(host.Contains(':') ? host : $"{host}:{Port}")}/sync/info"));
            return j.S("app") is "Halcyon" or "Phoenix" ? new PcInfo(j.S("name"), j.S("id"), Math.Max(1, (int)j.L("pairing"))) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Pairs with a computer: by asking (the person there chooses Allow; this waits up to two minutes) or with the
    /// token from the QR code shown there (at once). The computer hands back this phone's own key, kept instead of a code.
    /// </summary>
    public static async Task PairAsync(string host, string token, string phoneName)
    {
        if (S.SyncDeviceId == "") S.SyncDeviceId = Guid.NewGuid().ToString("N");
        var info = await InfoAsync(host) ?? throw new Exception("Can't reach Phoenix at " + host + ". Check that the PC and the phone are on the same Wi-Fi network, and that phone sync is turned on there.");
        if (info.Pairing < 2) throw new Exception("Phoenix on that PC is older and can only pair with its six-digit code: update it, or enter the code.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2.5) };
        var body = JsonSerializer.Serialize(new { name = phoneName, device = S.SyncDeviceId, token });
        HttpResponseMessage res;
        try { res = await http.PostAsync($"http://{(host.Contains(':') ? host : $"{host}:{Port}")}/sync/pair", new StringContent(body, Encoding.UTF8, "application/json")); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new Exception("The PC stopped answering. Try again."); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            JsonElement j;
            try { j = J.Parse(text); } catch { j = default; }
            if (!res.IsSuccessStatusCode)
                throw new Exception((int)res.StatusCode == 403 && token == "" ? $"{info.Name} said no." : j.ValueKind == JsonValueKind.Object && j.S("error") != "" ? Upper(j.S("error")) + "." : "Pairing failed.");
            S.SyncHost = host;
            S.SyncCode = j.S("key");
            S.SyncPcId = j.S("id");
            S.SyncPcName = j.S("pc") is { Length: > 0 } pc ? pc : info.Name;
            S.Save();
        }
    }

    static string Upper(string s) => s == "" ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Forgets the PC on this phone (the music already copied stays).</summary>
    public static void Unpair()
    {
        S.SyncHost = S.SyncCode = S.SyncPcId = S.SyncPcName = "";
        S.Save();
    }

    /// <summary>
    /// The PC answered at a new address (its network gave it another one): looks for it on the network by its id (or its
    /// name, for a PC paired by code) and remembers where it is now.
    /// </summary>
    public static async Task<bool> FindAgainAsync()
    {
        if (S.SyncPcId == "" && S.SyncPcName == "") return false;
        foreach (var (name, address) in await DiscoverAsync())
        {
            if (address == S.SyncHost) continue;
            var info = await InfoAsync(address);
            if (info == null) continue;
            if (S.SyncPcId != "" ? info.Id == S.SyncPcId : string.Equals(info.Name, S.SyncPcName, StringComparison.OrdinalIgnoreCase))
            {
                S.SyncHost = address;
                S.Save();
                return true;
            }
        }
        return false;
    }

    /// <summary>Is the PC there now? Quick, so a sync by itself never hangs away from home. Looks for it at a new address when it isn't where it was.</summary>
    public static async Task<bool> ReachableAsync()
    {
        if (S.SyncHost == "") return false;
        if (await InfoAsync(S.SyncHost) is { } info)
        {
            if (S.SyncPcId == "" && info.Id != "" && (S.SyncPcName == "" || S.SyncPcName == info.Name)) { S.SyncPcId = info.Id; S.SyncPcName = info.Name; S.Save(); }
            return true;
        }
        return await FindAgainAsync();
    }

    /// <summary>What this phone is doing, told to the PC with each request so it can show it ("copying 3 of 40").</summary>
    string _progress = "";

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? body = null, bool retried = false)
    {
        if (S.SyncHost == "") throw new Exception("Choose the PC to sync with first.");
        var req = new HttpRequestMessage(method, Base + path) { Content = body };
        req.Headers.TryAddWithoutValidation("X-Halcyon-Code", S.SyncCode);
        if (_progress != "") req.Headers.TryAddWithoutValidation("X-Halcyon-Progress", Uri.EscapeDataString(_progress));
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // the PC may have a new address: find it and try once more (a body can only be sent once)
            if (!retried && body == null && await FindAgainAsync()) return await SendAsync(method, path, null, true);
            throw new Exception($"Can't reach {(S.SyncPcName != "" ? S.SyncPcName : "the PC")}. Check that Phoenix is open there with phone sync turned on, and that both devices are on the same Wi-Fi network.");
        }
        if ((int)res.StatusCode == 401) { res.Dispose(); throw new Exception(S.SyncCode.Length > 6 ? "The PC doesn't know this phone any more (it was forgotten there): pair again." : "The PC rejected the pairing code. Enter the code shown in Phoenix's settings on the PC."); }
        res.EnsureSuccessStatusCode();
        return res;
    }

    public async Task<PcLibrary> FetchLibraryAsync()
    {
        using var res = await SendAsync(HttpMethod.Get, "/sync/library");
        return ParseLibrary(await res.Content.ReadAsStringAsync());
    }

    public static PcLibrary ParseLibrary(string json)
    {
        var root = J.Parse(json);
        var tracks = root.A("tracks").Select(t => new PcTrack(t.L("id"), t.S("title"), t.S("artist"), t.S("albumArtist"), t.S("album"), t.L("size"), t.L("modified"),
            t.S("path"), t.O("hasArt").ValueKind == JsonValueKind.True, (int)t.L("rating"), t.L("ratedAt"), (int)t.L("playCount"), t.L("lastPlayed"))).ToList();
        var playlists = root.A("playlists").Select(p => new PcPlaylist(p.S("name"), p.A("trackIds").Select(i => i.GetInt64()).ToList())).ToList();
        return new PcLibrary(root.S("name"), tracks, playlists);
    }

    // ---------- choosing ----------

    /// <summary>
    /// The songs the current selection asks for: chosen artists and albums, plus everything in chosen playlists and in
    /// the PC's "Phone sync" playlist.
    /// </summary>
    public List<PcTrack> Wanted(PcLibrary lib)
    {
        var artists = State.Artists.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var albums = State.Albums.ToHashSet();
        var inPlaylists = lib.Playlists.Where(p => IsChosen(p.Name)).SelectMany(p => p.TrackIds).ToHashSet();
        return lib.Tracks.Where(t => artists.Contains(t.AlbumArtist) || albums.Contains(t.AlbumKey) || inPlaylists.Contains(t.Id)).ToList();
    }

    public static void Toggle(List<string> list, string value)
    {
        var i = list.FindIndex(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list.RemoveAt(i); else list.Add(value);
    }

    // ---------- syncing ----------

    /// <summary>Keeps a file inside the sync folder whatever the PC sends as its path.</summary>
    static string LocalPath(string root, string relative)
    {
        var parts = relative.Split('/', '\\').Select(p => p.Trim()).Where(p => p != "" && p != "." && p != "..")
            .Select(p => new string(p.Select(c => c is ':' or '?' or '*' or '"' or '<' or '>' or '|' || c < ' ' ? '_' : c).ToArray()));
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    /// <summary>Brings the phone in line with the selection. Returns a one-line summary.</summary>
    public async Task<string> SyncAsync(string folder, Action<string> status)
    {
        if (IsBusy) return "a sync is already running";
        IsBusy = true;
        try
        {
            status("sync: reading the PC's collection…");
            _progress = "reading the collection";
            var lib = await FetchLibraryAsync();
            var wanted = Wanted(lib);
            var wantedIds = wanted.Select(t => t.Id).ToHashSet();
            var have = State.Files.ToDictionary(f => f.PcId);

            // 1. remove copies that are no longer chosen (or no longer on the PC)
            var removed = 0;
            foreach (var f in State.Files.Where(f => !wantedIds.Contains(f.PcId)).ToList())
            {
                try { if (File.Exists(f.Path)) File.Delete(f.Path); } catch { }
                RemoveEmptyFolders(Path.GetDirectoryName(f.Path), folder);
                State.Files.Remove(f);
                removed++;
            }

            // 2. copy what is missing or has changed on the PC
            var todo = wanted.Where(t => !have.TryGetValue(t.Id, out var f) || f.Size != t.Size || f.Modified != t.Modified || !File.Exists(f.Path)).ToList();
            var bytes = todo.Sum(t => t.Size);
            Directory.CreateDirectory(folder);
            long free = 0;
            try { free = new DriveInfo(folder).AvailableFreeSpace; } catch { /* unknown: just try */ }
            if (free > 0 && bytes > free - 200_000_000)
                throw new Exception($"Not enough room on the phone: {Size(bytes)} to copy, {Size(free)} free. Choose less to sync.");

            long done = 0;
            var copied = 0;
            var failed = 0;
            string? firstError = null;
            var artDone = new HashSet<string>();
            foreach (var t in todo)
            {
                status($"sync: copying {copied + 1} of {todo.Count}  ·  {Size(done)} of {Size(bytes)}  ·  {t.Title}");
                _progress = $"copying {copied + 1} of {todo.Count} ({Size(done)} of {Size(bytes)})";
                var path = LocalPath(folder, t.Path);
                try
                {
                    var dir = Path.GetDirectoryName(path)!;
                    Directory.CreateDirectory(dir);
                    await DownloadAsync($"/sync/file/{t.Id}", path);
                    // Covers go to Halcyon's own art cache (Android's shared Music folder only accepts audio files),
                    // under the key the scanner looks up for this album.
                    var art = Library.ArtCachePath("local", t.AlbumArtist, t.Album);
                    if (t.HasArt && artDone.Add(art) && !File.Exists(art))
                        try { await DownloadAsync($"/sync/art/{t.Id}", art); App.ArtInvalidate(art); } catch { }
                    if (have.TryGetValue(t.Id, out var old))
                    {
                        if (old.Path != path) try { File.Delete(old.Path); } catch { }
                        State.Files.Remove(old);
                    }
                    State.Files.Add(new SyncedFile { PcId = t.Id, Path = path, Size = t.Size, Modified = t.Modified });
                    copied++;
                    if (copied % 10 == 0) SaveState();
                }
                catch (Exception ex) when (ex.Message.StartsWith("Can't reach") || ex.Message.StartsWith("The PC rejected")) { SaveState(); throw; }
                catch (Exception ex) { failed++; firstError ??= ex.Message; }
                done += t.Size;
            }
            State.LastSync = Fmt.Now;
            SaveState();

            // 3. let the collection see the new files
            status("sync: updating the collection…");
            while (App.Lib.IsScanning) await Task.Delay(200);
            await App.Lib.ScanAsync(_ => { });

            // 4. likes and play counts, both ways; playlists from the PC
            var byPath = App.Lib.Tracks.Where(t => t.IsLocal).ToDictionary(t => t.SourceId);
            var pc = lib.Tracks.ToDictionary(t => t.Id);
            var local = new Dictionary<long, Track>();   // PC track id -> the phone's copy
            foreach (var f in State.Files)
                if (byPath.TryGetValue(f.Path, out var t)) local[f.PcId] = t;
            var back = new List<object>();
            var changed = new List<Track>();
            foreach (var (id, t) in local)
            {
                if (!pc.TryGetValue(id, out var p)) continue;
                var newerHere = t.RatedAt > p.RatedAt || t.PlayCount > p.PlayCount || t.LastPlayed > p.LastPlayed;
                if (p.RatedAt > t.RatedAt || p.PlayCount > t.PlayCount || p.LastPlayed > t.LastPlayed)
                {
                    if (p.RatedAt > t.RatedAt) { t.Rating = p.Rating; t.RatedAt = p.RatedAt; }
                    t.PlayCount = Math.Max(t.PlayCount, p.PlayCount);
                    t.LastPlayed = Math.Max(t.LastPlayed, p.LastPlayed);
                    changed.Add(t);
                }
                if (newerHere) back.Add(new { id, rating = t.Rating, ratedAt = t.RatedAt, playCount = t.PlayCount, lastPlayed = t.LastPlayed });
            }
            if (changed.Count > 0) App.Lib.StoreStats(changed);
            if (back.Count > 0)
                try { using var _ = await SendAsync(HttpMethod.Post, "/sync/stats", new StringContent(JsonSerializer.Serialize(back), Encoding.UTF8, "application/json")); }
                catch { /* the PC went away: the likes go across on the next sync */ }

            var lists = 0;
            foreach (var p in lib.Playlists.Where(p => IsChosen(p.Name)))
            {
                var target = App.Lib.Playlists.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)) ?? App.Lib.CreatePlaylist(p.Name);
                target.TrackIds = p.TrackIds.Where(local.ContainsKey).Select(id => local[id].Id).ToList();
                App.Lib.SavePlaylist(target);
                lists++;
            }

            status("");
            var summary = Fmt.Join(copied > 0 ? $"{copied} copied" : "", removed > 0 ? $"{removed} removed" : "", failed > 0 ? $"{failed} failed ({firstError})" : "",
                       changed.Count > 0 ? $"{changed.Count} likes/plays from the PC" : "", back.Count > 0 ? $"{back.Count} likes/plays sent to the PC" : "",
                       lists > 0 ? Fmt.Plural(lists, "playlist") + " updated" : "") is { Length: > 0 } s ? s : "already up to date";
            // the PC shows when this phone last synced, and what it did
            _progress = $"done: {Fmt.Plural(State.Files.Count, "song")} on the phone, {summary}";
            try { using var _ = await SendAsync(HttpMethod.Get, "/sync/done"); } catch { }
            return summary;
        }
        finally
        {
            IsBusy = false;
            _progress = "";
            status("");
        }
    }

    async Task DownloadAsync(string path, string target)
    {
        // The temporary name keeps the real extension: Android refuses non-audio file names in the shared Music folder.
        var part = Path.Combine(Path.GetDirectoryName(target)!, ".sync-" + Path.GetFileName(target));
        using (var res = await SendAsync(HttpMethod.Get, path))
        await using (var file = File.Create(part))
            await res.Content.CopyToAsync(file);
        File.Move(part, target, true);
    }

    static void RemoveEmptyFolders(string? dir, string root)
    {
        try
        {
            while (!string.IsNullOrEmpty(dir) && dir.Length > root.Length && dir.StartsWith(root))
            {
                if (Directory.GetFileSystemEntries(dir).Length > 0) return;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
    }

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(bytes > 0 ? 1 : 0, bytes / (1L << 20))} MB";
}
