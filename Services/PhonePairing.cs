using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Pairing phones with this computer, shared by the Windows app and the Mac/Linux helper (their sync servers call it).
/// A phone pairs in one of three ways, and then carries its own key in the X-Halcyon-Code header:
///  - it asks (POST /sync/pair) and the person at the computer chooses Allow or Deny;
///  - its camera scans the QR code shown in settings: the code's address opens a page (/pair) that hands the phone app
///    a short-lived pairing token, which pairs at once (scanning the screen in front of you is the consent);
///  - the six-digit pairing code typed on the phone, as before (older phone apps send the code with every request).
/// Each phone has a key of its own, so a phone can be forgotten without the others having to pair again. Only a hash
/// of each key is kept (in phones.json in the data folder).
/// </summary>
public static class PhonePairing
{
    public record Phone(string Id, string Name, string KeyHash, long Paired)
    {
        public long LastSeen { get; set; }
        public long LastSync { get; set; }
        public string LastSummary { get; set; } = "";
    }

    /// <summary>A phone asking to pair, waiting for Allow or Deny.</summary>
    public record Request(string Id, string Name, string Address)
    {
        internal readonly TaskCompletionSource<bool> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static readonly object Gate = new();
    static List<Phone>? _phones;
    static string _pcId = "";
    static string FilePath => Path.Combine(AppPaths.DataDir, "phones.json");

    /// <summary>Set by the self-tests: nothing is written to the data folder.</summary>
    public static bool InMemory { get; set; }

    /// <summary>Someone's phone asks to pair: the app shows Allow / Deny and calls <see cref="Decide"/>.</summary>
    public static event Action<Request>? Asked;
    /// <summary>A phone that is syncing: what it is doing, for the status line ("Pixel 8: copying 3 of 40").</summary>
    public static event Action<Phone, string>? Activity;
    /// <summary>The list of paired phones changed.</summary>
    public static event Action? Changed;

    public static readonly ConcurrentDictionary<string, Request> Pending = new();

    // ---------- the list of phones ----------

    class Stored { public string PcId { get; set; } = ""; public List<Phone> Phones { get; set; } = new(); }

    static List<Phone> Load()
    {
        if (_phones != null) return _phones;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            _phones = s?.Phones ?? new();
            _pcId = s?.PcId ?? "";
        }
        catch { _phones = new(); }
        if (_pcId == "") { _pcId = Guid.NewGuid().ToString("N"); SaveLocked(); }
        return _phones;
    }

