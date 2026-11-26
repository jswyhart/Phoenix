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

/// <summary>
/// Disc support: reading tracks off an audio CD (raw CDDA via the Windows CD-ROM driver), encoding them, and burning
/// (IMAPI2) audio CDs, MP3 discs and data discs.
/// </summary>
public static class CdAudio
{
    public const int SectorBytes = 2352;                 // one CD audio frame: 1/75 s of 44.1 kHz 16-bit stereo
    public static readonly WaveFormat CdFormat = new(44100, 16, 2);
    /// <summary>The rip formats. Settings keep the position here, so new ones go on the end; <see cref="FormatOrder"/> is the order shown.</summary>
    public static readonly string[] Formats =
    {
        "MP3 (320 kbps)", "AAC / M4A (256 kbps)", "WMA (192 kbps)", "WAV (lossless, uncompressed)", "FLAC (lossless)",
        "MP3 (256 kbps)", "MP3 (192 kbps)", "MP3 (128 kbps)", "AAC / M4A (192 kbps)", "AAC / M4A (128 kbps)", "WMA (128 kbps)",
    };
    public static readonly int[] FormatOrder = { 0, 5, 6, 7, 1, 8, 9, 2, 10, 4, 3 };
    static readonly Guid FlacSubtype = new("0000F1AC-0000-0010-8000-00AA00389B71");

    /// <summary>The three kinds of disc Phoenix burns (settings → burn, and the burn list).</summary>
    public static readonly string[] DiscKinds = { "audio CD", "MP3 CD", "data disc" };

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

    public static string Extension(int format) => format switch { 0 or 5 or 6 or 7 => ".mp3", 1 or 8 or 9 => ".m4a", 2 or 10 => ".wma", 4 => ".flac", _ => ".wav" };

    /// <summary>Encodes a WAV file to the chosen format using the encoders built into Windows.</summary>
    public static void Encode(string wavPath, string outPath, int format)
    {
        if (format == 3 || format < 0 || format >= Formats.Length) { File.Copy(wavPath, outPath, true); return; }
        MediaFoundationApi.Startup();
        using var reader = new WaveFileReader(wavPath);
        switch (format)
        {
            case 0: MediaFoundationEncoder.EncodeToMp3(reader, outPath, 320000); break;
            case 5: MediaFoundationEncoder.EncodeToMp3(reader, outPath, 256000); break;
            case 6: MediaFoundationEncoder.EncodeToMp3(reader, outPath, 192000); break;
            case 7: MediaFoundationEncoder.EncodeToMp3(reader, outPath, 128000); break;
            case 1: MediaFoundationEncoder.EncodeToAac(reader, outPath, 256000); break;
            case 8: MediaFoundationEncoder.EncodeToAac(reader, outPath, 192000); break;
            case 9: MediaFoundationEncoder.EncodeToAac(reader, outPath, 128000); break;
            case 10: MediaFoundationEncoder.EncodeToWma(reader, outPath, 128000); break;
            case 4:
            {
                // the FLAC encoder built into Windows 10 and later; it wants the exact PCM layout of the source
                var type = MediaFoundationEncoder.GetOutputMediaTypes(FlacSubtype)
                    .FirstOrDefault(t => t.SampleRate == reader.WaveFormat.SampleRate && t.ChannelCount == reader.WaveFormat.Channels && t.BitsPerSample == reader.WaveFormat.BitsPerSample)
                    ?? throw new NotSupportedException("This version of Windows has no FLAC encoder.");
                using var enc = new MediaFoundationEncoder(type);
                enc.Encode(outPath, reader);
                break;
            }
            default: MediaFoundationEncoder.EncodeToWma(reader, outPath, 192000); break;
        }
    }

    /// <summary>Any playable file or stream URL as 16 kHz mono samples, for the speech model that writes lyrics.</summary>
    public static float[] DecodeForSpeech(string source, CancellationToken cancel) => DecodeMono(source, 16000, cancel);

