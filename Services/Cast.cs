using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>A speaker or TV on the network that music is being played on instead of this PC.</summary>
public interface ICastDevice : IDisposable
{
    string Name { get; }
    /// <summary>"DLNA" or "Chromecast".</summary>
    string Kind { get; }
    /// <summary>Something has been loaded and the device has reported on it.</summary>
    bool Loaded { get; }
    bool Playing { get; }
    double Position { get; }
    double Duration { get; }
    /// <summary>What was playing ran to its end by itself.</summary>
    event Action? Ended;
    event Action? Changed;
    event Action<string>? Failed;
    Task LoadAsync(PlayItem item, double startSeconds);
    Task PlayAsync();
    Task PauseAsync();
    Task SeekAsync(double seconds);
    Task SetVolumeAsync(double volume);
    Task StopAsync();
}

/// <summary>A device found on the network, before it is connected to.</summary>
public record CastTarget(string Name, string Kind, string Address, int Port, string Location)
{
    public string Detail => $"{Kind}  ·  {Address}";
    public ICastDevice Connect() => Kind == "Chromecast" ? new ChromecastDevice(this) : new DlnaDevice(this);
}

/// <summary>What can be sent to a device, and the addresses the device fetches it from.</summary>
public static class CastMedia
{
    /// <summary>Sound only, and nothing a server has to transcode on the fly.</summary>
    public static bool Can(PlayItem item) => !item.IsVideo && item.SeekUrl == null && (item.Track != null || item.Episode != null || item.Book != null || item.Station != null);

    public static string Mime(string pathOrUrl)
    {
        var path = pathOrUrl.Split('?')[0];
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg", ".m4a" or ".m4b" or ".mp4" or ".aac" => "audio/mp4", ".flac" => "audio/flac", ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg", ".opus" => "audio/ogg", ".wma" => "audio/x-ms-wma",
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "audio/mpeg"
        };
    }

    /// <summary>The address a device should fetch an item from: a file on this PC is offered by the small server below.</summary>
    public static string Url(PlayItem item, string deviceAddress)
    {
        var url = item.Resolved ?? item.Url;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : CastServer.Publish(url, deviceAddress);
    }

    public static string ArtUrl(PlayItem item, string deviceAddress)
    {
        var art = item.ArtPath;
        if (string.IsNullOrEmpty(art) || art.StartsWith("shell:")) return "";
        if (art.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return art;
        return File.Exists(art) ? CastServer.Publish(art, deviceAddress) : "";
    }

    /// <summary>This PC's address on the network the device is on.</summary>
    public static string LocalAddressFor(string deviceAddress)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(IPAddress.Parse(deviceAddress), 9);
            return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
        }
        catch { return SyncServer.LocalAddresses().FirstOrDefault() ?? "127.0.0.1"; }
    }
}

/// <summary>
/// A very small web server that hands the files being cast to the device playing them. It only serves files that were
/// published for casting, each under an address with a random token in it, and it answers range requests so the
/// device can skip around in a song.
/// </summary>
public static class CastServer
{
    static TcpListener? _tcp;
    static readonly ConcurrentDictionary<string, string> Files = new();
    static readonly ConcurrentDictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    public static int Port { get; private set; }
    /// <summary>Requests answered since the server started (for the self-test).</summary>
    public static int Served;