    static void SaveLocked()
    {
        if (InMemory) return;
        try { Directory.CreateDirectory(AppPaths.DataDir); File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored { PcId = _pcId, Phones = _phones ?? new() })); } catch { }
    }

    /// <summary>This computer's own lasting id: lets a phone tell it apart from others with the same name, and find it again at a new address.</summary>
    public static string PcId { get { lock (Gate) { Load(); return _pcId; } } }

    public static List<Phone> Phones { get { lock (Gate) return Load().ToList(); } }

    public static void Forget(string id)
    {
        lock (Gate) { Load().RemoveAll(p => p.Id == id); SaveLocked(); }
        Changed?.Invoke();
    }

    /// <summary>For the self-tests: starts over with no phones.</summary>
    public static void Reset()
    {
        lock (Gate) { _phones = new(); SaveLocked(); }
        Pending.Clear();
        _token = ""; _tokenUntil = DateTime.MinValue;
    }

    static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    static (Phone Phone, string Key) Add(string name, string deviceId)
    {
        var key = NewKey();
        var phone = new Phone(deviceId is { Length: > 0 and <= 64 } ? deviceId : Guid.NewGuid().ToString("N"), Clean(name), Hash(key), Fmt.Now) { LastSeen = Fmt.Now };
        lock (Gate)
        {
            // the same phone pairing again replaces its old key
            Load().RemoveAll(p => p.Id == phone.Id);
            _phones!.Add(phone);
            SaveLocked();
        }
        Changed?.Invoke();
        return (phone, key);
    }

    static string Clean(string name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return n == "" ? "a phone" : n.Length > 60 ? n[..60] : n;
    }

    // ---------- is a request allowed? ----------

    /// <summary>
    /// The X-Halcyon-Code header: a paired phone's key, or the six-digit pairing code. True when it is either; the
    /// phone it belongs to comes back too (null for the code, which belongs to no particular phone).
    /// </summary>
    public static bool Allowed(string? given, string code, out Phone? phone)
    {
        phone = null;
        if (string.IsNullOrEmpty(given)) return false;
        if (code != "" && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(code))) return true;
        if (given.Length < 20) return false;
        var hash = Hash(given);
        lock (Gate)
        {
            phone = Load().FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.KeyHash), Encoding.ASCII.GetBytes(hash)));
            if (phone == null) return false;
            phone.LastSeen = Fmt.Now;
        }
        return true;
    }

    /// <summary>
    /// What a phone said it is doing (the X-Halcyon-Progress header: "copying 3 of 40", or "done: 12 copied" at the end).
    /// </summary>
    public static void Seen(Phone? phone, string? progress)
    {
        if (phone == null || string.IsNullOrWhiteSpace(progress)) return;
        var text = Clean(Uri.UnescapeDataString(progress));
        if (text.StartsWith("done", StringComparison.OrdinalIgnoreCase))
        {
            lock (Gate) { phone.LastSync = Fmt.Now; phone.LastSummary = text.Length > 5 ? text[5..].Trim(' ', ':') : ""; SaveLocked(); }
            Changed?.Invoke();
        }
        Activity?.Invoke(phone, text);
    }

    // ---------- asking to pair ----------

    /// <summary>The token in the QR code: valid for ten minutes after the QR code is shown.</summary>
    static string _token = "";
    static DateTime _tokenUntil = DateTime.MinValue;

    public static string QrToken()
    {
        lock (Gate)
        {
            if (_token == "" || DateTime.UtcNow > _tokenUntil - TimeSpan.FromMinutes(2)) _token = NewKey();
            _tokenUntil = DateTime.UtcNow.AddMinutes(10);
            return _token;
        }
    }

    static bool TokenMatches(string given)
    {
        lock (Gate)
            return given.Length > 0 && _token != "" && DateTime.UtcNow < _tokenUntil
                   && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(_token));
    }

    /// <summary>
    /// POST /sync/pair from a phone: {"name": "Pixel 8", "device": "&lt;its id&gt;", "token": "&lt;from the QR code, if scanned&gt;"}.
    /// With the QR code's token the phone is paired at once; otherwise the person at the computer is asked, and the
    /// phone waits (up to two minutes) for the answer. Returns the HTTP status and the reply.
    /// </summary>
    public static async Task<(int Status, string Json)> HandlePairAsync(byte[] body, string from, string pcName, CancellationToken ct)
    {
        string name, device, token;
        try
        {
            var j = JsonDocument.Parse(body).RootElement;
            name = j.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            device = j.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
            token = j.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
        }
        catch { return (400, "{\"error\":\"bad request\"}"); }

        string Reply(string key) => JsonSerializer.Serialize(new { key, pc = pcName, id = PcId });

        if (token != "")
        {
            if (!TokenMatches(token)) return (403, "{\"error\":\"this QR code has run out: show it again on the computer and scan it once more\"}");
            return (200, Reply(Add(name, device).Key));
        }

        // one question at a time from each address, and only a few waiting at once
        if (Pending.Values.Any(r => r.Address == from) || Pending.Count >= 3) return (429, "{\"error\":\"the computer is already being asked; answer it there\"}");
        if (Asked == null) return (503, "{\"error\":\"the computer can't ask right now; use the pairing code\"}");
        var req = new Request(Guid.NewGuid().ToString("N"), Clean(name), from);
        Pending[req.Id] = req;
        try
        {
            Asked.Invoke(req);
            var answered = await Task.WhenAny(req.Decision.Task, Task.Delay(TimeSpan.FromMinutes(2), ct));
            if (answered != req.Decision.Task) return (408, "{\"error\":\"nobody answered on the computer\"}");
            return req.Decision.Task.Result ? (200, Reply(Add(req.Name, device).Key)) : (403, "{\"error\":\"the computer said no\"}");
        }
        finally { Pending.TryRemove(req.Id, out _); }
    }

    /// <summary>The answer to a phone asking to pair.</summary>
    public static void Decide(string requestId, bool allow)
    {
        if (Pending.TryGetValue(requestId, out var r)) r.Decision.TrySetResult(allow);
    }

    // ---------- the QR code ----------

    /// <summary>
    /// The address a phone on the same network most likely reaches this computer at: one on an adapter with a gateway
    /// (the home network), private ranges first.
    /// </summary>
    public static string? BestAddress()
    {
        try
        {
            var found = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .SelectMany(p => p.UnicastAddresses.Select(a => (a.Address, Gateway: p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))))
                .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address) && !x.Address.ToString().StartsWith("169.254."))
                .OrderByDescending(x => x.Gateway).ThenByDescending(x => IsPrivate(x.Address)).ToList();
            return found.FirstOrDefault().Address?.ToString();
        }
        catch { return null; }
    }

    static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }

    /// <summary>What the QR code holds: the pairing page on this computer, with a fresh token.</summary>
    public static string? QrUrl(int port) => BestAddress() is { } ip ? $"http://{ip}:{port}/pair#t={QrToken()}" : null;

    /// <summary>The QR code as a PNG picture.</summary>
    public static byte[] QrPng(string text, int pixelsPerModule = 8)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>Where the phone app can be downloaded when it isn't on the phone yet.</summary>
    public const string AppDownload = "https://github.com/jswyhart/Phoenix/releases/latest";

    /// <summary>
    /// The page the QR code opens in the phone's browser: a button that opens the Phoenix app with the token (it then
    /// pairs), or the download page when the app isn't installed. The token stays in the part after "#", which is not
    /// sent over the network.
    /// </summary>
    public static string PairPage(string pcName) => """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Pair with Phoenix</title>
<style>
body{font-family:system-ui,sans-serif;background:#111;color:#eee;margin:0;padding:28px 22px;line-height:1.45}
h1{font-weight:300;font-size:30px;margin:0 0 6px}p{color:#bbb}
a.b{display:block;text-align:center;background:#e0457b;color:#fff;text-decoration:none;font-size:18px;padding:16px;border-radius:30px;margin:26px 0 14px}
a.l{color:#e0457b}
</style></head><body>
<h1>Pair with __PC__</h1>
<p>Tap the button to open Phoenix on this phone. It pairs with this computer by itself, with no code to type.</p>
<a class="b" id="open" href="#">Open Phoenix and pair</a>
<p>Don't have Phoenix on this phone yet? <a class="l" href="__DL__">Download it</a> (the .apk file), install it, then scan the QR code again.</p>
<script>
var t=(location.hash.match(/t=([^&]+)/)||[])[1]||"";
var host=location.host;
var q="pair?host="+encodeURIComponent(host)+"&t="+encodeURIComponent(t);
var a=document.getElementById("open");
if(/android/i.test(navigator.userAgent)){
 a.href="intent://"+q+"#Intent;scheme=phoenix;package=com.halcyon.player;S.browser_fallback_url="+encodeURIComponent("__DL__")+";end";
}else{a.href="phoenix://"+q;}
</script></body></html>
""".Replace("__PC__", WebUtility.HtmlEncode(pcName)).Replace("__DL__", AppDownload);
}
