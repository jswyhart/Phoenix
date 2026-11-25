using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Halcyon.UI;

/// <summary>
/// The sorting and filtering bar a collection page carries: a box to type a filter into, a "show" choice (liked,
/// unplayed, on this PC…), what to sort by, and which way. The sort and show choices are remembered per page.
/// </summary>
public class ViewOptions : StackPanel
{
    readonly string _id;
    readonly (string Key, string Label)[] _sorts, _shows;
    readonly ComboBox _sort = new() { Width = 136, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Sort by" };
    readonly ComboBox _show = new() { Width = 128, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Show" };
    readonly Button _direction;
    readonly TextBox _text = new() { Width = 150, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, Height = 26, ToolTip = "Filter: type part of a name" };
    readonly TextBlock _hint = Make.Text("filter", 12, "FgFaint", new Thickness(7, 0, 0, 0));
    readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(220) };
    bool _setting;

    /// <summary>What to sort by ("" = the page's own order).</summary>
    public string Sort { get; private set; } = "";
    public bool Descending { get; private set; }
    /// <summary>What to show ("" = everything).</summary>
    public string Show { get; private set; } = "";
    public string Text { get; private set; } = "";
    public event Action? Changed;

    /// <summary>Anything but the page's own order and everything shown.</summary>
    public bool Active => Sort != "" || Descending || Show != "" || Text != "";

    public ViewOptions(string id, (string Key, string Label)[] sorts, (string Key, string Label)[]? shows = null)
    {
        _id = id;
        _sorts = sorts;
        _shows = shows ?? Array.Empty<(string, string)>();
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(24, 0, 0, 0);

        _hint.IsHitTestVisible = false;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        var box = new Grid { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(_text);
        box.Children.Add(_hint);
        Children.Add(box);

        if (_shows.Length > 0)
        {
            _show.ItemsSource = _shows.Select(s => s.Label).ToList();
            Children.Add(_show);
        }
        if (_sorts.Length > 0)
        {
            _sort.ItemsSource = _sorts.Select(s => "by " + s.Label).ToList();
            Children.Add(_sort);
        }
        _direction = Make.Button("", "IconBtn", () => Set(descending: !Descending), "Turn the order around");
        _direction.Width = _direction.Height = 28;
        _direction.FontSize = 11;
        if (_sorts.Length > 0) Children.Add(_direction);

        // what was chosen last time
        if (App.Settings.Views.TryGetValue(id, out var saved))
        {
            var parts = saved.Split('|');
            if (parts.Length == 3)
            {
                if (_sorts.Any(s => s.Key == parts[0])) Sort = parts[0];
                Descending = parts[1] == "1";
                if (_shows.Any(s => s.Key == parts[2])) Show = parts[2];
            }
        }
        Reflect();

        _sort.SelectionChanged += (_, _) => { if (!_setting && _sort.SelectedIndex >= 0) Set(sort: _sorts[_sort.SelectedIndex].Key); };
        _show.SelectionChanged += (_, _) => { if (!_setting && _show.SelectedIndex >= 0) Set(show: _shows[_show.SelectedIndex].Key); };
        _text.TextChanged += (_, _) =>
        {
            _hint.Visibility = _text.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            if (_setting) return;
            _typing.Stop();
            _typing.Start();
        };
        _typing.Tick += (_, _) => { _typing.Stop(); Set(text: _text.Text); };
    }

    void Reflect()
    {
        _setting = true;
        if (_sorts.Length > 0) _sort.SelectedIndex = Math.Max(0, Array.FindIndex(_sorts, s => s.Key == Sort));
        if (_shows.Length > 0) _show.SelectedIndex = Math.Max(0, Array.FindIndex(_shows, s => s.Key == Show));
        if (_text.Text != Text) _text.Text = Text;
        _hint.Visibility = Text == "" ? Visibility.Visible : Visibility.Collapsed;
        _direction.Content = Descending ? "" : "";
        _direction.ToolTip = Descending ? "Last first (press to turn around)" : "First first (press to turn around)";
        _direction.SetResourceReference(Control.ForegroundProperty, Descending ? "Accent" : "Fg");
        _setting = false;
    }

    /// <summary>Changes the choices (as using the bar would) and tells the page.</summary>
    public void Set(string? sort = null, bool? descending = null, string? show = null, string? text = null)
    {
        if (sort != null && (sort == "" || _sorts.Any(s => s.Key == sort))) Sort = sort;
        if (descending != null) Descending = descending.Value;
        if (show != null && (show == "" || _shows.Any(s => s.Key == show))) Show = show;
        if (text != null) Text = text.Trim();
        App.Settings.Views[_id] = $"{Sort}|{(Descending ? "1" : "0")}|{Show}";
        Reflect();
        Changed?.Invoke();
    }

    /// <summary>Back to the page's own order with everything shown.</summary>
    public void Clear() => Set(sort: "", descending: false, show: "", text: "");

    /// <summary>Whether the typed filter fits: every word typed has to be in one of the fields.</summary>
    public bool Matches(params string?[] fields)
    {
        if (Text == "") return true;
        foreach (var word in Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var found = false;
            foreach (var f in fields) if (f != null && f.Contains(word, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Puts a list in the chosen order: <paramref name="keys"/> gives the way to order for each sort; the direction is applied on top.</summary>
    public List<T> Order<T>(IEnumerable<T> items, Func<string, Func<IEnumerable<T>, IEnumerable<T>>?> keys)
    {
        var ordered = keys(Sort)?.Invoke(items) ?? items;
        var list = ordered.ToList();
        if (Descending) list.Reverse();
        return list;
    }
}