    static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_tcp != null) return;
            var tcp = new TcpListener(IPAddress.Any, 0);
            tcp.Start();
            Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            _tcp = tcp;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await tcp.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(() => HandleAsync(client));
                }
            });
        }
    }

    /// <summary>Offers a file to a device; returns the address to give it.</summary>
    public static string Publish(string path, string deviceAddress)
    {
        EnsureStarted();
        var token = Tokens.GetOrAdd(path, _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant());
        Files[token] = path;
        return $"http://{CastMedia.LocalAddressFor(deviceAddress)}:{Port}/m/{token}/media{Path.GetExtension(path).ToLowerInvariant()}";
    }

    static async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var s = client.GetStream();
            var buf = new byte[8192];
            int len = 0, end = -1;
            while (end < 0 && len < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len));
                if (n == 0) return;
                len += n;
                end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            }
            if (end < 0) return;
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return;
            var parts = first[1].Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "m" || !Files.TryGetValue(parts[1], out var path) || !File.Exists(path))
            {
                await s.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                return;
            }
            Interlocked.Increment(ref Served);
            await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
            long from = 0, to = f.Length - 1;
            var ranged = false;
            var range = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            if (range != null && range.Contains("bytes="))
            {
                var spec = range[(range.IndexOf("bytes=") + 6)..].Split(',')[0].Trim().Split('-');
                if (spec.Length == 2)
                {
                    if (spec[0] == "" && long.TryParse(spec[1], out var last)) { from = Math.Max(0, f.Length - last); ranged = true; }
                    else if (long.TryParse(spec[0], out var a)) { from = a; if (long.TryParse(spec[1], out var b)) to = Math.Min(b, f.Length - 1); ranged = true; }
                }
            }
            if (from > to || from >= f.Length)
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{f.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                return;
            }
            var count = to - from + 1;
            var head = new StringBuilder(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            head.Append($"Content-Type: {CastMedia.Mime(path)}\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\n");
            if (ranged) head.Append($"Content-Range: bytes {from}-{to}/{f.Length}\r\n");
            // what DLNA players look for before they will stream and seek
            head.Append("transferMode.dlna.org: Streaming\r\ncontentFeatures.dlna.org: DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
            if (first[0] == "HEAD") return;
            f.Position = from;
            var chunk = new byte[1 << 16];
            while (count > 0)
            {
                var n = await f.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, count)));
                if (n <= 0) break;
                await s.WriteAsync(chunk.AsMemory(0, n));
                count -= n;
            }
        }
        catch { /* the device closed the connection (it does that when it skips around) */ }
    }
}

