using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Mixview: one album or artist in the middle, with the artist's other albums and related artists from your
/// collection drifting around it. Click one and it moves to the middle with its own surroundings. Drawn by the
/// graphics card (Direct2D on a Direct3D 11 surface), one picture per frame.
/// </summary>
public class MixView : FrameworkElement
{
    public sealed class Node
    {
        public string Key = "", Title = "", Sub = "", Art = "", Artist = "";
        public Album? Album;
        public bool IsArtist => Album == null;
        public double X, Y, Size, Alpha;          // where it is drawn now
        public double Tx, Ty, TSize, TAlpha = 1;  // where it is heading
        public double Phase;
        public bool Leaving;
    }

    readonly List<Node> _nodes = new();
    Node? _centre, _hover;
    D3DSurface? _gpu;
    bool _running, _failed;
    TimeSpan _last;
    double _time;
    static readonly HashSet<string> Loading = new();

    public Node? Centre => _centre;
    public IReadOnlyList<Node> Nodes => _nodes;
    public int Frames => _gpu?.Frames ?? 0;
    public bool OnGpu => _gpu?.Ready == true;
    public BitmapSource? Snapshot() => _gpu?.Snapshot();
    /// <summary>Raised when the middle item changes (the page shows its name).</summary>
    public event Action? CentreChanged;

    public MixView()
    {
        Focusable = false;
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
        SizeChanged += (_, _) => Arrange();
        MouseMove += (_, e) => SetHover(NodeAt(e.GetPosition(this)));
        MouseLeave += (_, _) => SetHover(null);
        MouseLeftButtonDown += (_, e) =>
        {
            if (NodeAt(e.GetPosition(this)) is not { } n) return;
            if (e.ClickCount >= 2 || ReferenceEquals(n, _centre)) Play(n); else Recentre(n);
            e.Handled = true;
        };
        MouseRightButtonUp += (_, e) =>
        {
            if (NodeAt(e.GetPosition(this)) is not { } n) return;
            Menus.Open(n.Album != null ? Menus.ForAlbum(n.Album) : Menus.ForArtist(n.Artist), this);
            e.Handled = true;
        };
    }

    // a transparent surface still has to take the mouse
    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    void SetHover(Node? n)
    {
        if (ReferenceEquals(n, _hover)) return;
        _hover = n;
        Cursor = n != null ? Cursors.Hand : null;
        ToolTip = null;
    }

    public Node? NodeAt(Point p) => _nodes.Where(n => !n.Leaving && n.Alpha > 0.4).OrderByDescending(n => ReferenceEquals(n, _centre))
        .FirstOrDefault(n => Math.Abs(p.X - n.X) <= n.Size / 2 && p.Y >= n.Y - n.Size / 2 && p.Y <= n.Y + n.Size / 2 + 34);

    public static void Play(Node n)
    {
        if (n.Album != null) Menus.Play(n.Album.Tracks);
        else Menus.PlayShuffled(AlbumsOf(n.Artist).SelectMany(a => a.Tracks));
    }

    // ---------- what surrounds what ----------

