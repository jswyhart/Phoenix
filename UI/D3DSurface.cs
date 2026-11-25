using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using D3D9 = Vortice.Direct3D9;
using WColor = System.Windows.Media.Color;

namespace Halcyon.UI;

/// <summary>
/// A picture drawn by the graphics card through Direct3D 11 and Direct2D, shown inside the WPF window. WPF itself can
/// only show Direct3D 9 pictures, so the Direct3D 11 picture is shared with a Direct3D 9 one (the same memory on the
/// card, nothing is copied) and handed to WPF as a <see cref="D3DImage"/>, which other parts of the window can sit
/// over and under like any other picture.
/// </summary>
public sealed class D3DSurface : IDisposable
{
    // one set of devices for the whole app (everything here runs on the interface thread)
    static ID3D11Device? _device;
    static ID3D11DeviceContext? _context;
    static ID2D1Factory1? _d2d;
    static ID2D1Device? _d2dDevice;
    static ID2D1DeviceContext? _dc;
    static IDWriteFactory? _dwrite;
    static D3D9.IDirect3D9Ex? _d3d9;
    static D3D9.IDirect3DDevice9Ex? _device9;
    static D2DPaint? _paint;
    static bool _tried;

    /// <summary>Why Direct3D 11 could not be used, if it could not.</summary>
    public static string Problem { get; private set; } = "";
    /// <summary>Whether Direct3D 11 drawing is available on this PC.</summary>
    public static bool Available => Devices();

    static readonly object Gate = new();

    /// <summary>Set the graphics devices up ahead of time, off the interface thread, so the first grid or visualizer
    /// shown does not have to wait for it (it takes a tenth of a second or more).</summary>
    public static void Warm() => Task.Run(() => { try { Devices(); } catch { } });

    static volatile bool _settled;

    static bool Devices()
    {
        if (_settled) return _paint != null;
        lock (Gate) { var ok = Create(); _settled = true; return ok; }
    }

