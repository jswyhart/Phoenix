using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using APath = Android.Graphics.Path;

namespace Halcyon.Droid;

/// <summary>Theme colours (the same palette as the Windows app) and small view-building helpers.</summary>
public static class Th
{
    public static bool Dark;
    public static Color Bg, BgAlt, Fg, FgDim, FgFaint, Line, Selected;
    public static readonly Color Accent = Color.ParseColor("#E0447C");
    public static float Density = 1, Scale = 1;
    public static int ScreenW = 1080, ScreenH = 2400;   // portrait width and height in pixels

    public static void Apply(string name, Context c)
    {
        // "system" is light or dark as the phone is set (dark theme in Android's display settings), and follows it
        Dark = name == "dark" || (name == "system" && SystemIsDark(c));
        // Everything is sized in "design dp": real dp times a factor that grows with the screen, so the layout fills a
        // large phone the way it fills a small one. Settings can override the automatic choice.
        var dm = c.Resources!.DisplayMetrics!;
        ScreenW = Math.Min(dm.WidthPixels, dm.HeightPixels);
        ScreenH = Math.Max(dm.WidthPixels, dm.HeightPixels);
        var auto = Math.Clamp(ScreenW / dm.Density / 380f, 1f, 1.35f) * 1.08f;
        Scale = Halcyon.Core.App.Settings.UiSize switch { 1 => 0.9f, 2 => 1f, 3 => 1.2f, 4 => 1.4f, _ => auto };
        Density = dm.Density * Scale;
        Bg = Color.ParseColor(Dark ? "#141414" : "#FFFFFF");
        BgAlt = Color.ParseColor(Dark ? "#1E1E1E" : "#F3F3F3");
        Fg = Color.ParseColor(Dark ? "#F2F2F2" : "#1A1A1A");
        FgDim = Color.ParseColor(Dark ? "#A6A6A6" : "#666666");
        FgFaint = Color.ParseColor(Dark ? "#6A6A6A" : "#A9A9A9");
        Line = Color.ParseColor(Dark ? "#2E2E2E" : "#E2E2E2");
        Selected = Color.ParseColor(Dark ? "#26FFFFFF" : "#1C000000");
    }

    public static bool SystemIsDark(Context c) =>
        (c.Resources!.Configuration!.UiMode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes;

    public static int Dp(float dp) => (int)(dp * Density + 0.5f);

    public static readonly Typeface Light = Typeface.Create("sans-serif-light", TypefaceStyle.Normal)!;
    public static readonly Typeface Regular = Typeface.Create("sans-serif", TypefaceStyle.Normal)!;
    public static readonly Typeface Medium = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal)!;
}

public static class Ui
{
    public static TextView Text(Context c, string text, float sp, Color color, Typeface? face = null, int maxLines = 1)
    {
        var t = new TextView(c) { Text = text };
        t.SetTextSize(ComplexUnitType.Sp, sp * Th.Scale);
        t.SetTextColor(color);
        t.Typeface = face ?? Th.Regular;
        if (maxLines > 0) { t.SetMaxLines(maxLines); t.Ellipsize = TextUtils.TruncateAt.End; }
        return t;
    }

    /// <summary>Small spaced capitals, used above lists ("ALBUMS  12").</summary>
    public static TextView Caption(Context c, string text)
    {
        var t = Text(c, text, 11, Th.FgDim, Th.Medium);
        t.LetterSpacing = 0.12f;
        t.SetPadding(Th.Dp(16), Th.Dp(14), Th.Dp(16), Th.Dp(6));
        return t;
    }

    /// <summary>A borderless lowercase text button.</summary>
    public static TextView Link(Context c, string text, Action click, bool accent = false)
    {
        var t = Text(c, text, 15, accent ? Th.Accent : Th.Fg, Th.Regular);
        t.SetPadding(Th.Dp(12), Th.Dp(9), Th.Dp(12), Th.Dp(9));
        t.Background = Ripple();
        t.Clickable = true;
        t.Click += (_, _) => click();
        return t;
    }

