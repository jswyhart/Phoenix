using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Edits your own tags on songs, videos or books. The box shows the tags every selected item shares: whatever is in the
/// box is added to all of them, and tags deleted from the box are removed from all of them. Tags that only some items
/// have are left alone. Click an existing tag below to add it.
/// </summary>
public class EditTagsDialog : Window
{
    readonly TextBox _box = new();
    readonly string _kind;
    readonly List<long> _ids;
    readonly List<string> _before;

    public EditTagsDialog(string kind, IEnumerable<long> ids, string what)
    {
        _kind = kind;
        _ids = ids.Distinct().ToList();
        _before = App.Lib.CommonTags(kind, _ids);
        Owner = App.Win;
        Title = "Search tags";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "TextFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "search tags", Style = (Style)FindResource("H2") });
        var hint = $"Search tags for {what}: Phoenix's own labels, kept in its library and not written into the files (for the files' own title, artist, genre and so on, use Edit file tags). "
                 + "Separate them with commas. Search finds them by plain words or as #tag (use #\"two words\" for a tag with a space).";
        if (_ids.Count > 1) hint += " Tags you add go on every selected item; tags you delete from the box come off every selected item.";
        panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = (System.Windows.Media.Brush)FindResource("FgDim") });
        _box.Text = string.Join(", ", _before);
        panel.Children.Add(_box);

        var existing = App.Lib.AllTags().Take(60).ToList();
        if (existing.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "YOUR TAGS", Style = (Style)FindResource("Cap"), Margin = new Thickness(0, 14, 0, 6) });
            var wrap = new WrapPanel();
            foreach (var (tag, count) in existing)
            {
                var b = new Button { Content = $"{tag}  {count}", Style = (Style)FindResource("PillBtn"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
                b.Click += (_, _) => Add(tag);
                wrap.Children.Add(b);
            }
            panel.Children.Add(new ScrollViewer { Content = wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        var ok = new Button { Content = "save", IsDefault = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80 };
        var cancel = new Button { Content = "cancel", IsCancel = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80, Margin = new Thickness(0) };
        ok.Click += (_, _) => Save();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }

    void Add(string tag)
    {
        var current = Library.ParseTags(_box.Text);
        if (!current.Contains(tag)) current.Add(tag);
        _box.Text = string.Join(", ", current);
        _box.Focus();
        _box.CaretIndex = _box.Text.Length;
    }

    void Save()
    {
        var now = Library.ParseTags(_box.Text);
        App.Lib.ApplyTags(_kind, _ids, now, _before.Where(t => !now.Contains(t, StringComparer.OrdinalIgnoreCase)));
        DialogResult = true;
    }
}