    static bool Real(string artist) => artist.Trim() != "" && !artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) && !artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase);

    static List<Album> AlbumsOf(string artist) => App.Lib.Albums.Where(a => string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase)).ToList();

    static IEnumerable<string> GenresOf(IEnumerable<Album> albums) =>
        albums.SelectMany(a => a.Genre.Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Artists in the collection that go with one: the same genres first, then the same years, with liked music counting for more.</summary>
    public static List<string> RelatedArtists(string artist, int count)
    {
        var own = AlbumsOf(artist);
        var genres = GenresOf(own).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var years = own.Where(a => a.Year > 0).Select(a => a.Year).ToList();
        var year = years.Count > 0 ? years.Average() : 0;
        var rnd = new Random(artist.ToLowerInvariant().GetHashCode(StringComparison.Ordinal));
        return App.Lib.Albums.Where(a => Real(a.Artist) && !string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var shared = GenresOf(g).Count(genres.Contains);
                var theirs = g.Where(a => a.Year > 0).Select(a => a.Year).ToList();
                var gap = year > 0 && theirs.Count > 0 ? Math.Abs(theirs.Average() - year) : 99;
                var score = (shared > 0 ? 2.0 + 0.5 * Math.Min(2, shared - 1) : 0) + (gap <= 5 ? 1.0 : gap <= 12 ? 0.4 : 0)
                            + (g.Any(a => a.Tracks.Any(t => t.Rating > 0)) ? 0.5 : 0) + Math.Min(0.4, g.Sum(a => a.Tracks.Count) / 100.0) + rnd.NextDouble() * 0.7;
                return (artist: g.Key, score, any: shared > 0 || gap <= 5);
            }).Where(x => x.any).OrderByDescending(x => x.score).Take(count).Select(x => x.artist).ToList();
    }

    static Node ForAlbum(Album a) => new() { Key = "album:" + a.Key, Title = a.Title, Sub = a.Year > 0 ? $"{a.Artist}  ·  {a.Year}" : a.Artist, Art = a.ArtPath, Artist = a.Artist, Album = a };

    static Node ForArtist(string artist)
    {
        var albums = AlbumsOf(artist);
        return new Node { Key = "artist:" + artist.ToLowerInvariant(), Title = artist, Sub = albums.Count == 1 ? "artist  ·  1 album" : $"artist  ·  {albums.Count} albums", Artist = artist,
            Art = albums.OrderByDescending(a => a.Tracks.Sum(t => t.PlayCount)).Select(a => a.ArtPath).FirstOrDefault(p => p != "") ?? "" };
    }

    /// <summary>The middle item and everything around it, for an album or (album == null) an artist.</summary>
    public static (Node Centre, List<Node> Around) Build(Album? album, string artist)
    {
        var around = new List<Node>();
        Node centre;
        if (album != null)
        {
            centre = ForAlbum(album);
            if (Real(artist)) around.Add(ForArtist(artist));
            around.AddRange(AlbumsOf(artist).Where(a => a.Key != album.Key).OrderByDescending(a => a.Year).Take(5).Select(ForAlbum));
        }
        else
        {
            centre = ForArtist(artist);
            around.AddRange(AlbumsOf(artist).OrderByDescending(a => a.Year).Take(7).Select(ForAlbum));
        }
        if (Real(artist)) around.AddRange(RelatedArtists(artist, 14 - around.Count > 7 ? 7 : Math.Max(3, 13 - around.Count)).Select(ForArtist));
        return (centre, around);
    }

    public void Show(Album album) => Set(Build(album, album.Artist));
    public void Show(string artist) => Set(Build(null, artist));

    void Recentre(Node n)
    {
        if (n.Album != null) Show(n.Album); else Show(n.Artist);
    }

    void Set((Node Centre, List<Node> Around) built)
    {
        var old = _nodes.Where(n => !n.Leaving).ToDictionary(n => n.Key);
        var origin = _centre != null ? new Point(_centre.X, _centre.Y) : new Point(ActualWidth / 2, ActualHeight / 2);
        Node Take(Node fresh)
        {
            // something already on screen keeps its place and glides to the new one
            if (old.Remove(fresh.Key, out var was)) { was.Title = fresh.Title; was.Sub = fresh.Sub; was.Album = fresh.Album; was.Artist = fresh.Artist; was.Art = fresh.Art; return was; }
            fresh.X = origin.X; fresh.Y = origin.Y; fresh.Size = 40; fresh.Alpha = 0;
            fresh.Phase = Random.Shared.NextDouble() * 20;
            _nodes.Add(fresh);
            return fresh;
        }
        _centre = Take(built.Centre);
        var keep = new List<Node> { _centre };
        foreach (var n in built.Around) keep.Add(Take(n));
        foreach (var gone in old.Values) { gone.Leaving = true; gone.TAlpha = 0; }
        // the middle one is drawn last, on top
        _nodes.Remove(_centre);
        _nodes.Add(_centre);
        _around = keep.Skip(1).ToList();
        Arrange();
        _hover = null;
        CentreChanged?.Invoke();
    }

    List<Node> _around = new();

    void Arrange()
    {
        if (_centre == null) return;
        double w = Math.Max(300, ActualWidth), h = Math.Max(300, ActualHeight);
        var unit = Math.Clamp(Math.Min(w / 9.5, h / 5.6), 60, 132);
        _centre.Tx = w / 2; _centre.Ty = h * 0.45; _centre.TSize = unit * 1.75; _centre.TAlpha = 1;
        var count = _around.Count;
        for (var i = 0; i < count; i++)
        {
            var n = _around[i];
            // around an ellipse, every other one a little further in, so neighbours do not sit on each other
            // (nothing sits straight below the middle one: its name is written there)
            const double gap = Math.PI * 0.42;
            var angle = Math.PI / 2 + gap / 2 + (i + 0.5) * (2 * Math.PI - gap) / count;
            var reach = count <= 6 ? 0.86 : i % 2 == 0 ? 1.0 : 0.66;
            n.Tx = w / 2 + Math.Cos(angle) * (w / 2 - unit * 0.95) * reach;
            n.Ty = h * 0.45 + Math.Sin(angle) * (h * 0.45 - unit * 0.78) * reach;
            n.TSize = n.IsArtist ? unit * 0.86 : unit;
            n.TAlpha = 1;
        }
    }

    // ---------- frames ----------

    void Start()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
        FramePacer.Hold();
    }

    void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        FramePacer.Release();
        _gpu?.Dispose();
        _gpu = null;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || frame.RenderingTime == _last) return;
        var dt = _last == TimeSpan.Zero ? 0.016 : Math.Clamp((frame.RenderingTime - _last).TotalSeconds, 0.0005, 0.1);
        _last = frame.RenderingTime;
        Step(dt);
        if (!EnsureGpu()) return;
        if (!_gpu!.Draw(Paint)) { _failed = true; InvalidateVisual(); }
    }

    /// <summary>Moves everything on by a length of time (also used by the self-test).</summary>
    public void Step(double dt)
    {
        _time += dt;
        var ease = 1 - Math.Exp(-dt / 0.16);
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            var n = _nodes[i];
            n.X += (n.Tx - n.X) * ease; n.Y += (n.Ty - n.Y) * ease;
            n.Size += (n.TSize - n.Size) * ease;
            n.Alpha += (n.TAlpha - n.Alpha) * (1 - Math.Exp(-dt / 0.22));
            if (n.Leaving && n.Alpha < 0.02) _nodes.RemoveAt(i);
        }
    }

    bool EnsureGpu()
    {
        if (_failed || !_running) return false;
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10 || !D3DSurface.Available) return false;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _gpu ??= new D3DSurface();
        var before = _gpu.Image;
        if (!_gpu.Resize((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), scale)) { _failed = true; _gpu.Dispose(); _gpu = null; InvalidateVisual(); return false; }
        if (!ReferenceEquals(before, _gpu.Image)) InvalidateVisual();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_gpu is { Ready: true, Image: { } image })
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            dc.DrawImage(image, new Rect(0, 0, _gpu.PixelWidth / scale, _gpu.PixelHeight / scale));
        }
        else if (_failed || !D3DSurface.Available)
        {
            var text = new FormattedText("Mixview needs Direct3D 11, which is not available here.", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 15, (Brush)FindResource("FgDim"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(28, 40));
        }
    }

    static Color Res(string key) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : Colors.Gray;

    void Paint(D2DPaint p)
    {
        Color fg = Res("Fg"), dim = Res("FgDim"), line = Res("FgFaint"), back = Res("BgAlt"), accent = Color.FromRgb(0xF0, 0x44, 0x7C);
        // the drift: every tile wanders a little around its place, each to its own slow rhythm
        Point At(Node n) => ReferenceEquals(n, _centre) ? new Point(n.X, n.Y)
            : new Point(n.X + Math.Sin(_time * 0.42 + n.Phase) * 7, n.Y + Math.Cos(_time * 0.33 + n.Phase * 1.7) * 6);
        if (_centre != null)
        {
            var c = At(_centre);
            foreach (var n in _nodes)
                if (!ReferenceEquals(n, _centre))
                    p.Line(Color.FromArgb((byte)(70 * Math.Clamp(n.Alpha, 0, 1)), line.R, line.G, line.B), 1, c, At(n));
        }
        foreach (var n in _nodes)
        {
            var at = At(n);
            var size = n.Size * (ReferenceEquals(n, _hover) && !ReferenceEquals(n, _centre) ? 1.06 : 1);
            var box = new Rect(at.X - size / 2, at.Y - size / 2, size, size);
            p.PushOpacity(Math.Clamp(n.Alpha, 0, 1));
            p.Rect(Fill.Of(back), box);
            Vortice.Direct2D1.ID2D1Bitmap? picture = null;
            if (n.Art != "")
            {
                var key = "mix|" + n.Art;
                if (!D3DSurface.TryBitmap(key, out picture)) Request(key, n.Art);
            }
            if (picture != null) p.Picture(picture, box);
            else
            {
                var glyph = n.IsArtist ? "" : "";
                var g = p.Measure(glyph, 26, D2DPaint.Icons);
                p.Text(glyph, new Point(at.X - g.Width / 2, at.Y - g.Height / 2), 60, 26, line, D2DPaint.Icons);
            }
            if (ReferenceEquals(n, _hover) || ReferenceEquals(n, _centre))
            {
                var edge = ReferenceEquals(n, _hover) ? accent : Color.FromArgb(0x60, fg.R, fg.G, fg.B);
                p.Rect(Fill.Of(edge), new Rect(box.X - 2, box.Y - 2, box.Width + 4, 2));
                p.Rect(Fill.Of(edge), new Rect(box.X - 2, box.Bottom, box.Width + 4, 2));
                p.Rect(Fill.Of(edge), new Rect(box.X - 2, box.Y, 2, box.Height));
                p.Rect(Fill.Of(edge), new Rect(box.Right, box.Y, 2, box.Height));
            }
            var centre = ReferenceEquals(n, _centre);
            var width = Math.Max(size, centre ? 320 : 150);
            double titleSize = centre ? 17 : 12.5, subSize = centre ? 13 : 11;
            var tw = Math.Min(width, p.Measure(n.Title, titleSize, bold: centre, maxWidth: width).Width);
            p.Text(n.Title, new Point(at.X - tw / 2, box.Bottom + (centre ? 8 : 5)), width, titleSize, fg, bold: centre);
            var sw = Math.Min(width, p.Measure(n.Sub, subSize, maxWidth: width).Width);
            p.Text(n.Sub, new Point(at.X - sw / 2, box.Bottom + (centre ? 31 : 22)), width, subSize, dim);
            p.Pop();
        }
    }

    async void Request(string key, string art)
    {
        if (!Loading.Add(key)) return;
        try
        {
            (byte[]? pixels, int w, int h) made = default;
            if (await Art.LoadAsync(art, 500) is BitmapSource source && source.IsFrozen) made = await Task.Run(() => TileGrid.Shrink(source, 360, 360, false));
            D3DSurface.StoreBitmap(key, made.pixels, made.w, made.h);
        }
        catch { D3DSurface.StoreBitmap(key, null, 0, 0); }
        finally { Loading.Remove(key); }
    }
}

