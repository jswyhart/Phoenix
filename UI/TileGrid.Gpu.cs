using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>The shape every tile of a grid shares.</summary>
public sealed class TileStyle
{
    public double ImageWidth, ImageHeight;
    /// <summary>The size artwork is loaded at (Windows keeps thumbnails at certain sizes).</summary>
    public int ArtSize = 300;
    /// <summary>Show the whole picture, standing on the bottom edge (book covers); otherwise it fills the box and is cropped.</summary>
    public bool Fit;
    /// <summary>Lines of text under the picture: 0, 1 or 2.</summary>
    public int Lines = 2;
    public double GlyphSize = 32;
    /// <summary>Show a translation beside titles in another language.</summary>
    public bool Translate;

    public double RowHeight => 12 + ImageHeight + (Lines > 0 ? 6 + 17 : 0) + (Lines > 1 ? 16 : 0);

    public static readonly TileStyle Album = new() { ImageWidth = 138, ImageHeight = 138, GlyphSize = 34, Translate = true };
    public static readonly TileStyle Book = new() { ImageWidth = 150, ImageHeight = 225, ArtSize = 320, Fit = true };
    public static readonly TileStyle Poster = new() { ImageWidth = 150, ImageHeight = 225, ArtSize = 320, GlyphSize = 30 };
    public static readonly TileStyle Wide = new() { ImageWidth = 208, ImageHeight = 117, GlyphSize = 30 };
    public static readonly TileStyle Picture = new() { ImageWidth = 148, ImageHeight = 148, ArtSize = 256, Lines = 0 };
}

/// <summary>What one tile shows.</summary>
public readonly record struct TileLook(string? Art, string Title = "", string Subtitle = "", string Glyph = "", string Badge = "", string Counter = "", bool Audio = false, double Progress = 0, string Tip = "");

/// <summary>Shows the grid's Direct3D picture; it sits behind the (see-through) scrolling area.</summary>
public sealed class GpuLayer : FrameworkElement
{
    public D3DSurface? Surface;

    protected override void OnRender(DrawingContext dc)
    {
        if (Surface is not { Ready: true, Image: { } image }) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        dc.DrawImage(image, new Rect(0, 0, Surface.PixelWidth / scale, Surface.PixelHeight / scale));
    }
}

// The grid drawn by the graphics card. Instead of building WPF controls for each tile, the whole screenful of tiles is
// one picture drawn through Direct3D 11 (covers are kept on the card, lines of text are laid out once and kept), and
// redrawn whenever the grid scrolls, the mouse moves to another tile, or a cover arrives. Clicks, the hover highlight,
// right-click menus, tooltips and dragging are worked out from where the mouse is.
public partial class TileGrid
{
    /// <summary>Set PHOENIX_GRID=wpf to build the grids from WPF controls instead (for comparison).</summary>
    public static bool GpuAllowed = Environment.GetEnvironmentVariable("PHOENIX_GRID") != "wpf";

    TileStyle? _style;
    Func<object, TileLook>? _look;
    D3DSurface? _surface;
    bool _gpuFailed, _drawQueued;
    int _hover = -1, _gpuRows;
    // Working out what a tile shows (its texts, the name its cover is stored under) is not free, and during a scroll
    // the same tiles are drawn hundreds of times a second. So each tile keeps its answer, and the answers are only
    // thrown away when something other than the scroll position changed.
    int _epoch;
    Action<D2DPaint>? _painter;
    double _paintHeight, _paintScale;
    readonly HashSet<string> _asked = new();
    List<INotifyPropertyChanged> _watched = new();

    /// <summary>Have the graphics card draw this grid: the tiles' shape and what each item shows.</summary>
    public void DrawWith(TileStyle style, Func<object, TileLook> look) { _style = style; _look = look; }

    bool GpuMode => _look != null && _style != null && GpuAllowed && !_gpuFailed && D3DSurface.Available;
    /// <summary>Whether the graphics card is drawing this grid through Direct3D 11 right now.</summary>
    public bool OnGpu => GpuMode && _surface?.Ready == true;
    public int GpuDraws => _surface?.Frames ?? 0;
    public BitmapSource? GpuSnapshot() => _surface?.Snapshot();
    /// <summary>The tile the mouse is over, or -1.</summary>
    public int HoverIndex => _hover;