/// <summary>Looks for devices that can play sound: DLNA renderers (by SSDP) and Chromecasts (by mDNS).</summary>
public static class CastFinder
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task FindAsync(Action<CastTarget> found, CancellationToken stop, int seconds = 5)
    {
        var seen = new ConcurrentDictionary<string, bool>();
        void Report(CastTarget t) { if (seen.TryAdd(t.Kind + t.Address + t.Name, true)) found(t); }
        await Task.WhenAll(Quiet(FindDlnaAsync(Report, stop, seconds)), Quiet(FindChromecastAsync(Report, stop, seconds)));
    }

    static async Task Quiet(Task t) { try { await t; } catch { } }

    static IEnumerable<IPAddress> LocalAddresses() => SyncServer.LocalAddresses().Select(IPAddress.Parse);

    static async Task FindDlnaAsync(Action<CastTarget> found, CancellationToken stop, int seconds)
    {
        var question = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:service:AVTransport:1\r\n\r\n");
        var locations = new ConcurrentDictionary<string, bool>();
        var jobs = new List<Task>();
        foreach (var local in LocalAddresses())
            jobs.Add(Task.Run(async () =>
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                await udp.SendAsync(question, question.Length, target);
                await Task.Delay(300, stop);
                await udp.SendAsync(question, question.Length, target);
                using var until = CancellationTokenSource.CreateLinkedTokenSource(stop);
                until.CancelAfter(TimeSpan.FromSeconds(seconds));
                while (!until.IsCancellationRequested)
                {
                    UdpReceiveResult answer;
                    try { answer = await udp.ReceiveAsync(until.Token); } catch { break; }
                    var location = Encoding.ASCII.GetString(answer.Buffer).Split("\r\n")
                        .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                    if (location == null || !locations.TryAdd(location, true)) continue;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var xml = XDocument.Parse(await Http.GetStringAsync(location, stop));
                            if (DlnaDevice.Describe(xml, location) is { } d) found(new CastTarget(d.Name, "DLNA", new Uri(location).Host, new Uri(location).Port, location));
                        }
                        catch { }
                    });
                }
            }, stop));
        await Task.WhenAll(jobs.Select(Quiet));
    }

    /// <summary>The mDNS question "who offers _googlecast._tcp here?".</summary>
    public static byte[] CastQuestion()
    {
        var q = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in new[] { "_googlecast", "_tcp", "local" }) { q.Add((byte)label.Length); q.AddRange(Encoding.ASCII.GetBytes(label)); }
        q.AddRange(new byte[] { 0, 0, 12, 0, 1 });   // end of name, type PTR, class IN
        return q.ToArray();
    }

    /// <summary>Picks the device's own name ("fn=…") and port out of an mDNS answer.</summary>
    public static (string Name, int Port) ParseCastAnswer(byte[] m)
    {
        var name = ""; var port = 8009;
        try
        {
            int questions = (m[4] << 8) | m[5], records = ((m[6] << 8) | m[7]) + ((m[8] << 8) | m[9]) + ((m[10] << 8) | m[11]);
            var at = 12;
            int SkipName(int i)
            {
                while (i < m.Length)
                {
                    var n = m[i];
                    if (n == 0) return i + 1;
                    if ((n & 0xC0) == 0xC0) return i + 2;   // a pointer to a name written earlier
                    i += n + 1;
                }
                return i;
            }
            for (var i = 0; i < questions; i++) at = SkipName(at) + 4;
            for (var i = 0; i < records && at + 10 <= m.Length; i++)
            {
                at = SkipName(at);
                int type = (m[at] << 8) | m[at + 1], size = (m[at + 8] << 8) | m[at + 9];
                at += 10;
                if (type == 33 && size >= 6) port = (m[at + 4] << 8) | m[at + 5];
                if (type == 16)
                    for (var t = at; t < at + size;)
                    {
                        var n = m[t];
                        var text = Encoding.UTF8.GetString(m, t + 1, Math.Min(n, m.Length - t - 1));
                        if (text.StartsWith("fn=")) name = text[3..];
                        t += n + 1;
                    }
                at += size;
            }
        }
        catch { }
        return (name, port);
    }

    static async Task FindChromecastAsync(Action<CastTarget> found, CancellationToken stop, int seconds)
    {
        var question = CastQuestion();
        var jobs = new List<Task>();
        foreach (var local in LocalAddresses())
            jobs.Add(Task.Run(async () =>
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                var target = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                await udp.SendAsync(question, question.Length, target);
                await Task.Delay(400, stop);
                await udp.SendAsync(question, question.Length, target);
                using var until = CancellationTokenSource.CreateLinkedTokenSource(stop);
                until.CancelAfter(TimeSpan.FromSeconds(seconds));
                while (!until.IsCancellationRequested)
                {
                    UdpReceiveResult answer;
                    try { answer = await udp.ReceiveAsync(until.Token); } catch { break; }
                    var (name, port) = ParseCastAnswer(answer.Buffer);
                    if (name == "") continue;
                    found(new CastTarget(name, "Chromecast", answer.RemoteEndPoint.Address.ToString(), port, ""));
                }
            }, stop));
        await Task.WhenAll(jobs.Select(Quiet));
    }
}

/// <summary>What the two kinds of device share: the state last heard from the device, with the position moved on by the clock in between.</summary>
public abstract class CastDeviceBase : ICastDevice
{
    protected readonly CastTarget Target;
    protected readonly CancellationTokenSource Life = new();
    double _position, _duration;
    DateTime _stamp = DateTime.UtcNow;
    bool _playing;

    protected CastDeviceBase(CastTarget target) => Target = target;

    public string Name => Target.Name;
    public string Kind => Target.Kind;
    public bool Loaded { get; protected set; }
    public bool Playing => _playing;
    public double Duration => _duration;
    public double Position
    {
        get
        {
            var p = _position + (_playing ? (DateTime.UtcNow - _stamp).TotalSeconds : 0);
            return _duration > 0 ? Math.Min(p, _duration) : p;
        }
    }

    public event Action? Ended;
    public event Action? Changed;
    public event Action<string>? Failed;

    protected void Report(bool playing, double position, double duration)
    {
        var changed = playing != _playing;
        _playing = playing;
        _position = position;
        if (duration > 0) _duration = duration;
        _stamp = DateTime.UtcNow;
        if (changed) Changed?.Invoke();
    }

    protected void ResetState() { _playing = false; _position = 0; _duration = 0; _stamp = DateTime.UtcNow; Loaded = false; }
    protected void RaiseEnded() => Ended?.Invoke();
    protected void RaiseFailed(string message) => Failed?.Invoke(message);