/// <summary>The Mixview page: the drifting picture with a heading over it.</summary>
public class MixviewPage : UserControl
{
    readonly MixView _mix = new();
    readonly TextBlock _hint = Make.Text("click a cover to move it to the middle  ·  double-click to play  ·  right-click for more", 12, "FgFaint", new Thickness(28, 0, 0, 14));

    public MixView Mix => _mix;

    public MixviewPage()
    {
        var head = Make.Styled("mixview", "ZuneHead", new Thickness(28, 8, 0, 0));
        head.IsHitTestVisible = false;
        _hint.VerticalAlignment = VerticalAlignment.Bottom;
        _hint.IsHitTestVisible = false;
        head.VerticalAlignment = VerticalAlignment.Top;
        var grid = new Grid();
        grid.Children.Add(_mix);
        grid.Children.Add(head);
        grid.Children.Add(_hint);
        Content = grid;
    }

    public void Show(Album album) => _mix.Show(album);
    public void Show(string artist) => _mix.Show(artist);

    /// <summary>Opens Mixview on an album or an artist.</summary>
    public static void Open(Album? album, string artist)
    {
        App.Win.Navigate("mixview");
        var page = App.Win.Page<MixviewPage>("mixview");
        if (album != null) page.Show(album); else page.Show(artist);
    }
}
