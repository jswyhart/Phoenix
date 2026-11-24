using Android.Content;
using Android.Graphics;
using Android.Views;
using Halcyon.Core;
using AFx = Android.Media.Audiofx;
using APath = Android.Graphics.Path;

namespace Halcyon.Droid;

/// <summary>
/// The music visualizer: fifteen styles (index 0 shows the album art instead) in eleven colour schemes, the same lists as
/// the Windows app. Samples come from the platform audio-effect visualizer attached to the player's own audio session;
/// <see cref="Spectrum"/> turns them into bars, beats, ripples, sparks and meter needles, and this view only draws.
/// </summary>
public class VisualizerView : View
{
    readonly Spectrum _spec = new();
    readonly Paint _fill = new(PaintFlags.AntiAlias), _line = new(PaintFlags.AntiAlias), _text = new(PaintFlags.AntiAlias);
    AFx.Visualizer? _capture;
    Listener? _listener;
    int _session = -1;
    Color? _cover;
    string _coverFor = "\0";

    /// <summary>Which style to draw (index into <see cref="VisualizerOptions.Styles"/>).</summary>
    public int Mode = 1;

    public VisualizerView(Context c) : base(c)
    {
        _line.SetStyle(Paint.Style.Stroke);
        _line.StrokeCap = Paint.Cap.Round;
        _line.StrokeJoin = Paint.Join.Round;
        _text.TextAlign = Paint.Align.Center;
    }

    class Listener(Spectrum spec) : Java.Lang.Object, AFx.Visualizer.IOnDataCaptureListener
    {
        public void OnFftDataCapture(AFx.Visualizer? visualizer, byte[]? fft, int samplingRate) { }
        public void OnWaveFormDataCapture(AFx.Visualizer? visualizer, byte[]? waveform, int samplingRate)
        {
            if (waveform != null) spec.FeedU8(waveform, samplingRate / 1000);   // rate arrives in millihertz
        }
    }

