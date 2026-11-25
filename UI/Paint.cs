using System.Windows;
using System.Windows.Media;

namespace Halcyon.UI;

/// <summary>What a shape is filled or stroked with: one colour, or a gradient across the shape's own bounds.</summary>
public readonly struct Fill
{
    public readonly Color Solid;
    public readonly (Color colour, double at)[]? Stops;
    /// <summary>The gradient runs left to right; otherwise bottom to top.</summary>
    public readonly bool Across;

    Fill(Color solid, (Color, double)[]? stops, bool across) { Solid = solid; Stops = stops; Across = across; }
    public static Fill Of(Color c) => new(c, null, false);
    public static Fill Of(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B), null, false);
    public static Fill Up(params (Color, double)[] stops) => new(default, stops, false);
    public static Fill Right(params (Color, double)[] stops) => new(default, stops, true);
}

/// <summary>An outline made of straight lines, curves and arcs.</summary>
public sealed class Shape
{
    public enum Kind : byte { Line, Bezier, Arc }
    public Point Start;
    public bool Filled, Closed;
    public readonly List<(Kind kind, Point a, Point b, Point c, double radius)> Parts = new();

    public Shape(Point start, bool filled, bool closed, int capacity = 16) { Start = start; Filled = filled; Closed = closed; Parts.Capacity = capacity; }
    public void Line(Point p) => Parts.Add((Kind.Line, p, default, default, 0));
    public void Bezier(Point c1, Point c2, Point p) => Parts.Add((Kind.Bezier, c1, c2, p, 0));
    /// <summary>A clockwise arc of a circle, the short way round.</summary>
    public void Arc(Point p, double radius) => Parts.Add((Kind.Arc, p, default, default, radius));
}

/// <summary>
/// The handful of drawing operations the visualizer needs, so its styles are written once and can be drawn either by
/// the graphics card through Direct3D 11 (<see cref="D3DSurface"/>) or by WPF's own drawing (the fallback).
/// </summary>
public abstract class Paint
{
    public abstract void Rect(Fill fill, Rect r);
    public abstract void Ellipse(Fill? fill, Fill? stroke, double width, Point centre, double rx, double ry);
    public abstract void Line(Color colour, double width, Point a, Point b, bool round = true);
    public abstract void Path(Shape shape, Fill? fill, Fill? stroke, double width = 1, bool round = true);
    public abstract void PushOpacity(double opacity);
    public abstract void PushClip(Rect r);
    /// <summary>Mirror what follows top to bottom about a horizontal line.</summary>
    public abstract void PushFlip(double y);
    public abstract void Pop();
    /// <summary>A short label, centred on <paramref name="topCentre"/>.</summary>
    public abstract void Label(string text, Point topCentre, double size, Color colour);
}

/// <summary>Drawing through WPF (used when Direct3D 11 is not available, for instance over remote desktop).</summary>
public sealed class WpfPaint : Paint
{
    readonly DrawingContext _dc;
    readonly double _pixelsPerDip;
    public WpfPaint(DrawingContext dc, double pixelsPerDip) { _dc = dc; _pixelsPerDip = pixelsPerDip; }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static Brush BrushOf(Fill f)
    {
        if (f.Stops == null) return Frozen(new SolidColorBrush(f.Solid));
        var stops = new GradientStopCollection(f.Stops.Select(s => new GradientStop(s.colour, s.at)));
        return f.Across ? Frozen(new LinearGradientBrush(stops, 0)) : Frozen(new LinearGradientBrush(stops, new Point(0, 1), new Point(0, 0)));
    }

    static Pen PenOf(Fill f, double width, bool round) => Frozen(round
        ? new Pen(BrushOf(f), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }
        : new Pen(BrushOf(f), width));

    public override void Rect(Fill fill, Rect r) => _dc.DrawRectangle(BrushOf(fill), null, r);

    public override void Ellipse(Fill? fill, Fill? stroke, double width, Point centre, double rx, double ry) =>
        _dc.DrawEllipse(fill is { } f ? BrushOf(f) : null, stroke is { } s ? PenOf(s, width, true) : null, centre, rx, ry);

    public override void Line(Color colour, double width, Point a, Point b, bool round = true) => _dc.DrawLine(PenOf(Fill.Of(colour), width, round), a, b);

    public override void Path(Shape shape, Fill? fill, Fill? stroke, double width = 1, bool round = true)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(shape.Start, shape.Filled, shape.Closed);
            foreach (var (kind, a, b, c, radius) in shape.Parts)
            {
                if (kind == Shape.Kind.Line) g.LineTo(a, true, true);
                else if (kind == Shape.Kind.Bezier) g.BezierTo(a, b, c, true, true);
                else g.ArcTo(a, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        geo.Freeze();
        _dc.DrawGeometry(fill is { } f ? BrushOf(f) : null, stroke is { } s ? PenOf(s, width, round) : null, geo);
    }

    public override void PushOpacity(double opacity) => _dc.PushOpacity(opacity);
    public override void PushClip(Rect r) => _dc.PushClip(new RectangleGeometry(r));
    public override void PushFlip(double y) => _dc.PushTransform(new ScaleTransform(1, -1, 0, y));
    public override void Pop() => _dc.Pop();

    public override void Label(string text, Point topCentre, double size, Color colour)
    {
        var label = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
            size, Frozen(new SolidColorBrush(colour)), _pixelsPerDip);
        _dc.DrawText(label, new Point(topCentre.X - label.Width / 2, topCentre.Y));
    }
}
