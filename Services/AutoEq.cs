using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// The auto equalizer: each song is measured once (how much of its sound falls in each of the equalizer's ten bands),
/// and while it plays the equalizer is set from that measurement for the sound you chose: accuracy (an even tonal
/// balance), clarity (voices and detail forward, muddiness trimmed) or bass (a fuller low end, more on thin songs, less on
/// bass-heavy ones). The measurement is kept in Phoenix's library, in the song's tags, or in a phoenix-eq.xml file in the
/// album folder; it doesn't depend on the sound chosen, so switching sounds needs no new scan.
/// Shared by the Windows app, the Mac/Linux helper and the Android app; each supplies <see cref="Decode"/>.
/// </summary>
public static class AutoEq
{
    public static readonly float[] Bands = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public record Profile(string Id, string Name, string About);
    public static readonly Profile[] Profiles =
    {
        new("accuracy", "sound accuracy", "evens out each song's tonal balance, gently"),
        new("clarity", "clarity", "voices and detail forward, muddiness trimmed"),
        new("bass", "bass", "a fuller low end: more on thin songs, less on bass-heavy ones"),
    };

    public record Store(string Id, string Name);
    public static readonly Store[] Stores =
    {
        new("phoenix", "in Phoenix's library (your files are not changed)"),
        new("tags", "in each song's tags (travels with the file, also to your phone)"),
        new("folder", "in a phoenix-eq.xml file in each album folder"),
    };

    public const string TagName = "PHOENIX_AUTOEQ";
    public const string FolderFile = "phoenix-eq.xml";

    /// <summary>Turns a file into mono samples and their rate. Set by the app.</summary>
    public static Func<string, CancellationToken, (float[] Samples, int Rate)>? Decode;

    /// <summary>Raised (from any thread) when a song has been measured.</summary>
    public static event Action<Track>? Measured;

    // ---------- measuring ----------

