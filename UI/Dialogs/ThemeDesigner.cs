using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Halcyon.UI;

/// <summary>
/// Designs a theme with Phoenix itself as the preview: every change shows at once in the whole app (the window stays
/// out of the way, so you can go to any page, or open now playing, while it is open). Closing without saving puts
/// back the theme you had.
/// </summary>
public sealed class ThemeDesigner
{
    static Window? _open;

    readonly Theme _original;
    readonly Theme _t;
    readonly Action<string?> _done;
    readonly Window _w;
    readonly Dictionary<string, (Border Swatch, TextBox Hex, TextBlock Changed, Border Row)> _rows = new();
    readonly ColourPicker _picker = new();
    readonly TextBlock _pickedName = Make.Text("", 15, "Fg");
    readonly TextBlock _pickedTip = Make.Text("", 12, "FgDim", wrap: true);
    readonly TextBox _pickedHex = new() { Width = 110 };
    readonly TextBox _name = new() { Width = 240 };
    readonly RadioButton _light = new() { Content = "light", GroupName = "themeBase", Margin = new Thickness(0, 0, 16, 0) };
    readonly RadioButton _dark = new() { Content = "dark", GroupName = "themeBase" };
    readonly ComboBox _textFont = FontBox(), _headFont = FontBox();
    readonly StackPanel _problems = new();
    readonly CheckBox _compare = new() { Content = "show the theme as it was", VerticalAlignment = VerticalAlignment.Center };
    string _picked = "Bg";
    bool _saved, _quiet, _queued;

    /// <summary>
    /// Opens the designer on a copy of <paramref name="start"/> (or brings it forward if it is open). <paramref name="done"/>
    /// gets the saved theme's id, or null if it was closed without saving.
    /// </summary>
    public static void Open(Theme start, Action<string?> done)
    {
        if (_open != null) { _open.Activate(); return; }
        new ThemeDesigner(start, done);
    }

