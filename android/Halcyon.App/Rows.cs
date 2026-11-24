using Android.Content;
using Android.Graphics;
using Android.Views;
using Halcyon.Core;

namespace Halcyon.Droid;

public enum RowKind { Item, Caption, Strip, Text, Menu }

/// <summary>One line of any list or one tile of any grid; every screen is just a function that produces these.</summary>
public class Row
{
    public RowKind Kind;
    public string Title = "", Sub = "", Trail = "", Lead = "", Art = "";
    public int ArtShape;              // 0 no picture, 1 square, 2 poster (2:3), 3 wide (16:9)
    public Action? Click, Long;
    public Track? Rate;               // shows the like/dislike heart for this song
    public Func<bool>? IsCurrent;     // drawn in the accent colour while true
    public bool Dim;
    public List<Row>? Strip;          // Kind == Strip: tiles in a sideways-scrolling row

    public static Row Caption(string text) => new() { Kind = RowKind.Caption, Title = text };
    public static Row Note(string text) => new() { Kind = RowKind.Text, Title = text };
}

/// <summary>What a page shows right now: a list, or a grid when GridDp (the tile width) is set.</summary>
public record Content(List<Row> Rows, int GridDp = 0, float Ratio = 1f, string Empty = "");

/// <summary>An image that keeps a fixed height-to-width ratio.</summary>
public class AspectImage : ImageView
{
    public float Ratio = 1f;
    public AspectImage(Context c) : base(c) => SetScaleType(ScaleType.CenterCrop);
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var w = MeasureSpec.GetSize(widthMeasureSpec);
        SetMeasuredDimension(w, (int)(w * Ratio));
    }
}

public class TileView : LinearLayout
{
    readonly AspectImage _art;
    readonly TextView _title, _sub;
    Row? _row;

    public TileView(Context c) : base(c)
    {
        Orientation = Orientation.Vertical;
        SetPadding(Th.Dp(6), Th.Dp(6), Th.Dp(6), Th.Dp(8));
        _art = new AspectImage(c);
        _title = Ui.Text(c, "", 13, Th.Fg);
        _sub = Ui.Text(c, "", 12, Th.FgDim);
        AddView(_art, Ui.Fill());
        _title.SetPadding(0, Th.Dp(5), 0, 0);
        AddView(_title, Ui.Fill());
        AddView(_sub, Ui.Fill());
        Background = Ui.Ripple();
        Click += (_, _) => _row?.Click?.Invoke();
        LongClick += (_, e) => { e.Handled = _row?.Long != null; _row?.Long?.Invoke(); };
    }

    public void Bind(Row r, float ratio, int widthPx)
    {
        _row = r;
        _art.Ratio = ratio;
        _title.Text = r.Title;
        _sub.Text = r.Sub;
        _sub.Visibility = r.Sub == "" ? ViewStates.Gone : ViewStates.Visible;
        Art.Into(_art, r.Art, Math.Max(widthPx, Th.Dp(120)));
    }
}

public class RowView : LinearLayout
{
    readonly TextView _lead, _title, _sub, _trail;
    readonly ImageView _art, _heart;
    Row? _row;

    public RowView(Context c) : base(c)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        SetPadding(Th.Dp(16), Th.Dp(7), Th.Dp(6), Th.Dp(7));
        SetMinimumHeight(Th.Dp(52));
        _lead = Ui.Text(c, "", 14, Th.FgFaint);
        _art = new ImageView(c);
        _art.SetScaleType(ImageView.ScaleType.CenterCrop);
        _title = Ui.Text(c, "", 16, Th.Fg);
        _sub = Ui.Text(c, "", 13, Th.FgDim, null, 3);
        _trail = Ui.Text(c, "", 13, Th.FgDim);
        _heart = new ImageView(c);
        _heart.SetPadding(Th.Dp(10), Th.Dp(10), Th.Dp(10), Th.Dp(10));
        _heart.Background = Ui.Ripple(true);
        _heart.Click += (_, _) => { if (_row?.Rate is { } t) App.Lib.Rate(new[] { t }, t.Rating == 0 ? 1 : t.Rating == 1 ? -1 : 0); };

