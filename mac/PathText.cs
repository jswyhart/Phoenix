using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Halcyon.Desktop;

/// <summary>
/// One line showing a folder path. A path too long for its box keeps its start and as many whole trailing folders
/// as fit, with three plain dots between ("/Users/.../Albums/Live"). The dots are ordinary full stops rather than
/// the single ellipsis character, which not every font and platform draws.
/// </summary>
public sealed class PathText : Control
{
    public static readonly StyledProperty<string?> PathProperty = AvaloniaProperty.Register<PathText, string?>(nameof(Path));
    public string? Path { get => GetValue(PathProperty); set => SetValue(PathProperty, value); }

    /// <summary>The text as last drawn (for the self-test).</summary>
    public string Shown { get; private set; } = "";
    /// <summary>Width of the text as last drawn.</summary>
    public double ShownWidth { get; private set; }

    static PathText()
    {
        AffectsMeasure<PathText>(PathProperty, TextElement.FontSizeProperty, TextElement.FontFamilyProperty);
        AffectsRender<PathText>(PathProperty, TextElement.ForegroundProperty);
    }

    FormattedText Layout(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this)),
        TextElement.GetFontSize(this), TextElement.GetForeground(this) ?? Brushes.Gray);

    protected override Size MeasureOverride(Size available)
    {
        var full = Layout(Path ?? "");
        return new Size(double.IsInfinity(available.Width) ? full.Width : Math.Min(full.Width, available.Width), full.Height);
    }

    public override void Render(DrawingContext context)
    {
        var text = Fit(Path ?? "", Bounds.Width);
        var layout = Layout(text);
        Shown = text;
        ShownWidth = layout.Width;
        context.DrawText(layout, new Point(0, Math.Max(0, (Bounds.Height - layout.Height) / 2)));
    }

    /// <summary>The path, shortened if need be so it is no wider than the space given.</summary>
    public string Fit(string path, double width)
    {
        bool Fits(string s) => Layout(s).Width <= width + 0.5;
        if (path == "" || Fits(path)) return path;
        const char back = (char)92;   // backslash
        var sep = path.Contains(back) && !path.Contains('/') ? back : '/';
        var parts = path.Split(sep);
        // the start of the path: the root and the first folder ("/Users", "C:/Music" on Windows with its own separator)
        var headCount = parts[0] == "" || parts[0].EndsWith(':') ? 2 : 1;
        if (parts.Length > headCount + 1)
        {
            var head = string.Join(sep, parts.Take(headCount));
            for (var tail = parts.Length - headCount - 1; tail >= 1; tail--)
            {
                var s = head + sep + "..." + sep + string.Join(sep, parts.Skip(parts.Length - tail));
                if (Fits(s)) return s;
            }
            // not even the last folder fits beside the start: drop the start
            for (var tail = Math.Min(2, parts.Length - 1); tail >= 1; tail--)
            {
                var s = "..." + sep + string.Join(sep, parts.Skip(parts.Length - tail));
                if (Fits(s)) return s;
            }
        }
        // a single name wider than the box: keep as much of its end as fits
        var last = parts[^1];
        for (var skip = 1; skip < last.Length; skip++)
            if (Fits("..." + last[skip..])) return "..." + last[skip..];
        return "...";
    }
}
