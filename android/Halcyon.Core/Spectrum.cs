namespace Halcyon.Core;

/// <summary>The visualizer's named choices (same order as the Windows app), stored by index in the settings.</summary>
public static class VisualizerOptions
{
    /// <summary>Index 0 means "off" (the album art is shown instead).</summary>
    public static readonly string[] Styles =
    {
        "album art wall", "spectrum bars", "mirrored bars", "oscilloscope", "radial spectrum", "spectrum wave", "starburst", "waterfall",
        "dot matrix", "lissajous", "circular scope", "ripples", "fireworks", "tunnel", "analog meters"
    };
    public static readonly string[] Palettes = { "sunset", "rainbow", "ice", "emerald", "mono", "fire", "neon", "ocean", "violet", "gold", "album colours" };
    public static readonly string[] Sensitivities = { "low", "normal", "high", "max" };
}

/// <summary>
/// The analysis half of the classic visualizer: keeps the latest samples and turns them into spectrum bars with
/// falling peak caps, plus the raw wave for the oscilloscope. Drawing is done by the platform view.
/// </summary>
public class Spectrum
{
    public const int FftSize = 1024, BarCount = 40;
    readonly float[] _ring = new float[FftSize];
    readonly object _lock = new();
    int _write;

    public const int HistoryRows = 48;

    /// <summary>0 low, 1 normal, 2 high, 3 max: how strongly quiet music moves the display.</summary>
    public int Sensitivity { get; set; } = 1;
    public int SampleRate { get; private set; } = 44100;
    public double[] Bars { get; } = new double[BarCount];
    public double[] Peaks { get; } = new double[BarCount];
    public float[] Scope { get; private set; } = new float[FftSize];

    // ---- what the animated styles need, advanced by every Update() ----
    /// <summary>Average level of all bars, 0..1.</summary>
    public double Energy { get; private set; }
    /// <summary>A running clock that moves faster when the music is loud (spins and drifts).</summary>
    public double Phase { get; private set; }
    /// <summary>1 on a beat, then fading towards 0.</summary>
    public double Beat { get; private set; }
    /// <summary>Number of beats detected so far.</summary>
    public int Beats { get; private set; }
    /// <summary>Recent bar levels, newest first (for the waterfall).</summary>
    public List<double[]> History { get; } = new();
    public record struct Ripple(double Radius, double Level, double Hue);
    public List<Ripple> Ripples { get; } = new();
    public class Spark { public double X, Y, Vx, Vy, Life, Hue, Size; }
    public List<Spark> Sparks { get; } = new();
    /// <summary>Analog meter needles: low end and high end of the spectrum, 0..1.</summary>
    public double[] Needle { get; } = new double[2];

    double _bassAvg;
    long _lastBeatMs = -10000;
    readonly Random _rnd = new();
    /// <summary>Clock used for the beat spacing; tests replace it.</summary>
    public Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    /// <summary>Adds mono samples (-1..1) to the analysis window.</summary>
    public void Feed(ReadOnlySpan<float> mono, int sampleRate)
    {
        lock (_lock)
        {
            SampleRate = sampleRate;
            foreach (var s in mono) { _ring[_write] = s; _write = (_write + 1) % FftSize; }
        }
    }

    /// <summary>Adds unsigned 8-bit PCM, the format Android's audio-effect visualizer captures.</summary>
    public void FeedU8(byte[] wave, int sampleRate)
    {
        var mono = new float[wave.Length];
        for (var i = 0; i < wave.Length; i++) mono[i] = (wave[i] - 128) / 128f;
        Feed(mono, sampleRate);
    }

    public void Clear()
    {
        lock (_lock) Array.Clear(_ring);
        Array.Clear(Bars);
        Array.Clear(Peaks);
        Scope = new float[FftSize];
        History.Clear(); Ripples.Clear(); Sparks.Clear();
        Array.Clear(Needle);
        Energy = Beat = _bassAvg = 0;
    }