    void HookGpu()
    {
        // before anything else sees the press, so a drag started on a tile knows which tile it is
        PreviewMouseLeftButtonDown += (_, e) => { if (GpuMode) SetHover(IndexAt(e.GetPosition(Scroller))); };
        MouseMove += (_, e) => { if (GpuMode) SetHover(IndexAt(e.GetPosition(Scroller))); };
        MouseLeave += (_, _) => { if (GpuMode) SetHover(-1); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!GpuMode || IndexAt(e.GetPosition(Scroller)) is not (>= 0 and var i)) return;
            Press(i, e.ClickCount);
        };
        MouseRightButtonUp += (_, e) =>
        {
            if (!GpuMode || IndexAt(e.GetPosition(Scroller)) is not (>= 0 and var i)) return;
            ItemContext?.Invoke(_cells[i].Item, this);
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible && GpuMode) { ReleaseGpu(); _stale = true; } };
        Unloaded += (_, _) => ReleaseGpu();
    }

    void Press(int index, int clicks)
    {
        var item = _cells[index].Item;
        if (clicks == 2) ItemActivated?.Invoke(item); else ItemClicked?.Invoke(item);
    }

    /// <summary>For the self-test: a click (or double click) on a tile.</summary>
    public void PressForTest(int index, int clicks = 1) => Press(index, clicks);
    /// <summary>For the self-test: the mouse resting at a point of the grid.</summary>
    public void PointForTest(Point p) => SetHover(IndexAt(p));

    /// <summary>Which tile is at a point of the scrolling area, or -1.</summary>
    public int IndexAt(Point p)
    {
        if (_rowH <= 0 || p.X < 0 || p.Y < 0 || p.Y > Scroller.ViewportHeight || p.X > Scroller.ViewportWidth) return -1;
        var col = (int)(p.X / TileWidth);
        if (col >= _cols) return -1;
        var index = (int)((p.Y + Scroller.VerticalOffset) / _rowH) * _cols + col;
        return index < _cells.Count ? index : -1;
    }

    void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        var cell = index >= 0 ? _cells[index] : null;
        Surface.DataContext = cell;                    // what a drag started here carries
        Scroller.Cursor = cell != null ? Cursors.Hand : null;
        var tip = cell != null ? _look!(cell.Item).Tip : "";
        ToolTip = tip == "" ? null : tip;
        QueueDraw();
    }

    void QueueDraw()
    {
        if (_drawQueued) return;
        _drawQueued = true;
        Dispatcher.BeginInvoke(() => { _drawQueued = false; if (GpuMode && !_stale) { _epoch++; DrawGpu(); } }, System.Windows.Threading.DispatcherPriority.Render);
    }

    void ReleaseGpu()
    {
        if (_surface == null) return;
        Layer.Surface = null;
        Layer.InvalidateVisual();
        _surface.Dispose();
        _surface = null;
    }

    void FailGpu()
    {
        // Direct3D gave up (the graphics driver was reset, say): carry on with the grid built from WPF controls
        _gpuFailed = true;
        ReleaseGpu();
        Surface.DataContext = null;
        Scroller.Cursor = null;
        Arrange(true);
    }

    void WatchItems()
    {
        foreach (var w in _watched) w.PropertyChanged -= ItemChanged;
        _watched = GpuMode ? _cells.Select(c => c.Item).OfType<INotifyPropertyChanged>().ToList() : new();
        foreach (var w in _watched) w.PropertyChanged += ItemChanged;
    }

    void ItemChanged(object? sender, PropertyChangedEventArgs e) => QueueDraw();

    void ArrangeGpu()
    {
        Surface.Children.Clear();
        _rows.Clear();
        _spare.Clear();
        _toBuild.Clear();
        _cols = Math.Max(1, (int)((ActualWidth - 14) / TileWidth));
        _rowH = _style!.RowHeight;
        Surface.Background = Brushes.Transparent;      // the scrolling area is empty; it only has to take the mouse
        Surface.Height = (_cells.Count + _cols - 1) / _cols * _rowH;
        if (_hover >= _cells.Count) SetHover(-1);
        _epoch++;
        DrawGpu();
    }

    void DrawGpu()
    {
        if (!IsVisible || ActualWidth < 10 || ActualHeight < 10) return;
        // Before the page has settled the grid can briefly be measured as tall as its whole list. Drawing then would
        // mean a picture thousands of rows tall and every cover in the library loaded at once: wait for the real size.
        var limit = (Window.GetWindow(this)?.ActualHeight ?? 2400) + 200;
        if (ActualHeight > limit) { _stale = true; Dispatcher.BeginInvoke(() => { if (_stale) Arrange(true); }, System.Windows.Threading.DispatcherPriority.Loaded); return; }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _surface ??= new D3DSurface();
        var before = _surface.Image;
        if (!_surface.Resize((int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale), scale)) { FailGpu(); return; }
        if (!ReferenceEquals(before, _surface.Image)) { Layer.Surface = _surface; Layer.InvalidateVisual(); _epoch++; }
        _paintHeight = ActualHeight;
        _paintScale = scale;
        _painter ??= p => PaintTiles(p, _paintHeight, _paintScale);
        if (!_surface.Draw(_painter)) FailGpu();
    }

    Color Col(string key) => (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
    static readonly Halcyon.UI.Fill AccentGrad = Halcyon.UI.Fill.Right((Color.FromRgb(0xF0, 0x60, 0x3A), 0), (Color.FromRgb(0xD5, 0x35, 0x8F), 1));
    static readonly Color BadgeBack = Color.FromArgb(0xC0, 0, 0, 0);

    void PaintTiles(D2DPaint p, double height, double scale)
    {
        var style = _style!;
        var look = _look!;
        var offset = Scroller.VerticalOffset;
        var rows = (_cells.Count + _cols - 1) / _cols;
        var first = Math.Max(0, (int)(offset / _rowH));
        var last = Math.Min(rows - 1, (int)((offset + height) / _rowH));
        last = Math.Min(last, first + 60);   // never more than a few screens' worth, whatever the measurements say
        _gpuRows = Math.Max(0, last - first + 1);
        Color fg = Col("Fg"), dim = Col("FgDim"), faint = Col("FgFaint"), back = Col("BgAlt"), hover = Col("Hover"), selected = Col("Selected");
        double iw = style.ImageWidth, ih = style.ImageHeight;
        int pw = (int)Math.Round(iw * scale), ph = (int)Math.Round(ih * scale);
        double Snap(double v) => Math.Round(v * scale) / scale;

        for (var row = first; row <= last; row++)
        {
            var y = Snap(row * _rowH - offset);
            for (var col = 0; col < _cols; col++)
            {
                var index = row * _cols + col;
                if (index >= _cells.Count) break;
                var cell = _cells[index];
                if (cell.LookEpoch != _epoch)
                {
                    cell.Look = look(cell.Item);
                    cell.ArtKey = string.IsNullOrEmpty(cell.Look.Art) ? null : $"{cell.Look.Art}|{style.ArtSize}|{pw}x{ph}{(style.Fit ? "f" : "c")}";
                    cell.LookEpoch = _epoch;
                }
                var l = cell.Look;
                var x = col * TileWidth;
                if (cell.IsSelected) p.Rect(Halcyon.UI.Fill.Of(selected), new Rect(x, y, TileWidth, _rowH));
                else if (index == _hover) p.Rect(Halcyon.UI.Fill.Of(hover), new Rect(x, y, TileWidth, _rowH));

                var box = new Rect(x + 6, y + 6, iw, ih);
                p.Rect(Halcyon.UI.Fill.Of(back), box);
                Vortice.Direct2D1.ID2D1Bitmap? picture = null;
                if (cell.ArtKey != null && !D3DSurface.TryBitmap(cell.ArtKey, out picture)) RequestArt(cell.ArtKey, l.Art!, style.ArtSize, pw, ph, style.Fit);
                if (l.Glyph != "" && (picture == null || style.Fit))
                {
                    var g = p.Measure(l.Glyph, style.GlyphSize, D2DPaint.Icons);
                    p.Text(l.Glyph, new Point(box.X + (iw - g.Width) / 2, box.Y + (ih - g.Height) / 2), iw, style.GlyphSize, faint, D2DPaint.Icons);
                }
                if (picture != null)
                {
                    if (style.Fit)
                    {
                        double dw = Math.Min(iw, picture.PixelSize.Width / scale), dh = Math.Min(ih, picture.PixelSize.Height / scale);
                        p.Picture(picture, new Rect(Snap(box.X + (iw - dw) / 2), box.Bottom - dh, dw, dh));
                    }
                    else p.Picture(picture, box);
                }

                // the notes in the corners of the picture
                if (l.Badge != "")
                {
                    var t = p.Measure(l.Badge, 10);
                    var r = new Rect(box.Right - t.Width - 10, box.Bottom - t.Height - 2, t.Width + 10, t.Height + 2);
                    p.Rect(Halcyon.UI.Fill.Of(Color.FromArgb(0xB0, 0, 0, 0)), r);
                    p.Text(l.Badge, new Point(r.X + 5, r.Y + 1), iw, 10, Colors.White);
                }
                if (l.Counter != "")
                {
                    var t = p.Measure(l.Counter, 11, bold: true);
                    var r = new Rect(box.Right - t.Width - 14, box.Y + 6, t.Width + 14, t.Height + 6);
                    p.Rect(l.Counter == "read" ? AccentGrad : Halcyon.UI.Fill.Of(BadgeBack), r);
                    p.Text(l.Counter, new Point(r.X + 7, r.Y + 3), iw, 11, Colors.White, bold: true);
                }
                if (l.Audio)
                {
                    const string headphones = "";
                    var t = p.Measure(headphones, 12, D2DPaint.Icons);
                    var r = new Rect(box.X, box.Y + 6, t.Width + 12, t.Height + 6);
                    p.Rect(Halcyon.UI.Fill.Of(BadgeBack), r);
                    p.Text(headphones, new Point(r.X + 6, r.Y + 3), iw, 12, Colors.White, D2DPaint.Icons);
                }
                if (l.Progress > 0) p.Rect(AccentGrad, new Rect(box.X, box.Bottom - 4, Math.Min(1, l.Progress) * iw, 4));

                // the name, with its translation after it in a quieter colour, and the line under it
                if (style.Lines > 0 && l.Title != "")
                {
                    var ty = box.Bottom + 6;
                    var used = p.Text(l.Title, new Point(box.X, ty), iw, 13, fg);
                    if (style.Translate && Translator.Needs(l.Title))
                    {
                        var translated = Translator.Lookup(l.Title);
                        if (translated != "") p.Text(translated, new Point(box.X + used + 10, ty), iw - used - 10, 13, faint);
                        else if (_asked.Add(l.Title)) Ask(l.Title);
                    }
                }
                if (style.Lines > 1 && l.Subtitle != "") p.Text(l.Subtitle, new Point(box.X, box.Bottom + 6 + 17), iw, 12, dim);
            }
        }

        // covers for the two rows either side, so they are on the card before they glide into view
        for (var row = Math.Max(0, first - 2); row <= Math.Min(rows - 1, last + 2); row++)
        {
            if (row >= first && row <= last) continue;
            for (var index = row * _cols; index < Math.Min(_cells.Count, (row + 1) * _cols); index++)
            {
                var ahead = _cells[index];
                if (ahead.LookEpoch != _epoch)
                {
                    ahead.Look = look(ahead.Item);
                    ahead.ArtKey = string.IsNullOrEmpty(ahead.Look.Art) ? null : $"{ahead.Look.Art}|{style.ArtSize}|{pw}x{ph}{(style.Fit ? "f" : "c")}";
                    ahead.LookEpoch = _epoch;
                }
                if (ahead.ArtKey != null && !D3DSurface.TryBitmap(ahead.ArtKey, out _)) RequestArt(ahead.ArtKey, ahead.Look.Art!, style.ArtSize, pw, ph, style.Fit);
            }
        }
    }

    async void Ask(string text)
    {
        try { if (await Translator.GetAsync(text) != "") QueueDraw(); } catch { }
    }

    static readonly HashSet<string> Loading = new();
    /// <summary>Covers asked for so far (timing harness).</summary>
    public static int ArtRequests;

    /// <summary>Loads a cover, shrinks it to the size it is shown at (off the interface thread) and puts it on the card.</summary>
    async void RequestArt(string key, string art, int artSize, int pw, int ph, bool fit)
    {
        if (!Loading.Add(key)) return;
        ArtRequests++;
        try
        {
            (byte[]? pixels, int w, int h) made = default;
            if (await Art.LoadAsync(art, artSize) is BitmapSource source && source.IsFrozen) made = await Task.Run(() => Shrink(source, pw, ph, fit));
            D3DSurface.StoreBitmap(key, made.pixels, made.w, made.h);
        }
        catch { D3DSurface.StoreBitmap(key, null, 0, 0); }
        finally { Loading.Remove(key); }
        QueueDraw();
    }

    internal static (byte[]? pixels, int w, int h) Shrink(BitmapSource source, int pw, int ph, bool fit)
    {
        if (source.PixelWidth < 1 || source.PixelHeight < 1) return default;
        double sx = pw / (double)source.PixelWidth, sy = ph / (double)source.PixelHeight;
        var s = fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        BitmapSource scaled = Math.Abs(s - 1) < 0.01 ? source : new TransformedBitmap(source, new ScaleTransform(s, s));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        int w = Math.Min(bgra.PixelWidth, pw), h = Math.Min(bgra.PixelHeight, ph);
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(new Int32Rect((bgra.PixelWidth - w) / 2, (bgra.PixelHeight - h) / 2, w, h), pixels, w * 4, 0);
        return (pixels, w, h);
    }
}
