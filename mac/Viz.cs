using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Halcyon.Core;

namespace Halcyon.Desktop;

/// <summary>Classic music visualizer: several styles and colour schemes drawn from the engine's spectrum analysis.</summary>
public class Viz : Control
{
    /// <summary>Index 0 means "off" (the album art wall is shown instead).</summary>
    public static readonly string[] Styles =
    {
        "album art wall", "spectrum bars", "mirrored bars", "oscilloscope", "radial spectrum", "spectrum wave", "starburst", "waterfall",
        "dot matrix", "lissajous", "circular scope", "ripples", "fireworks", "tunnel", "analog meters"
    };
    public static readonly string[] Palettes = { "sunset", "rainbow", "ice", "emerald", "mono", "fire", "neon", "ocean", "violet", "gold", "album colours" };
    public static readonly string[] Sensitivities = { "low", "normal", "high", "max" };
    const int HistoryRows = 48;

    readonly DispatcherTimer _timer;
    readonly List<double[]> _history = new();
    double _phase, _energy;

    // the engine's spectrum scaled by the sensitivity setting, and the animated styles' state
    double[] Bars = new double[Spectrum.BarCount], Peaks = new double[Spectrum.BarCount];
    double _bassAvg, _beat;
    DateTime _lastBeat;
    readonly Random _rnd = new();
    readonly List<(double r, double level, double hue)> _ripples = new();
    sealed class Spark { public double X, Y, Vx, Vy, Life, Hue, Size; }
    readonly List<Spark> _sparks = new();
    readonly double[] _needle = new double[2];

    public int Mode { get; set; } = 1;
    public int Palette { get; set; }
    /// <summary>0 low, 1 normal, 2 high, 3 max: how strongly quiet music moves the display.</summary>
    public int Sensitivity { get; set; } = 1;
    /// <summary>The dominant colour of the current cover; used by the "album colours" scheme.</summary>
    public Color? CoverColor { get; set; }
    /// <summary>Self-test only: draw what was fed in without advancing the analysis.</summary>
    public bool Hold { get; set; }
    public Spectrum Spectrum => HalcyonApp.Engine.Spectrum;