    static bool Create()
    {
        if (_tried) return _paint != null;
        _tried = true;
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 }, out _device, out _context).CheckError();
            _d2d = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            using (var dxgi = _device!.QueryInterface<IDXGIDevice>()) _d2dDevice = _d2d.CreateDevice(dxgi);
            _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;   // the pictures are see-through, which rules out ClearType
            _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
            D3D9.D3D9.Direct3DCreate9Ex(out _d3d9).CheckError();
            var present = new D3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = D3D9.SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = D3D9.PresentInterval.Default,
                BackBufferWidth = 1,
                BackBufferHeight = 1
            };
            _device9 = _d3d9!.CreateDeviceEx(0, D3D9.DeviceType.Hardware, IntPtr.Zero,
                D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve, present);
            _paint = new D2DPaint(_dc, _d2d, _dwrite);
            return true;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            _paint = null;
            return false;
        }
    }

    ID3D11Texture2D? _texture;
    ID2D1Bitmap1? _target;
    D3D9.IDirect3DTexture9? _texture9;
    D3D9.IDirect3DSurface9? _surface9;
    int _w, _h;
    double _scale = 1;

    /// <summary>The picture, for WPF to show.</summary>
    public D3DImage? Image { get; private set; }
    public bool Ready => _target != null && Image != null;
    /// <summary>Frames drawn on the graphics card.</summary>
    public int Frames { get; private set; }
    public int PixelWidth => _w;
    public int PixelHeight => _h;

    /// <summary>Make the picture this many screen pixels; <paramref name="scale"/> is screen pixels per layout unit.</summary>
    public bool Resize(int width, int height, double scale)
    {
        if (Ready && width == _w && height == _h && scale == _scale) return true;
        Release();
        if (width < 8 || height < 8 || !Devices()) return false;
        try
        {
            _texture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared
            });
            IntPtr shared;
            using (var resource = _texture.QueryInterface<IDXGIResource>()) shared = resource.SharedHandle;
            using (var surface = _texture.QueryInterface<IDXGISurface>())
                _target = _dc!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    (float)(96 * scale), (float)(96 * scale), BitmapOptions.Target | BitmapOptions.CannotDraw));
            _texture9 = _device9!.CreateTexture((uint)width, (uint)height, 1, D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref shared);
            _surface9 = _texture9.GetSurfaceLevel(0);
            _w = width; _h = height; _scale = scale;
            Image = new D3DImage(96 * scale, 96 * scale);
            Image.Lock();
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer, true);
            Image.Unlock();
            return true;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            Release();
            return false;
        }
    }

    /// <summary>Timing harness only: where the time of a redraw goes (ticks: drawing calls, finishing, handing to WPF) and bytes allocated.</summary>
    public static long[]? Timing;

    /// <summary>Draw one frame and show it.</summary>
    public bool Draw(Action<D2DPaint> draw)
    {
        if (!Ready) return false;
        try
        {
            long t0 = Timing != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, a0 = Timing != null ? GC.GetAllocatedBytesForCurrentThread() : 0, t1 = 0, t2 = 0, a1 = 0;
            var dc = _dc!;
            dc.Target = _target;
            dc.SetDpi((float)(96 * _scale), (float)(96 * _scale));
            dc.BeginDraw();
            dc.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
            _paint!.Begin();
            try { draw(_paint); }
            finally
            {
                _paint.End();
                if (Timing != null) { t1 = System.Diagnostics.Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread(); }
                dc.EndDraw().CheckError();
                dc.Target = null;
            }
            _context!.Flush();
            if (Timing != null) t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            var image = Image!;
            if (image.IsFrontBufferAvailable)
            {
                image.Lock();
                image.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
                image.Unlock();
            }
            Frames++;
            if (Timing != null)
            {
                var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                Timing[0] += t1 - t0; Timing[1] += t2 - t1; Timing[2] += t3 - t2; Timing[3]++;
                Timing[4] += a1 - a0; Timing[5] += GC.GetAllocatedBytesForCurrentThread() - a1;
            }
            return true;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            Release();
            return false;
        }
    }

    /// <summary>A copy of what is on the picture now (for the self-test).</summary>
    public BitmapSource? Snapshot()
    {
        if (_texture == null) return null;
        try
        {
            var desc = _texture.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            using var copy = _device!.CreateTexture2D(desc);
            _context!.CopyResource(copy, _texture);
            var map = _context.Map(copy, 0, MapMode.Read);
            try
            {
                var pixels = new byte[_w * _h * 4];
                for (var y = 0; y < _h; y++) Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), pixels, y * _w * 4, _w * 4);
                var bmp = BitmapSource.Create(_w, _h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, _w * 4);
                bmp.Freeze();
                return bmp;
            }
            finally { _context.Unmap(copy, 0); }
        }
        catch { return null; }
    }

    void Release()
    {
        if (Image != null)
        {
            try { Image.Lock(); Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); Image.Unlock(); } catch { }
            Image = null;
        }
        _surface9?.Dispose(); _surface9 = null;
        _texture9?.Dispose(); _texture9 = null;
        _target?.Dispose(); _target = null;
        _texture?.Dispose(); _texture = null;
        _w = _h = 0;
    }

    public void Dispose() => Release();

    // ---------- pictures kept on the graphics card (covers) ----------

    static readonly Dictionary<string, ID2D1Bitmap?> Bitmaps = new();
    static long _bitmapBytes;
    /// <summary>How many pictures are on the graphics card (for the self-test).</summary>
    public static int BitmapCount => Bitmaps.Count(b => b.Value != null);

    /// <summary>The picture stored under a name; false when nothing (not even "there is none") is stored yet.</summary>
    public static bool TryBitmap(string key, out ID2D1Bitmap? bitmap) => Bitmaps.TryGetValue(key, out bitmap);

    /// <summary>Put a picture (premultiplied BGRA pixels) on the graphics card, or note that there is none.</summary>
    public static void StoreBitmap(string key, byte[]? pixels, int width, int height)
    {
        if (!Devices()) return;
        if (Bitmaps.Remove(key, out var old)) old?.Dispose();
        if (pixels == null || width < 1 || height < 1) { Bitmaps[key] = null; return; }
        if (_bitmapBytes > 256L << 20) ForgetBitmaps();   // start again rather than fill the card
        try
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Bitmaps[key] = _dc!.CreateBitmap(new Vortice.Mathematics.SizeI(width, height), handle.AddrOfPinnedObject(), (uint)(width * 4),
                    new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                _bitmapBytes += pixels.Length;
            }
            finally { handle.Free(); }
        }
        catch { Bitmaps[key] = null; }
    }

    public static void ForgetBitmaps(string? startingWith = null)
    {
        foreach (var key in Bitmaps.Keys.Where(k => startingWith == null || k.StartsWith(startingWith)).ToList())
        {
            Bitmaps[key]?.Dispose();
            Bitmaps.Remove(key);
        }
        if (startingWith == null) _bitmapBytes = 0;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
}

