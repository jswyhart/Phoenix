using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// Lets the phone app copy music from this computer over the local network: the same server the Windows app has
/// (Services\SyncServer.cs), speaking the same protocol, because the Android app is the client for both.
/// The computer is the master collection: the phone reads the list of local songs and playlists, downloads the files it
/// wants, and likes are merged both ways (the most recent change wins). Every request must carry the pairing code shown
/// in settings. A tiny HTTP server on a plain TCP socket, plus a UDP responder so the phone can find the computer
/// without typing its address.
/// </summary>
public class SyncServer
{
    public const int DefaultPort = 47800;
    TcpListener? _tcp;
    UdpClient? _udp;
    CancellationTokenSource? _cts;

    public int Port { get; private set; } = DefaultPort;
    public bool IsRunning => _tcp != null;
    public string? LastError { get; private set; }

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");

    /// <summary>The addresses a phone on the same network can reach this computer at.</summary>
    public static List<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => a.Address.ToString()).Distinct().ToList();
        }
        catch { return new(); }
    }

    /// <summary>Starts listening. The phone app expects port 47800; the self-test passes another so it can't collide with a running copy.</summary>
    public void Start(int port = DefaultPort, bool loopbackOnly = false)
    {
        if (IsRunning) return;
        try
        {
            if (App.Settings.SyncCode == "") { App.Settings.SyncCode = NewCode(); if (!Program.SmokeMode) App.Settings.Save(); }
            var address = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
            _cts = new CancellationTokenSource();
            // a port the system picks is free for TCP but may be one it keeps back for UDP: then try another
            for (var attempt = 0; ; attempt++)
            {
                _tcp = new TcpListener(address, port);
                _tcp.Start();
                Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                try { _udp = new UdpClient(new IPEndPoint(address, Port)); break; }
                catch (SocketException) when (port == 0 && attempt < 20) { _tcp.Stop(); }
            }
            LastError = null;
            _ = AcceptLoopAsync(_tcp, _cts.Token);
            _ = DiscoveryLoopAsync(_udp, Port, _cts.Token);
        }
        catch (Exception ex)
        {
            LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? $"Port {port} is already in use: another copy of Phoenix (or Halcyon) with phone sync on is running on this computer."
                : ex.Message;
            Stop();
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _tcp = null; _udp = null; _cts = null;
    }

    // ---------- discovery: the phone broadcasts a question, we answer with our name and port ----------

    static async Task DiscoveryLoopAsync(UdpClient udp, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var msg = await udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(msg.Buffer) != "HALCYON_DISCOVER") continue;
                var reply = Encoding.UTF8.GetBytes($"HALCYON_HERE|{Environment.MachineName}|{port}");
                await udp.SendAsync(reply, reply.Length, msg.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { /* a malformed or unreachable datagram: keep listening */ }
        }
    }

    // ---------- http ----------

    async Task AcceptLoopAsync(TcpListener tcp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    static async Task<Request?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[16384];
        var len = 0;
        var end = -1;
        while (end < 0 && len < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len), ct);
            if (n == 0) return null;
            len += n;
            end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
        }
        if (end < 0) return null;
        var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var size) && size is > 0 and <= 8_000_000)
        {
            body = new byte[size];
            var have = Math.Min(size, len - (end + 4));
            Array.Copy(buf, end + 4, body, 0, have);
            while (have < size)
            {
                var n = await s.ReadAsync(body.AsMemory(have, size - have), ct);
                if (n == 0) return null;
                have += n;
            }
        }
        return new Request(first[0], Uri.UnescapeDataString(first[1].Split('?')[0]), headers, body);
    }

    static async Task WriteHeadAsync(Stream s, int status, string type, long length, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
    }

    static async Task SendAsync(Stream s, int status, string text, CancellationToken ct, string type = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteHeadAsync(s, status, type, bytes.Length, ct);
        await s.WriteAsync(bytes, ct);
    }

    static async Task SendFileAsync(Stream s, string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
        await WriteHeadAsync(s, 200, "application/octet-stream", f.Length, ct);
        await f.CopyToAsync(s, 1 << 16, ct);
    }

    static bool CodeMatches(string given)
    {
        byte[] a = Encoding.UTF8.GetBytes(given), b = Encoding.UTF8.GetBytes(App.Settings.SyncCode);
        return b.Length > 0 && CryptographicOperations.FixedTimeEquals(a, b);
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var s = client.GetStream();
            var req = await ReadRequestAsync(s, ct);
            if (req == null) return;

            if (req.Path == "/sync/info")   // lets the phone check it found the right kind of computer before asking for the code
            {
                // the phone app looks for app = "Halcyon" (its name before the rename), so that is what every build answers
                await SendAsync(s, 200, JsonSerializer.Serialize(new { name = Environment.MachineName, app = "Halcyon", version = 1 }), ct);
                return;
            }
            if (!req.Headers.TryGetValue("X-Halcyon-Code", out var code) || !CodeMatches(code))
            {
                await Task.Delay(400, ct);   // slows down guessing the 6-digit code
                await SendAsync(s, 401, "{\"error\":\"wrong pairing code\"}", ct);
                return;
            }

            // the library is only touched on the helper's working thread; requests arrive on others
            var tracks = new List<Track>();
            MainLoop.Invoke(() => tracks = App.Lib.Tracks.Where(t => t.IsLocal).ToList());
            if (req.Path == "/sync/library")
            {
                var json = "";
                MainLoop.Invoke(() => json = LibraryJson(tracks));
                await SendAsync(s, 200, json, ct);
            }
            else if (req.Path.StartsWith("/sync/file/") && Find(tracks, req.Path) is { } t && File.Exists(t.SourceId))
                await SendFileAsync(s, t.SourceId, ct);
            else if (req.Path.StartsWith("/sync/art/") && Find(tracks, req.Path) is { } a && IsLocalFile(a.ArtPath))
                await SendFileAsync(s, a.ArtPath, ct);
            else if (req.Path == "/sync/stats" && req.Method == "POST")
                await SendAsync(s, 200, JsonSerializer.Serialize(new { applied = ApplyStats(req.Body) }), ct);
            else
                await SendAsync(s, 404, "{\"error\":\"not found\"}", ct);
        }
        catch { /* the phone went away mid-transfer; it retries on its next sync */ }
    }

    static Track? Find(List<Track> tracks, string path) =>
        long.TryParse(path[(path.LastIndexOf('/') + 1)..], out var id) ? tracks.FirstOrDefault(t => t.Id == id) : null;

    static bool IsLocalFile(string art) => art != "" && !art.Contains("://") && !art.StartsWith("shell:") && !art.StartsWith("thumb:") && File.Exists(art);

    // the characters Windows refuses in file names, on every system, so the phone gets the same folders whichever computer it syncs with
    static readonly HashSet<char> BadChars = new("\"<>|:*?\\/".Concat(Enumerable.Range(0, 32).Select(i => (char)i)));

    static string Safe(string name)
    {
        var clean = new string(name.Select(c => BadChars.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean == "" ? "Unknown" : clean.Length > 80 ? clean[..80].Trim() : clean;
    }

    /// <summary>Local songs (with where the phone should put them) and the playlists that contain any of them.</summary>
    public static string LibraryJson(List<Track> tracks)
    {
        var ids = tracks.Select(t => t.Id).ToHashSet();
        var list = tracks.Where(t => File.Exists(t.SourceId)).Select(t =>
        {
            var info = new FileInfo(t.SourceId);
            return new
            {
                id = t.Id, title = t.Title, artist = t.Artist, albumArtist = t.AlbumArtist, album = t.Album, genre = t.Genre,
                year = t.Year, trackNo = t.TrackNo, discNo = t.DiscNo, durationMs = t.DurationMs,
                size = info.Length, modified = info.LastWriteTimeUtc.Ticks,
                path = $"{Safe(t.AlbumArtist)}/{Safe(t.Album)}/{Safe(Path.GetFileName(t.SourceId))}",
                hasArt = IsLocalFile(t.ArtPath),
                rating = t.Rating, ratedAt = t.RatedAt, playCount = t.PlayCount, lastPlayed = t.LastPlayed
            };
        }).ToList();
        var playlists = App.Lib.Playlists.Select(p => new { name = p.Name, trackIds = p.TrackIds.Where(ids.Contains).ToList() }).Where(p => p.trackIds.Count > 0).ToList();
        return JsonSerializer.Serialize(new { name = Environment.MachineName, tracks = list, playlists });
    }

    /// <summary>Likes and play counts coming back from the phone: a rating is taken only when it was changed more recently there.</summary>
    static int ApplyStats(byte[] body)
    {
        var applied = 0;
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.EnumerateArray().Select(e => (
            id: e.GetProperty("id").GetInt64(), rating: e.GetProperty("rating").GetInt32(), ratedAt: e.GetProperty("ratedAt").GetInt64(),
            playCount: e.GetProperty("playCount").GetInt32(), lastPlayed: e.GetProperty("lastPlayed").GetInt64())).ToList();
        MainLoop.Invoke(() =>
        {
            foreach (var i in items)
            {
                if (!App.Lib.ById.TryGetValue(i.id, out var t) || !t.IsLocal) continue;
                var changed = false;
                if (i.ratedAt > t.RatedAt && i.rating is >= -1 and <= 1) { t.Rating = i.rating; t.RatedAt = i.ratedAt; changed = true; }
                if (i.playCount > t.PlayCount) { t.PlayCount = i.playCount; changed = true; }
                if (i.lastPlayed > t.LastPlayed) { t.LastPlayed = i.lastPlayed; changed = true; }
                if (!changed) continue;
                App.Lib.Db.SaveStats(t);
                t.Raise();
                applied++;
            }
            if (applied > 0) App.Lib.NotifyChanged();
        });
        return applied;
    }
}
