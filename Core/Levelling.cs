using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;

namespace Halcyon.Core;

/// <summary>
/// Volume levelling: how much louder or quieter a song should be played so that everything sits at about the same
/// loudness. The answer comes from the file's ReplayGain tags when it has them; otherwise the song is measured here
/// (ITU-R BS.1770, the method behind ReplayGain 2 and "LUFS") and the result is remembered in the library.
/// </summary>
public static class Levelling
{
    /// <summary>The loudness everything is brought to, in LUFS (the ReplayGain 2 reference).</summary>
    public const double Target = -18;
    const double MaxBoost = 12, MaxCut = -20;

    static readonly ConcurrentDictionary<long, (double Track, double Album)> Known = new();
    static readonly ConcurrentDictionary<long, Task<double>> Measuring = new();

    /// <summary>Songs measured since the app started (for the self-test).</summary>
    public static int MeasuredCount;

    static float Linear(double db) => (float)Math.Pow(10, Math.Clamp(db, MaxCut, MaxBoost) / 20);

    /// <summary>
    /// The engine's question when it opens a song: the gain to start with, and (if the song has to be measured first)
    /// a later call to <paramref name="update"/> with the real one.
    /// </summary>
    public static float For(PlayItem item, Action<float> update)
    {
        if (!App.Settings.Levelling || item.Track is not { } t) return 1;
        var byAlbum = App.Settings.LevelByAlbum;
        if (Lookup(t) is { } g)
        {
            var db = byAlbum && !double.IsNaN(g.Album) ? g.Album : g.Track;
            if (!double.IsNaN(db)) return Linear(db);
        }
        if (!t.IsLocal || !App.Settings.LevelMeasure || !File.Exists(t.Location)) return 1;
        _ = MeasureLater(t).ContinueWith(done =>
        {
            if (done.Status == TaskStatus.RanToCompletion && !double.IsNaN(done.Result) && App.Settings.Levelling) update(Linear(done.Result));
        });
        return 1;
    }

    /// <summary>What is already known about a song: from this run, from the library, or from the file's tags.</summary>
    public static (double Track, double Album)? Lookup(Track t)
    {
        if (Known.TryGetValue(t.Id, out var k)) return k;
        if (t.Id > 0 && App.Lib.Db.Loudness(t.Id, t.FileModified) is { } stored) return Known[t.Id] = (stored.Track, stored.Album);
        if (!t.IsLocal) return null;
        var tags = ReadTags(t.Location);
        if (tags == null) return null;
        if (t.Id > 0) App.Lib.Db.SaveLoudness(t.Id, t.FileModified, tags.Value.Track, tags.Value.Album, false);
        return Known[t.Id] = tags.Value;
    }

    /// <summary>ReplayGain as written in a file's tags, or null if it has none.</summary>
    public static (double Track, double Album)? ReadTags(string file)
    {
        try
        {
            using var f = TagLib.File.Create(file);
            double track = f.Tag.ReplayGainTrackGain, album = f.Tag.ReplayGainAlbumGain;
            return double.IsNaN(track) && double.IsNaN(album) ? null : (double.IsNaN(track) ? album : track, album);
        }
        catch { return null; }
    }

    static Task<double> MeasureLater(Track t) => Measuring.GetOrAdd(t.Id, id => Task.Run(() =>
    {
        try
        {
            var lufs = Measure(t.Location);
            if (double.IsNaN(lufs)) return double.NaN;
            var gain = Target - lufs;
            Known[t.Id] = (gain, double.NaN);
            if (t.Id > 0) App.Lib.Db.SaveLoudness(t.Id, t.FileModified, gain, double.NaN, true);
            Interlocked.Increment(ref MeasuredCount);
            return gain;
        }
        catch { return double.NaN; }
        finally { Measuring.TryRemove(t.Id, out _); }
    }));

    /// <summary>The gain for a song in decibels, measuring it now if nothing is known (for the self-test and settings).</summary>
    public static async Task<double> GainAsync(Track t)
    {
        if (Lookup(t) is { } g && !double.IsNaN(g.Track)) return g.Track;
        return await MeasureLater(t);
    }

    public static void Forget() => Known.Clear();

    // ---------- measuring (ITU-R BS.1770-4) ----------

    sealed class KFilter
    {
        readonly double _b0, _b1, _b2, _a1, _a2, _c1, _c2;
        double _x1, _x2, _y1, _y2, _u1, _u2, _v1, _v2;

        public KFilter(double rate)
        {
            // stage 1: a high shelf that stands for the head's effect on what reaches the ear
            double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * f0 / rate);
            double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416), a0 = 1 + k / q + k * k;
            _b0 = (vh + vb * k / q + k * k) / a0; _b1 = 2 * (k * k - vh) / a0; _b2 = (vh - vb * k / q + k * k) / a0;
            _a1 = 2 * (k * k - 1) / a0; _a2 = (1 - k / q + k * k) / a0;
            // stage 2: a high pass (very low rumble counts for little)
            f0 = 38.13547087602444; q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / rate);
            a0 = 1 + k / q + k * k;
            _c1 = 2 * (k * k - 1) / a0; _c2 = (1 - k / q + k * k) / a0;
        }

        public double Run(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            var v = y - 2 * _u1 + _u2 - _c1 * _v1 - _c2 * _v2;
            _u2 = _u1; _u1 = y; _v2 = _v1; _v1 = v;
            return v;
        }
    }

    /// <summary>Integrated loudness of a file in LUFS; NaN if it is silent or cannot be read.</summary>
    public static double Measure(string file)
    {
        using var reader = new MediaFoundationReader(file, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true });
        return Measure(reader.ToSampleProvider());
    }

    public static double Measure(ISampleProvider source)
    {
        int channels = source.WaveFormat.Channels, rate = source.WaveFormat.SampleRate;
        var use = Math.Min(channels, 2);
        var filters = Enumerable.Range(0, use).Select(_ => new KFilter(rate)).ToArray();
        // loudness is taken over 400 ms blocks that overlap by three quarters: sums are kept per 100 ms
        var step = rate / 10;
        var steps = new List<double>();
        var buffer = new float[step * channels];
        double sum = 0;
        var inStep = 0;
        while (true)
        {
            var read = source.Read(buffer.AsSpan());
            if (read <= 0) break;
            for (var i = 0; i + channels <= read; i += channels)
            {
                for (var c = 0; c < use; c++) { var v = filters[c].Run(buffer[i + c]); sum += v * v; }
                if (++inStep == step) { steps.Add(sum / step); sum = 0; inStep = 0; }
            }
            if (steps.Count > 10 * 60 * 30) break;   // half an hour is plenty to judge by
        }
        if (channels == 1) for (var i = 0; i < steps.Count; i++) steps[i] *= 2;   // a mono file plays from both speakers
        var blocks = new List<double>();
        for (var i = 0; i + 4 <= steps.Count; i++) blocks.Add((steps[i] + steps[i + 1] + steps[i + 2] + steps[i + 3]) / 4);
        if (blocks.Count == 0) return double.NaN;
        static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);
        var loud = blocks.Where(b => b > 0 && Lufs(b) > -70).ToList();           // silence does not count
        if (loud.Count == 0) return double.NaN;
        var gate = Lufs(loud.Average()) - 10;                                    // nor do passages far quieter than the rest
        var kept = loud.Where(b => Lufs(b) > gate).ToList();
        return kept.Count == 0 ? double.NaN : Lufs(kept.Average());
    }
}