    ThemeDesigner(Theme start, Action<string?> done)
    {
        _original = start;
        _done = done;
        _t = start.Copy();
        if (start.BuiltIn) { _t.Id = ""; _t.Name = "my " + start.Name + " theme"; }
        Themes.Designing = true;

        _w = Make.Window("Design a theme", 860, 680);
        _w.MinWidth = 760;
        _w.MinHeight = 520;
        _w.ShowInTaskbar = true;
        _w.Content = Build();
        _w.Closed += (_, _) => Finish();
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not ComboBox) _w.Close(); };
        _open = _w;
        Load();
        Pick("Bg");
        _w.Show();
    }

    // ---------- layout ----------

    UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(0) };

        // bottom: compare, reset, save
        var bottom = new DockPanel { Margin = new Thickness(20, 10, 20, 16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var buttons = Make.Row(
            Make.Button("SAVE", "PillBtn", () => Save(asNew: false), _original.BuiltIn ? "Save as a new theme of yours" : "Save over this theme", new Thickness(0, 0, 8, 0)),
            Make.Button("SAVE AS NEW", "PillBtn", () => Save(asNew: true), "Keep the theme you started from, and save this as another one", new Thickness(0, 0, 8, 0)),
            Make.Button("CANCEL", "PillBtn", () => _w.Close(), "Close without saving: the theme you had comes back", new Thickness(0)));
        if (_original.BuiltIn) buttons.Children.RemoveAt(1);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        _compare.Checked += (_, _) => Themes.Apply(_original);
        _compare.Unchecked += (_, _) => Preview();
        bottom.Children.Add(Make.Row(_compare,
            Make.Button("now playing", "TextBtn", ToggleNow, "Open or close now playing, to see its colours", new Thickness(20, 0, 0, 0)),
            Make.Button("start again", "TextBtn", ResetAll, "Every colour and font back to the base's", new Thickness(16, 0, 0, 0))));
        root.Children.Add(bottom);

        // left: every colour, by group
        var list = new StackPanel { Margin = new Thickness(20, 16, 12, 16) };
        string? group = null;
        foreach (var k in Themes.Keys)
        {
            if (k.Group != group) { group = k.Group; list.Children.Add(Make.Text(k.Group.ToUpperInvariant(), 12, "FgDim", new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 4))); }
            list.Children.Add(ColourRow(k));
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 330 };
        DockPanel.SetDock(scroll, Dock.Left);
        root.Children.Add(scroll);

        // right: name and base, the picker, fonts, readability
        var right = new StackPanel { Margin = new Thickness(16, 16, 20, 0) };
        right.Children.Add(Make.Text("NAME", 12, "FgDim", new Thickness(0, 0, 0, 4)));
        right.Children.Add(Make.Row(_name));
        _name.TextChanged += (_, _) => { if (!_quiet) _t.Name = _name.Text; };
        right.Children.Add(Make.Text("BASE", 12, "FgDim", new Thickness(0, 14, 0, 4)));
        right.Children.Add(Make.Row(_light, _dark));
        right.Children.Add(Make.Text("Colours you haven't changed come from the base, and it sets light or dark title bars and the book reader.", 12, "FgFaint", new Thickness(0, 4, 0, 0), wrap: true));
        foreach (var rb in new[] { _light, _dark })
        {
            rb.SetResourceReference(Control.ForegroundProperty, "Fg");
            rb.Click += (_, _) => { _t.Base = _dark.IsChecked == true ? "dark" : "light"; Changed(); Pick(_picked); };
        }

        right.Children.Add(Make.Text("COLOUR", 12, "FgDim", new Thickness(0, 18, 0, 4)));
        right.Children.Add(_pickedName);
        right.Children.Add(_pickedTip);
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        pickRow.Children.Add(_picker);
        var side = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        side.Children.Add(Make.Text("hex (#RRGGBB, or #AARRGGBB to see through)", 11.5, "FgFaint", wrap: true));
        side.Children.Add(_pickedHex);
        side.Children.Add(Make.Button("use the base colour", "TextBtn", () => { _t.Colours.Remove(_picked); Changed(); Pick(_picked); }, null, new Thickness(0, 10, 0, 0)));
        side.Children.Add(Make.Button("copy from accent", "TextBtn", () => SetPicked(_t.Get("Accent")), "Use the accent colour here", new Thickness(0, 6, 0, 0)));
        side.Width = 170;
        pickRow.Children.Add(side);
        right.Children.Add(pickRow);
        _picker.Changed += c => SetPicked(c, fromPicker: true);
        _pickedHex.LostFocus += (_, _) => TypedHex(_pickedHex, _picked);
        _pickedHex.KeyDown += (_, e) => { if (e.Key == Key.Enter) TypedHex(_pickedHex, _picked); };

        right.Children.Add(Make.Text("FONTS", 12, "FgDim", new Thickness(0, 18, 0, 4)));
        var fonts = new Grid();
        fonts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        fonts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fonts.RowDefinitions.Add(new RowDefinition());
        fonts.RowDefinitions.Add(new RowDefinition());
        void FontRow(int row, string label, ComboBox box)
        {
            var l = Make.Text(label, 12, "FgDim");
            l.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(l, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            box.Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0);
            fonts.Children.Add(l);
            fonts.Children.Add(box);
        }
        FontRow(0, "text", _textFont);
        FontRow(1, "headings", _headFont);
        right.Children.Add(fonts);
        _textFont.SelectionChanged += (_, _) => { if (!_quiet) { _t.TextFont = FontName(_textFont); Changed(); } };
        _headFont.SelectionChanged += (_, _) => { if (!_quiet) { _t.HeadFont = FontName(_headFont); Changed(); } };

        right.Children.Add(Make.Text("EASY TO READ?", 12, "FgDim", new Thickness(0, 18, 0, 4)));
        right.Children.Add(_problems);

        root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    UIElement ColourRow(ThemeKey k)
    {
        var swatch = new Border { Width = 26, Height = 20, BorderThickness = new Thickness(1) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Line");
        var under = new Border { Background = ColourPicker.Checker(), Child = swatch, Margin = new Thickness(0, 0, 10, 0) };
        var label = Make.Text(k.Label, 13, "Fg");
        label.VerticalAlignment = VerticalAlignment.Center;
        var changed = Make.Text("•", 14, "Accent", new Thickness(6, 0, 0, 0));
        changed.VerticalAlignment = VerticalAlignment.Center;
        changed.ToolTip = "Changed from the base";
        var hex = new TextBox { Width = 92, VerticalAlignment = VerticalAlignment.Center };
        hex.LostFocus += (_, _) => TypedHex(hex, k.Key);
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) TypedHex(hex, k.Key); };
        hex.GotKeyboardFocus += (_, _) => Pick(k.Key);
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(under, Dock.Left);
        DockPanel.SetDock(hex, Dock.Right);
        dock.Children.Add(under);
        dock.Children.Add(hex);
        dock.Children.Add(Make.Row(label, changed));
        ((StackPanel)dock.Children[2]).VerticalAlignment = VerticalAlignment.Center;
        var row = new Border { Child = dock, Padding = new Thickness(8, 5, 4, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = k.Tip };
        row.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) Pick(k.Key); };
        _rows[k.Key] = (swatch, hex, changed, row);
        return row;
    }

    static ComboBox FontBox()
    {
        // type the first letters of a font to jump to it; each name is shown in its own font
        var box = new ComboBox { Width = 240, IsTextSearchEnabled = true, MaxDropDownHeight = 360 };
        box.Items.Add(new ComboBoxItem { Content = "(standard)", Tag = "" });
        foreach (var name in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            box.Items.Add(new ComboBoxItem { Content = name, Tag = name, FontFamily = new FontFamily(name) });
        return box;
    }

    static string FontName(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    static void ShowFont(ComboBox box, string name)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (item == null && name.Trim() != "")
        {
            // a font named in the file that this PC doesn't have: keep it, so saving doesn't lose it
            item = new ComboBoxItem { Content = name.Trim() + "  (not on this PC)", Tag = name.Trim() };
            box.Items.Insert(1, item);
        }
        box.SelectedItem = item ?? box.Items[0];
    }

    // ---------- state ----------

    void Load()
    {
        _quiet = true;
        _name.Text = _t.Name;
        _light.IsChecked = !_t.IsDark;
        _dark.IsChecked = _t.IsDark;
        ShowFont(_textFont, _t.TextFont);
        ShowFont(_headFont, _t.HeadFont);
        _quiet = false;
        Refresh();
    }

    void Pick(string key)
    {
        _picked = key;
        var k = Themes.Keys.First(x => x.Key == key);
        _pickedName.Text = $"{k.Group}: {k.Label}";
        _pickedTip.Text = k.Tip;
        _quiet = true;
        _picker.Colour = _t.Get(key);
        _pickedHex.Text = Themes.Hex(_t.Get(key));
        _quiet = false;
        foreach (var (name, r) in _rows)
            if (name == key) r.Row.SetResourceReference(Border.BackgroundProperty, "Selected");
            else r.Row.Background = Brushes.Transparent;
    }

    void SetPicked(Color c) => SetPicked(c, fromPicker: false);

    void SetPicked(Color c, bool fromPicker)
    {
        if (_quiet) return;
        _t.Set(_picked, c);
        _quiet = true;
        if (!_pickedHex.IsKeyboardFocused) _pickedHex.Text = Themes.Hex(c);
        if (!fromPicker) _picker.Colour = c;
        _quiet = false;
        Changed();
    }

    void TypedHex(TextBox box, string key)
    {
        if (!Themes.TryParse(box.Text, out var c)) { box.Text = Themes.Hex(_t.Get(key)); return; }
        if (c == _t.Get(key)) return;
        if (key != _picked) Pick(key);
        SetPicked(c);
    }

    void ResetAll()
    {
        _t.Colours.Clear();
        _t.TextFont = "";
        _t.HeadFont = "";
        Load();
        Pick(_picked);
        Changed();
    }

    void ToggleNow()
    {
        if (App.Win == null) return;
        App.Win.ShowNow(App.Win.NowLayer.Visibility != Visibility.Visible);
    }

    /// <summary>Something changed: show it in the app (once per moment, however fast a colour is dragged).</summary>
    void Changed()
    {
        Refresh();
        if (_compare.IsChecked == true) _compare.IsChecked = false;   // unchecking previews
        else Preview();
    }

    void Preview()
    {
        if (_queued) return;
        _queued = true;
        _w.Dispatcher.BeginInvoke(() => { _queued = false; if (_compare.IsChecked != true && _open == _w) Themes.Apply(_t.Copy()); }, DispatcherPriority.Background);
    }

    void Refresh()
    {
        foreach (var k in Themes.Keys)
        {
            var (swatch, hex, changed, _) = _rows[k.Key];
            var c = _t.Get(k.Key);
            swatch.Background = new SolidColorBrush(c);
            if (!hex.IsKeyboardFocused) hex.Text = Themes.Hex(c);
            changed.Visibility = _t.Colours.ContainsKey(k.Key) ? Visibility.Visible : Visibility.Hidden;
        }
        _problems.Children.Clear();
        var problems = Themes.Problems(_t);
        if (problems.Count == 0) _problems.Children.Add(Make.Text("Yes: text and the accent stand out clearly from what is behind them.", 12.5, "FgDim", wrap: true));
        foreach (var (what, ratio, need) in problems)
            _problems.Children.Add(Make.Text($"⚠  {what} is hard to read ({ratio:0.0} : 1, wants {need:0.#} or more)", 12.5, "Accent", new Thickness(0, 0, 0, 2), wrap: true));
    }

    void Save(bool asNew)
    {
        if (string.IsNullOrWhiteSpace(_t.Name)) _t.Name = "my theme";
        var t = _t.Copy();
        if (asNew || _original.BuiltIn) t.Id = "";
        else t.Id = _original.Id;
        string id;
        try { id = Themes.Save(t); }
        catch (Exception ex) { MessageBox.Show(_w, ex.Message, "Phoenix", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _saved = true;
        _savedId = id;
        _w.Close();
    }

    string? _savedId;

    void Finish()
    {
        _open = null;
        Themes.Designing = false;
        if (!_saved) Themes.Apply(_original);   // closed without saving: the theme you had comes back
        _done(_saved ? _savedId : null);
    }
}
