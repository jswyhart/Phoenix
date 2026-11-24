using System.IO;
using System.Runtime.InteropServices;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// A one-line technical description of what is playing, for the player bar: for music the format, sample rate, bit
/// depth and bitrate; for video the picture size, frame rate and the video and audio bitrates. Read from the file
/// itself (its tags and Windows' own file properties); streams from a server say only where they come from.
/// </summary>
public static class MediaInfo
{
    static readonly Dictionary<string, string> Cache = new();

    public static string For(PlayItem? item)
    {
        if (item == null) return "";
        var remote = item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (remote)
        {
            var source = item.Track?.Source ?? (item.Episode != null ? "podcast" : item.Book?.Source ?? "");
            return source is "" or "local" ? "stream" : source + " stream";
        }
        lock (Cache) if (Cache.TryGetValue(item.Url, out var hit)) return hit;
        var text = "";
        try { text = item.IsVideo ? Video(item.Url) : Audio(item.Url); } catch { }
        lock (Cache) { if (Cache.Count > 400) Cache.Clear(); Cache[item.Url] = text; }
        return text;
    }

    static string Kbps(double bitsPerSecond) => bitsPerSecond >= 10_000_000 ? $"{bitsPerSecond / 1_000_000:0.#} Mbps"
        : bitsPerSecond >= 1_000_000 ? $"{bitsPerSecond / 1_000_000:0.0#} Mbps" : $"{Math.Round(bitsPerSecond / 1000):#,0} kbps";

    static string Join(IEnumerable<string> parts) => string.Join("  ·  ", parts.Where(p => p != ""));

    public static string FormatName(string file, string codecs)
    {
        var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "m4a" or "m4b" or "mp4" => codecs.Contains("lossless", StringComparison.OrdinalIgnoreCase) || codecs.Contains("alac", StringComparison.OrdinalIgnoreCase) ? "ALAC" : "AAC",
            "ogg" => codecs.Contains("opus", StringComparison.OrdinalIgnoreCase) ? "Opus" : "Vorbis",
            "opus" => "Opus",
            "" => "",
            _ => ext.ToUpperInvariant()
        };
    }

    /// <summary>"FLAC · 44.1 kHz · 16-bit · 1,012 kbps"</summary>
    public static string Audio(string file)
    {
        if (!File.Exists(file)) return "";
        using var f = TagLib.File.Create(file);
        var p = f.Properties;
        var codecs = string.Join(" ", p.Codecs.Where(c => c != null).Select(c => c.Description));
        double bits = p.AudioBitrate * 1000.0;
        if (bits <= 0 && p.Duration.TotalSeconds > 1) bits = new FileInfo(file).Length * 8 / p.Duration.TotalSeconds;
        return Join(new[]
        {
            FormatName(file, codecs),
            p.AudioSampleRate > 0 ? $"{p.AudioSampleRate / 1000.0:0.#} kHz" : "",
            p.BitsPerSample > 0 ? $"{p.BitsPerSample}-bit" : "",
            p.AudioChannels > 2 ? $"{p.AudioChannels} channels" : p.AudioChannels == 1 ? "mono" : "",
            bits > 0 ? Kbps(bits) : ""
        });
    }

    /// <summary>"1920×1080 · 23.976 fps · video 4.2 Mbps · audio 192 kbps"</summary>
    public static string Video(string file)
    {
        if (!File.Exists(file)) return "";
        uint width = 0, height = 0, frames = 0, videoBits = 0, audioBits = 0;
        try
        {
            SHGetPropertyStoreFromParsingName(file, IntPtr.Zero, 0, typeof(IPropertyStore).GUID, out var store);
            try
            {
                width = Read(store, VideoKey(3)); height = Read(store, VideoKey(4));
                frames = Read(store, VideoKey(6));          // frames per 1000 seconds
                videoBits = Read(store, VideoKey(8));
                audioBits = Read(store, AudioKey(4));
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { }
        return Join(new[]
        {
            width > 0 && height > 0 ? $"{width}×{height}" : "",
            frames > 0 ? $"{frames / 1000.0:0.###} fps" : "",
            videoBits > 0 ? "video " + Kbps(videoBits) : "",
            audioBits > 0 ? "audio " + Kbps(audioBits) : ""
        });
    }

    // ---- Windows file properties (the same ones File Explorer's Details pane shows) ----

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public uint UInt; [FieldOffset(8)] public ulong ULong; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    static PropertyKey VideoKey(uint id) => new() { Format = new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), Id = id };
    static PropertyKey AudioKey(uint id) => new() { Format = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), Id = id };

    static uint Read(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) != 0) return 0;
        try { return v.Type is 19 or 23 or 18 or 3 or 22 ? v.UInt : v.Type is 21 or 20 ? (uint)Math.Min(uint.MaxValue, v.ULong) : 0; }   // VT_UI4, VT_UINT, VT_UI2, VT_I4, VT_INT, VT_UI8, VT_I8
        finally { PropVariantClear(ref v); }
    }
}