        AddView(_lead, new LayoutParams(Th.Dp(36), ViewGroup.LayoutParams.WrapContent));
        AddView(_art);
        var text = Ui.Column(c, _title, _sub);
        text.SetPadding(0, 0, Th.Dp(8), 0);
        AddView(text, Ui.Weight());
        AddView(_trail, Ui.Wrap());
        AddView(_heart, new LayoutParams(Th.Dp(42), Th.Dp(42)));
        var spacer = new View(c);
        AddView(spacer, new LayoutParams(Th.Dp(10), 1));

        Background = Ui.Ripple();
        Click += (_, _) => _row?.Click?.Invoke();
        LongClick += (_, e) => { e.Handled = _row?.Long != null; _row?.Long?.Invoke(); };
    }

    public void Bind(Row r)
    {
        _row = r;
        var current = r.IsCurrent?.Invoke() == true;
        _title.Text = r.Title;
        _title.SetTextColor(current ? Th.Accent : r.Dim ? Th.FgFaint : Th.Fg);
        _sub.Text = r.Sub;
        _sub.Visibility = r.Sub == "" ? ViewStates.Gone : ViewStates.Visible;
        _trail.Text = r.Trail;
        _lead.Text = r.Lead;
        _lead.Visibility = r.Lead == "" ? ViewStates.Gone : ViewStates.Visible;

        if (r.ArtShape == 0) _art.Visibility = ViewStates.Gone;
        else
        {
            int h = Th.Dp(r.ArtShape == 2 ? 66 : 46), w = r.ArtShape == 2 ? Th.Dp(44) : r.ArtShape == 3 ? Th.Dp(82) : h;
            _art.Visibility = ViewStates.Visible;
            _art.LayoutParameters = new LayoutParams(w, h) { RightMargin = Th.Dp(12) };
            Art.Into(_art, r.Art, Th.Dp(96));
        }

        if (r.Rate is { } t)
        {
            _heart.Visibility = ViewStates.Visible;
            _heart.SetImageDrawable(new IconDrawable(t.Rating > 0 ? IconKind.HeartFull : t.Rating < 0 ? IconKind.HeartBroken : IconKind.Heart,
                t.Rating > 0 ? Th.Accent : t.Rating < 0 ? Th.FgDim : Th.FgFaint));
        }
        else _heart.Visibility = ViewStates.Gone;
    }
}

/// <summary>A click listener that can be replaced each time a recycled view is bound to another row.</summary>
public class ClickAction(Action act) : Java.Lang.Object, View.IOnClickListener
{
    public void OnClick(View? v) => act();
}

public class RowAdapter : BaseAdapter
{
    readonly Context _c;
    List<Row> _rows = new();
    public float Ratio = 1f;
    public bool Tiles;
    public int TileWidthPx;

    public RowAdapter(Context c) => _c = c;

    public void Set(List<Row> rows) { _rows = rows; NotifyDataSetChanged(); }
    public override int Count => _rows.Count;
    public override Java.Lang.Object? GetItem(int position) => null;
    public override long GetItemId(int position) => position;
    public override int ViewTypeCount => 5;
    public override int GetItemViewType(int position) => (int)_rows[position].Kind;
    public override bool IsEnabled(int position) => false;   // rows handle their own taps
    public override bool AreAllItemsEnabled() => false;