    /// <summary>Any playable file or stream URL as mono samples at the given rate (the speech model, the auto equalizer).</summary>
    public static float[] DecodeMono(string source, int rate, CancellationToken cancel)
    {
        MediaFoundationApi.Startup();
        using var reader = new MediaFoundationReader(source);
        using var resampler = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)) { ResamplerQuality = 60 };
        var samples = new List<float>((int)Math.Min(int.MaxValue / 8, reader.TotalTime.TotalSeconds * rate + rate));
        var buffer = new byte[rate * 4];
        int read;
        while ((read = resampler.Read(buffer.AsSpan())) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            for (var i = 0; i + 3 < read; i += 4) samples.Add(BitConverter.ToSingle(buffer, i));
        }
        return samples.ToArray();
    }

    /// <summary>Converts any playable file or stream URL to an MP3 (for MP3 discs).</summary>
    public static void ToMp3(string source, string outPath, int bitrate, CancellationToken cancel)
    {
        MediaFoundationApi.Startup();
        var wav = outPath + ".wav";
        try
        {
            using (var reader = new MediaFoundationReader(source))
            using (var resampler = new MediaFoundationResampler(reader, CdFormat) { ResamplerQuality = 60 })
            using (var writer = new WaveFileWriter(wav, CdFormat))
            {
                var buffer = new byte[SectorBytes * 64];
                int read;
                while ((read = resampler.Read(buffer.AsSpan())) > 0) { cancel.ThrowIfCancellationRequested(); writer.Write(buffer, 0, read); }
            }
            using var pcm = new WaveFileReader(wav);
            MediaFoundationEncoder.EncodeToMp3(pcm, outPath, bitrate);
        }
        finally { try { File.Delete(wav); } catch { } }
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

    /// <summary>One song for an MP3 or data disc.</summary>
    public record DiscFile(string Source, bool IsLocal, string Title, string Artist, string Album, int Number, long DurationMs, long Bytes);

    /// <summary>Bytes a CD-R holds as a data disc (700 MB).</summary>
    public const long CdBytes = 703_119_360;

    /// <summary>
    /// Lays the songs out in a folder ready to burn as an MP3 CD (everything as MP3) or a data disc (files as they are;
    /// songs on a server become MP3s). Files are numbered in burn list order, so players that sort by name play them in
    /// that order, and a playlist (.m3u) lists them too.
    /// </summary>
    public static void StageFiles(IReadOnlyList<DiscFile> songs, string folder, bool mp3Only, int bitrate, Action<int> preparing, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var names = new List<string>();
        for (var i = 0; i < songs.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            preparing(i);
            var s = songs[i];
            var ext = s.IsLocal ? Path.GetExtension(s.Source).ToLowerInvariant() : "";
            var copy = s.IsLocal && (ext == ".mp3" || !mp3Only) && File.Exists(s.Source);
            var name = DiscName($"{i + 1:00} {s.Artist} - {s.Title}") + (copy ? ext : ".mp3");
            var target = Path.Combine(folder, name);
            if (copy) File.Copy(s.Source, target, true);
            else
            {
                ToMp3(s.Source, target, bitrate, cancel);
                using var tf = TagLib.File.Create(target);
                tf.Tag.Title = s.Title;
                if (s.Artist != "") tf.Tag.Performers = new[] { s.Artist };
                if (s.Album != "") tf.Tag.Album = s.Album;
                if (s.Number > 0) tf.Tag.Track = (uint)s.Number;
                tf.Save();
            }
            names.Add(name);
        }
        File.WriteAllLines(Path.Combine(folder, "playlist.m3u"), names, new System.Text.UTF8Encoding(false));
    }

    /// <summary>A file name that suits the disc's file system (Joliet allows 64 characters; some players are fussy).</summary>
    public static string DiscName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { ';', '*', '?' })) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length > 59 ? name[..59].TrimEnd() : name;
    }

    /// <summary>Builds the disc image (ISO 9660 and Joliet) of a folder; returns its size in bytes. Needs an STA thread.</summary>
    public static long ImageSize(string folder, string label)
    {
        dynamic image = Create("IMAPI2FS.MsftFileSystemImage");
        image.FileSystemsToCreate = 3;   // ISO 9660 + Joliet: every CD player, car stereo and computer reads it
        image.FreeMediaBlocks = 0;       // no disc to measure against: as large as it needs to be
        image.VolumeName = VolumeLabel(label);
        image.Root.AddTree(folder, false);
        dynamic result = image.CreateResultImage();
        return (long)(int)result.TotalBlocks * (int)result.BlockSize;
    }

    static string VolumeLabel(string label)
    {
        var s = new string(label.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return s.Length == 0 ? "PHOENIX" : s.Length > 16 ? s[..16] : s;
    }

    static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new NotSupportedException("Disc burning (IMAPI2) is not available on this PC."))!;

    static dynamic FindRecorder(string drive)
    {
        dynamic master = Create("IMAPI2.MsftDiscMaster2");
        for (var i = 0; i < (int)master.Count; i++)
        {
            dynamic r = Create("IMAPI2.MsftDiscRecorder2");
            r.InitializeDiscRecorder((string)master[i]);
            foreach (object path in (object[])r.VolumePathNames)
                if (((string)path).TrimEnd('\\').Equals(drive, StringComparison.OrdinalIgnoreCase)) return r;
        }
        throw new IOException($"Drive {drive} is not a disc recorder.");
    }

    /// <summary>
    /// Burns a folder to a blank disc (CD-R/RW, DVD±R/RW or BD-R) as a data disc any computer, car stereo or MP3 CD
    /// player can read. Must be called from an STA thread; blocks until the disc is finished.
    /// </summary>
    public static void BurnData(string drive, string folder, string label, Action<string> status)
    {
        var recorder = FindRecorder(drive);
        dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
        format.ClientName = "Phoenix";
        format.Recorder = recorder;
        if (!(bool)format.IsCurrentMediaSupported(recorder) || !(bool)format.MediaHeuristicallyBlank)
            throw new IOException("Put a blank recordable disc (CD-R, CD-RW, DVD±R or BD-R) in the drive.");
        format.ForceMediaToBeClosed = true;
        status("building the disc…");
        dynamic image = Create("IMAPI2FS.MsftFileSystemImage");
        image.ChooseImageDefaults(recorder);
        image.FileSystemsToCreate = 3;
        image.VolumeName = VolumeLabel(label);
        image.Root.AddTree(folder, false);
        dynamic result = image.CreateResultImage();
        var bytes = (long)(int)result.TotalBlocks * (int)result.BlockSize;
        var room = (long)(int)format.FreeSectorsOnMedia * 2048;
        if (room > 0 && bytes > room) throw new IOException($"The songs need {bytes / 1_000_000} MB but the disc holds {room / 1_000_000} MB.");
        status("burning…");
        format.Write(result.ImageStream);
        status("finishing disc…");
        try { recorder.EjectMedia(); } catch { }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IStream? template, out IStream stream);

    /// <summary>
    /// Burns raw CD-audio files (from <see cref="DecodeForCd"/>) to a blank disc as an audio CD, one track each.
    /// Must be called from an STA thread; blocks until the disc is finished.
    /// </summary>
    public static void Burn(string drive, IReadOnlyList<string> rawFiles, Action<string> status)
    {
        var recorder = FindRecorder(drive);

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
