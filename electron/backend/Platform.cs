using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// Encrypts tokens with AES-GCM. On macOS the key lives in the login Keychain (via the system `security` tool);
/// elsewhere, or if the Keychain can't be used, it is kept in a file next to the settings. The Keychain entry keeps
/// the name the Halcyon build used, so tokens saved by that build still open.
/// </summary>
public class Secrets : ISecretProtector
{
    const string Service = "Halcyon media player", Account = "settings-key";
    readonly string _keyFile;
    byte[]? _key;

    public Secrets(string dataDir) => _keyFile = Path.Combine(dataDir, "settings.key");

    byte[] Key()
    {
        if (_key != null) return _key;
        // a key file that is already there wins (a scratch data folder, or one carried over from another machine)
        try { if (TryKey(File.ReadAllText(_keyFile).Trim(), out var k)) return _key = k; } catch { }
        if (Program.IsMac && !Program.SmokeMode)
        {
            var stored = Run("security", "find-generic-password", "-s", Service, "-a", Account, "-w");
            if (stored != null && TryKey(stored.Trim(), out var k)) return _key = k;
            var fresh = RandomNumberGenerator.GetBytes(32);
            if (Run("security", "add-generic-password", "-U", "-s", Service, "-a", Account, "-w", Convert.ToBase64String(fresh)) != null) return _key = fresh;
        }
        _key = RandomNumberGenerator.GetBytes(32);
        try { File.WriteAllText(_keyFile, Convert.ToBase64String(_key)); } catch { }
        return _key;
    }

    static bool TryKey(string base64, out byte[] key)
    {
        key = Array.Empty<byte>();
        try { key = Convert.FromBase64String(base64); return key.Length == 32; } catch { return false; }
    }

    static string? Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    public string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public string Unprotect(string stored)
    {
        var all = Convert.FromBase64String(stored);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>A small PNG writer (8-bit RGB, no alpha), for video thumbnails and the self-test's sample pictures.</summary>
public static class Png
{
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in kind) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    static void Chunk(Stream s, string kind, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(n, (uint)data.Length);
        s.Write(n);
        var k = Encoding.ASCII.GetBytes(kind);
        s.Write(k);
        s.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(n, Crc(k, data));
        s.Write(n);
    }

    /// <summary>Encodes pixels stored blue, green, red, (unused) per pixel, as VLC's RV32 frames are.</summary>
    public static byte[] FromBgra(byte[] bgra, int width, int height)
    {
        var raw = new byte[(width * 3 + 1) * height];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0;   // no row filter
            var i = y * width * 4;
            for (var x = 0; x < width; x++, i += 4) { raw[o++] = bgra[i + 2]; raw[o++] = bgra[i + 1]; raw[o++] = bgra[i]; }
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(raw);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var head = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)height);
        head[8] = 8; head[9] = 2;   // 8 bits per channel, RGB
        Chunk(ms, "IHDR", head);
        Chunk(ms, "IDAT", packed.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>A flat-colour picture.</summary>
    public static byte[] Flat(int width, int height, byte r, byte g, byte b)
    {
        var px = new byte[width * height * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return FromBgra(px, width, height);
    }
}

/// <summary>
/// Turns whatever the library calls artwork into a picture file on disk the window can show: a local image as it is,
/// an http(s) address downloaded once into the image cache, "thumb:&lt;video file&gt;" as a picture taken from the video,
/// and (on a Mac) HEIC/TIFF/RAW converted to JPEG by the system's own image tool.
/// </summary>
public static class ArtFiles
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim Gate = new(4);
    // HEIC (iPhone photos), TIFF and camera RAW: the browser engine can't read them, but macOS can
    static readonly HashSet<string> Converted = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".tif", ".tiff", ".dng" };

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    public static async Task<string?> ResolveAsync(string path, int size)
    {
        await Gate.WaitAsync();
        try { return await Task.Run(() => Resolve(path, size)); }
        finally { Gate.Release(); }
    }

    static string? Resolve(string path, int size)
    {
        try
        {
            if (path.StartsWith("http://") || path.StartsWith("https://"))
            {
                var file = Path.Combine(AppPaths.ImgCacheDir, Hash(path) + ".img");
                if (!File.Exists(file))
                {
                    var bytes = Http.GetByteArrayAsync(path).GetAwaiter().GetResult();
                    var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    try { File.Move(tmp, file, true); } catch { try { File.Delete(tmp); } catch { } }
                }
                return File.Exists(file) ? file : null;
            }
            if (path.StartsWith("thumb:")) return VideoThumbnail(path[6..]);
            if (!File.Exists(path)) return null;
            if (Converted.Contains(Path.GetExtension(path))) return ConvertWithMac(path, size) ?? path;
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// The cached thumbnail for a video, made the first time it is asked for. Keyed by the file and its date, so an
    /// edited video gets a new one; a video that gives no picture is remembered so it is not tried every time.
    /// </summary>
    static string? VideoThumbnail(string video)
    {
        if (!File.Exists(video)) return null;
        var png = Path.Combine(AppPaths.ArtDir, "video-" + Hash(video + "|" + File.GetLastWriteTimeUtc(video).Ticks) + ".png");
        var none = png + ".none";
        if (File.Exists(png)) return png;
        if (File.Exists(none)) return null;
        try
        {
            if (Program.Engine.Thumbnail(video, png)) return png;
            File.WriteAllText(none, "");
        }
        catch { }
        return null;
    }

    static string? ConvertWithMac(string file, int size)
    {
        if (!Program.IsMac) return null;
        var jpg = Path.Combine(AppPaths.ImgCacheDir, Hash($"{file}|{File.GetLastWriteTimeUtc(file).Ticks}|{size}") + ".jpg");
        if (File.Exists(jpg)) return jpg;
        try
        {
            Directory.CreateDirectory(AppPaths.ImgCacheDir);
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/sips",
                new[] { "-s", "format", "jpeg", "-Z", Math.Max(size, 64).ToString(), file, "--out", jpg }) { RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return null;
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 && File.Exists(jpg) ? jpg : null;
        }
        catch { return null; }
    }
}