    public override View? GetView(int position, View? convertView, ViewGroup? parent)
    {
        var r = _rows[position];
        if (Tiles)
        {
            var tile = convertView as TileView ?? new TileView(_c);
            tile.Bind(r, Ratio, TileWidthPx);
            return tile;
        }
        switch (r.Kind)
        {
            case RowKind.Menu:
                // One entry of the home menu: a very large light lowercase word on its own line.
                var word = convertView as TextView ?? Ui.Text(_c, "", 54, Th.Fg, Th.Light);
                // Sized so the whole menu fills the screen: as tall as an even share of the height allows,
                // but never so big that the longest word ("quickplay") runs off the side.
                var share = (Th.ScreenH - Th.Dp(170)) / Math.Max(1, _rows.Count);
                var size = Math.Min(share * 0.72f, (Th.ScreenW - Th.Dp(40)) / 4.75f);
                word.SetTextSize(Android.Util.ComplexUnitType.Px, size);
                word.SetPadding(Th.Dp(20), 0, Th.Dp(12), (int)(size * 0.12f));
                word.SetIncludeFontPadding(false);
                word.Text = r.Title;
                word.SetTextColor(r.IsCurrent?.Invoke() == false ? Th.FgDim : Th.Fg);
                word.Background = Ui.Ripple();
                word.SetOnClickListener(null);
                word.Clickable = true;
                word.Tag = null;
                var menuRow = r;
                word.SetOnClickListener(new ClickAction(() => menuRow.Click?.Invoke()));
                return word;
            case RowKind.Caption:
                var cap = convertView as TextView ?? Ui.Caption(_c, "");
                cap.Text = r.Title;
                return cap;
            case RowKind.Text:
                var note = convertView as TextView ?? Ui.Text(_c, "", 14, Th.FgDim, null, 0);
                note.SetPadding(Th.Dp(16), Th.Dp(6), Th.Dp(16), Th.Dp(10));
                note.Text = r.Title;
                return note;
            case RowKind.Strip:
                var scroll = convertView as HorizontalScrollView ?? new HorizontalScrollView(_c) { HorizontalScrollBarEnabled = false };
                scroll.RemoveAllViews();
                scroll.ScrollTo(0, 0);
                var line = new LinearLayout(_c) { Orientation = Orientation.Horizontal };
                line.SetPadding(Th.Dp(10), 0, Th.Dp(10), 0);
                foreach (var t in r.Strip ?? new())
                {
                    var tile = new TileView(_c);
                    tile.Bind(t, 1f, Th.Dp(124));
                    line.AddView(tile, new LinearLayout.LayoutParams(Th.Dp(124), ViewGroup.LayoutParams.WrapContent));
                }
                scroll.AddView(line);
                return scroll;
            default:
                var row = convertView as RowView ?? new RowView(_c);
                row.Bind(r);
                return row;
        }
    }
}

/// <summary>
/// A screen: optional back arrow + big lowercase title, optional action links and filter chips, then a list or grid
/// that is rebuilt from a function whenever the library changes.
/// </summary>
public class Page : LinearLayout
{
    public readonly MainActivity A;
    readonly LinearLayout _actions, _chips;
    readonly HorizontalScrollView _chipScroll;
    readonly ListView _list;
    readonly GridView _grid;
    readonly TextView _empty, _subtitle;
    readonly TextView? _title;
    readonly RowAdapter _listAdapter, _gridAdapter;
    Func<Content>? _content;
    Func<IEnumerable<string>>? _chipSource;
    Func<string>? _chipCurrent;
    Action<string>? _chipSelect;

    /// <summary>Rebuild (not just repaint) when a rating changes: for screens that list liked songs.</summary>
    public bool ReloadOnRatings;
    public Func<string>? Subtitle;
    public Func<string>? TitleSource;
    public Action? Closed;

