using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Halcyon.Core;

namespace Halcyon.Desktop;

/// <summary>
/// Column headings for a song list (same columns as the SongRow template), as in the Windows app. Click one to sort by
/// it, again to reverse, a third time for the original order; right-click for every sort option.
/// </summary>
public class SongHeader : Grid
{
    public SongSort Sort { get; } = new();
    public event Action? Changed;
    readonly Dictionary<string, (Button button, string label)> _cells = new();

    public SongHeader()
    {
        // must match SongRow in Theme.axaml; the right margin leaves room for the list's scroll bar
        ColumnDefinitions = new ColumnDefinitions("34,3*,2*,2*,1.5*,56,30");
        Margin = new Thickness(8, 0, 18, 2);
        Add("no", "#", 0, HorizontalAlignment.Right, new Thickness(0, 0, 12, 0));
        Add("title", "title", 1);
        Add("artist", "artist", 2);
        Add("album", "album", 3);
        Add("tags", "tags", 4);
        Add("time", "time", 5, HorizontalAlignment.Right);
        Add("rating", "♥", 6, HorizontalAlignment.Center);
        ContextRequested += (_, e) => { OptionsMenu().Show(this); e.Handled = true; };
        Refresh();
    }

    /// <summary>Back to the original order (updates the headings too).</summary>
    public void Reset() { Sort.Clear(); Refresh(); }

    void Add(string key, string label, int column, HorizontalAlignment align = HorizontalAlignment.Left, Thickness? margin = null)
    {
        var b = Ui.TextBtn(label, () => { Sort.Click(key); Refresh(); Changed?.Invoke(); }, margin ?? default);
        b.HorizontalAlignment = align;
        b.FontSize = 11;
        b.Padding = new Thickness(2, 3);
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
            button.Classes.Set("sorted", active);
            button.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    ContextMenu OptionsMenu() => Ui.Menu(SongSort.Keys
        .Select(k => (object?)Ui.MenuItem("Sort by " + k.Label, () => { Sort.Set(k.Key, Sort.Key == k.Key && !Sort.Descending); Refresh(); Changed?.Invoke(); }, check: Sort.Key == k.Key))
        .Append(new Separator())
        .Append(Ui.MenuItem("Original order", () => { Sort.Clear(); Refresh(); Changed?.Invoke(); }, Sort.Active))
        .ToArray());
}