    public abstract Task LoadAsync(PlayItem item, double startSeconds);
    public abstract Task PlayAsync();
    public abstract Task PauseAsync();
    public abstract Task SeekAsync(double seconds);
    public abstract Task SetVolumeAsync(double volume);
    public abstract Task StopAsync();

    public virtual void Dispose()
    {
        try { Life.Cancel(); } catch { }
    }
}

/// <summary>A DLNA / UPnP media renderer (most network speakers, receivers and smart TVs), worked with SOAP calls.</summary>
public sealed class DlnaDevice : CastDeviceBase
{
    const string Transport = "urn:schemas-upnp-org:service:AVTransport:1", Rendering = "urn:schemas-upnp-org:service:RenderingControl:1";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    string _transportUrl = "", _renderingUrl = "";
    bool _wasPlaying, _stopping;
    Task? _watch;

    public DlnaDevice(CastTarget target) : base(target) { }

    public record Described(string Name, string TransportUrl, string RenderingUrl);

    /// <summary>Reads a device's description: its name and where its play and volume controls are. Null if it cannot play media.</summary>
    public static Described? Describe(XDocument xml, string location)
    {
        var root = xml.Root;
        if (root == null) return null;
        XElement? El(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        var device = El(root, "device");
        if (device == null) return null;
        var baseUrl = El(root, "URLBase")?.Value is { Length: > 0 } b ? new Uri(b) : new Uri(location);
        string Control(string type) => xml.Descendants().Where(x => x.Name.LocalName == "service")
            .Where(sv => (El(sv, "serviceType")?.Value ?? "").StartsWith(type[..^1], StringComparison.OrdinalIgnoreCase))
            .Select(sv => El(sv, "controlURL")?.Value ?? "").Where(u => u != "").Select(u => new Uri(baseUrl, u).ToString()).FirstOrDefault() ?? "";
        var transport = Control(Transport);
        return transport == "" ? null : new Described(El(device, "friendlyName")?.Value is { Length: > 0 } n ? n : new Uri(location).Host, transport, Control(Rendering));
    }

    async Task EnsureAsync()
    {
        if (_transportUrl != "") return;
        var d = Describe(XDocument.Parse(await Http.GetStringAsync(Target.Location)), Target.Location) ?? throw new Exception("this device has no play controls");
        _transportUrl = d.TransportUrl;
        _renderingUrl = d.RenderingUrl;
    }

    static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    public static string Envelope(string service, string action, params (string Name, string Value)[] args) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
        $"<u:{action} xmlns:u=\"{service}\">{string.Concat(args.Select(a => $"<{a.Name}>{X(a.Value)}</{a.Name}>"))}</u:{action}></s:Body></s:Envelope>";

    /// <summary>The description of a song that a renderer shows on its display.</summary>
    public static string Didl(string url, string mime, string title, string artist, string album, string art) =>
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
        $"<item id=\"0\" parentID=\"-1\" restricted=\"1\"><dc:title>{X(title)}</dc:title><dc:creator>{X(artist)}</dc:creator><upnp:artist>{X(artist)}</upnp:artist><upnp:album>{X(album)}</upnp:album>" +
        (art != "" ? $"<upnp:albumArtURI>{X(art)}</upnp:albumArtURI>" : "") +
        $"<upnp:class>object.item.audioItem.musicTrack</upnp:class><res protocolInfo=\"http-get:*:{mime}:DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\">{X(url)}</res></item></DIDL-Lite>";

    async Task<XDocument> CallAsync(string url, string service, string action, params (string, string)[] args)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(Envelope(service, action, args), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
        using var response = await Http.SendAsync(request, Life.Token);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var why = "";
            try { why = XDocument.Parse(text).Descendants().FirstOrDefault(x => x.Name.LocalName == "errorDescription")?.Value ?? ""; } catch { }
            throw new Exception(why != "" ? why : $"the device answered {(int)response.StatusCode} to {action}");
        }
        return XDocument.Parse(text);
    }

