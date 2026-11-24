using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Column headings for a song list (same columns as the SongRow template). Click one to sort by it, again to reverse,
/// a third time for the original order; right-click for every sort option, including ones with no column.
/// </summary>
public class SongHeader : Grid
{
    public SongSort Sort { get; } = new();
    public event Action? Changed;
    readonly Dictionary<string, (Button button, string label)> _cells = new();

    public SongHeader()
    {
        // must match SongRow in Theme.xaml; the right margin leaves room for the list's scroll bar
        foreach (var w in new[] { new GridLength(34), new(3, GridUnitType.Star), new(2, GridUnitType.Star), new(2, GridUnitType.Star), new(1.5, GridUnitType.Star), new GridLength(56), new GridLength(30) })
            ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Margin = new Thickness(8, 0, 18, 2);

        Add("no", "#", 0, HorizontalAlignment.Right, new Thickness(0, 0, 12, 0));
        Add("title", "title", 1);
        Add("artist", "artist", 2);
        Add("album", "album", 3);
        Add("tags", "tags", 4);
        Add("time", "time", 5, HorizontalAlignment.Right);
        Add("rating", "♥", 6, HorizontalAlignment.Center);
        MouseRightButtonUp += (_, e) => { Menus.Open(OptionsMenu(), this); e.Handled = true; };
        Refresh();
    }

    /// <summary>Sorts from code (as clicking a heading would).</summary>
    public void SortBy(string key, bool descending) { Sort.Set(key, descending); Refresh(); Changed?.Invoke(); }

    /// <summary>Back to the original order (updates the headings too).</summary>
    public void Reset() { Sort.Clear(); Refresh(); }

    void Add(string key, string label, int column, HorizontalAlignment align = HorizontalAlignment.Left, Thickness? margin = null)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("TextBtn"), HorizontalAlignment = align, FontSize = 11, Padding = new Thickness(2, 3, 2, 3), Margin = margin ?? default };
        b.Click += (_, _) => { Sort.Click(key); Refresh(); Changed?.Invoke(); };
        SetColumn(b, column);
        Children.Add(b);
        _cells[key] = (b, label);
    }

    void Refresh()
    {
        foreach (var (key, (button, label)) in _cells)
        {
            var active = Sort.Key == key;
            button.Content = active ? label + (Sort.Descending ? " ▼" : " ▲") : label;
            button.SetResourceReference(Control.ForegroundProperty, active ? "Fg" : "FgDim");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    ContextMenu OptionsMenu()
    {
        var m = new ContextMenu();
        foreach (var (key, label) in SongSort.Keys)
        {
            var k = key;
            var item = Menus.Item("Sort by " + label, () => { Sort.Set(k, Sort.Key == k && !Sort.Descending); Refresh(); Changed?.Invoke(); });
            item.IsChecked = Sort.Key == key;
            m.Items.Add(item);
        }
        m.Items.Add(new Separator());
        m.Items.Add(Menus.Item("Original order", () => { Sort.Clear(); Refresh(); Changed?.Invoke(); }, Sort.Active));
        return m;
    }
}
