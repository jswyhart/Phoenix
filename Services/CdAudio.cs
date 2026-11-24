using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Halcyon.Services;

public record CdTrack(int Number, int StartLba, int Sectors)
{
    public long DurationMs => Sectors * 1000L / 75;
}

/// <summary>Audio CD support: reading tracks off a disc (raw CDDA via the Windows CD-ROM driver), encoding, and burning (IMAPI2).</summary>
public static class CdAudio
{
    public const int SectorBytes = 2352;                 // one CD audio frame: 1/75 s of 44.1 kHz 16-bit stereo
    public static readonly WaveFormat CdFormat = new(44100, 16, 2);
    public static readonly string[] Formats = { "MP3 (320 kbps)", "AAC / M4A (256 kbps)", "WMA (192 kbps)", "WAV (lossless)" };

    public static List<string> Drives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => d.Name.TrimEnd('\\')).ToList();

    // ---------- reading ----------

    const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint IOCTL_CDROM_READ_TOC = 0x00024000, IOCTL_CDROM_RAW_READ = 0x0002403E;

    [StructLayout(LayoutKind.Sequential)]
    struct RAW_READ_INFO { public long DiskOffset; public uint SectorCount; public uint TrackMode; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref RAW_READ_INFO inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    static SafeFileHandle Open(string drive)
    {
        var h = CreateFile(@"\\.\" + drive, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Can't open drive {drive}.");
        return h;
    }

    /// <summary>Audio tracks on the disc in the drive; empty when there is no audio CD.</summary>
    public static List<CdTrack> ReadToc(string drive)
    {
        using var h = Open(drive);
        var toc = new byte[804];   // CDROM_TOC: 4-byte header + 100 track entries of 8 bytes
        if (!DeviceIoControl(h, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, toc, toc.Length, out _, IntPtr.Zero)) return new();
        int first = toc[2], last = toc[3];
        var tracks = new List<CdTrack>();
        for (var n = first; n <= last; n++)
        {
            var e = 4 + (n - first) * 8;
            var next = e + 8;                               // following entry is the next track, or the lead-out after the last one
            var isData = (toc[e + 1] & 0x04) != 0;
            int start = Lba(toc, e), end = Lba(toc, next);
            if (!isData && end > start) tracks.Add(new CdTrack(toc[e + 2], start, end - start));
        }
        return tracks;

        static int Lba(byte[] t, int entry) => (t[entry + 5] * 60 + t[entry + 6]) * 75 + t[entry + 7] - 150;   // minutes:seconds:frames
    }

    /// <summary>Copies one track to a 44.1 kHz 16-bit stereo WAV file.</summary>
    public static void RipTrack(string drive, CdTrack track, string wavPath, Action<double> progress, CancellationToken cancel)
    {
        const int chunk = 20;
        using var h = Open(drive);
        using var writer = new WaveFileWriter(wavPath, CdFormat);
        var buffer = new byte[SectorBytes * chunk];
        for (var done = 0; done < track.Sectors; done += chunk)
        {
            cancel.ThrowIfCancellationRequested();
            var count = Math.Min(chunk, track.Sectors - done);
            var info = new RAW_READ_INFO { DiskOffset = (long)(track.StartLba + done) * 2048, SectorCount = (uint)count, TrackMode = 2 /* CDDA */ };
            var ok = false;
            for (var attempt = 0; attempt < 4 && !ok; attempt++)
                ok = DeviceIoControl(h, IOCTL_CDROM_RAW_READ, ref info, Marshal.SizeOf<RAW_READ_INFO>(), buffer, count * SectorBytes, out _, IntPtr.Zero);
            if (!ok) throw new IOException($"Read error on track {track.Number} (the disc may be scratched).");
            writer.Write(buffer, 0, count * SectorBytes);
            progress((double)(done + count) / track.Sectors);
        }
    }

    // ---------- encoding ----------

    public static string Extension(int format) => format switch { 0 => ".mp3", 1 => ".m4a", 2 => ".wma", _ => ".wav" };

    /// <summary>Encodes a WAV file to the chosen format using the encoders built into Windows.</summary>
    public static void Encode(string wavPath, string outPath, int format)
    {
        if (format >= 3) { File.Copy(wavPath, outPath, true); return; }
        MediaFoundationApi.Startup();
        using var reader = new WaveFileReader(wavPath);
        switch (format)
        {
            case 0: MediaFoundationEncoder.EncodeToMp3(reader, outPath, 320000); break;
            case 1: MediaFoundationEncoder.EncodeToAac(reader, outPath, 256000); break;
            default: MediaFoundationEncoder.EncodeToWma(reader, outPath, 192000); break;
        }
    }

    // ---------- burning ----------

    /// <summary>Decodes any playable file or stream URL to raw CD audio, padded to a whole number of sectors.</summary>
    public static void DecodeForCd(string source, string rawPath, CancellationToken cancel)
    {
        MediaFoundationApi.Startup();
        using var reader = new MediaFoundationReader(source);
        using var resampler = new MediaFoundationResampler(reader, CdFormat) { ResamplerQuality = 60 };
        using var output = File.Create(rawPath);
        var buffer = new byte[SectorBytes * 64];
        int read;
        long total = 0;
        while ((read = resampler.Read(buffer.AsSpan())) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            total += read;
        }
        var pad = (int)((SectorBytes - total % SectorBytes) % SectorBytes);
        if (pad > 0) output.Write(new byte[pad], 0, pad);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IStream? template, out IStream stream);

    /// <summary>
    /// Burns raw CD-audio files (from <see cref="DecodeForCd"/>) to a blank disc as an audio CD, one track each.
    /// Must be called from an STA thread; blocks until the disc is finished.
    /// </summary>
    public static void Burn(string drive, IReadOnlyList<string> rawFiles, Action<string> status)
    {
        static dynamic Create(string progId) =>
            Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new NotSupportedException("Disc burning (IMAPI2) is not available on this PC."))!;

        dynamic master = Create("IMAPI2.MsftDiscMaster2");
        dynamic? recorder = null;
        for (var i = 0; i < (int)master.Count; i++)
        {
            dynamic r = Create("IMAPI2.MsftDiscRecorder2");
            r.InitializeDiscRecorder((string)master[i]);
            foreach (object path in (object[])r.VolumePathNames)
                if (((string)path).TrimEnd('\\').Equals(drive, StringComparison.OrdinalIgnoreCase)) recorder = r;
        }
        if (recorder == null) throw new IOException($"Drive {drive} is not a disc recorder.");

        dynamic format = Create("IMAPI2.MsftDiscFormat2TrackAtOnce");
        format.ClientName = "Phoenix";
        if (!(bool)format.IsCurrentMediaSupported(recorder)) throw new IOException("Put a blank recordable CD (CD-R or CD-RW) in the drive.");
        format.Recorder = recorder;
        status("preparing disc…");
        format.PrepareMedia();
        try
        {
            for (var i = 0; i < rawFiles.Count; i++)
            {
                status($"burning track {i + 1} of {rawFiles.Count}…");
                SHCreateStreamOnFileEx(rawFiles[i], 0 /* STGM_READ */, 0, false, null, out var stream);
                try { format.AddAudioTrack(stream); }
                finally { Marshal.ReleaseComObject(stream); }
            }
        }
        finally
        {
            status("finishing disc…");
            format.ReleaseMedia();
        }
        try { recorder.EjectMedia(); } catch { }
    }
}
