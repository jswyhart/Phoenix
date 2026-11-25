using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Halcyon.UI;

/// <summary>
/// Picks a colour: a square for how strong and how bright, a strip for the hue, and one for how see-through.
/// <see cref="Changed"/> fires while you drag.
/// </summary>
public sealed class ColourPicker : Grid
{
    const double Square = 210, Strip = 18;
    double _h, _s, _v;
    byte _a = 255;
    readonly Rectangle _hueFill = new() { Width = Square, Height = Square };
    readonly Ellipse _svThumb = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    readonly Rectangle _hueThumb = new() { Width = Strip + 6, Height = 4, Stroke = Brushes.White, StrokeThickness = 1.5, Fill = Brushes.Transparent, IsHitTestVisible = false };
    readonly Rectangle _alphaFill = new() { Width = Square + Strip + 10, Height = Strip };
    readonly Rectangle _alphaThumb = new() { Width = 4, Height = Strip + 6, Stroke = Brushes.White, StrokeThickness = 1.5, Fill = Brushes.Transparent, IsHitTestVisible = false };
    readonly Canvas _sv = new() { Width = Square, Height = Square, ClipToBounds = false, Background = Brushes.Transparent };
    readonly Canvas _hue = new() { Width = Strip, Height = Square, Background = Brushes.Transparent };
    readonly Canvas _alpha = new() { Width = Square + Strip + 10, Height = Strip, Background = Brushes.Transparent };

    public event Action<Color>? Changed;

    public Color Colour
    {
        get => FromHsv(_h, _s, _v, _a);
        set
        {
            var (h, s, v) = ToHsv(value);
            if (s > 0 && v > 0) _h = h;   // grey has no hue: keep the one the strip is on
            if (v > 0) _s = s;
            _v = v;
            _a = value.A;
            Show();
        }
    }

    public ColourPicker()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // strength runs left to right (white to the hue), brightness top to bottom (the colour to black)
        _sv.Children.Add(_hueFill);
        _sv.Children.Add(new Rectangle { Width = Square, Height = Square, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        _sv.Children.Add(new Rectangle { Width = Square, Height = Square, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        _sv.Children.Add(_svThumb);
        _svThumb.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 };
        Children.Add(_sv);

        var hues = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (var i = 0; i <= 6; i++) hues.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1, 255), i / 6.0));
        _hue.Children.Add(new Rectangle { Width = Strip, Height = Square, Fill = hues });
        _hue.Children.Add(_hueThumb);
        _hue.Margin = new Thickness(10, 0, 0, 0);
        SetColumn(_hue, 1);
        Children.Add(_hue);

        _alpha.Children.Add(new Rectangle { Width = _alpha.Width, Height = Strip, Fill = Checker() });
        _alpha.Children.Add(_alphaFill);
        _alpha.Children.Add(_alphaThumb);
        _alpha.Margin = new Thickness(0, 10, 0, 0);
        _alpha.ToolTip = "How see-through: left is clear, right is solid";
        SetRow(_alpha, 1);
        SetColumnSpan(_alpha, 2);
        Children.Add(_alpha);

        Drag(_sv, p => { _s = Math.Clamp(p.X / Square, 0, 1); _v = 1 - Math.Clamp(p.Y / Square, 0, 1); });
        Drag(_hue, p => _h = Math.Clamp(p.Y / Square, 0, 1) * 359.999);
        Drag(_alpha, p => _a = (byte)Math.Round(Math.Clamp(p.X / _alpha.Width, 0, 1) * 255));
        Show();
    }

    void Drag(Canvas area, Action<Point> set)
    {
        void At(MouseEventArgs e) { set(e.GetPosition(area)); Show(); Changed?.Invoke(Colour); }
        area.Cursor = Cursors.Cross;
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); At(e); e.Handled = true; };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) At(e); };
        area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
    }

    void Show()
    {
        _hueFill.Fill = new SolidColorBrush(FromHsv(_h, 1, 1, 255));
        Canvas.SetLeft(_svThumb, _s * Square - 6);
        Canvas.SetTop(_svThumb, (1 - _v) * Square - 6);
        Canvas.SetLeft(_hueThumb, -3);
        Canvas.SetTop(_hueThumb, _h / 360 * Square - 2);
        var solid = FromHsv(_h, _s, _v, 255);
        _alphaFill.Fill = new LinearGradientBrush(Color.FromArgb(0, solid.R, solid.G, solid.B), solid, 0);
        Canvas.SetLeft(_alphaThumb, _a / 255.0 * _alpha.Width - 2);
        Canvas.SetTop(_alphaThumb, -3);
    }

    /// <summary>Grey and white squares, to show through a see-through colour.</summary>
    public static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    public static Color FromHsv(double h, double s, double v, byte a)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        static byte B(double d) => (byte)Math.Round(Math.Clamp(d, 0, 1) * 255);
        return Color.FromArgb(a, B(r + m), B(g + m), B(b + m));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