    /// <summary>
    /// How loud each band is, in dB, averaged over the song (quiet stretches left out). Null for a song too short or too
    /// quiet to tell.
    /// </summary>
    public static double[]? Measure(float[] samples, int rate)
    {
        const int n = 4096;
        if (samples.Length < n * 8 || rate < 8000) return null;
        var window = new double[n];
        for (var i = 0; i < n; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        var power = new double[n / 2];
        var re = new double[n];
        var im = new double[n];
        var frames = 0;
        // every frame for a short song, every other one for a long one: the average hardly changes and it is twice as quick
        var hop = samples.Length > rate * 240 ? n * 2 : n;
        for (var start = 0; start + n <= samples.Length; start += hop)
        {
            double sum = 0;
            for (var i = 0; i < n; i++) { var v = samples[start + i]; sum += v * v; }
            if (sum / n < 1e-6) continue;   // below -60 dBFS: silence or a fade, left out
            for (var i = 0; i < n; i++) { re[i] = samples[start + i] * window[i]; im[i] = 0; }
            Fft(re, im);
            for (var k = 1; k < n / 2; k++) power[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }
        if (frames < 4) return null;
        var levels = new double[Bands.Length];
        var binHz = (double)rate / n;
        for (var b = 0; b < Bands.Length; b++)
        {
            double lo = Bands[b] / Math.Sqrt(2), hi = Bands[b] * Math.Sqrt(2), e = 0;
            for (var k = Math.Max(1, (int)Math.Ceiling(lo / binHz)); k < n / 2 && k * binHz < hi; k++) e += power[k];
            levels[b] = 10 * Math.Log10(e / frames + 1e-12);
        }
        return levels;
    }

    /// <summary>An in-place radix-2 FFT.</summary>
    static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    int a = i + j, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    // ---------- from a measurement to the equalizer ----------

    /// <summary>
    /// A well-balanced mix, band by band (dB, octave bands, so pink noise would be flat): full lows, a gentle fall
    /// through the mids and a steeper one at the very top, as typical of well-mastered music.
    /// </summary>
    static readonly double[] Target = { -6, 0, 0, -1, -2, -3, -4.5, -6, -8, -14 };

    /// <summary>The ten band gains (dB) for a measured song and the sound chosen.</summary>
    public static double[] Gains(double[] levels, string profile)
    {
        // line the song up with the target through the middle bands, then see where it strays
        double mid = 0, midT = 0;
        for (var b = 2; b <= 7; b++) { mid += levels[b]; midT += Target[b]; }
        var shift = (midT - mid) / 6;
        var off = new double[Bands.Length];
        for (var b = 0; b < Bands.Length; b++) off[b] = Target[b] - (levels[b] + shift);   // > 0: the song is short of this band

        // sound accuracy: half way back to the target, smoothed so no band jumps away from its neighbours
        var accuracy = new double[Bands.Length];
        for (var b = 0; b < Bands.Length; b++) accuracy[b] = Math.Clamp(off[b] * 0.5, -6, 6);
        accuracy = Smooth(accuracy);
        accuracy[0] = Math.Min(accuracy[0], 3);    // deep bass and the very top are only lifted a little
        accuracy[9] = Math.Min(accuracy[9], 4);

        var g = new double[Bands.Length];
        switch (profile)
        {
            case "clarity":
            {
                // presence and air lifted; low mids trimmed, more where the song has too much of them
                double[] lift = { 0, 0, 0, -1, -1, 0, 1.5, 2.5, 1.5, 0.5 };
                for (var b = 0; b < Bands.Length; b++) g[b] = accuracy[b] * 0.6 + lift[b];
                g[3] -= Math.Clamp(-off[3] * 0.5, 0, 2);
                g[4] -= Math.Clamp(-off[4] * 0.5, 0, 2);
                break;
            }
            case "bass":
            {
                // a low shelf, stronger the thinner the song's low end is
                var shortOfBass = (off[0] + off[1] + off[2]) / 3;
                var boost = Math.Clamp(3 + shortOfBass * 0.7, 1, 8);
                double[] shelf = { 0.8, 1, 0.8, 0.35, 0, 0, 0, 0, 0, 0 };
                for (var b = 0; b < Bands.Length; b++) g[b] = accuracy[b] * 0.5 + boost * shelf[b];
                g[0] = Math.Min(g[0], 6);
                break;
            }
            default:
                g = accuracy;
                break;
        }
        for (var b = 0; b < Bands.Length; b++) g[b] = Math.Round(Math.Clamp(g[b], -8, 8) * 2) / 2;
        return g;
    }

    static double[] Smooth(double[] v)
    {
        var s = new double[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            double l = v[Math.Max(0, i - 1)], r = v[Math.Min(v.Length - 1, i + 1)];
            s[i] = l * 0.25 + v[i] * 0.5 + r * 0.25;
        }
        return s;
    }

    /// <summary>
    /// What the equalizer is set to for a song: the equalizer of your own (when it is on) plus the auto equalizer's
    /// gains for this song (when that is on and the song has been measured). Null for no equalizing at all.
    /// </summary>
    public static double[]? Combine(Settings s, Track? t)
    {
        var manual = s.EqEnabled && s.EqGains.Length == Bands.Length ? s.EqGains : null;
        var auto = s.AutoEq && t != null && LevelsFor(t) is { } l ? Gains(l, s.AutoEqProfile) : null;
        if (auto == null) return manual;
        if (manual == null) return auto;
        return manual.Zip(auto, (a, b) => Math.Clamp(a + b, -12, 12)).ToArray();
    }

    // ---------- where measurements are kept ----------

    static string Key(Track t) => t.Source + "|" + t.SourceId;
    static string LibraryFile => Path.Combine(AppPaths.DataDir, "autoeq.json");
    static Dictionary<string, string>? _library;
    static readonly object Gate = new();

    static Dictionary<string, string> Library()
    {
        lock (Gate)
        {
            if (_library != null) return _library;
            try { _library = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LibraryFile)) ?? new(); }
            catch { _library = new(); }
            return _library;
        }
    }