    public static ImageView Icon(Context c, IconKind kind, Color color, int sizeDp, Action? click = null, int padDp = 10)
    {
        var v = new ImageView(c);
        v.SetImageDrawable(new IconDrawable(kind, color));
        v.SetPadding(Th.Dp(padDp), Th.Dp(padDp), Th.Dp(padDp), Th.Dp(padDp));
        v.LayoutParameters = new ViewGroup.LayoutParams(Th.Dp(sizeDp + padDp * 2), Th.Dp(sizeDp + padDp * 2));
        if (click != null)
        {
            v.Background = Ripple(true);
            v.Clickable = true;
            v.Click += (_, _) => click();
        }
        return v;
    }

    public static Drawable Ripple(bool round = false)
    {
        var mask = new GradientDrawable();
        mask.SetColor(Color.White);
        if (round) mask.SetShape(ShapeType.Oval);
        return new RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Th.Selected), null, mask);
    }

    public static EditText Input(Context c, string hint, string text = "", bool password = false, bool number = false)
    {
        var e = new EditText(c) { Hint = hint, Text = text };
        e.SetSingleLine(true);
        e.SetTextSize(ComplexUnitType.Sp, 16 * Th.Scale);
        e.SetTextColor(Th.Fg);
        e.SetHintTextColor(Th.FgFaint);
        e.InputType = password ? InputTypes.ClassText | InputTypes.TextVariationPassword
            : number ? InputTypes.ClassNumber
            : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        return e;
    }

    public static LinearLayout Column(Context c, params View[] children)
    {
        var l = new LinearLayout(c) { Orientation = Orientation.Vertical };
        foreach (var v in children) l.AddView(v);
        return l;
    }

    public static LinearLayout RowOf(Context c, params View[] children)
    {
        var l = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        l.SetGravity(GravityFlags.CenterVertical);
        foreach (var v in children) l.AddView(v);
        return l;
    }

    public static LinearLayout.LayoutParams Fill(int height = ViewGroup.LayoutParams.WrapContent) => new(ViewGroup.LayoutParams.MatchParent, height);
    public static LinearLayout.LayoutParams Weight(float w = 1) => new(0, ViewGroup.LayoutParams.WrapContent, w);
    public static LinearLayout.LayoutParams Wrap() => new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);

    public static void Toast(Context c, string text) => Android.Widget.Toast.MakeText(c, text, ToastLength.Long)!.Show();

    // ---------- dialogs ----------

    public static void Menu(Context c, string? title, params (string label, Action act)[] items)
    {
        var b = new AlertDialog.Builder(c);
        if (!string.IsNullOrEmpty(title)) b.SetTitle(title);
        b.SetItems(items.Select(i => i.label).ToArray(), (_, e) => items[e.Which].act());
        b.Show();
    }

    public static void Ask(Context c, string title, string hint, string initial, Action<string> ok, bool password = false)
    {
        var input = Input(c, hint, initial, password);
        var box = new FrameLayout(c);
        box.SetPadding(Th.Dp(20), Th.Dp(8), Th.Dp(20), 0);
        box.AddView(input);
        new AlertDialog.Builder(c).SetTitle(title)!.SetView(box)!
            .SetPositiveButton("OK", (_, _) => ok(input.Text ?? ""))!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
        input.SetSelection(input.Text?.Length ?? 0);
    }

    public static void Confirm(Context c, string message, string yes, Action ok) =>
        new AlertDialog.Builder(c).SetMessage(message)!.SetPositiveButton(yes, (_, _) => ok())!.SetNegativeButton("Cancel", (_, _) => { })!.Show();

    public static void Alert(Context c, string message) =>
        new AlertDialog.Builder(c).SetMessage(message)!.SetPositiveButton("OK", (_, _) => { })!.Show();

    /// <summary>A simple folder browser (the system document picker hands out content URIs, which the tag reader can't open).</summary>
    public static void PickFolder(Context c, string start, Action<string> picked)
    {
        var dir = Directory.Exists(start) ? start : Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath;
        List<string> subs;
        try { subs = Directory.GetDirectories(dir).Where(d => !System.IO.Path.GetFileName(d).StartsWith('.')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { subs = new(); }
        var parent = System.IO.Path.GetDirectoryName(dir);
        var labels = new List<string>();
        if (!string.IsNullOrEmpty(parent) && parent.Length > 1) labels.Add("↑  up");
        var offset = labels.Count;
        labels.AddRange(subs.Select(s => System.IO.Path.GetFileName(s)!));
        new AlertDialog.Builder(c).SetTitle(dir)!
            .SetItems(labels.ToArray(), (_, e) => PickFolder(c, e.Which < offset ? parent! : subs[e.Which - offset], picked))!
            .SetPositiveButton("Use this folder", (_, _) => picked(dir))!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
    }
}

public enum IconKind { Play, Pause, Next, Prev, Heart, HeartFull, HeartBroken, Shuffle, Repeat, RepeatOne, Search, Back, Down, More, Queue, Close, Bars, Plus, Refresh, Bookmark }

/// <summary>Line icons drawn in code on a 24×24 grid, so the app ships no icon font or bitmap assets.</summary>
public class IconDrawable : Drawable
{
    readonly IconKind _kind;
    readonly Paint _fill = new(PaintFlags.AntiAlias), _stroke = new(PaintFlags.AntiAlias);

    public IconDrawable(IconKind kind, Color color)
    {
        _kind = kind;
        _fill.Color = color;
        _fill.SetStyle(Paint.Style.Fill);
        _stroke.Color = color;
        _stroke.SetStyle(Paint.Style.Stroke);
        _stroke.StrokeWidth = 1.9f;
        _stroke.StrokeCap = Paint.Cap.Round;
        _stroke.StrokeJoin = Paint.Join.Round;
    }

    static APath Poly(bool close, params float[] xy)
    {
        var p = new APath();
        p.MoveTo(xy[0], xy[1]);
        for (var i = 2; i < xy.Length; i += 2) p.LineTo(xy[i], xy[i + 1]);
        if (close) p.Close();
        return p;
    }

    static APath HeartPath()
    {
        var p = new APath();
        p.MoveTo(12, 20.5f);
        p.CubicTo(4.5f, 14.5f, 2.5f, 11.5f, 2.5f, 8.2f);
        p.CubicTo(2.5f, 5.3f, 4.7f, 3.3f, 7.3f, 3.3f);
        p.CubicTo(9.2f, 3.3f, 11, 4.4f, 12, 6.2f);
        p.CubicTo(13, 4.4f, 14.8f, 3.3f, 16.7f, 3.3f);
        p.CubicTo(19.3f, 3.3f, 21.5f, 5.3f, 21.5f, 8.2f);
        p.CubicTo(21.5f, 11.5f, 19.5f, 14.5f, 12, 20.5f);
        p.Close();
        return p;
    }

    public override void Draw(Canvas canvas)
    {
        var b = Bounds;
        var s = Math.Min(b.Width(), b.Height()) / 24f;
        canvas.Save();
        canvas.Translate(b.Left + (b.Width() - 24 * s) / 2, b.Top + (b.Height() - 24 * s) / 2);
        canvas.Scale(s, s);
        switch (_kind)
        {
            case IconKind.Play: canvas.DrawPath(Poly(true, 8, 4.5f, 20, 12, 8, 19.5f), _fill); break;
            case IconKind.Pause: canvas.DrawRect(6, 5, 10, 19, _fill); canvas.DrawRect(14, 5, 18, 19, _fill); break;
            case IconKind.Next: canvas.DrawPath(Poly(true, 5, 5.5f, 15, 12, 5, 18.5f), _fill); canvas.DrawRect(16.5f, 5.5f, 19, 18.5f, _fill); break;
            case IconKind.Prev: canvas.DrawPath(Poly(true, 19, 5.5f, 9, 12, 19, 18.5f), _fill); canvas.DrawRect(5, 5.5f, 7.5f, 18.5f, _fill); break;
            case IconKind.Heart: canvas.DrawPath(HeartPath(), _stroke); break;
            case IconKind.HeartFull: canvas.DrawPath(HeartPath(), _fill); break;
            case IconKind.HeartBroken:
                canvas.DrawPath(HeartPath(), _stroke);
                canvas.DrawPath(Poly(false, 12, 6.2f, 10, 10, 13.5f, 12.5f, 11, 16, 12, 20), _stroke);
                break;
            case IconKind.Shuffle:
                canvas.DrawPath(Poly(false, 3.5f, 7, 8, 7, 15, 17, 20, 17), _stroke);
                canvas.DrawPath(Poly(false, 3.5f, 17, 8, 17, 15, 7, 20, 7), _stroke);
                canvas.DrawPath(Poly(false, 17.5f, 4.5f, 20, 7, 17.5f, 9.5f), _stroke);
                canvas.DrawPath(Poly(false, 17.5f, 14.5f, 20, 17, 17.5f, 19.5f), _stroke);
                break;
            case IconKind.Repeat:
            case IconKind.RepeatOne:
                canvas.DrawPath(Poly(false, 5, 11, 5, 7.5f, 19, 7.5f), _stroke);
                canvas.DrawPath(Poly(false, 16.5f, 5, 19, 7.5f, 16.5f, 10), _stroke);
                canvas.DrawPath(Poly(false, 19, 13, 19, 16.5f, 5, 16.5f), _stroke);
                canvas.DrawPath(Poly(false, 7.5f, 14, 5, 16.5f, 7.5f, 19), _stroke);
                if (_kind == IconKind.RepeatOne) canvas.DrawPath(Poly(false, 11, 10.5f, 12.3f, 9.8f, 12.3f, 14.3f), _stroke);
                break;
            case IconKind.Search: canvas.DrawCircle(10.5f, 10.5f, 6, _stroke); canvas.DrawLine(15, 15, 20, 20, _stroke); break;
            case IconKind.Back: canvas.DrawPath(Poly(false, 14.5f, 5, 7.5f, 12, 14.5f, 19), _stroke); break;
            case IconKind.Down: canvas.DrawPath(Poly(false, 5, 9, 12, 16, 19, 9), _stroke); break;
            case IconKind.More: canvas.DrawCircle(12, 5.5f, 1.6f, _fill); canvas.DrawCircle(12, 12, 1.6f, _fill); canvas.DrawCircle(12, 18.5f, 1.6f, _fill); break;
            case IconKind.Queue: canvas.DrawLine(4, 7, 20, 7, _stroke); canvas.DrawLine(4, 12, 20, 12, _stroke); canvas.DrawLine(4, 17, 14, 17, _stroke); break;
            case IconKind.Close: canvas.DrawLine(6, 6, 18, 18, _stroke); canvas.DrawLine(18, 6, 6, 18, _stroke); break;
            case IconKind.Bars: canvas.DrawRect(4, 12, 7, 19, _fill); canvas.DrawRect(8.5f, 6, 11.5f, 19, _fill); canvas.DrawRect(13, 10, 16, 19, _fill); canvas.DrawRect(17.5f, 14, 20.5f, 19, _fill); break;
            case IconKind.Plus: canvas.DrawLine(12, 5, 12, 19, _stroke); canvas.DrawLine(5, 12, 19, 12, _stroke); break;
            case IconKind.Bookmark: canvas.DrawPath(Poly(true, 7, 4, 17, 4, 17, 20, 12, 15.5f, 7, 20), _stroke); break;
            case IconKind.Refresh:
                canvas.DrawArc(5, 5, 19, 19, -50, 290, false, _stroke);
                canvas.DrawPath(Poly(false, 14, 4, 17.5f, 6.8f, 14.5f, 9.8f), _stroke);
                break;
        }
        canvas.Restore();
    }

    public override void SetAlpha(int alpha) { _fill.Alpha = alpha; _stroke.Alpha = alpha; }
    public override void SetColorFilter(ColorFilter? colorFilter) { }
    public override int Opacity => (int)Format.Translucent;
}
