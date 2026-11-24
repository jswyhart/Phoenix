using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Halcyon.Desktop;

/// <summary>Small builders so the screens can be composed in code with the shared styles.</summary>
public static class Ui
{
    public static IDataTemplate Template(string key) =>
        Application.Current!.TryFindResource(key, out var v) && v is IDataTemplate t ? t : throw new KeyNotFoundException(key);

    public static TextBlock Text(string text, string? cls = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text };
        if (cls != null) t.Classes.Add(cls);
        if (margin is { } m) t.Margin = m;
        return t;
    }

    public static TextBlock Cap(string text) => Text(text, "cap");
    public static TextBlock H2(string text) => Text(text, "h2");
    public static TextBlock Label(string text) => Text(text, "label");
    public static TextBlock Note(string text) => Text(text, "note");
    public static TextBlock Status() => Text("", "status");

    public static Button TextBtn(string text, Action click, Thickness? margin = null)
    {
        var b = new Button { Content = text, Margin = margin ?? new Thickness(0, 0, 14, 0) };
        b.Classes.Add("text");
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Pill(string text, Action click)
    {
        var b = new Button { Content = text };
        b.Classes.Add("pill");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A row of word tabs: the panel, a way to select one in code, and a way to show only some.</summary>
    public static (StackPanel panel, Action<string> select, Action<IEnumerable<string>> setVisible) Tabs(string group, (string key, string label)[] tabs, Action<string> chosen)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new Dictionary<string, RadioButton>();
        foreach (var (key, label) in tabs)
        {
            var rb = new RadioButton { Content = label, GroupName = group };
            rb.Classes.Add("nav");
            rb.Classes.Add("tab");
            rb.Click += (_, _) => chosen(key);
            buttons[key] = rb;
            panel.Children.Add(rb);
        }
        return (panel, key => { foreach (var (k, rb) in buttons) rb.IsChecked = k == key; },
            visible => { var set = visible.ToHashSet(); foreach (var (k, rb) in buttons) rb.IsVisible = set.Contains(k); });
    }

    public static ListBox List(string template, bool multi = false, bool boxed = false)
    {
        var list = new ListBox { ItemTemplate = Template(template), SelectionMode = multi ? SelectionMode.Multiple : SelectionMode.Single };
        if (boxed) list.Classes.Add("boxed");
        return list;
    }

    /// <summary>A caption (or any header) above a control that fills the rest.</summary>
    public static DockPanel Under(Control top, Control fill, Thickness? margin = null)
    {
        var d = new DockPanel { Margin = margin ?? default };
        DockPanel.SetDock(top, Dock.Top);
        d.Children.Add(top);
        d.Children.Add(fill);
        return d;
    }

    /// <summary>Left-aligned content with something pinned to the right edge.</summary>
    public static DockPanel Split(Control left, Control right)
    {
        var d = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right);
        d.Children.Add(left);
        return d;
    }

    public static StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.AddRange(children);
        return p;
    }

    public static StackPanel Column(params Control[] children)
    {
        var p = new StackPanel();
        p.Children.AddRange(children);
        return p;
    }

    public static T At<T>(this T control, int column = 0, int row = 0, int columnSpan = 1, int rowSpan = 1) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        Grid.SetColumnSpan(control, columnSpan);
        Grid.SetRowSpan(control, rowSpan);
        return control;
    }

    public static T With<T>(this T control, Action<T> setup) where T : Control
    {
        setup(control);
        return control;
    }

    public static Grid Cells(string columns, string rows, params Control[] children)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), RowDefinitions = new RowDefinitions(rows) };
        g.Children.AddRange(children);
        return g;
    }

    /// <summary>The data item under the pointer for a list event.</summary>
    public static T? Item<T>(RoutedEventArgs e) where T : class => (e.Source as StyledElement)?.DataContext as T;

    public static List<T> Selected<T>(this ListBox list) => list.SelectedItems?.OfType<T>().ToList() ?? new List<T>();

    // ---------- menus ----------

    public static MenuItem MenuItem(string header, Action click, bool enabled = true, bool check = false)
    {
        var mi = new MenuItem { Header = (check ? "✓  " : "") + header, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    public static ContextMenu Menu(params object?[] items)
    {
        var m = new ContextMenu();
        foreach (var i in items) if (i != null) m.Items.Add(i);
        return m;
    }

    public static MenuItem Sub(string header, IEnumerable<object> items)
    {
        var mi = new MenuItem { Header = header };
        foreach (var i in items) mi.Items.Add(i);
        return mi;
    }

    public static void Show(this ContextMenu menu, Control target)
    {
        target.ContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(target.ContextMenu, menu)) target.ContextMenu = null; };
        menu.Open(target);
    }
}