    /// <summary>Starts listening to an audio session. Returns false when the system refuses (no permission, no effect).</summary>
    public bool Start(int audioSession)
    {
        if (_capture != null && _session == audioSession) return true;
        Stop();
        try
        {
            _capture = new AFx.Visualizer(audioSession);
            _capture.SetEnabled(false);
            _capture.SetCaptureSize(AFx.Visualizer.GetCaptureSizeRange()![1]);
            _listener = new Listener(_spec);
            _capture.SetDataCaptureListener(_listener, AFx.Visualizer.MaxCaptureRate, true, false);
            _capture.SetEnabled(true);
            _session = audioSession;
            Invalidate();
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        _session = -1;
        if (c != null) try { c.SetEnabled(false); c.Release(); } catch { }
        _spec.Clear();
    }

    protected override void OnDetachedFromWindow()
    {
        Stop();
        base.OnDetachedFromWindow();
    }

    // ---------- colour ----------

    static Color Hsv(double h, double s, double v) => Color.HSVToColor(new[] { (float)(((h % 1) + 1) % 1 * 360), (float)s, (float)v });

    static Color Mix(Color a, Color b, double t) =>
        Color.Rgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static Color Rgb(int rgb) => Color.Rgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    static readonly int[][] Ramps =
    {
        new[] { 0xD5358F, 0xF0603A, 0xFFD070 },   // sunset
        Array.Empty<int>(),                       // rainbow (by position)
        new[] { 0x1E4FD8, 0x35C6F0, 0xF0FBFF },   // ice
        new[] { 0x0B6B3A, 0x2FD06E, 0xD8FF9A },   // emerald
        new[] { 0x707070, 0xC8C8C8, 0xFFFFFF },   // mono
        new[] { 0x9A1010, 0xFF6A00, 0xFFF080 },   // fire
        new[] { 0x7A1FFF, 0x19F0FF, 0xB9FF4A },   // neon
        new[] { 0x082A6B, 0x109CB8, 0xB8FFE8 },   // ocean
        new[] { 0x3A107A, 0xA040E0, 0xF4B8FF },   // violet
        new[] { 0x6B4A08, 0xE0A820, 0xFFF4B0 },   // gold
        Array.Empty<int>()                        // album colours (from the cover)
    };

    int Palette => Math.Clamp(App.Settings.VisualizerColor, 0, Ramps.Length - 1);

    Color[] Ramp()
    {
        if (Palette == 10)
        {
            var c = _cover ?? Color.Rgb(0xE0, 0x44, 0x7C);
            return new[] { Mix(c, Color.Black, 0.55), c, Mix(c, Color.White, 0.6) };
        }
        return Ramps[Palette].Select(Rgb).ToArray();
    }

    /// <summary>Colour for a position across the display (0..1) at an intensity (0..1).</summary>
    Color ColorAt(double across, double level)
    {
        if (Palette == 1) return Hsv(across * 0.85 + _spec.Phase * 0.02, 0.85, 0.55 + 0.45 * Math.Clamp(level, 0, 1));
        var ramp = Ramp();
        level = Math.Clamp(level, 0, 1);
        return level < 0.5 ? Mix(ramp[0], ramp[1], level * 2) : Mix(ramp[1], ramp[2], level * 2 - 1);
    }

    static Color WithAlpha(Color c, int alpha) => Color.Argb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    /// <summary>Tells the "album colours" scheme which cover is showing; its dominant colour is worked out off the UI thread.</summary>
    public void SetCover(string artPath)
    {
        if (_coverFor == artPath) return;
        _coverFor = artPath;
        _cover = null;
        _ = Task.Run(async () =>
        {
            var c = await DominantAsync(artPath);
            HalcyonApp.Main.Post(() => { if (_coverFor == artPath) { _cover = c; Invalidate(); } });
        });
    }

    /// <summary>The most colourful average colour of a picture (vivid, not too dark), or null if it has none.</summary>
    static async Task<Color?> DominantAsync(string path)
    {
        if (path == "") return null;
        try
        {
            var bmp = await Art.LoadAsync(path, 24);
            if (bmp == null) return null;
            int w = Math.Min(bmp.Width, 32), h = Math.Min(bmp.Height, 32);
            var small = Bitmap.CreateScaledBitmap(bmp, w, h, true)!;
            var px = new int[w * h];
            small.GetPixels(px, 0, w, 0, 0, w, h);
            double r = 0, g = 0, b = 0, total = 0;
            foreach (var p in px)
            {
                double R = (p >> 16) & 255, G = (p >> 8) & 255, B = p & 255;
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                var weight = 0.05 + (max - min) / 255.0 * Math.Min(1, max / 128.0);
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            if (total <= 0) return null;
            double cr = r / total, cg = g / total, cb = b / total, peak = Math.Max(cr, Math.Max(cg, cb));
            if (peak is < 140 and > 0) { var k = 170.0 / peak; cr = Math.Min(255, cr * k); cg = Math.Min(255, cg * k); cb = Math.Min(255, cb * k); }
            return Color.Rgb((int)cr, (int)cg, (int)cb);
        }
        catch { return null; }
    }

    // ---------- drawing ----------

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float w = Width, h = Height;
        if (w < 10 || h < 10 || Mode == 0) return;
        _spec.Sensitivity = Math.Clamp(App.Settings.VisualizerSensitivity, 0, 3);
        _spec.Update();
        switch (Mode)
        {
            case 2: DrawBars(canvas, w, h, true); break;
            case 3: DrawScope(canvas, w, h); break;
            case 4: DrawRadial(canvas, w, h, false); break;
            case 5: DrawWave(canvas, w, h); break;
            case 6: DrawRadial(canvas, w, h, true); break;
            case 7: DrawWaterfall(canvas, w, h); break;
            case 8: DrawDots(canvas, w, h); break;
            case 9: DrawLissajous(canvas, w, h); break;
            case 10: DrawCircularScope(canvas, w, h); break;
            case 11: DrawRipples(canvas, w, h); break;
            case 12: DrawFireworks(canvas, w, h); break;
            case 13: DrawTunnel(canvas, w, h); break;
            case 14: DrawMeters(canvas, w, h); break;
            default: DrawBars(canvas, w, h, false); break;
        }
        if (_capture != null) PostInvalidateDelayed(33);
    }

    void Fill(Color c) { _fill.SetShader(null); _fill.Color = c; }
    void Stroke(Color c, float width) { _line.SetShader(null); _line.Color = c; _line.StrokeWidth = width; }

    const int Bars = Spectrum.BarCount;

    void DrawBars(Canvas canvas, float w, float h, bool mirrored)
    {
        var slot = w / Bars;
        var barW = Math.Max(2f, slot - Th.Dp(2));
        var area = mirrored ? h * 0.42f : h * 0.78f;
        var baseY = mirrored ? h * 0.5f : h;
        float seg = Th.Dp(5), capH = Th.Dp(2);                     // LED-style segments
        for (var b = 0; b < Bars; b++)
        {
            var x = b * slot + (slot - barW) / 2;
            var segs = (int)Math.Floor(_spec.Bars[b] * area / seg);
            for (var j = 0; j < segs; j++)
            {
                var color = ColorAt((double)b / Bars, (double)j * seg / area);
                var top = baseY - (j + 1) * seg;
                Fill(color);
                canvas.DrawRect(x, top + 1, x + barW, top + seg, _fill);
                if (mirrored)
                {
                    Fill(WithAlpha(color, 115));                  // the reflection below the centre line, dimmer
                    canvas.DrawRect(x, baseY + j * seg, x + barW, baseY + (j + 1) * seg - 1, _fill);
                }
            }
            Fill(Color.Argb(0xE0, 0xFF, 0xFF, 0xFF));
            var capY = baseY - Math.Max(capH, (float)_spec.Peaks[b] * area) - capH;
            canvas.DrawRect(x, capY, x + barW, capY + capH, _fill);
        }
    }

    void DrawScope(Canvas canvas, float w, float h)
    {
        var mid = h * 0.5f;
        var scope = _spec.Scope;
        const int points = 256;
        var start = scope.Length - points * 2;
        var path = new APath();
        for (var i = 0; i < points; i++)
        {
            float x = w * i / (points - 1), y = mid - Math.Clamp(scope[start + i * 2], -1f, 1f) * h * 0.36f;   // the capture is already normalised to full scale
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        Stroke(Color.Argb(0x40, 0xFF, 0xFF, 0xFF), Th.Dp(6));
        canvas.DrawPath(path, _line);
        if (Palette == 1)
        {
            _line.SetShader(new LinearGradient(0, 0, w, 0, Enumerable.Range(0, 7).Select(i => (int)ColorAt(i / 6.0, 1)).ToArray(), null, Shader.TileMode.Clamp!));
            _line.StrokeWidth = Th.Dp(2);
        }
        else Stroke(ColorAt(0.5, 0.75), Th.Dp(2));
        canvas.DrawPath(path, _line);
        _line.SetShader(null);
    }

    void DrawRadial(Canvas canvas, float w, float h, bool starburst)
    {
        float cx = w / 2, cy = h / 2, size = Math.Min(w, h);
        var inner = starburst ? size * 0.03f : size * (0.15f + (float)_spec.Energy * 0.05f);
        var reach = size * (starburst ? 0.46f : 0.30f);
        var spokes = Bars * 2;                                      // the spectrum runs up one side and back down the other
        var spin = starburst ? _spec.Phase * 0.15 : -Math.PI / 2;
        for (var i = 0; i < spokes; i++)
        {
            var b = i < Bars ? i : spokes - 1 - i;
            var level = _spec.Bars[b];
            if (level <= 0.01) continue;
            var a = spin + 2 * Math.PI * i / spokes;
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a), len = (float)level * reach;
            Stroke(ColorAt((double)b / Bars, level), starburst ? Th.Dp(2 + (float)level * 5) : Math.Max(Th.Dp(2), size * 0.012f));
            canvas.DrawLine(cx + cos * inner, cy + sin * inner, cx + cos * (inner + len), cy + sin * (inner + len), _line);
            if (!starburst)
            {
                var cap = inner + (float)_spec.Peaks[b] * reach + Th.Dp(5);
                Fill(Color.Argb(0xE0, 0xFF, 0xFF, 0xFF));
                canvas.DrawCircle(cx + cos * cap, cy + sin * cap, Th.Dp(1.6f), _fill);
            }
        }
        if (!starburst) { Stroke(ColorAt(0.5, 0.6), Th.Dp(2)); canvas.DrawCircle(cx, cy, inner - Th.Dp(4), _line); }
    }

    /// <summary>A smooth curve through the points (Catmull-Rom as cubic Béziers).</summary>
    static void SmoothThrough(APath path, List<(float x, float y)> p)
    {
        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(0, i - 1)]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[Math.Min(p.Count - 1, i + 2)];
            path.CubicTo(p1.x + (p2.x - p0.x) / 6, p1.y + (p2.y - p0.y) / 6, p2.x - (p3.x - p1.x) / 6, p2.y - (p3.y - p1.y) / 6, p2.x, p2.y);
        }
    }

    void DrawWave(Canvas canvas, float w, float h)
    {
        // Two filled, smoothed spectrum curves: the live levels in front and the slower peak envelope behind.
        APath Curve(double[] levels, float scale)
        {
            var pts = new List<(float, float)> { (0, h - (float)levels[0] * scale) };
            for (var b = 0; b < Bars; b++) pts.Add((w * (b + 0.5f) / Bars, h - (float)levels[b] * scale));
            pts.Add((w, h - (float)levels[^1] * scale));
            var path = new APath();
            path.MoveTo(0, h);
            path.LineTo(pts[0].Item1, pts[0].Item2);
            SmoothThrough(path, pts.Select(p => (p.Item1, p.Item2)).ToList());
            path.LineTo(w, h);
            path.Close();
            return path;
        }
        void Shade()
        {
            if (Palette == 1) _fill.SetShader(new LinearGradient(0, 0, w, 0, Enumerable.Range(0, 7).Select(i => (int)ColorAt(i / 6.0, 0.9)).ToArray(), null, Shader.TileMode.Clamp!));
            else _fill.SetShader(new LinearGradient(0, h, 0, h * 0.2f, new[] { (int)ColorAt(0, 0), (int)ColorAt(0, 0.5), (int)ColorAt(0, 1) }, new[] { 0f, 0.55f, 1f }, Shader.TileMode.Clamp!));
        }
        Shade();
        _fill.Alpha = 70;
        canvas.DrawPath(Curve(_spec.Peaks, h * 0.74f), _fill);
        Shade();
        _fill.Alpha = 215;
        canvas.DrawPath(Curve(_spec.Bars, h * 0.70f), _fill);
        _fill.SetShader(null);
        _fill.Alpha = 255;
    }

    void DrawWaterfall(Canvas canvas, float w, float h)
    {
        // A scrolling spectrogram: each row is a moment in time, newest at the bottom, receding upward.
        var history = _spec.History;
        if (history.Count == 0) return;
        const int rows = Spectrum.HistoryRows;
        var area = h * 0.86f;
        var rowH = area / rows;
        for (var r = 0; r < history.Count; r++)
        {
            var levels = history[r];
            var depth = 1 - (double)r / rows;                        // 1 = newest
            var inset = (float)(1 - depth) * w * 0.18f;              // older rows narrow slightly, like perspective
            var slot = (w - inset * 2) / Bars;
            var y = h - (r + 1) * rowH;
            for (var b = 0; b < Bars; b++)
            {
                var level = levels[b];
                if (level < 0.06) continue;
                Fill(WithAlpha(ColorAt((double)b / Bars, level), (int)(255 * Math.Min(1, level * 1.2) * (0.25 + 0.75 * depth))));
                canvas.DrawRect(inset + b * slot, y, inset + b * slot + Math.Max(1, slot - 1), y + Math.Max(1, rowH - 1), _fill);
            }
        }
    }

    void DrawDots(Canvas canvas, float w, float h)
    {
        // a dot-matrix equalizer: each column is a stack of round lamps
        const int rows = 18;
        var slot = w / Bars;
        var cell = Math.Min(slot, h * 0.78f / rows);
        var r = Math.Max(1.5f, cell * 0.36f);
        var top = h - rows * cell - h * 0.06f;
        for (var b = 0; b < Bars; b++)
        {
            var x = b * slot + slot / 2;
            var lit = (int)Math.Round(_spec.Bars[b] * rows);
            var capRow = (int)Math.Round(_spec.Peaks[b] * rows);
            for (var row = 0; row < rows; row++)
            {
                var y = top + (rows - 1 - row) * cell + cell / 2;
                if (row < lit) { Fill(ColorAt((double)b / Bars, (double)row / rows)); canvas.DrawCircle(x, y, r, _fill); }
                else if (row == capRow - 1 && capRow > 0) { Fill(Color.Argb(0xE0, 0xFF, 0xFF, 0xFF)); canvas.DrawCircle(x, y, r, _fill); }
                else { Fill(WithAlpha(ColorAt(0.5, 0.3), 0x16)); canvas.DrawCircle(x, y, r * 0.8f, _fill); }
            }
        }
    }

    void DrawLissajous(Canvas canvas, float w, float h)
    {
        // each sample against the one a moment later: a steady tone draws a clean figure, music a restless knot
        const int points = 700, lag = 30;
        var scope = _spec.Scope;
        var baseIdx = scope.Length - points - lag;
        var peak = 0.05f;
        for (var i = 0; i < points; i++) peak = Math.Max(peak, Math.Abs(scope[baseIdx + i]));
        var scale = Math.Min(w, h) * 0.42f / Math.Min(peak, 1f);
        float cx = w / 2, cy = h / 2;
        var path = new APath();
        for (var i = 0; i < points; i++)
        {
            float x = scope[baseIdx + i], y = scope[baseIdx + i + lag];
            float px = cx + (x - y) * 0.7071f * scale * 1.5f, py = cy - (x + y) * 0.7071f * scale;
            if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
        }
        Stroke(WithAlpha(ColorAt(0.5, 0.6), 0x30), Th.Dp(7));
        canvas.DrawPath(path, _line);
        Stroke(ColorAt(0.25 + _spec.Energy, 1), Th.Dp(2));
        canvas.DrawPath(path, _line);
    }

    void DrawCircularScope(Canvas canvas, float w, float h)
    {
        float size = Math.Min(w, h), cx = w / 2, cy = h / 2;
        var radius = size * (0.22f + (float)_spec.Energy * 0.05f + (float)_spec.Beat * 0.015f);
        const int points = 360;
        var scope = _spec.Scope;
        var path = new APath();
        for (var i = 0; i <= points; i++)
        {
            var s = scope[scope.Length - 2 * points + (i % points) * 2];
            var a = 2 * Math.PI * i / points - Math.PI / 2;
            var r = radius + Math.Clamp(s * 1.2f, -1f, 1f) * size * 0.16f;
            float x = cx + (float)Math.Cos(a) * r, y = cy + (float)Math.Sin(a) * r;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
        Fill(WithAlpha(ColorAt(0.5, 0.4), 0x18));
        canvas.DrawPath(path, _fill);
        Stroke(WithAlpha(ColorAt(0.5, 0.7), 0x30), Th.Dp(8));
        canvas.DrawPath(path, _line);
        Stroke(ColorAt(0.35 + _spec.Phase * 0.01 % 0.5, 1), Th.Dp(2.2f));
        canvas.DrawPath(path, _line);
        Stroke(WithAlpha(ColorAt(0.8, 0.5), 0x80), Th.Dp(1.2f));
        canvas.DrawCircle(cx, cy, radius * 0.55f, _line);
    }

    void DrawRipples(Canvas canvas, float w, float h)
    {
        float cx = w / 2, cy = h / 2;
        var reach = (float)Math.Sqrt(w * w + h * h) / 2;
        foreach (var r in _spec.Ripples)
        {
            var fade = 1 - r.Radius;
            Stroke(WithAlpha(ColorAt(r.Hue, 0.4 + r.Level * 0.6), (int)(220 * fade * fade)), Th.Dp(2 + (float)(r.Level * 8 * fade)));
            canvas.DrawCircle(cx, cy, (float)r.Radius * reach, _line);
        }
        // the heart of it swells with the bass
        var core = Math.Min(w, h) * (0.05f + (float)_spec.Energy * 0.12f + (float)_spec.Beat * 0.05f);
        Fill(WithAlpha(ColorAt(0.5, 0.8), 0x40));
        canvas.DrawCircle(cx, cy, core * 1.7f, _fill);
        Fill(WithAlpha(ColorAt(0.5, 1), 0xC0));
        canvas.DrawCircle(cx, cy, core, _fill);
    }

    void DrawFireworks(Canvas canvas, float w, float h)
    {
        foreach (var k in _spec.Sparks)
        {
            var life = (float)Math.Clamp(k.Life, 0, 1);
            float x = (float)k.X * w, y = (float)k.Y * h, size = (float)k.Size;
            Fill(WithAlpha(ColorAt(k.Hue, 0.4 + life * 0.6), (int)(255 * life * 0.35)));
            canvas.DrawCircle(x, y, Th.Dp(size * 2.6f * life + 1), _fill);
            Fill(WithAlpha(ColorAt(k.Hue, 1), (int)(255 * life)));
            canvas.DrawCircle(x, y, Th.Dp(size * life + 0.5f), _fill);
        }
        // a low glow along the bottom shows the spectrum
        var slot = w / Bars;
        for (var b = 0; b < Bars; b++)
        {
            Fill(WithAlpha(ColorAt((double)b / Bars, _spec.Bars[b]), 0x90));
            var bh = Th.Dp(3) + (float)_spec.Bars[b] * h * 0.10f;
            canvas.DrawRect(b * slot + 1, h - bh, b * slot + Math.Max(2, slot - 1), h, _fill);
        }
    }

    void DrawTunnel(Canvas canvas, float w, float h)
    {
        // rings rush towards the viewer, faster when the music is loud, wobbling with the spectrum
        const int rings = 16, sides = 72;
        float cx = w / 2, cy = h / 2;
        var reach = (float)Math.Sqrt(w * w + h * h) / 2 * 1.05f;
        var drift = _spec.Phase * 0.7 % 1;
        for (var k = rings - 1; k >= 0; k--)
        {
            var t = (k + drift) / rings;                              // 0 far, 1 near
            var r = (float)Math.Pow(t, 2.3) * reach;
            var spin = _spec.Phase * 0.12 * (k % 2 == 0 ? 1 : -1);
            var path = new APath();
            for (var i = 0; i <= sides; i++)
            {
                var a = 2 * Math.PI * i / sides + spin;
                var rr = r * (1 + _spec.Bars[(i % sides) * Bars / sides] * 0.12 * t + _spec.Beat * 0.03);
                float x = cx + (float)(Math.Cos(a) * rr), y = cy + (float)(Math.Sin(a) * rr * 0.85);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
            Stroke(WithAlpha(ColorAt(t, 0.3 + t * 0.7), (int)(40 + 215 * t)), Th.Dp((float)(1 + t * 5 + _spec.Energy * 3 * t)));
            canvas.DrawPath(path, _line);
        }
    }

    void DrawMeters(Canvas canvas, float w, float h)
    {
        // two analog dials: bass on the left, treble on the right
        var radius = Math.Min(w * 0.22f, h * 0.36f);
        var labels = new[] { "LOW", "HIGH" };
        const float from = -150, to = -30;
        for (var m = 0; m < 2; m++)
        {
            float cx = w * (m == 0 ? 0.27f : 0.73f), cy = h * 0.62f;
            (float x, float y) At(float deg, float r) { var a = deg * Math.PI / 180; return (cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r); }
            var face = new RectF(cx - radius * 1.12f, cy - radius * 1.12f, cx + radius * 1.12f, cy + radius * 1.12f);
            Fill(Color.Argb(0x0C, 0xFF, 0xFF, 0xFF));
            canvas.DrawArc(face, from, to - from, true, _fill);
            Stroke(Color.Argb(0x40, 0xFF, 0xFF, 0xFF), Th.Dp(1.5f));
            canvas.DrawArc(face, from, to - from, true, _line);
            for (var t = 0; t <= 20; t++)
            {
                var frac = t / 20.0;
                var major = t % 5 == 0;
                var colour = frac > 0.8 ? ColorAt(0.9, 1) : ColorAt(0.2, 0.6);
                Stroke(major ? colour : WithAlpha(colour, 150), Th.Dp(major ? 2.4f : 1.2f));
                var a = At(from + (to - from) * (float)frac, radius * (major ? 0.82f : 0.9f));
                var b = At(from + (to - from) * (float)frac, radius);
                canvas.DrawLine(a.x, a.y, b.x, b.y, _line);
            }
            var angle = from + (to - from) * (float)Math.Clamp(_spec.Needle[m], 0, 1);
            var tip = At(angle, radius * 0.95f);
            Stroke(Color.Argb(0x50, 0, 0, 0), Th.Dp(6));
            canvas.DrawLine(cx, cy, tip.x, tip.y, _line);
            Stroke(ColorAt(0.6, 1), Th.Dp(3));
            canvas.DrawLine(cx, cy, tip.x, tip.y, _line);
            Fill(ColorAt(0.6, 1));
            canvas.DrawCircle(cx, cy, radius * 0.06f, _fill);
            _text.Color = Color.Argb(0xB0, 0xFF, 0xFF, 0xFF);
            _text.TextSize = Math.Max(Th.Dp(11), radius * 0.1f);
            _text.SetTypeface(Th.Medium);
            canvas.DrawText(labels[m], cx, cy + radius * 0.14f + _text.TextSize, _text);
        }
    }
}

/// <summary>The visualizer's pickers (style, colour scheme, sensitivity), shared by the now playing screen and settings.</summary>
public static class VisualizerPicker
{
    static Settings S => App.Settings;

    static string Mark(bool on, string name) => (on ? "●  " : "○  ") + name;

    /// <summary>Choose what the visualizer shows, how it is coloured and how strongly it reacts.</summary>
    public static void Show(MainActivity a, Action changed)
    {
        Ui.Menu(a, "Visualizer",
            ($"Style: {VisualizerOptions.Styles[Math.Clamp(S.Visualizer, 0, VisualizerOptions.Styles.Length - 1)]}…", () => Style(a, changed)),
            ($"Colours: {VisualizerOptions.Palettes[Math.Clamp(S.VisualizerColor, 0, VisualizerOptions.Palettes.Length - 1)]}…", () => Colours(a, changed)),
            ($"Sensitivity: {VisualizerOptions.Sensitivities[Math.Clamp(S.VisualizerSensitivity, 0, VisualizerOptions.Sensitivities.Length - 1)]}…", () => Sensitivity(a, changed)));
    }

    public static void Style(MainActivity a, Action changed) =>
        Ui.Menu(a, "Visualizer style", VisualizerOptions.Styles.Select((name, i) => (Mark(i == S.Visualizer, name), (Action)(() =>
        {
            if (i != 0 && !a.EnsureAudioPermission()) return;   // asks; choose again once granted
            S.Visualizer = i;
            S.Save();
            changed();
        }))).ToArray());

    public static void Colours(MainActivity a, Action changed) =>
        Ui.Menu(a, "Colours", VisualizerOptions.Palettes.Select((name, i) => (Mark(i == S.VisualizerColor, name), (Action)(() => { S.VisualizerColor = i; S.Save(); changed(); }))).ToArray());

    public static void Sensitivity(MainActivity a, Action changed) =>
        Ui.Menu(a, "Sensitivity", VisualizerOptions.Sensitivities.Select((name, i) => (Mark(i == S.VisualizerSensitivity, name), (Action)(() => { S.VisualizerSensitivity = i; S.Save(); changed(); }))).ToArray());
}
