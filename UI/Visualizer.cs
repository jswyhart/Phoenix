using System.Windows;
using System.Windows.Media;
using NAudio.Wave;

namespace Halcyon.UI;

#pragma warning disable CS0618 // system-wide loopback is what a visualizer wants; the replacement API is per-process

/// <summary>
/// Classic music visualizer with several styles and colour schemes. It listens to what the PC is playing
/// through a WASAPI loopback capture, so it follows any source.
/// </summary>
public class Visualizer : FrameworkElement
{
    /// <summary>Index 0 means "off" (the album art wall is shown instead).</summary>
    public static readonly string[] Styles =
    {
        "album art wall", "spectrum bars", "mirrored bars", "oscilloscope", "radial spectrum", "spectrum wave", "starburst", "waterfall",
        "dot matrix", "lissajous", "circular scope", "ripples", "fireworks", "tunnel", "analog meters"
    };
    public static readonly string[] Palettes = { "sunset", "rainbow", "ice", "emerald", "mono", "fire", "neon", "ocean", "violet", "gold", "album colours" };
    public static readonly string[] Sensitivities = { "low", "normal", "high", "max" };

    const int FftSize = 2048, BarCount = 56, HistoryRows = 48;
    // Windows hands captured audio over in bursts (several screen frames' worth at a time). Drawing straight from the
    // newest burst makes the picture jump at the burst rate however fast the screen is, so the samples go into a longer
    // buffer and a playhead moves through them at the speed of real time, a little behind the newest one.
    const int RingSize = 1 << 16;
    readonly float[] _ring = new float[RingSize];
    long _total, _totalAtBurst;
    double _burstAt, _burstEvery = 40, _head;
    bool _live;                 // samples are arriving from the sound card (not fed in by a test)
    TimeSpan _lastRendering;
    readonly object _lock = new();
    int _write;
    WasapiLoopbackCapture? _capture;
    bool _running;
    DateTime _lastFrame;
    double _phase;
    double _k = 1;          // how much of a 1/30 s step this frame is; every movement is scaled by it
    double _historyDue;     // the waterfall still adds a row 30 times a second
    readonly double[] _re = new double[FftSize], _im = new double[FftSize];
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _lastTick;
    int _frames;
    /// <summary>Frames drawn since the visualizer was created (for the self-test).</summary>
    public int Frames => _frames;

    /// <summary>Self-test only: ignore live audio so fed samples stay on screen.</summary>
    public bool Hold { get; set; }
    public int Mode { get; set; } = 1;
    public int Palette { get; set; }
    /// <summary>0 low, 1 normal, 2 high, 3 max: how strongly quiet music moves the display.</summary>
    public int Sensitivity { get; set; } = 1;
    /// <summary>The dominant colour of the current cover; used by the "album colours" scheme.</summary>
    public Color? CoverColor { get; set; }
    public int SampleRate { get; private set; } = 48000;
    public double[] Bars { get; } = new double[BarCount];
    readonly double[] _peaks = new double[BarCount];
    readonly List<double[]> _history = new();   // newest first, for the waterfall
    float[] _scope = new float[FftSize];
    double _energy;

    // beat tracking and the animated styles' state
    double _bassAvg, _beat;
    DateTime _lastBeat;
    readonly Random _rnd = new();
    readonly List<(double r, double level, double hue)> _ripples = new();
    sealed class Spark { public double X, Y, Vx, Vy, Life, Hue, Size; }
    readonly List<Spark> _sparks = new();
    readonly double[] _needle = new double[2];

    static readonly Brush CapBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---------- colour ----------