/// <summary>Drawing through Direct2D on a Direct3D 11 picture.</summary>
public sealed class D2DPaint : Paint
{
    readonly ID2D1DeviceContext _rt;
    readonly ID2D1Factory _factory;
    readonly IDWriteFactory _dwrite;
    readonly ID2D1SolidColorBrush _solid;
    readonly ID2D1StrokeStyle _round;
    readonly Dictionary<long, ID2D1LinearGradientBrush> _gradients = new();
    readonly Dictionary<(string font, int size, bool bold), IDWriteTextFormat> _formats = new();
    readonly Dictionary<(string text, string font, int size, bool bold, int width), IDWriteTextLayout> _layouts = new();
    readonly Dictionary<IDWriteTextFormat, IDWriteInlineObject> _ellipses = new();
    // what each Push did, so Pop can undo it: 0 opacity, 1 clip, 2 transform
    readonly Stack<(int kind, float opacity, Matrix3x2 transform)> _pushed = new();
    float _opacity = 1;

    internal D2DPaint(ID2D1DeviceContext rt, ID2D1Factory factory, IDWriteFactory dwrite)
    {
        _rt = rt; _factory = factory; _dwrite = dwrite;
        _solid = rt.CreateSolidColorBrush(new Vortice.Mathematics.Color4(1, 1, 1, 1));
        _round = factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = Vortice.Direct2D1.LineJoin.Round, MiterLimit = 10 });
    }

    internal void Begin() { _opacity = 1; _pushed.Clear(); _rt.Transform = Matrix3x2.Identity; }
    internal void End() { while (_pushed.Count > 0) Pop(); }

    static Vector2 V(Point p) => new((float)p.X, (float)p.Y);
    static Vortice.Mathematics.Color4 C(WColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    ID2D1Brush BrushFor(Fill f, Rect bounds)
    {
        if (f.Stops == null)
        {
            _solid.Color = C(f.Solid);
            _solid.Opacity = _opacity;
            return _solid;
        }
        // The same gradients come back frame after frame, so they are kept; colours are matched a little coarsely so
        // that a slowly turning rainbow does not make a new gradient for every bar on every frame.
        long key = f.Across ? 1 : 0;
        foreach (var (c, at) in f.Stops) key = key * 1000003 + ((c.A >> 3) << 15 | (c.R >> 3) << 10 | (c.G >> 3) << 5 | c.B >> 3) * 31 + (int)(at * 64);
        if (!_gradients.TryGetValue(key, out var brush))
        {
            if (_gradients.Count > 400) { foreach (var b in _gradients.Values) b.Dispose(); _gradients.Clear(); }
            using var stops = _rt.CreateGradientStopCollection(f.Stops.Select(s => new Vortice.Direct2D1.GradientStop((float)s.at, C(s.colour))).ToArray());
            _gradients[key] = brush = _rt.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.One), stops);
        }
        if (f.Across) { brush.StartPoint = new Vector2((float)bounds.Left, 0); brush.EndPoint = new Vector2((float)bounds.Right, 0); }
        else { brush.StartPoint = new Vector2(0, (float)bounds.Bottom); brush.EndPoint = new Vector2(0, (float)bounds.Top); }
        brush.Opacity = _opacity;
        return brush;
    }

    static Vortice.Mathematics.Rect R(Rect r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    public override void Rect(Fill fill, Rect r) => _rt.FillRectangle(R(r), BrushFor(fill, r));

    public override void Ellipse(Fill? fill, Fill? stroke, double width, Point centre, double rx, double ry)
    {
        var bounds = new Rect(centre.X - rx, centre.Y - ry, rx * 2, ry * 2);
        var e = new Ellipse(V(centre), (float)rx, (float)ry);
        if (fill is { } f) _rt.FillEllipse(e, BrushFor(f, bounds));
        if (stroke is { } s) _rt.DrawEllipse(e, BrushFor(s, bounds), (float)width);
    }

    public override void Line(WColor colour, double width, Point a, Point b, bool round = true) =>
        _rt.DrawLine(V(a), V(b), BrushFor(Fill.Of(colour), default), (float)width, round ? _round : null);

    public override void Path(Shape shape, Fill? fill, Fill? stroke, double width = 1, bool round = true)
    {
        using var geo = _factory.CreatePathGeometry();
        double left = shape.Start.X, right = left, top = shape.Start.Y, bottom = top;
        void Grow(Point p) { if (p.X < left) left = p.X; if (p.X > right) right = p.X; if (p.Y < top) top = p.Y; if (p.Y > bottom) bottom = p.Y; }
        using (var sink = geo.Open())
        {
            sink.BeginFigure(V(shape.Start), shape.Filled ? FigureBegin.Filled : FigureBegin.Hollow);
            foreach (var (kind, a, b, c, radius) in shape.Parts)
            {
                if (kind == Shape.Kind.Line) { sink.AddLine(V(a)); Grow(a); }
                else if (kind == Shape.Kind.Bezier) { sink.AddBezier(new BezierSegment(V(a), V(b), V(c))); Grow(c); }
                else { sink.AddArc(new ArcSegment(V(a), new Vortice.Mathematics.Size((float)radius, (float)radius), 0, SweepDirection.Clockwise, ArcSize.Small)); Grow(a); }
            }
            sink.EndFigure(shape.Closed ? FigureEnd.Closed : FigureEnd.Open);
            sink.Close();
        }
        var bounds = new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        if (fill is { } f) _rt.FillGeometry(geo, BrushFor(f, bounds));
        if (stroke is { } s) _rt.DrawGeometry(geo, BrushFor(s, bounds), (float)width, round ? _round : null);
    }

    public override void PushOpacity(double opacity)
    {
        _pushed.Push((0, _opacity, default));
        _opacity *= (float)opacity;
    }

    public override void PushClip(Rect r)
    {
        _pushed.Push((1, 0, default));
        _rt.PushAxisAlignedClip(R(r), AntialiasMode.Aliased);
    }

    public override void PushFlip(double y)
    {
        var before = _rt.Transform;
        _pushed.Push((2, 0, before));
        _rt.Transform = Matrix3x2.CreateScale(1, -1, new Vector2(0, (float)y)) * before;
    }

    public override void Pop()
    {
        if (_pushed.Count == 0) return;
        var (kind, opacity, transform) = _pushed.Pop();
        if (kind == 0) _opacity = opacity;
        else if (kind == 1) _rt.PopAxisAlignedClip();
        else _rt.Transform = transform;
    }

    public override void Label(string text, Point topCentre, double size, WColor colour)
    {
        var w = Measure(text, size, bold: true).Width;
        Text(text, new Point(topCentre.X - w / 2, topCentre.Y), 2000, size, colour, bold: true);
    }

    // ---------- text and pictures (the cover grids) ----------

    public const string Icons = "Segoe MDL2 Assets";

    IDWriteTextLayout Layout(string text, double size, double maxWidth, string font, bool bold)
    {
        var key = (text, font, (int)Math.Round(size * 4), bold, (int)Math.Ceiling(maxWidth));
        if (_layouts.TryGetValue(key, out var layout)) return layout;
        // A line of text is worked out once and kept: drawing it again is then nearly free, which matters when a
        // screenful of titles is redrawn on every frame of a scroll.
        if (_layouts.Count > 3000) { foreach (var l in _layouts.Values) l.Dispose(); _layouts.Clear(); }
        var fkey = (font, key.Item3, bold);
        if (!_formats.TryGetValue(fkey, out var format))
        {
            _formats[fkey] = format = _dwrite.CreateTextFormat(font, bold ? Vortice.DirectWrite.FontWeight.SemiBold : Vortice.DirectWrite.FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, (float)size);
            format.WordWrapping = WordWrapping.NoWrap;
            _ellipses[format] = _dwrite.CreateEllipsisTrimmingSign(format);
        }
        layout = _dwrite.CreateTextLayout(text, format, (float)Math.Max(1, Math.Ceiling(maxWidth)), 1000);
        layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, _ellipses[format]);
        return _layouts[key] = layout;
    }

    /// <summary>The size a line of text takes up.</summary>
    public Size Measure(string text, double size, string? font = null, bool bold = false, double maxWidth = 4000)
    {
        font ??= Themes.TextFontName;
        if (text == "") return default;
        var m = Layout(text, size, maxWidth, font, bold).Metrics;
        return new Size(Math.Min(m.WidthIncludingTrailingWhitespace, maxWidth), m.Height);
    }

    /// <summary>One line of text, cut short with "…" if it is wider than <paramref name="maxWidth"/>; returns its width.</summary>
    public double Text(string text, Point at, double maxWidth, double size, WColor colour, string? font = null, bool bold = false)
    {
        font ??= Themes.TextFontName;
        if (text == "" || maxWidth < 4) return 0;
        var layout = Layout(text, size, maxWidth, font, bold);
        _rt.DrawTextLayout(V(at), layout, BrushFor(Fill.Of(colour), default), DrawTextOptions.Clip);
        return Math.Min(layout.Metrics.WidthIncludingTrailingWhitespace, maxWidth);
    }

    /// <summary>A picture already on the graphics card; <paramref name="from"/> is the part of it to show (all of it if null).</summary>
    public void Picture(ID2D1Bitmap picture, Rect to, Rect? from = null)
    {
        if (from is { } part) _rt.DrawBitmap(picture, R(to), _opacity, BitmapInterpolationMode.Linear, R(part));
        else _rt.DrawBitmap(picture, R(to), _opacity, BitmapInterpolationMode.Linear, null);
    }
}
