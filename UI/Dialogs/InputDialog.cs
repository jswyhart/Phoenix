using System.Windows;
using System.Windows.Controls;

namespace Halcyon.UI;

/// <summary>Small themed prompt for a single line of text.</summary>
public class InputDialog : Window
{
    readonly TextBox _box = new();

    InputDialog(string title, string prompt, string initial)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var ok = new Button { Content = "ok", IsDefault = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80 };
        var cancel = new Button { Content = "cancel", IsCancel = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80, Margin = new Thickness(0) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        _box.Text = initial;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(string title, string prompt, string initial)
    {
        var d = new InputDialog(title, prompt, initial) { Owner = App.Win };
        return d.ShowDialog() == true ? d._box.Text : null;
    }
}