    static Color Hsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        var i = (int)h;
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static readonly Color[][] Ramps =
    {
        new[] { Color.FromRgb(0xD5, 0x35, 0x8F), Color.FromRgb(0xF0, 0x60, 0x3A), Color.FromRgb(0xFF, 0xD0, 0x70) },   // sunset
        Array.Empty<Color>(),                                                                                          // rainbow (per position)
        new[] { Color.FromRgb(0x1E, 0x4F, 0xD8), Color.FromRgb(0x35, 0xC6, 0xF0), Color.FromRgb(0xF0, 0xFB, 0xFF) },   // ice
        new[] { Color.FromRgb(0x0B, 0x6B, 0x3A), Color.FromRgb(0x2F, 0xD0, 0x6E), Color.FromRgb(0xD8, 0xFF, 0x9A) },   // emerald
        new[] { Color.FromRgb(0x70, 0x70, 0x70), Color.FromRgb(0xC8, 0xC8, 0xC8), Color.FromRgb(0xFF, 0xFF, 0xFF) },   // mono
        new[] { Color.FromRgb(0x9A, 0x10, 0x10), Color.FromRgb(0xFF, 0x6A, 0x00), Color.FromRgb(0xFF, 0xF0, 0x80) },   // fire
        new[] { Color.FromRgb(0x7A, 0x1F, 0xFF), Color.FromRgb(0x19, 0xF0, 0xFF), Color.FromRgb(0xB9, 0xFF, 0x4A) },   // neon
        new[] { Color.FromRgb(0x08, 0x2A, 0x6B), Color.FromRgb(0x10, 0x9C, 0xB8), Color.FromRgb(0xB8, 0xFF, 0xE8) },   // ocean
        new[] { Color.FromRgb(0x3A, 0x10, 0x7A), Color.FromRgb(0xA0, 0x40, 0xE0), Color.FromRgb(0xF4, 0xB8, 0xFF) },   // violet
        new[] { Color.FromRgb(0x6B, 0x4A, 0x08), Color.FromRgb(0xE0, 0xA8, 0x20), Color.FromRgb(0xFF, 0xF4, 0xB0) },   // gold
        Array.Empty<Color>()                                                                                           // album colours (from the cover)
    };

    /// <summary>Three-step ramp of the current colour scheme.</summary>
    Color[] Ramp()
    {
        if (Palette == 10)
        {
            var c = CoverColor ?? Color.FromRgb(0xE0, 0x44, 0x7C);
            return new[] { Mix(c, Colors.Black, 0.55), c, Mix(c, Colors.White, 0.6) };
        }
        return Ramps[Math.Clamp(Palette, 0, Ramps.Length - 1)];
    }

