using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Halcyon.Core;

namespace Halcyon.Desktop;

public class TileCell : Observable
{
    public Tile Item { get; init; } = null!;
    bool _sel;
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
}

public partial class TileGrid : UserControl
{
    public static readonly StyledProperty<IDataTemplate?> TileTemplateProperty = AvaloniaProperty.Register<TileGrid, IDataTemplate?>(nameof(TileTemplate));
    public IDataTemplate? TileTemplate { get => GetValue(TileTemplateProperty); set => SetValue(TileTemplateProperty, value); }

    public static readonly StyledProperty<double> TileWidthProperty = AvaloniaProperty.Register<TileGrid, double>(nameof(TileWidth), 150);
    public double TileWidth { get => GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }

    List<TileCell> _cells = new();
    int _cols;

    /// <summary>Single click, double click and secondary click; each passes the tile's underlying item.</summary>
    public event Action<object>? ItemClicked;
    public event Action<object>? ItemActivated;
    public event Action<object, Control>? ItemContext;

    public TileGrid()
    {
        InitializeComponent();
        // clicks are caught on the grid itself (even if something inside already handled them) and traced back to
        // the tile under the pointer; handlers wired inside the tile template did not receive them on macOS
        AddHandler(PointerPressedEvent, Cell_Pressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(Gestures.DoubleTappedEvent, Cell_DoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) => Layout(false);
    }

    public TileGrid(string template, double tileWidth) : this()
    {
        TileTemplate = Ui.Template(template);
        TileWidth = tileWidth;
    }

    public void SetItems(IEnumerable<Tile> items)
    {
        var selected = SelectedItem;
        _cells = items.Select(i => new TileCell { Item = i, IsSelected = selected != null && ReferenceEquals(i.Item, selected) }).ToList();
        Layout(true);
    }

    public object? SelectedItem
    {
        get => _cells.FirstOrDefault(c => c.IsSelected)?.Item.Item;
        set { foreach (var c in _cells) c.IsSelected = value != null && ReferenceEquals(c.Item.Item, value); }
    }

    void Layout(bool force)
    {
        var cols = Math.Max(1, (int)((Bounds.Width - 14) / TileWidth));
        if (!force && cols == _cols) return;
        _cols = cols;
        RowList.ItemsSource = _cells.Chunk(cols).ToList();
    }

    /// <summary>The tile (its frame) that a click landed in, found by walking up from what was clicked.</summary>
    static Control? TileAt(object? source)
    {
        for (var v = source as Avalonia.Visual; v != null; v = v.GetVisualParent())
            if (v is Border { DataContext: TileCell } b && b.Classes.Contains("tile")) return b;
        return null;
    }

    void Cell_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (TileAt(e.Source) is not { DataContext: TileCell cell } c) return;
        if (e.GetCurrentPoint(c).Properties.IsRightButtonPressed) { ItemContext?.Invoke(cell.Item.Item, c); e.Handled = true; }
        else if (e.ClickCount == 1) { ItemClicked?.Invoke(cell.Item.Item); e.Handled = true; }
    }

    void Cell_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TileAt(e.Source) is { DataContext: TileCell cell }) ItemActivated?.Invoke(cell.Item.Item);
    }
}