    public Viz()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => { if (!Hold) Step(); InvalidateVisual(); });
        _timer.Stop();   // a timer made with a callback starts by itself; this one must only run while the visualizer is on screen
    }

    public void Start() { HalcyonApp.Engine.AnalysisEnabled = true; _timer.Start(); }

    public void Stop()
    {
        _timer.Stop();
        HalcyonApp.Engine.AnalysisEnabled = false;
        _history.Clear();
    }

    /// <summary>Advances the analysis by one frame.</summary>
    public void Step()
    {
        Spectrum.Sensitivity = Sensitivity;
        Spectrum.Update();
        Bars = (double[])Spectrum.Bars.Clone();
        Peaks = (double[])Spectrum.Peaks.Clone();
        _energy = Bars.Average();
        _phase += 0.02 + _energy * 0.12;
        _history.Insert(0, (double[])Spectrum.Bars.Clone());
        if (_history.Count > HistoryRows) _history.RemoveAt(_history.Count - 1);
        Animate();
    }

    /// <summary>Advances beat detection, ripples, sparks and meter needles by one analysis step.</summary>
    void Animate()
    {
        var n = Bars.Length;
        double bass = 0;
        for (var b = 0; b < 6; b++) bass += Bars[b];
        bass /= 6;
        _bassAvg = _bassAvg * 0.94 + bass * 0.06;
        _beat *= 0.86;
        var now = DateTime.UtcNow;
        if (bass > 0.3 && bass > _bassAvg * 1.22 + 0.04 && (now - _lastBeat).TotalMilliseconds > 170)
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
            r += 0.012 + _energy * 0.03;
            if (r > 1) _ripples.RemoveAt(i); else _ripples[i] = (r, level, hue);
        }
        for (var b = 0; b < n; b += 2)
            if (Bars[b] > 0.55 && _rnd.NextDouble() < 0.18 && _sparks.Count < 500)
                _sparks.Add(new Spark { X = (b + 0.5) / n, Y = 1, Vx = (_rnd.NextDouble() - 0.5) * 0.004, Vy = -(0.008 + Bars[b] * 0.016), Life = 1, Hue = (double)b / n, Size = 1.5 + Bars[b] * 2 });
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var k = _sparks[i];
            k.X += k.Vx; k.Y += k.Vy; k.Vy += 0.00045; k.Life -= 0.02;
            if (k.Life <= 0) _sparks.RemoveAt(i);
        }
        double low = 0, high = 0;
        var lowEnd = Math.Max(1, n / 4);
        var highStart = n * 5 / 8;
        for (var b = 0; b < lowEnd; b++) low += Bars[b];
        for (var b = highStart; b < n; b++) high += Bars[b];
        low /= lowEnd; high /= n - highStart;
        _needle[0] += (Math.Min(1, low * 1.15) - _needle[0]) * 0.35;
        _needle[1] += (Math.Min(1, high * 1.5) - _needle[1]) * 0.35;
    }

    void SpawnBurst(double level)
    {
        if (_sparks.Count > 420) return;
        var cx = 0.2 + _rnd.NextDouble() * 0.6;
        var cy = 0.15 + _rnd.NextDouble() * 0.4;
        var hue = _rnd.NextDouble();
        var count = 26 + (int)(level * 40);
        for (var i = 0; i < count; i++)
        {
            var a = 2 * Math.PI * i / count + _rnd.NextDouble() * 0.2;
            var speed = 0.004 + _rnd.NextDouble() * 0.010 * (0.6 + level);
            _sparks.Add(new Spark { X = cx, Y = cy, Vx = Math.Cos(a) * speed, Vy = Math.Sin(a) * speed, Life = 1, Hue = (hue + _rnd.NextDouble() * 0.15) % 1, Size = 2 + level * 2 });
        }
    }

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
            if (await Art.LoadAsync(path, 24) is not { } bmp) return null;
            int w = (int)bmp.PixelSize.Width, h = (int)bmp.PixelSize.Height, stride = w * 4;
            var px = new byte[stride * h];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, stride); }
            finally { handle.Free(); }
            var bgra = bmp.Format != Avalonia.Platform.PixelFormat.Rgba8888;
            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                double R = bgra ? px[i + 2] : px[i], G = px[i + 1], B = bgra ? px[i] : px[i + 2];
                var max = Math.Max(R, Math.Max(G, B)); var min = Math.Min(R, Math.Min(G, B));
                var weight = 0.05 + (max - min) / 255.0 * Math.Min(1, max / 128.0);   // favour vivid, not-too-dark pixels
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            if (total <= 0) return null;
            var c = Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
            var peak = Math.Max(c.R, Math.Max(c.G, c.B));
            if (peak < 140 && peak > 0) { var k = 170.0 / peak; c = Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k)); }
            return c;
        }
        catch { return null; }
    }

    Color ColorAt(double across, double level)
    {
        if (Palette == 1) return Hsv(across * 0.85 + _phase * 0.02, 0.85, 0.55 + 0.45 * level);
        var ramp = Ramp();
        return level < 0.5 ? Mix(ramp[0], ramp[1], level * 2) : Mix(ramp[1], ramp[2], level * 2 - 1);
    }

    /// <summary>Bottom-to-top gradient for one bar.</summary>
    IBrush Gradient(double across) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(ColorAt(across, 0), 0), new GradientStop(ColorAt(across, 0.5), 0.55), new GradientStop(ColorAt(across, 1), 1) }
    };

    IBrush Across(double level) => Palette != 1 ? new SolidColorBrush(ColorAt(0.5, level)) : new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops().With(Enumerable.Range(0, 7).Select(i => new GradientStop(ColorAt(i / 6.0, level), i / 6.0)))
    };

    static readonly IBrush Cap = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));

    // ---------- drawing ----------

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10) return;
        switch (Mode)
        {
            case 2: DrawBars(dc, w, h, true); break;
            case 3: DrawScope(dc, w, h); break;
            case 4: DrawRadial(dc, w, h, false); break;
            case 5: DrawWave(dc, w, h); break;
            case 6: DrawRadial(dc, w, h, true); break;
            case 7: DrawWaterfall(dc, w, h); break;
            case 8: DrawDots(dc, w, h); break;
            case 9: DrawLissajous(dc, w, h); break;
            case 10: DrawCircularScope(dc, w, h); break;
            case 11: DrawRipples(dc, w, h); break;
            case 12: DrawFireworks(dc, w, h); break;
            case 13: DrawTunnel(dc, w, h); break;
            case 14: DrawMeters(dc, w, h); break;
            default: DrawBars(dc, w, h, false); break;
        }
    }

    void DrawBars(DrawingContext dc, double w, double h, bool mirrored)
    {
        double[] bars = Bars, peaks = Peaks;
        var n = bars.Length;
        var slot = w / n;
        var barW = Math.Max(2, slot - 4);
        var area = mirrored ? h * 0.42 : h * 0.72;
        var baseY = mirrored ? h * 0.5 : h;
        const double seg = 7;   // LED-style segments
        var shared = Palette == 1 ? null : Gradient(0);
        for (var b = 0; b < n; b++)
        {
            var x = b * slot + (slot - barW) / 2;
            var barH = Math.Floor(bars[b] * area / seg) * seg;
            var brush = shared ?? Gradient((double)b / n);
            if (barH > 0)
            {
                using (dc.PushClip(new Rect(x, baseY - barH, barW, barH)))
                    dc.FillRectangle(brush, new Rect(x, baseY - area, barW, area));
                if (mirrored)
                    using (dc.PushOpacity(0.45))
                    using (dc.PushClip(new Rect(x, baseY, barW, barH)))
                    using (dc.PushTransform(Matrix.CreateTranslation(0, -baseY) * Matrix.CreateScale(1, -1) * Matrix.CreateTranslation(0, baseY)))
                        dc.FillRectangle(brush, new Rect(x, baseY - area, barW, area));
            }
            dc.FillRectangle(Cap, new Rect(x, baseY - Math.Max(3, peaks[b] * area) - 3, barW, 3));
        }
    }

    void DrawScope(DrawingContext dc, double w, double h)
    {
        var scope = Spectrum.Scope;
        var points = Math.Min(512, scope.Length);
        var mid = h * 0.5;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            for (var i = 0; i < points; i++)
            {
                var p = new Point(w * i / (points - 1), mid - Math.Clamp(scope[scope.Length - points + i] * 2.2, -1, 1) * h * 0.4);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 7), geo);   // soft glow
        dc.DrawGeometry(null, new Pen(Across(0.75), 2.2), geo);
    }

    void DrawRadial(DrawingContext dc, double w, double h, bool starburst)
    {
        double[] bars = Bars, peaks = Peaks;
        var n = bars.Length;
        var c = new Point(w / 2, h / 2);
        var size = Math.Min(w, h);
        var inner = starburst ? size * 0.03 : size * (0.15 + _energy * 0.05);
        var reach = size * (starburst ? 0.46 : 0.30);
        var spokes = n * 2;   // the spectrum runs up one side and back down the other
        var spin = starburst ? _phase * 0.15 : -Math.PI / 2;
        for (var i = 0; i < spokes; i++)
        {
            var b = i < n ? i : spokes - 1 - i;
            var level = bars[b];
            if (level <= 0.01) continue;
            var a = spin + 2 * Math.PI * i / spokes;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            var pen = new Pen(new SolidColorBrush(ColorAt((double)b / n, level)), starburst ? 2 + level * 5 : Math.Max(2, size * 0.014), lineCap: PenLineCap.Round);
            dc.DrawLine(pen, new Point(c.X + cos * inner, c.Y + sin * inner), new Point(c.X + cos * (inner + level * reach), c.Y + sin * (inner + level * reach)));
            if (!starburst)
            {
                var cap = inner + peaks[b] * reach + 5;
                dc.DrawEllipse(Cap, null, new Point(c.X + cos * cap, c.Y + sin * cap), 1.6, 1.6);
            }
        }
        if (!starburst) dc.DrawEllipse(null, new Pen(new SolidColorBrush(ColorAt(0.5, 0.6)), 2), c, inner - 4, inner - 4);
    }

    void DrawWave(DrawingContext dc, double w, double h)
    {
        // Two filled, smoothed spectrum curves: the live levels in front and the slower peak envelope behind.
        StreamGeometry Curve(double[] levels, double scale)
        {
            var n = levels.Length;
            var pts = new List<Point> { new(0, h - levels[0] * scale) };
            for (var b = 0; b < n; b++) pts.Add(new Point(w * (b + 0.5) / n, h - levels[b] * scale));
            pts.Add(new Point(w, h - levels[^1] * scale));
            var geo = new StreamGeometry();
            using var g = geo.Open();
            g.BeginFigure(new Point(0, h), true);
            g.LineTo(pts[0]);
            for (var i = 0; i < pts.Count - 1; i++)
            {
                // Catmull-Rom through the points, as cubic Béziers
                Point p0 = pts[Math.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                g.CubicBezierTo(new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6), new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6), p2);
            }
            g.LineTo(new Point(w, h));
            g.EndFigure(true);
            return geo;
        }
        var fill = Palette == 1 ? Across(0.9) : Gradient(0);
        using (dc.PushOpacity(0.28)) dc.DrawGeometry(fill, null, Curve(Spectrum.Peaks, h * 0.74));
        using (dc.PushOpacity(0.85)) dc.DrawGeometry(fill, null, Curve(Spectrum.Bars, h * 0.70));
    }

    void DrawWaterfall(DrawingContext dc, double w, double h)
    {
        // A scrolling spectrogram: each row is a moment in time, newest at the bottom, receding upward.
        if (_history.Count == 0) return;
        var area = h * 0.86;
        var rowH = area / HistoryRows;
        var brushes = new Dictionary<uint, IBrush>();
        for (var r = 0; r < _history.Count; r++)
        {
            var levels = _history[r];
            var n = levels.Length;
            var depth = 1 - (double)r / HistoryRows;   // 1 = newest
            var inset = (1 - depth) * w * 0.18;        // older rows narrow slightly, like perspective
            var slot = (w - inset * 2) / n;
            var y = h - (r + 1) * rowH;
            for (var b = 0; b < n; b++)
            {
                var level = levels[b];
                if (level < 0.06) continue;
                var c = ColorAt((double)b / n, level);
                var color = Color.FromArgb((byte)(255 * Math.Min(1, level * 1.2) * (0.25 + 0.75 * depth)), c.R, c.G, c.B);
                var key = color.ToUInt32() & 0xF8F8F8F8;
                if (!brushes.TryGetValue(key, out var brush)) brushes[key] = brush = new SolidColorBrush(color);
                dc.FillRectangle(brush, new Rect(inset + b * slot, y, Math.Max(1, slot - 1), Math.Max(1, rowH - 1)));
            }
        }
    }

    // ---------- more styles ----------

    static Pen PenOf(Color c, double width, byte alpha = 255) =>
        new(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    static IBrush BrushOf(Color c, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    void DrawDots(DrawingContext dc, double w, double h)
    {
        // a dot-matrix equalizer: each column is a stack of round lamps
        const int rows = 18;
        var n = Bars.Length;
        var slot = w / n;
        var cell = Math.Min(slot, h * 0.78 / rows);
        var r = Math.Max(1.5, cell * 0.36);
        var top = h - rows * cell - h * 0.06;
        for (var b = 0; b < n; b++)
        {
            var x = b * slot + slot / 2;
            var lit = (int)Math.Round(Bars[b] * rows);
            var capRow = (int)Math.Round(Peaks[b] * rows);
            for (var row = 0; row < rows; row++)
            {
                var y = top + (rows - 1 - row) * cell + cell / 2;
                if (row < lit) dc.DrawEllipse(BrushOf(ColorAt((double)b / n, (double)row / rows)), null, new Point(x, y), r, r);
                else if (row == capRow - 1 && capRow > 0) dc.DrawEllipse(Cap, null, new Point(x, y), r, r);
                else dc.DrawEllipse(BrushOf(ColorAt(0.5, 0.3), 0x16), null, new Point(x, y), r * 0.8, r * 0.8);
            }
        }
    }

    void DrawLissajous(DrawingContext dc, double w, double h)
    {
        // each sample against the one a moment later: a steady tone draws a clean figure, music a restless knot
        var scope = Spectrum.Scope;
        const int lag = 30;
        var points = Math.Min(700, scope.Length - lag - 1);
        var baseIdx = scope.Length - points - lag;
        float peak = 0.05f;
        for (var i = 0; i < points; i++) peak = Math.Max(peak, Math.Abs(scope[baseIdx + i]));
        var scale = Math.Min(w, h) * 0.42 / Math.Min(peak, 1f);
        var c = new Point(w / 2, h / 2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
            for (var i = 0; i < points; i++)
            {
                double x = scope[baseIdx + i], y = scope[baseIdx + i + lag];
                var p = new Point(c.X + (x - y) * 0.7071 * scale * 1.5, c.Y - (x + y) * 0.7071 * scale);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
        dc.DrawGeometry(null, PenOf(ColorAt(0.5, 0.6), 8, 0x30), geo);
        dc.DrawGeometry(null, PenOf(ColorAt(0.25 + _energy, 1), 2), geo);
    }

    void DrawCircularScope(DrawingContext dc, double w, double h)
    {
        var scope = Spectrum.Scope;
        var size = Math.Min(w, h);
        var c = new Point(w / 2, h / 2);
        var radius = size * (0.22 + _energy * 0.05 + _beat * 0.015);
        const int points = 360;
        var step = Math.Max(1, scope.Length / (points * 2));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i <= points; i++)
            {
                var s = scope[scope.Length - 1 - ((points - (i % points)) * step)];
                var a = 2 * Math.PI * i / points - Math.PI / 2;
                var r = radius + Math.Clamp(s * 2.4, -1, 1) * size * 0.16;
                var p = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
                if (i == 0) g.BeginFigure(p, true); else g.LineTo(p);
            }
            g.EndFigure(true);
        }
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
        var n = Bars.Length;
        var slot = w / n;
        for (var b = 0; b < n; b++)
            dc.FillRectangle(BrushOf(ColorAt((double)b / n, Bars[b]), 0x90), new Rect(b * slot + 1, h - 3 - Bars[b] * h * 0.10, Math.Max(1, slot - 2), 3 + Bars[b] * h * 0.10));
    }

    void DrawTunnel(DrawingContext dc, double w, double h)
    {
        // rings rush towards the viewer, faster when the music is loud, wobbling with the spectrum
        const int rings = 16, sides = 72;
        var n = Bars.Length;
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
            {
                for (var i = 0; i <= sides; i++)
                {
                    var a = 2 * Math.PI * i / sides + spin;
                    var rr = r * (1 + Bars[(i % sides) * n / sides] * 0.12 * t + _beat * 0.03);
                    var p = new Point(c.X + Math.Cos(a) * rr, c.Y + Math.Sin(a) * rr * 0.85);
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(true);
            }
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
            var face = new StreamGeometry();
            using (var g = face.Open())
            {
                g.BeginFigure(c, true);
                g.LineTo(At(from, radius * 1.12));
                g.ArcTo(At(to, radius * 1.12), new Size(radius * 1.12, radius * 1.12), 0, false, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
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
            var label = new FormattedText(labels[m], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                Math.Max(11, radius * 0.1), BrushOf(Colors.White, 0xB0));
            dc.DrawText(label, new Point(c.X - label.Width / 2, c.Y + radius * 0.14));
        }
    }
}

static class GradientExtensions
{
    public static GradientStops With(this GradientStops stops, IEnumerable<GradientStop> items)
    {
        stops.AddRange(items);
        return stops;
    }

}