    /// <summary>The most colourful average colour of a picture, or null if it has none.</summary>
    public static async Task<Color?> DominantAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (await Art.LoadAsync(path, 24) is not System.Windows.Media.Imaging.BitmapSource src) return null;
            var bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                var max = Math.Max(R, Math.Max(G, B)); var min = Math.Min(R, Math.Min(G, B));
                var weight = 0.05 + (max - min) / 255.0 * Math.Min(1, max / 128.0);   // favour vivid, not-too-dark pixels
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            if (total <= 0) return null;
            var c = Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
            // keep it bright enough to show on a dark background
            var peak = Math.Max(c.R, Math.Max(c.G, c.B));
            if (peak < 140 && peak > 0) { var k = 170.0 / peak; c = Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k)); }
            return c;
        }
        catch { return null; }
    }

    /// <summary>Colour for a position across the display (0..1) at an intensity (0..1).</summary>
    Color ColorAt(double across, double level)
    {
        if (Palette == 1) return Hsv(across * 0.85 + _phase * 0.02, 0.85, 0.55 + 0.45 * level);
        var ramp = Ramp();
        return level < 0.5 ? Mix(ramp[0], ramp[1], level * 2) : Mix(ramp[1], ramp[2], level * 2 - 1);
    }

    /// <summary>Bottom-to-top gradient for one bar.</summary>
    Brush Gradient(double across) => Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(ColorAt(across, 0), 0), new(ColorAt(across, 0.5), 0.55), new(ColorAt(across, 1), 1) },
        new Point(0, 1), new Point(0, 0)));

    // ---------- capture ----------

    public void Start()
    {
        if (_running) return;
        _running = true;
        try
        {
            _capture = new WasapiLoopbackCapture();
            SampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch { _capture = null; }   // no audio device: the visualizer just stays flat
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        _live = false;
        var c = _capture;
        _capture = null;
        if (c != null)
        {
            c.DataAvailable -= OnData;
            try { c.StopRecording(); c.Dispose(); } catch { }
        }
        Array.Clear(Bars);
        Array.Clear(_peaks);
        _history.Clear();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = (sender as WasapiLoopbackCapture)?.WaveFormat;
        if (fmt == null || Hold) return;
        int channels = fmt.Channels, bytes = fmt.BitsPerSample / 8, frame = channels * bytes;
        if (frame == 0) return;
        var count = e.BytesRecorded / frame;
        var mono = new float[count];
        for (var i = 0; i < count; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = i * frame + c * bytes;
                sum += bytes == 4 ? BitConverter.ToSingle(e.Buffer, o) : bytes == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f : 0;
            }
            mono[i] = sum / channels;
        }
        _live = true;
        Feed(mono, fmt.SampleRate);
    }

    /// <summary>Adds mono samples to the analysis window.</summary>
    public void Feed(ReadOnlySpan<float> mono, int sampleRate)
    {
        lock (_lock)
        {
            SampleRate = sampleRate;
            foreach (var s in mono) { _ring[_write] = s; _write = (_write + 1) % RingSize; }
            _total += mono.Length;
            var now = _clock.Elapsed.TotalMilliseconds;
            var gap = now - _burstAt;
            if (gap is > 1 and < 400) _burstEvery = _burstEvery * 0.85 + gap * 0.15;
            _burstAt = now;
            _totalAtBurst = _total;
        }
    }

    void OnFrame(object? sender, EventArgs e)
    {
        // CompositionTarget.Rendering can fire more than once for the same screen frame: take exactly one step per frame,
        // so the visualizer runs at whatever rate the display refreshes at
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _lastRendering) return;
            _lastRendering = frame.RenderingTime;
        }
        var t = _clock.Elapsed.TotalMilliseconds;
        var dt = t - _lastTick;
        if (dt < 1) return;
        _lastTick = t;
        _lastFrame = DateTime.UtcNow;
        _k = Math.Clamp(dt / (1000.0 / 30), 0.2, 2.5);
        _frames++;
        if (!Hold) Update();
        InvalidateVisual();
    }

    // ---------- analysis ----------

    /// <summary>Recomputes bar heights from the most recent samples.</summary>
    public void Update()
    {
        var re = _re;
        var im = _im;
        Array.Clear(im);
        var scope = new float[FftSize];
        lock (_lock)
        {
            // where real time has got to in the audio: the last burst's end, plus the time since, less a margin of
            // a little more than one burst so the playhead never runs out of samples and has to wait
            double head = _total;
            if (!Hold && _live)
            {
                var behind = (_burstEvery * 1.25 + 12) / 1000 * SampleRate;
                head = _totalAtBurst + (_clock.Elapsed.TotalMilliseconds - _burstAt) / 1000 * SampleRate - behind;
                if (head > _total) head = _total;
                if (head < _head && _head - head < SampleRate * 0.25) head = _head;   // never step backwards over a hiccup
                if (_total - head > SampleRate * 0.5) head = _total - behind;         // far behind (audio was paused): catch up
            }
            _head = head;
            var end = (long)head;
            var start = end - FftSize;
            for (var i = 0; i < FftSize; i++)
            {
                var at = start + i;
                var s = at < 0 || _total - at > RingSize ? 0f : _ring[(int)(((at % RingSize) + RingSize) % RingSize)];
                scope[i] = s;
                re[i] = s * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // Hann window
            }
        }
        _scope = scope;
        Fft(re, im);

        // Bars are spaced logarithmically from 40 Hz to 16 kHz, like a graphic equalizer.
        const double lo = 40, hi = 16000;
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
            Bars[b] = Math.Max(level, Bars[b] - 0.045 * _k);      // fast attack, steady fall
            _peaks[b] = Math.Max(Bars[b], _peaks[b] - 0.012 * _k); // caps hang, then drop slowly
            sum += Bars[b];
        }
        _energy = sum / BarCount;
        _phase += (0.02 + _energy * 0.12) * _k;
        _historyDue += _k;
        if (_historyDue >= 1 || _history.Count == 0)
        {
            _historyDue = Math.Min(_historyDue - 1, 1);
            _history.Insert(0, (double[])Bars.Clone());
            if (_history.Count > HistoryRows) _history.RemoveAt(_history.Count - 1);
        }
        Animate();
    }

    /// <summary>Advances beat detection, ripples, sparks and meter needles by one analysis step.</summary>
    void Animate()
    {
        double bass = 0;
        for (var b = 0; b < 8; b++) bass += Bars[b];
        bass /= 8;
        var keep = Math.Pow(0.94, _k);
        _bassAvg = _bassAvg * keep + bass * (1 - keep);
        _beat *= Math.Pow(0.86, _k);
        var now = DateTime.UtcNow;
        var isBeat = bass > 0.3 && bass > _bassAvg * 1.22 + 0.04 && (now - _lastBeat).TotalMilliseconds > 170;
        if (isBeat)
        {
            _lastBeat = now;
            _beat = 1;
            _ripples.Add((0, bass, _rnd.NextDouble()));
            if (_ripples.Count > 8) _ripples.RemoveAt(0);
            SpawnBurst(bass);
        }
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var (r, level, hue) = _ripples[i];
            r += (0.012 + _energy * 0.03) * _k;
            if (r > 1) _ripples.RemoveAt(i); else _ripples[i] = (r, level, hue);
        }
        // sparks rise from the loudest bars between beats
        for (var b = 0; b < BarCount; b += 2)
            if (Bars[b] > 0.55 && _rnd.NextDouble() < 0.18 * _k && _sparks.Count < 500)
                _sparks.Add(new Spark { X = (b + 0.5) / BarCount, Y = 1, Vx = (_rnd.NextDouble() - 0.5) * 0.004, Vy = -(0.008 + Bars[b] * 0.016), Life = 1, Hue = (double)b / BarCount, Size = 1.5 + Bars[b] * 2 });
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var k = _sparks[i];
            k.X += k.Vx * _k; k.Y += k.Vy * _k; k.Vy += 0.00045 * _k; k.Life -= 0.02 * _k;
            if (k.Life <= 0) _sparks.RemoveAt(i);
        }
        // analog meters: low and high ends of the spectrum, with a little needle inertia
        double low = 0, high = 0;
        for (var b = 0; b < 14; b++) low += Bars[b];
        for (var b = 30; b < BarCount; b++) high += Bars[b];
        low /= 14; high /= BarCount - 30;
        var pull = 1 - Math.Pow(0.65, _k);
        _needle[0] += (Math.Min(1, low * 1.15) - _needle[0]) * pull;
        _needle[1] += (Math.Min(1, high * 1.5) - _needle[1]) * pull;
    }

    void SpawnBurst(double level)
    {
        if (_sparks.Count > 420) return;
        var cx = 0.2 + _rnd.NextDouble() * 0.6;
        var cy = 0.15 + _rnd.NextDouble() * 0.4;
        var hue = _rnd.NextDouble();
        var n = 26 + (int)(level * 40);
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n + _rnd.NextDouble() * 0.2;
            var speed = 0.004 + _rnd.NextDouble() * 0.010 * (0.6 + level);
            _sparks.Add(new Spark { X = cx, Y = cy, Vx = Math.Cos(a) * speed, Vy = Math.Sin(a) * speed, Life = 1, Hue = (hue + _rnd.NextDouble() * 0.15) % 1, Size = 2 + level * 2 });
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

    // ---------- drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        switch (Mode)
        {
            case 2: DrawBars(dc, w, h, mirrored: true); break;
            case 3: DrawScope(dc, w, h); break;
            case 4: DrawRadial(dc, w, h, starburst: false); break;
            case 5: DrawWave(dc, w, h); break;
            case 6: DrawRadial(dc, w, h, starburst: true); break;
            case 7: DrawWaterfall(dc, w, h); break;
            case 8: DrawDots(dc, w, h); break;
            case 9: DrawLissajous(dc, w, h); break;
            case 10: DrawCircularScope(dc, w, h); break;
            case 11: DrawRipples(dc, w, h); break;
            case 12: DrawFireworks(dc, w, h); break;
            case 13: DrawTunnel(dc, w, h); break;
            case 14: DrawMeters(dc, w, h); break;
            default: DrawBars(dc, w, h, mirrored: false); break;
        }
    }

    void DrawBars(DrawingContext dc, double w, double h, bool mirrored)
    {
        var slot = w / BarCount;
        var barW = Math.Max(2, slot - 4);
        var area = mirrored ? h * 0.42 : h * 0.72;
        var baseY = mirrored ? h * 0.5 : h;
        const double seg = 7;                     // LED-style segments
        Brush? shared = Palette == 1 ? null : Gradient(0);
        for (var b = 0; b < BarCount; b++)
        {
            var x = b * slot + (slot - barW) / 2;
            var barH = Math.Floor(Bars[b] * area / seg) * seg;
            var brush = shared ?? Gradient((double)b / BarCount);
            if (barH > 0)
            {
                dc.PushClip(new RectangleGeometry(new Rect(x, baseY - barH, barW, barH)));
                dc.DrawRectangle(brush, null, new Rect(x, baseY - area, barW, area));
                dc.Pop();
                if (mirrored)
                {
                    // the reflection below the centre line, dimmer
                    dc.PushOpacity(0.45);
                    dc.PushClip(new RectangleGeometry(new Rect(x, baseY, barW, barH)));
                    dc.PushTransform(new ScaleTransform(1, -1, 0, baseY));
                    dc.DrawRectangle(brush, null, new Rect(x, baseY - area, barW, area));
                    dc.Pop(); dc.Pop(); dc.Pop();
                }
            }
            dc.DrawRectangle(CapBrush, null, new Rect(x, baseY - Math.Max(3, _peaks[b] * area) - 3, barW, 3));
        }
    }

    void DrawScope(DrawingContext dc, double w, double h)
    {
        const int points = 512;
        var mid = h * 0.5;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            for (var i = 0; i < points; i++)
            {
                var s = _scope[FftSize - points * 2 + i * 2];
                var p = new Point(w * i / (points - 1), mid - Math.Clamp(s * 2.2, -1, 1) * h * 0.4);
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        geo.Freeze();
        var stroke = Palette == 1
            ? Frozen(new LinearGradientBrush(new GradientStopCollection(Enumerable.Range(0, 7).Select(i => new GradientStop(ColorAt(i / 6.0, 1), i / 6.0))), 0))
            : (Brush)Frozen(new SolidColorBrush(ColorAt(0.5, 0.75)));
        dc.DrawGeometry(null, Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 7)), geo);   // soft glow
        dc.DrawGeometry(null, Frozen(new Pen(stroke, 2.2)), geo);
    }

    void DrawRadial(DrawingContext dc, double w, double h, bool starburst)
    {
        var c = new Point(w / 2, h / 2);
        var size = Math.Min(w, h);
        var inner = starburst ? size * 0.03 : size * (0.15 + _energy * 0.05);
        var reach = size * (starburst ? 0.46 : 0.30);
        var spokes = BarCount * 2;               // the spectrum runs up one side and back down the other
        var spin = starburst ? _phase * 0.15 : -Math.PI / 2;
        for (var i = 0; i < spokes; i++)
        {
            var b = i < BarCount ? i : spokes - 1 - i;
            var level = Bars[b];
            if (level <= 0.01) continue;
            var a = spin + 2 * Math.PI * i / spokes;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            var len = level * reach;
            var pen = Frozen(new Pen(Frozen(new SolidColorBrush(ColorAt((double)b / BarCount, level))), starburst ? 2 + level * 5 : Math.Max(2, size * 0.012)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            dc.DrawLine(pen, new Point(c.X + cos * inner, c.Y + sin * inner), new Point(c.X + cos * (inner + len), c.Y + sin * (inner + len)));
            if (!starburst)
            {
                var cap = inner + _peaks[b] * reach + 5;
                dc.DrawEllipse(CapBrush, null, new Point(c.X + cos * cap, c.Y + sin * cap), 1.6, 1.6);
            }
        }
        if (!starburst) dc.DrawEllipse(null, Frozen(new Pen(Frozen(new SolidColorBrush(ColorAt(0.5, 0.6))), 2)), c, inner - 4, inner - 4);
    }

    void DrawWave(DrawingContext dc, double w, double h)
    {
        // Two filled, smoothed spectrum curves: the live levels in front and the slower peak envelope behind.
        StreamGeometry Curve(double[] levels, double scale)
        {
            var geo = new StreamGeometry();
            var pts = new List<Point> { new(0, h - levels[0] * scale) };
            for (var b = 0; b < BarCount; b++) pts.Add(new Point(w * (b + 0.5) / BarCount, h - levels[b] * scale));
            pts.Add(new Point(w, h - levels[^1] * scale));
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                g.LineTo(pts[0], true, false);
                g.PolyBezierTo(Smooth(pts), true, true);
                g.LineTo(new Point(w, h), true, false);
            }
            geo.Freeze();
            return geo;
        }
        var fill = Palette == 1
            ? Frozen(new LinearGradientBrush(new GradientStopCollection(Enumerable.Range(0, 7).Select(i => new GradientStop(ColorAt(i / 6.0, 0.9), i / 6.0))), 0))
            : Gradient(0);
        dc.PushOpacity(0.28);
        dc.DrawGeometry(fill, null, Curve(_peaks, h * 0.74));
        dc.Pop();
        dc.PushOpacity(0.85);
        dc.DrawGeometry(fill, null, Curve(Bars, h * 0.70));
        dc.Pop();
    }

    /// <summary>Control points for a smooth curve through the given points (Catmull-Rom as cubic Béziers).</summary>
    static List<Point> Smooth(List<Point> p)
    {
        var result = new List<Point>();
        for (var i = 0; i < p.Count - 1; i++)
        {
            Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Count - 1, i + 2)];
            result.Add(new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6));
            result.Add(new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6));
            result.Add(p2);
        }
        return result;
    }

    void DrawWaterfall(DrawingContext dc, double w, double h)
    {
        // A scrolling spectrogram: each row is a moment in time, newest at the bottom, receding upward.
        if (_history.Count == 0) return;
        var rows = HistoryRows;
        var area = h * 0.86;
        var rowH = area / rows;
        var brushes = new Dictionary<int, Brush>();
        for (var r = 0; r < _history.Count; r++)
        {
            var levels = _history[r];
            var depth = 1 - (double)r / rows;                    // 1 = newest
            var inset = (1 - depth) * w * 0.18;                  // older rows narrow slightly, like perspective
            var slot = (w - inset * 2) / BarCount;
            var y = h - (r + 1) * rowH;
            for (var b = 0; b < BarCount; b++)
            {
                var level = levels[b];
                if (level < 0.06) continue;
                var c = ColorAt((double)b / BarCount, level);
                var key = (c.R << 16 | c.G << 8 | c.B) & 0xF8F8F8 | (int)(depth * 7) << 24;
                if (!brushes.TryGetValue(key, out var brush))
                    brushes[key] = brush = Frozen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, level * 1.2) * (0.25 + 0.75 * depth)), c.R, c.G, c.B)));
                dc.DrawRectangle(brush, null, new Rect(inset + b * slot, y, Math.Max(1, slot - 1), Math.Max(1, rowH - 1)));
            }
        }
    }

    // ---------- more styles ----------

    static Pen PenOf(Color c, double width, byte alpha = 255) =>
        Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B))), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    static Brush BrushOf(Color c, byte alpha = 255) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));

    void DrawDots(DrawingContext dc, double w, double h)
    {
        // a dot-matrix equalizer: each column is a stack of round lamps
        const int rows = 18;
        var slot = w / BarCount;
        var cell = Math.Min(slot, h * 0.78 / rows);
        var r = Math.Max(1.5, cell * 0.36);
        var top = h - rows * cell - h * 0.06;
        for (var b = 0; b < BarCount; b++)
        {
            var x = b * slot + slot / 2;
            var lit = (int)Math.Round(Bars[b] * rows);
            var capRow = (int)Math.Round(_peaks[b] * rows);
            for (var row = 0; row < rows; row++)
            {
                var y = top + (rows - 1 - row) * cell + cell / 2;
                if (row < lit) dc.DrawEllipse(BrushOf(ColorAt((double)b / BarCount, (double)row / rows)), null, new Point(x, y), r, r);
                else if (row == capRow - 1 && capRow > 0) dc.DrawEllipse(CapBrush, null, new Point(x, y), r, r);
                else dc.DrawEllipse(BrushOf(ColorAt(0.5, 0.3), 0x16), null, new Point(x, y), r * 0.8, r * 0.8);
            }
        }
    }

    void DrawLissajous(DrawingContext dc, double w, double h)
    {
        // each sample against the one a moment later: a steady tone draws a clean figure, music a restless knot
        const int points = 700, lag = 30;
        var baseIdx = FftSize - points - lag;
        float peak = 0.05f;
        for (var i = 0; i < points; i++) peak = Math.Max(peak, Math.Abs(_scope[baseIdx + i]));
        var scale = Math.Min(w, h) * 0.42 / Math.Min(peak, 1f);
        var c = new Point(w / 2, h / 2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            for (var i = 0; i < points; i++)
            {
                double x = _scope[baseIdx + i], y = _scope[baseIdx + i + lag];
                var p = new Point(c.X + (x - y) * 0.7071 * scale * 1.5, c.Y - (x + y) * 0.7071 * scale);
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        geo.Freeze();
        dc.DrawGeometry(null, PenOf(ColorAt(0.5, 0.6), 8, 0x30), geo);
        dc.DrawGeometry(null, PenOf(ColorAt(0.25 + _energy, 1), 2), geo);
    }

    void DrawCircularScope(DrawingContext dc, double w, double h)
    {
        var size = Math.Min(w, h);
        var c = new Point(w / 2, h / 2);
        var radius = size * (0.22 + _energy * 0.05 + _beat * 0.015);
        const int points = 360;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            for (var i = 0; i <= points; i++)
            {
                var s = _scope[FftSize - 2 * points + (i % points) * 2];
                var a = 2 * Math.PI * i / points - Math.PI / 2;
                var r = radius + Math.Clamp(s * 2.4, -1, 1) * size * 0.16;
                var p = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
                if (i == 0) g.BeginFigure(p, true, true); else g.LineTo(p, true, true);
            }
        geo.Freeze();
        dc.DrawGeometry(BrushOf(ColorAt(0.5, 0.4), 0x18), null, geo);
        dc.DrawGeometry(null, PenOf(ColorAt(0.5, 0.7), 9, 0x30), geo);
        dc.DrawGeometry(null, PenOf(ColorAt(0.35 + _phase * 0.01 % 0.5, 1), 2.2), geo);
        dc.DrawEllipse(null, PenOf(ColorAt(0.8, 0.5), 1.2, 0x80), c, radius * 0.55, radius * 0.55);
    }

    void DrawRipples(DrawingContext dc, double w, double h)
    {
        var c = new Point(w / 2, h / 2);
        var reach = Math.Sqrt(w * w + h * h) / 2;
        foreach (var (r, level, hue) in _ripples)
        {
            var fade = 1 - r;
            dc.DrawEllipse(null, PenOf(ColorAt(hue, 0.4 + level * 0.6), 2 + level * 8 * fade, (byte)(220 * fade * fade)), c, r * reach, r * reach);
        }
        // the heart of it swells with the bass
        var core = Math.Min(w, h) * (0.05 + _energy * 0.12 + _beat * 0.05);
        dc.DrawEllipse(BrushOf(ColorAt(0.5, 0.8), 0x40), null, c, core * 1.7, core * 1.7);
        dc.DrawEllipse(BrushOf(ColorAt(0.5, 1), 0xC0), null, c, core, core);
    }

    void DrawFireworks(DrawingContext dc, double w, double h)
    {
        foreach (var k in _sparks)
        {
            var life = Math.Clamp(k.Life, 0, 1);
            var p = new Point(k.X * w, k.Y * h);
            dc.DrawEllipse(BrushOf(ColorAt(k.Hue, 0.4 + life * 0.6), (byte)(255 * life * 0.35)), null, p, k.Size * 2.6 * life + 1, k.Size * 2.6 * life + 1);
            dc.DrawEllipse(BrushOf(ColorAt(k.Hue, 1), (byte)(255 * life)), null, p, k.Size * life + 0.5, k.Size * life + 0.5);
        }
        // a low glow along the bottom shows the spectrum
        var slot = w / BarCount;
        for (var b = 0; b < BarCount; b++)
            dc.DrawRectangle(BrushOf(ColorAt((double)b / BarCount, Bars[b]), 0x90), null, new Rect(b * slot + 1, h - 3 - Bars[b] * h * 0.10, Math.Max(1, slot - 2), 3 + Bars[b] * h * 0.10));
    }

    void DrawTunnel(DrawingContext dc, double w, double h)
    {
        // rings rush towards the viewer, faster when the music is loud, wobbling with the spectrum
        const int rings = 16, sides = 72;
        var c = new Point(w / 2, h / 2);
        var reach = Math.Sqrt(w * w + h * h) / 2 * 1.05;
        var drift = _phase * 0.7 % 1;
        for (var k = rings - 1; k >= 0; k--)
        {
            var t = (k + drift) / rings;                 // 0 far, 1 near
            var r = Math.Pow(t, 2.3) * reach;
            var spin = _phase * 0.12 * (k % 2 == 0 ? 1 : -1);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
                for (var i = 0; i <= sides; i++)
                {
                    var a = 2 * Math.PI * i / sides + spin;
                    var rr = r * (1 + Bars[(i % sides) * BarCount / sides] * 0.12 * t + _beat * 0.03);
                    var p = new Point(c.X + Math.Cos(a) * rr, c.Y + Math.Sin(a) * rr * 0.85);
                    if (i == 0) g.BeginFigure(p, false, true); else g.LineTo(p, true, true);
                }
            geo.Freeze();
            dc.DrawGeometry(null, PenOf(ColorAt(t, 0.3 + t * 0.7), 1 + t * 5 + _energy * 3 * t, (byte)(40 + 215 * t)), geo);
        }
    }

    void DrawMeters(DrawingContext dc, double w, double h)
    {
        // two analog dials: bass on the left, treble on the right
        var radius = Math.Min(w * 0.22, h * 0.36);
        var labels = new[] { "LOW", "HIGH" };
        for (var m = 0; m < 2; m++)
        {
            var c = new Point(w * (m == 0 ? 0.27 : 0.73), h * 0.62);
            const double from = -150, to = -30;
            Point At(double deg, double r) { var a = deg * Math.PI / 180; return new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r); }
            // dial face
            var face = new StreamGeometry();
            using (var g = face.Open())
            {
                g.BeginFigure(c, true, true);
                g.LineTo(At(from, radius * 1.12), true, false);
                g.ArcTo(At(to, radius * 1.12), new Size(radius * 1.12, radius * 1.12), 0, false, SweepDirection.Clockwise, true, false);
            }
            face.Freeze();
            dc.DrawGeometry(BrushOf(Colors.White, 0x0C), PenOf(Colors.White, 1.5, 0x40), face);
            for (var t = 0; t <= 20; t++)
            {
                var frac = t / 20.0;
                var major = t % 5 == 0;
                var colour = frac > 0.8 ? ColorAt(0.9, 1) : ColorAt(0.2, 0.6);
                dc.DrawLine(PenOf(colour, major ? 2.4 : 1.2, major ? (byte)255 : (byte)150), At(from + (to - from) * frac, radius * (major ? 0.82 : 0.9)), At(from + (to - from) * frac, radius));
            }
            var angle = from + (to - from) * Math.Clamp(_needle[m], 0, 1);
            dc.DrawLine(PenOf(Colors.Black, 6, 0x50), At(angle, 0), At(angle, radius * 0.95));
            dc.DrawLine(PenOf(ColorAt(0.6, 1), 3), At(angle, 0), At(angle, radius * 0.95));
            dc.DrawEllipse(BrushOf(ColorAt(0.6, 1)), null, c, radius * 0.06, radius * 0.06);
            var label = new FormattedText(labels[m], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
                Math.Max(11, radius * 0.1), BrushOf(Colors.White, 0xB0), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(c.X - label.Width / 2, c.Y + radius * 0.14));
        }
    }
}
