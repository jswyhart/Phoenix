using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

public class TileCell : Observable
{
    public object Item { get; init; } = null!;
    bool _sel;
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
}

public partial class TileGrid : UserControl
{
    public static readonly DependencyProperty TileTemplateProperty =
        DependencyProperty.Register(nameof(TileTemplate), typeof(DataTemplate), typeof(TileGrid));
    public DataTemplate? TileTemplate { get => (DataTemplate?)GetValue(TileTemplateProperty); set => SetValue(TileTemplateProperty, value); }

    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(TileGrid), new PropertyMetadata(150.0));
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }

    List<TileCell> _cells = new();
    int _cols = 1;
    double _rowH;
    bool _resizePending, _remeasureQueued;
    bool _stale;    // items changed while the grid was hidden or had no size yet: lay out when it is next shown
    /// <summary>Rows built right now (for the self-test): only what is on screen, never the whole list.</summary>
    public int BuiltRows => _rows.Count;
    public ScrollViewer ScrollArea => Scroller;
    readonly Dictionary<int, FrameworkElement> _rows = new();

    public event Action<object>? ItemClicked;
    public event Action<object>? ItemActivated;
    public event Action<object, FrameworkElement>? ItemContext;

    public TileGrid()
    {
        InitializeComponent();
        // resizes (and the page re-flowing around a mode switch) are handled once, after the dust settles
        SizeChanged += (_, e) =>
        {
            if (_resizePending || !(e.WidthChanged || e.HeightChanged)) return;
            _resizePending = true;
            Dispatcher.BeginInvoke(() => { _resizePending = false; Arrange(_stale); }, System.Windows.Threading.DispatcherPriority.Background);
        };
        // a list that changed while the page was hidden is laid out when the page comes back
        Loaded += (_, _) => { if (_stale) Arrange(true); };
        IsVisibleChanged += (_, _) => { if (IsVisible && _stale) Dispatcher.BeginInvoke(() => { if (_stale) Arrange(true); }, System.Windows.Threading.DispatcherPriority.Loaded); };
    }

    public void SetItems(IEnumerable<object> items)
    {
        var selected = SelectedItem;
        _cells = items.Select(i => new TileCell { Item = i, IsSelected = ReferenceEquals(i, selected) }).ToList();
        Arrange(true);
    }

    public object? SelectedItem
    {
        get => _cells.FirstOrDefault(c => c.IsSelected)?.Item;
        set { foreach (var c in _cells) c.IsSelected = value != null && ReferenceEquals(c.Item, value); }
    }

    FrameworkElement BuildRow(int index) => BuildRow(index, now: true);

    // Building a row of covers takes several milliseconds, which is more than one frame on a fast display. So while
    // the grid is being scrolled a row arrives as an empty strip of the right size and its covers are added one at a
    // time between frames; rows are asked for two ahead of the screen, so they are normally full before they show.
    readonly Queue<(StackPanel row, TileCell cell)> _toBuild = new();
    bool _building;

    FrameworkElement BuildRow(int index, bool now)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RowsMade++;
        if (!now && _rowH > 0) row.Height = _rowH;
        foreach (var c in _cells.Skip(index * _cols).Take(_cols))
        {
            if (now) row.Children.Add(Tile(c));
            else _toBuild.Enqueue((row, c));
        }
        if (!now && !_building) { _building = true; Dispatcher.BeginInvoke(BuildSome, System.Windows.Threading.DispatcherPriority.Background); }
        return row;
    }

    readonly Stack<StackPanel> _spare = new();
    /// <summary>Rows made since the grid was last laid out afresh, and rows reused (for the self-test).</summary>
    public int RowsMade { get; private set; }
    public int RowsReused { get; private set; }

    /// <summary>Gives a row that scrolled away the covers of another row.</summary>
    FrameworkElement Reuse(StackPanel row, int index)
    {
        var cells = _cells.Skip(index * _cols).Take(_cols).ToList();
        for (var k = 0; k < cells.Count; k++)
        {
            if (k < row.Children.Count)
            {
                var tile = (ContentControl)row.Children[k];
                if (!ReferenceEquals(tile.Content, cells[k])) tile.Content = cells[k];
                if (tile.Visibility != Visibility.Visible) tile.Visibility = Visibility.Visible;
            }
            else row.Children.Add(Tile(cells[k]));
        }
        for (var k = cells.Count; k < row.Children.Count; k++) row.Children[k].Visibility = Visibility.Collapsed;   // a short last row
        if (row.Visibility != Visibility.Visible) row.Visibility = Visibility.Visible;
        RowsReused++;
        return row;
    }

    ContentControl Tile(TileCell c) => new() { Content = c, ContentTemplate = (DataTemplate)Resources["Cell"], Focusable = false, IsTabStop = false, SnapsToDevicePixels = true };

    void BuildSome()
    {
        // one cover per turn: drawing and input come first, the rest of the row follows straight after
        while (_toBuild.Count > 0)
        {
            var (row, cell) = _toBuild.Dequeue();
            if (!_rows.ContainsValue(row)) continue;      // the row scrolled away before it was filled
            row.Children.Add(Tile(cell));
            break;
        }
        if (_toBuild.Count > 0) Dispatcher.BeginInvoke(BuildSome, System.Windows.Threading.DispatcherPriority.Background);
        else _building = false;
    }

    /// <summary>Covers still waiting to be added to rows (for the self-test).</summary>
    public int Waiting => _toBuild.Count;

    /// <summary>Lays the grid out for the current width and builds just the rows that are on screen.</summary>
    void Arrange(bool rebuild)
    {
        // Off screen (or not measured yet) there is no width or height to go by. Laying out then would take the grid
        // for one column wide and rows a pixel tall, and build hundreds of rows nobody can see, which is what made a
        // long list (thousands of books) freeze the app for seconds when its page was opened.
        if (!IsLoaded || !IsVisible || ActualWidth < TileWidth)
        {
            _stale = true;
            Surface.Children.Clear();
            _rows.Clear();
            _rowH = 0;
            return;
        }
        _stale = false;
        if (rebuild) _toBuild.Clear();
        var cols = Math.Max(1, (int)((ActualWidth - 14) / TileWidth));
        if (cols != _cols) { _cols = cols; rebuild = true; }
        if (rebuild)
        {
            Surface.Children.Clear();
            _rows.Clear();
            _spare.Clear();
            RowsMade = RowsReused = 0;
            _rowH = 0;
            if (_cells.Count > 0)
            {
                // every row is as tall as the first one
                // (it has to be in the window first, or the tile template cannot find this grid and measures empty)
                var probe = BuildRow(0);
                Surface.Children.Add(probe);
                Surface.Height = double.NaN;
                UpdateLayout();
                _rowH = probe.ActualHeight;
                _rows[0] = probe;
                if (_rowH < 8)
                {
                    // the first row has not been measured yet: try again once the layout has settled
                    _stale = true;
                    Dispatcher.BeginInvoke(() => { if (_stale) Arrange(true); }, System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }
            }
        }
        var rows = (_cells.Count + _cols - 1) / _cols;
        Surface.Height = rows * _rowH;
        Fill();
    }

    void Fill()
    {
        if (_rowH <= 0) return;
        var rows = (_cells.Count + _cols - 1) / _cols;
        var view = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : ActualHeight;
        // two rows either side of the screen are kept built, so a row is ready before it glides into view
        var first = Math.Max(0, (int)(Scroller.VerticalOffset / _rowH) - 2);
        var last = Math.Min(rows - 1, (int)((Scroller.VerticalOffset + view) / _rowH) + 2);
        last = Math.Min(last, first + 60);   // never more than a few screens' worth, whatever the measurements say
        // Rows that have left are not thrown away: they stay in place, hidden, and are handed the covers of the rows
        // coming in. Re-pointing an existing row costs a fraction of making one (no templates to build), which is what
        // keeps scrolling inside one frame on a fast display.
        foreach (var i in _rows.Keys.Where(i => i < first || i > last).ToList())
        {
            _spare.Push((StackPanel)_rows[i]);
            _rows.Remove(i);
        }
        if (_rows.TryGetValue(0, out var top) && top.Margin.Top != 0) top.Margin = new Thickness(0);
        for (var i = first; i <= last; i++)
        {
            if (_rows.ContainsKey(i)) continue;
            // rows on screen right now are built at once; the ones kept ready beyond it are filled between frames
            FrameworkElement row;
            if (_spare.Count > 0) row = Reuse(_spare.Pop(), i);
            else { row = BuildRow(i, now: true); Surface.Children.Add(row); }
            row.Margin = new Thickness(0, i * _rowH, 0, 0);
            _rows[i] = row;
        }
        foreach (var idle in _spare) if (idle.Visibility != Visibility.Collapsed) idle.Visibility = Visibility.Collapsed;
        if (!_remeasureQueued)
        {
            _remeasureQueued = true;
            Dispatcher.BeginInvoke(() => { _remeasureQueued = false; Remeasure(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Tiles can grow once their artwork and text arrive; if the real row height differs from the estimate, redo the positions.</summary>
    void Remeasure()
    {
        if (_rows.Count == 0 || _toBuild.Count > 0) return;
        var h = _rows.Values.Max(r => r.ActualHeight);
        if (h < 1 || Math.Abs(h - _rowH) < 1) return;
        _rowH = h;
        foreach (var (i, row) in _rows) row.Margin = new Thickness(0, i * _rowH, 0, 0);
        Surface.Height = ((_cells.Count + _cols - 1) / _cols) * _rowH;
        Fill();
    }

    bool _scrolling;

    void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;
        _scrolling = e.VerticalChange != 0;
        Fill();
        _scrolling = false;
    }

    void Cell_Down(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TileCell cell) return;
        if (e.ClickCount == 2) ItemActivated?.Invoke(cell.Item); else ItemClicked?.Invoke(cell.Item);
    }

    void Cell_Right(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TileCell cell) ItemContext?.Invoke(cell.Item, (FrameworkElement)sender);
        e.Handled = true;
    }
}
