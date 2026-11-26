using Android.Content;
using Android.Views;

namespace Halcyon.Droid;

/// <summary>
/// Lays its children out left to right and starts a new line when one doesn't fit, so tabs, links and tiles never need
/// sideways scrolling. With <see cref="TileDp"/> set, every child gets the same width: as many columns as fit, filling
/// the line.
/// </summary>
public class FlowLayout : ViewGroup
{
    /// <summary>Tiles: the narrowest a column may be (dp); 0 lets each child keep its own width.</summary>
    public int TileDp;

    public FlowLayout(Context c) : base(c) { }

    int Columns(int width) => Math.Max(1, width / Math.Max(1, Th.Dp(TileDp)));

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var avail = MeasureSpec.GetSize(widthMeasureSpec) - PaddingLeft - PaddingRight;
        if (MeasureSpec.GetMode(widthMeasureSpec) == MeasureSpecMode.Unspecified) avail = int.MaxValue / 2;
        int x = 0, lineHeight = 0, height = 0, widest = 0;
        var tileWidth = TileDp > 0 ? avail / Columns(avail) : 0;
        for (var i = 0; i < ChildCount; i++)
        {
            var child = GetChildAt(i)!;
            if (child.Visibility == ViewStates.Gone) continue;
            var childWidthSpec = tileWidth > 0
                ? MeasureSpec.MakeMeasureSpec(tileWidth, MeasureSpecMode.Exactly)
                : MeasureSpec.MakeMeasureSpec(avail, MeasureSpecMode.AtMost);
            child.Measure(childWidthSpec, MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
            int w = child.MeasuredWidth, h = child.MeasuredHeight;
            if (x > 0 && x + w > avail) { height += lineHeight; x = 0; lineHeight = 0; }
            x += w;
            widest = Math.Max(widest, x);
            lineHeight = Math.Max(lineHeight, h);
        }
        height += lineHeight;
        var width = MeasureSpec.GetMode(widthMeasureSpec) == MeasureSpecMode.Exactly ? MeasureSpec.GetSize(widthMeasureSpec) : widest + PaddingLeft + PaddingRight;
        SetMeasuredDimension(width, height + PaddingTop + PaddingBottom);
    }

    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        var avail = r - l - PaddingLeft - PaddingRight;
        int x = 0, y = PaddingTop, lineHeight = 0;
        for (var i = 0; i < ChildCount; i++)
        {
            var child = GetChildAt(i)!;
            if (child.Visibility == ViewStates.Gone) continue;
            int w = child.MeasuredWidth, h = child.MeasuredHeight;
            if (x > 0 && x + w > avail) { y += lineHeight; x = 0; lineHeight = 0; }
            child.Layout(PaddingLeft + x, y, PaddingLeft + x + w, y + h);
            x += w;
            lineHeight = Math.Max(lineHeight, h);
        }
    }
}