    static string Value(XDocument d, string name) => d.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
    static double Seconds(string hms) => TimeSpan.TryParse(hms, out var t) ? t.TotalSeconds : 0;
    static string Hms(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"; }

    public override async Task LoadAsync(PlayItem item, double startSeconds)
    {
        await EnsureAsync();
        _stopping = true;
        ResetState();
        string url = CastMedia.Url(item, Target.Address), art = CastMedia.ArtUrl(item, Target.Address);
        try { await CallAsync(_transportUrl, Transport, "Stop", ("InstanceID", "0")); } catch { }
        await CallAsync(_transportUrl, Transport, "SetAVTransportURI", ("InstanceID", "0"), ("CurrentURI", url),
            ("CurrentURIMetaData", Didl(url, CastMedia.Mime(url), item.Title, item.Subtitle, item.Album, art)));
        await CallAsync(_transportUrl, Transport, "Play", ("InstanceID", "0"), ("Speed", "1"));
        _wasPlaying = false;
        _stopping = false;
        Report(true, 0, item.DurationMs / 1000.0);
        if (startSeconds > 1)
        {
            await Task.Delay(700);
            try { await SeekAsync(startSeconds); } catch { }
        }
        _watch ??= Task.Run(WatchAsync);
    }

    /// <summary>Asks the device every second where it is, and notices when a song has played out.</summary>
    async Task WatchAsync()
    {
        var misses = 0;
        while (!Life.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, Life.Token);
                if (_stopping) continue;
                var state = Value(await CallAsync(_transportUrl, Transport, "GetTransportInfo", ("InstanceID", "0")), "CurrentTransportState");
                var info = await CallAsync(_transportUrl, Transport, "GetPositionInfo", ("InstanceID", "0"));
                double position = Seconds(Value(info, "RelTime")), duration = Seconds(Value(info, "TrackDuration"));
                misses = 0;
                Loaded = true;
                if (state == "PLAYING") { _wasPlaying = true; Report(true, position, duration); }
                else if (state == "PAUSED_PLAYBACK") Report(false, position, duration);
                else if (state is "STOPPED" or "NO_MEDIA_PRESENT")
                {
                    Report(false, position, duration);
                    if (_wasPlaying && !_stopping) { _wasPlaying = false; RaiseEnded(); }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (++misses == 5) RaiseFailed("stopped answering (" + ex.Message + ")"); }
        }
    }

    public override async Task PlayAsync() { await CallAsync(_transportUrl, Transport, "Play", ("InstanceID", "0"), ("Speed", "1")); Report(true, Position, Duration); }
    public override async Task PauseAsync() { await CallAsync(_transportUrl, Transport, "Pause", ("InstanceID", "0")); Report(false, Position, Duration); }

    public override async Task SeekAsync(double seconds)
    {
        await CallAsync(_transportUrl, Transport, "Seek", ("InstanceID", "0"), ("Unit", "REL_TIME"), ("Target", Hms(seconds)));
        Report(Playing, seconds, Duration);
    }

    public override async Task SetVolumeAsync(double volume)
    {
        if (_renderingUrl == "") return;
        await CallAsync(_renderingUrl, Rendering, "SetVolume", ("InstanceID", "0"), ("Channel", "Master"), ("DesiredVolume", ((int)Math.Round(Math.Clamp(volume, 0, 1) * 100)).ToString()));
    }

    public override async Task StopAsync()
    {
        _stopping = true;
        _wasPlaying = false;
        if (_transportUrl == "") return;
        try { await CallAsync(_transportUrl, Transport, "Stop", ("InstanceID", "0")); } catch { }
        ResetState();
    }
}

/// <summary>A Chromecast (or a speaker or TV with Chromecast built in), spoken to over its own protocol.</summary>
public sealed class ChromecastDevice : CastDeviceBase
{
    const string Connection = "urn:x-cast:com.google.cast.tp.connection", Heartbeat = "urn:x-cast:com.google.cast.tp.heartbeat",
        Receiver = "urn:x-cast:com.google.cast.receiver", Media = "urn:x-cast:com.google.cast.media", MediaApp = "CC1AD845";
    TcpClient? _tcp;
    SslStream? _ssl;
    readonly SemaphoreSlim _write = new(1, 1);
    string _transport = "", _session = "";
    long _mediaSession;
    int _request;
    bool _wasPlaying, _stopping;
    TaskCompletionSource<bool>? _launched;

    public ChromecastDevice(CastTarget target) : base(target) { }