    /// <summary>Recomputes bar heights from the most recent samples.</summary>
    public void Update()
    {
        var re = new double[FftSize];
        var im = new double[FftSize];
        var scope = new float[FftSize];
        lock (_lock)
            for (var i = 0; i < FftSize; i++)
            {
                var s = _ring[(_write + i) % FftSize];
                scope[i] = s;
                re[i] = s * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // Hann window
            }
        Scope = scope;
        Fft(re, im);

        // Bars are spaced logarithmically from 40 Hz to 16 kHz, like a graphic equalizer.
        const double lo = 40;
        var hi = Math.Min(16000, SampleRate / 2.0 * 0.95);
        var binHz = (double)SampleRate / FftSize;
        double sum = 0;
        for (var b = 0; b < BarCount; b++)
        {
            var f0 = lo * Math.Pow(hi / lo, (double)b / BarCount);
            var f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / BarCount);
            int i0 = Math.Max(1, (int)(f0 / binHz)), i1 = Math.Min(FftSize / 2 - 1, Math.Max(i0, (int)(f1 / binHz)));
            double max = 0;
            for (var i = i0; i <= i1; i++) max = Math.Max(max, Math.Sqrt(re[i] * re[i] + im[i] * im[i]));
            var db = 20 * Math.Log10(max / (FftSize / 4.0) + 1e-9);
            var level = Math.Clamp((db + 66 + (Sensitivity - 1) * 7) / 60, 0, 1);
            Bars[b] = Math.Max(level, Bars[b] - 0.045);      // fast attack, steady fall
            Peaks[b] = Math.Max(Bars[b], Peaks[b] - 0.012);  // caps hang, then drop slowly
            sum += Bars[b];
        }
        Energy = sum / BarCount;
        Phase += 0.02 + Energy * 0.12;
        History.Insert(0, (double[])Bars.Clone());
        if (History.Count > HistoryRows) History.RemoveAt(History.Count - 1);
        Animate();
    }

    /// <summary>
    /// Beat detection (bass above its running average and above a floor, at most one beat per 170 ms), ripples, firework
    /// sparks and meter needles, advanced by one analysis step. The bands are the Windows app's, scaled to this bar count.
    /// </summary>
    void Animate()
    {
        int bassBars = Math.Max(1, BarCount * 8 / 56), lowBars = Math.Max(1, BarCount * 14 / 56), highFrom = BarCount * 30 / 56;
        double bass = 0;
        for (var b = 0; b < bassBars; b++) bass += Bars[b];
        bass /= bassBars;
        _bassAvg = _bassAvg * 0.94 + bass * 0.06;
        Beat *= 0.86;
        var now = NowMs();
        if (bass > 0.3 && bass > _bassAvg * 1.22 + 0.04 && now - _lastBeatMs > 170)
        {
            _lastBeatMs = now;
            Beat = 1;
            Beats++;
            Ripples.Add(new Ripple(0, bass, _rnd.NextDouble()));
            if (Ripples.Count > 8) Ripples.RemoveAt(0);
            SpawnBurst(bass);
        }
        for (var i = Ripples.Count - 1; i >= 0; i--)
        {
            var r = Ripples[i];
            var radius = r.Radius + 0.012 + Energy * 0.03;
            if (radius > 1) Ripples.RemoveAt(i); else Ripples[i] = r with { Radius = radius };
        }
        // sparks rise from the loudest bars between beats
        for (var b = 0; b < BarCount; b += 2)
            if (Bars[b] > 0.55 && _rnd.NextDouble() < 0.18 && Sparks.Count < 500)
                Sparks.Add(new Spark { X = (b + 0.5) / BarCount, Y = 1, Vx = (_rnd.NextDouble() - 0.5) * 0.004, Vy = -(0.008 + Bars[b] * 0.016), Life = 1, Hue = (double)b / BarCount, Size = 1.5 + Bars[b] * 2 });
        for (var i = Sparks.Count - 1; i >= 0; i--)
        {
            var k = Sparks[i];
            k.X += k.Vx; k.Y += k.Vy; k.Vy += 0.00045; k.Life -= 0.02;
            if (k.Life <= 0) Sparks.RemoveAt(i);
        }
        // analog meters: low and high ends of the spectrum, with a little needle inertia
        double low = 0, high = 0;
        for (var b = 0; b < lowBars; b++) low += Bars[b];
        for (var b = highFrom; b < BarCount; b++) high += Bars[b];
        low /= lowBars; high /= Math.Max(1, BarCount - highFrom);
        Needle[0] += (Math.Min(1, low * 1.15) - Needle[0]) * 0.35;
        Needle[1] += (Math.Min(1, high * 1.5) - Needle[1]) * 0.35;
    }

    void SpawnBurst(double level)
    {
        if (Sparks.Count > 420) return;
        var cx = 0.2 + _rnd.NextDouble() * 0.6;
        var cy = 0.15 + _rnd.NextDouble() * 0.4;
        var hue = _rnd.NextDouble();
        var n = 26 + (int)(level * 40);
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n + _rnd.NextDouble() * 0.2;
            var speed = 0.004 + _rnd.NextDouble() * 0.010 * (0.6 + level);
            Sparks.Add(new Spark { X = cx, Y = cy, Vx = Math.Cos(a) * speed, Vy = Math.Sin(a) * speed, Life = 1, Hue = (hue + _rnd.NextDouble() * 0.15) % 1, Size = 2 + level * 2 });
        }
    }

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
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