    static void SaveLibrary()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(LibraryFile, JsonSerializer.Serialize(_library ?? new()));
            }
            catch { }
        }
    }

    public static string Format(double[] levels) => "v1;" + string.Join(",", levels.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture)));

    public static double[]? Parse(string? text)
    {
        if (text == null || !text.StartsWith("v1;")) return null;
        var parts = text[3..].Split(',');
        if (parts.Length != Bands.Length) return null;
        var v = new double[Bands.Length];
        for (var i = 0; i < v.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        return v;
    }

    static readonly Dictionary<string, double[]?> Cache = new();

    /// <summary>A song's measurement, wherever it was kept (Phoenix's library, its tags, or its album folder's file).</summary>
    public static double[]? LevelsFor(Track t)
    {
        var key = Key(t);
        lock (Gate) if (Cache.TryGetValue(key, out var hit)) return hit;
        double[]? found = null;
        if (Library().TryGetValue(key, out var kept)) found = Parse(kept);
        if (found == null && t.IsLocal && File.Exists(t.Location))
            found = Parse(ReadTag(t.Location)) ?? Parse(ReadFolder(t.Location));
        lock (Gate) Cache[key] = found;
        return found;
    }

    public static bool IsMeasured(Track t) => LevelsFor(t) != null;

    /// <summary>Keeps a measurement where settings say.</summary>
    public static void Remember(Track t, double[] levels, string store)
    {
        var text = Format(levels);
        var kept = false;
        if (t.IsLocal && File.Exists(t.Location))
        {
            if (store == "tags") kept = WriteTag(t.Location, text);
            else if (store == "folder") kept = WriteFolder(t.Location, text);
        }
        lock (Gate)
        {
            // Phoenix's library is the fallback for songs whose tags can't be written, and for songs on a server
            if (!kept || store == "phoenix") { Library()[Key(t)] = text; }
            Cache[Key(t)] = levels;
        }
        if (!kept || store == "phoenix") SaveLibrary();
    }

    /// <summary>Forgets every measurement Phoenix keeps itself (tags and album files are left alone).</summary>
    public static void ForgetLibrary()
    {
        lock (Gate) { _library = new(); Cache.Clear(); }
        SaveLibrary();
    }

    public static string? ReadTag(string file)
    {
        try
        {
            using var f = TagLib.File.Create(file);
            if (f.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment x && x.GetFirstField(TagName) is { Length: > 0 } xv) return xv;
            if (f.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag a && a.GetDashBox("com.phoenix", "AUTOEQ") is { Length: > 0 } av) return av;
            if (f.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag w && w.GetDescriptorString("PHOENIX/AutoEq") is { Length: > 0 } wv) return wv;
            if (f.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3 && TagLib.Id3v2.UserTextInformationFrame.Get(id3, TagName, false) is { } fr) return fr.Text.FirstOrDefault();
        }
        catch { }
        return null;
    }

    static bool WriteTag(string file, string text)
    {
        try
        {
            using var f = TagLib.File.Create(file);
            if (f.GetTag(TagLib.TagTypes.Xiph, true) is TagLib.Ogg.XiphComment x) x.SetField(TagName, text);
            else if (f.GetTag(TagLib.TagTypes.Apple, true) is TagLib.Mpeg4.AppleTag a) a.SetDashBox("com.phoenix", "AUTOEQ", text);
            else if (f.GetTag(TagLib.TagTypes.Asf, true) is TagLib.Asf.Tag w) w.SetDescriptorString(text, "PHOENIX/AutoEq");
            else if (f.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3) TagLib.Id3v2.UserTextInformationFrame.Get(id3, TagName, true).Text = new[] { text };
            else return false;
            f.Save();
            return true;
        }
        catch { return false; }
    }

    static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, string> Songs)> Folders = new(StringComparer.OrdinalIgnoreCase);

    static string? ReadFolder(string file)
    {
        try
        {
            var xml = Path.Combine(Path.GetDirectoryName(file)!, FolderFile);
            if (!File.Exists(xml)) return null;
            var stamp = File.GetLastWriteTimeUtc(xml);
            Dictionary<string, string> songs;
            lock (Gate)
            {
                if (!Folders.TryGetValue(xml, out var cached) || cached.Stamp != stamp)
                {
                    cached = (stamp, XDocument.Load(xml).Root!.Elements("track")
                        .Where(e => e.Attribute("file") != null && e.Attribute("levels") != null)
                        .GroupBy(e => (string)e.Attribute("file")!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => (string)g.Last().Attribute("levels")!, StringComparer.OrdinalIgnoreCase));
                    Folders[xml] = cached;
                }
                songs = cached.Songs;
            }
            return songs.TryGetValue(Path.GetFileName(file), out var v) ? v : null;
        }
        catch { return null; }
    }

    static bool WriteFolder(string file, string text)
    {
        try
        {
            var xml = Path.Combine(Path.GetDirectoryName(file)!, FolderFile);
            lock (Gate)
            {
                var doc = File.Exists(xml) ? XDocument.Load(xml) : new XDocument(new XElement("phoenix-autoeq", new XAttribute("version", "1"),
                    new XComment(" Phoenix's auto equalizer: how much of each song's sound falls in each band (31 Hz to 16 kHz, dB). ")));
                var root = doc.Root!;
                var name = Path.GetFileName(file);
                root.Elements("track").Where(e => string.Equals((string?)e.Attribute("file"), name, StringComparison.OrdinalIgnoreCase)).Remove();
                root.Add(new XElement("track", new XAttribute("file", name), new XAttribute("levels", text)));
                doc.Save(xml);
                Folders.Remove(xml);
            }
            return true;
        }
        catch { return false; }
    }

    // ---------- scanning ----------

    static readonly SemaphoreSlim One = new(1, 1);

    /// <summary>Measures one song (if it can be read) and keeps the result. True when it was measured.</summary>
    public static async Task<bool> MeasureAsync(Track t, string store, CancellationToken cancel)
    {
        if (!t.IsLocal || !File.Exists(t.Location) || Decode is not { } decode) return false;
        await One.WaitAsync(cancel);
        try
        {
            var levels = await Task.Run(() =>
            {
                var (samples, rate) = decode(t.Location, cancel);
                return Measure(samples, rate);
            }, cancel);
            if (levels == null) return false;
            Remember(t, levels, store);
            Measured?.Invoke(t);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally { One.Release(); }
    }

    static readonly HashSet<string> Tried = new();

    /// <summary>Measures the song playing in the background if it hasn't been yet (once per song per session).</summary>
    public static void EnsureMeasured(Settings s, Track? t)
    {
        if (!s.AutoEq || t == null || !t.IsLocal) return;
        lock (Gate) if (!Tried.Add(Key(t))) return;
        if (IsMeasured(t)) return;
        _ = MeasureAsync(t, s.AutoEqStore, CancellationToken.None);
    }

    /// <summary>Measures every song not measured yet, one at a time; reports (done, total, measured).</summary>
    public static async Task<(int Done, int Measured)> ScanAsync(IReadOnlyList<Track> songs, string store, Action<int, int, int>? progress, CancellationToken cancel)
    {
        var todo = songs.Where(t => t.IsLocal && !IsMeasured(t)).ToList();
        int done = 0, measured = 0;
        foreach (var t in todo)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(done, todo.Count, measured);
            if (await MeasureAsync(t, store, cancel)) measured++;
            done++;
        }
        progress?.Invoke(done, todo.Count, measured);
        return (done, measured);
    }
}