    // ----- the wire format: a length, then a small protocol-buffers message -----

    static void Varint(List<byte> o, ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    static void Text(List<byte> o, int field, string s) { var b = Encoding.UTF8.GetBytes(s); o.Add((byte)((field << 3) | 2)); Varint(o, (ulong)b.Length); o.AddRange(b); }

    public static byte[] Encode(string source, string destination, string ns, string json)
    {
        var o = new List<byte> { 0x08, 0x00 };              // protocol version 0
        Text(o, 2, source); Text(o, 3, destination); Text(o, 4, ns);
        o.Add(0x28); o.Add(0x00);                           // the payload is text
        Text(o, 6, json);
        var frame = new byte[o.Count + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, o.Count);
        o.CopyTo(frame, 4);
        return frame;
    }

    public static (string Source, string Namespace, string Json) Decode(ReadOnlySpan<byte> m)
    {
        string source = "", ns = "", json = "";
        var i = 0;
        ulong ReadVarint(ReadOnlySpan<byte> b)
        {
            ulong v = 0; var shift = 0;
            while (i < b.Length) { var x = b[i++]; v |= (ulong)(x & 0x7F) << shift; if ((x & 0x80) == 0) break; shift += 7; }
            return v;
        }
        while (i < m.Length)
        {
            var tag = ReadVarint(m);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (wire == 0) { ReadVarint(m); continue; }
            if (wire != 2) break;
            var n = (int)ReadVarint(m);
            if (n < 0 || i + n > m.Length) break;
            var text = Encoding.UTF8.GetString(m.Slice(i, n));
            i += n;
            if (field == 2) source = text; else if (field == 4) ns = text; else if (field == 6) json = text;
        }
        return (source, ns, json);
    }

    async Task SendAsync(string destination, string ns, object message)
    {
        var frame = Encode("sender-0", destination, ns, JsonSerializer.Serialize(message));
        await _write.WaitAsync();
        try { await _ssl!.WriteAsync(frame, Life.Token); await _ssl.FlushAsync(Life.Token); }
        finally { _write.Release(); }
    }

    async Task ConnectAsync()
    {
        if (_ssl != null) return;
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(Target.Address, Target.Port, Life.Token);
        // a Chromecast identifies itself with a certificate it issued to itself; there is nothing to check it against
        var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Target.Address }, Life.Token);
        _tcp = tcp; _ssl = ssl;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(PingLoopAsync);
        await SendAsync("receiver-0", Connection, new { type = "CONNECT" });
        _launched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync("receiver-0", Receiver, new { type = "LAUNCH", appId = MediaApp, requestId = ++_request });
        if (await Task.WhenAny(_launched.Task, Task.Delay(12000, Life.Token)) != _launched.Task) throw new Exception("the device did not start its player");
        await SendAsync(_transport, Connection, new { type = "CONNECT" });
    }

    async Task PingLoopAsync()
    {
        try
        {
            while (!Life.IsCancellationRequested)
            {
                await Task.Delay(5000, Life.Token);
                await SendAsync("receiver-0", Heartbeat, new { type = "PING" });
                if (_transport != "" && Playing) await SendAsync(_transport, Media, new { type = "GET_STATUS", requestId = ++_request });
            }
        }
        catch { }
    }

    async Task ReadLoopAsync()
    {
        var head = new byte[4];
        try
        {
            while (!Life.IsCancellationRequested)
            {
                await _ssl!.ReadExactlyAsync(head, Life.Token);
                var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head);
                if (size is <= 0 or > 1 << 20) throw new IOException("bad message from the device");
                var body = new byte[size];
                await _ssl.ReadExactlyAsync(body, Life.Token);
                var (_, ns, json) = Decode(body);
                if (json != "") Handle(ns, J.Parse(json));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!Life.IsCancellationRequested) RaiseFailed("connection lost (" + ex.Message + ")"); }
    }

    void Handle(string ns, JsonElement m)
    {
        var type = m.S("type");
        if (ns == Heartbeat && type == "PING") { _ = SendAsync("receiver-0", Heartbeat, new { type = "PONG" }); return; }
        if (ns == Receiver && type == "RECEIVER_STATUS")
        {
            var app = m.O("status").A("applications").FirstOrDefault(a => a.S("appId") == MediaApp);
            if (app.ValueKind == JsonValueKind.Object && app.S("transportId") != "")
            {
                _transport = app.S("transportId");
                _session = app.S("sessionId");
                _launched?.TrySetResult(true);
            }
            return;
        }
        if (ns == Receiver && type == "LAUNCH_ERROR") { _launched?.TrySetException(new Exception("the device could not start its player")); return; }
        if (ns != Media) return;
        if (type is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST") { RaiseFailed("could not play that (" + type.ToLowerInvariant().Replace('_', ' ') + ")"); return; }
        if (type != "MEDIA_STATUS") return;
        foreach (var st in m.A("status"))
        {
            if (st.L("mediaSessionId") != 0) _mediaSession = st.L("mediaSessionId");
            var state = st.S("playerState");
            double position = st.O("currentTime").ValueKind == JsonValueKind.Number ? st.O("currentTime").GetDouble() : Position;
            var duration = st.O("media").O("duration").ValueKind == JsonValueKind.Number ? st.O("media").O("duration").GetDouble() : 0;
            Loaded = true;
            if (state == "PLAYING") { _wasPlaying = true; Report(true, position, duration); }
            else if (state is "PAUSED" or "BUFFERING") Report(state == "BUFFERING" && Playing, position, duration);
            else if (state == "IDLE")
            {
                Report(false, position, duration);
                if (st.S("idleReason") == "FINISHED" && _wasPlaying && !_stopping) { _wasPlaying = false; RaiseEnded(); }
                else if (st.S("idleReason") == "ERROR") RaiseFailed("could not play that");
            }
        }
    }

    public override async Task LoadAsync(PlayItem item, double startSeconds)
    {
        await ConnectAsync();
        _stopping = true;
        ResetState();
        string url = CastMedia.Url(item, Target.Address), art = CastMedia.ArtUrl(item, Target.Address);
        var metadata = new Dictionary<string, object> { ["metadataType"] = 3, ["title"] = item.Title, ["artist"] = item.Subtitle, ["albumName"] = item.Album };
        if (art != "") metadata["images"] = new[] { new { url = art } };
        _wasPlaying = false;
        await SendAsync(_transport, Media, new
        {
            type = "LOAD", requestId = ++_request, autoplay = true, currentTime = Math.Max(0, startSeconds),
            media = new { contentId = url, streamType = item.Station != null ? "LIVE" : "BUFFERED", contentType = CastMedia.Mime(url), metadata }
        });
        _stopping = false;
        Report(true, startSeconds, item.DurationMs / 1000.0);
    }

    Task MediaAsync(string type) => _mediaSession == 0 ? Task.CompletedTask : SendAsync(_transport, Media, new { type, mediaSessionId = _mediaSession, requestId = ++_request });

    public override async Task PlayAsync() { await MediaAsync("PLAY"); Report(true, Position, Duration); }
    public override async Task PauseAsync() { await MediaAsync("PAUSE"); Report(false, Position, Duration); }

    public override async Task SeekAsync(double seconds)
    {
        if (_mediaSession == 0) return;
        await SendAsync(_transport, Media, new { type = "SEEK", mediaSessionId = _mediaSession, currentTime = Math.Max(0, seconds), requestId = ++_request });
        Report(Playing, seconds, Duration);
    }

    public override Task SetVolumeAsync(double volume) => _ssl == null ? Task.CompletedTask
        : SendAsync("receiver-0", Receiver, new { type = "SET_VOLUME", volume = new { level = Math.Clamp(volume, 0, 1) }, requestId = ++_request });

    public override async Task StopAsync()
    {
        _stopping = true;
        _wasPlaying = false;
        if (_ssl == null) return;
        try { await MediaAsync("STOP"); } catch { }
        ResetState();
    }

    public override void Dispose()
    {
        // close the player on the device so it goes back to its idle screen
        try { if (_ssl != null && _session != "") SendAsync("receiver-0", Receiver, new { type = "STOP", sessionId = _session, requestId = ++_request }).Wait(1500); } catch { }
        base.Dispose();
        try { _ssl?.Dispose(); _tcp?.Dispose(); } catch { }
    }
}
