using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>A book's bookmarks: jump to one (double-click or "go to"), rename it, or delete it.</summary>
public class BookmarksDialog : Window
{
    readonly Book _book;
    readonly Action<Bookmark> _jump;
    readonly ListBox _list = new() { Height = 260, BorderThickness = new Thickness(1) };

    public BookmarksDialog(Book book, Action<Bookmark> jump)
    {
        _book = book;
        _jump = jump;
        Owner = App.Win;
        Title = "Bookmarks";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        _list.SetResourceReference(Control.BorderBrushProperty, "Line");

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "bookmarks", Style = (Style)FindResource("H2") });
        panel.Children.Add(new TextBlock { Text = book.Title, Foreground = (System.Windows.Media.Brush)FindResource("FgDim"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 10) });

        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(HeightProperty, 40.0);
        var when = new FrameworkElementFactory(typeof(TextBlock));
        when.SetValue(DockPanel.DockProperty, Dock.Right);
        when.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Bookmark.CreatedText)));
        when.SetValue(TextBlock.FontSizeProperty, 12.0);
        when.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        when.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Bookmark.Label)));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(when);
        row.AppendChild(label);
        _list.ItemTemplate = new DataTemplate { VisualTree = row };
        _list.MouseDoubleClick += (_, e) => { if ((e.OriginalSource as FrameworkElement)?.DataContext is Bookmark) Go(); };
        panel.Children.Add(_list);

        Button Btn(string text, Action click, bool def = false)
        {
            var b = new Button { Content = text, Style = (Style)FindResource("PillBtn"), MinWidth = 80, IsDefault = def };
            b.Click += (_, _) => click();
            return b;
        }
        var close = Btn("close", Close);
        close.IsCancel = true;
        close.Margin = new Thickness(0);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
            Children = { Btn("go to", Go, true), Btn("rename", Rename), Btn("delete", Delete), close }
        });
        Content = panel;
        Reload();
    }

    Bookmark? Selected => _list.SelectedItem as Bookmark;

    void Reload()
    {
        var keep = Selected?.Id;
        var marks = App.Lib.BookmarksOf(_book);
        _list.ItemsSource = marks;
        _list.SelectedItem = marks.FirstOrDefault(m => m.Id == keep) ?? marks.FirstOrDefault();
    }

    void Go()
    {
        if (Selected is not { } m) return;
        Close();
        _jump(m);
    }

    void Rename()
    {
        if (Selected is not { } m) return;
        var name = InputDialog.Ask("Rename bookmark", "Name", m.Label);
        if (string.IsNullOrWhiteSpace(name)) return;
        App.Lib.RenameBookmark(m, name);
        Reload();
    }

    void Delete()
    {
        if (Selected is not { } m) return;
        App.Lib.DeleteBookmark(m);
        Reload();
    }
}