    public Page(MainActivity a, string? title = null) : base(a)
    {
        A = a;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(Th.Bg);
        Clickable = true;

        if (title != null)
        {
            _title = Ui.Text(a, title.ToLowerInvariant(), 30, Th.Fg, Th.Light);
            _title.SetPadding(0, 0, Th.Dp(16), Th.Dp(2));
            var head = Ui.RowOf(a, Ui.Icon(a, IconKind.Back, Th.Fg, 22, a.Pop, 12), _title);
            head.SetPadding(Th.Dp(2), Th.Dp(2), 0, 0);
            AddView(head, Ui.Fill());
        }
        _subtitle = Ui.Text(a, "", 13, Th.FgDim);
        _subtitle.SetPadding(Th.Dp(16), 0, Th.Dp(16), Th.Dp(4));
        _subtitle.Visibility = ViewStates.Gone;
        AddView(_subtitle, Ui.Fill());

        _actions = new LinearLayout(a) { Orientation = Orientation.Horizontal };
        _actions.SetPadding(Th.Dp(4), 0, Th.Dp(4), 0);
        var actionScroll = new HorizontalScrollView(a) { HorizontalScrollBarEnabled = false };
        actionScroll.AddView(_actions);
        AddView(actionScroll, Ui.Fill());

        _chips = new LinearLayout(a) { Orientation = Orientation.Horizontal };
        _chips.SetPadding(Th.Dp(8), 0, Th.Dp(8), 0);
        _chipScroll = new HorizontalScrollView(a) { HorizontalScrollBarEnabled = false, Visibility = ViewStates.Gone };
        _chipScroll.AddView(_chips);
        AddView(_chipScroll, Ui.Fill());

        var body = new FrameLayout(a);
        _listAdapter = new RowAdapter(a);
        _list = new ListView(a) { Divider = null, Adapter = _listAdapter };
        _list.SetSelector(Android.Resource.Color.Transparent);
        _gridAdapter = new RowAdapter(a) { Tiles = true };
        _grid = new GridView(a) { Adapter = _gridAdapter, Visibility = ViewStates.Gone };
        _grid.SetNumColumns(-1);   // auto-fit
        _grid.StretchMode = StretchMode.StretchColumnWidth;
        _grid.SetSelector(Android.Resource.Color.Transparent);
        _grid.SetPadding(Th.Dp(8), 0, Th.Dp(8), 0);
        _empty = Ui.Text(a, "", 15, Th.FgDim, null, 0);
        _empty.SetPadding(Th.Dp(24), Th.Dp(40), Th.Dp(24), 0);
        _empty.Gravity = GravityFlags.CenterHorizontal;
        _empty.Visibility = ViewStates.Gone;
        body.AddView(_list);
        body.AddView(_grid);
        body.AddView(_empty);
        AddView(body, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
    }

    int _topCount;

    /// <summary>Adds a view (e.g. a search box) under the title, above the list.</summary>
    public Page Above(View v)
    {
        var lp = Ui.Fill();
        lp.LeftMargin = lp.RightMargin = Th.Dp(14);
        AddView(v, (_title != null ? 1 : 0) + _topCount++, lp);
        return this;
    }

    public Page Actions(params (string label, Action act)[] buttons)
    {
        _actions.RemoveAllViews();
        foreach (var (label, act) in buttons) _actions.AddView(Ui.Link(Context!, label, act, true));
        return this;
    }

    public Page Chips(Func<IEnumerable<string>> all, Func<string> current, Action<string> select)
    {
        _chipSource = all; _chipCurrent = current; _chipSelect = select;
        _chipScroll.Visibility = ViewStates.Visible;
        return this;
    }

    public Page Show(Func<Content> content) { _content = content; Reload(); return this; }
    public Page List(Func<List<Row>> rows, string empty = "") => Show(() => new Content(rows(), 0, 1f, empty));
    public Page Grid(Func<List<Row>> rows, int tileDp, float ratio, string empty = "") => Show(() => new Content(rows(), tileDp, ratio, empty));

    /// <summary>Rebuilds the rows from the library.</summary>
    public void Reload()
    {
        if (_content == null) return;
        var c = _content();   // first: building the content may settle which chip is selected
        if (TitleSource != null && _title != null) _title.Text = TitleSource().ToLowerInvariant();
        if (Subtitle != null)
        {
            var s = Subtitle();
            _subtitle.Text = s;
            _subtitle.Visibility = s == "" ? ViewStates.Gone : ViewStates.Visible;
        }
        if (_chipSource != null)
        {
            _chips.RemoveAllViews();
            var cur = _chipCurrent!();
            foreach (var chip in _chipSource())
            {
                var t = Ui.Text(Context!, chip, 19, chip == cur ? Th.Fg : Th.FgFaint, Th.Light);
                t.SetPadding(Th.Dp(8), Th.Dp(2), Th.Dp(10), Th.Dp(8));
                t.Click += (_, _) => { _chipSelect!(chip); Reload(); };
                _chips.AddView(t);
            }
        }
        var grid = c.GridDp > 0;
        _grid.Visibility = grid ? ViewStates.Visible : ViewStates.Gone;
        _list.Visibility = grid ? ViewStates.Gone : ViewStates.Visible;
        if (grid)
        {
            _grid.SetColumnWidth(Th.Dp(c.GridDp));
            _gridAdapter.Ratio = c.Ratio;
            _gridAdapter.TileWidthPx = Th.Dp(c.GridDp);
            _gridAdapter.Set(c.Rows);
        }
        else _listAdapter.Set(c.Rows);
        _empty.Text = c.Empty;
        _empty.Visibility = c.Rows.Count == 0 && c.Empty != "" ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>Repaints the visible rows (current-song highlight, hearts) without rebuilding them.</summary>
    public void Repaint()
    {
        _listAdapter.NotifyDataSetChanged();
    }
}
