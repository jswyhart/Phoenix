using System.Windows;
using System.Windows.Controls;

namespace Halcyon.UI;

/// <summary>
/// Stand-in for the system message box that follows Phoenix's light/dark theme (the system one is always light).
/// Same calls as <see cref="System.Windows.MessageBox"/>, which it hides inside this namespace.
/// </summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string text) => Show(null, text, "Phoenix", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show(null, text, caption, buttons, image);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var result = buttons switch { MessageBoxButton.YesNo => MessageBoxResult.No, MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel, _ => MessageBoxResult.Cancel };
        if (buttons == MessageBoxButton.OK) result = MessageBoxResult.OK;
        var w = new Window
        {
            Title = caption, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontSize = 13
        };
        if (owner != null && owner.IsLoaded) w.Owner = owner;
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        w.SetResourceReference(Window.ForegroundProperty, "Fg");
        w.SetResourceReference(Window.FontFamilyProperty, "TextFont");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };

        void Add(string label, MessageBoxResult r, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = label, MinWidth = 80, IsDefault = isDefault, IsCancel = isCancel, Style = (Style)Application.Current.FindResource("PillBtn") };
            b.Click += (_, _) => { result = r; w.Close(); };
            row.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK: Add("ok", MessageBoxResult.OK, true, true); break;
            case MessageBoxButton.OKCancel: Add("ok", MessageBoxResult.OK, true); Add("cancel", MessageBoxResult.Cancel, isCancel: true); break;
            case MessageBoxButton.YesNo: Add("yes", MessageBoxResult.Yes, true); Add("no", MessageBoxResult.No, isCancel: true); break;
            default: Add("yes", MessageBoxResult.Yes, true); Add("no", MessageBoxResult.No); Add("cancel", MessageBoxResult.Cancel, isCancel: true); break;
        }
        ((Button)row.Children[^1]).Margin = new Thickness(0);
        panel.Children.Add(row);
        w.Content = panel;
        w.ShowDialog();
        return result;
    }
}
