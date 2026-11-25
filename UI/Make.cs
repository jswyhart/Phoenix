using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Halcyon.UI;

/// <summary>Small helpers for the screens that are built in code rather than XAML.</summary>
public static class Make
{
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Text(string text, double size = 13, string brush = "Fg", Thickness? margin = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, Margin = margin ?? default, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Styled(string text, string style, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, Style = Style(style) };
        if (margin != null) t.Margin = margin.Value;
        return t;
    }

    public static Button Button(string text, string style, Action click, string? tip = null, Thickness? margin = null)
    {
        var b = new Button { Content = text, Style = Style(style), ToolTip = tip };
        if (margin != null) b.Margin = margin.Value;
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A cover picture in a square with the usual grey behind it.</summary>
    public static FrameworkElement Cover(string? art, double size, string glyph = "")
    {
        var grid = new Grid { Width = size, Height = size };
        grid.SetResourceReference(Panel.BackgroundProperty, "BgAlt");
        var g = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = size * 0.36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
        grid.Children.Add(g);
        if (!string.IsNullOrEmpty(art))
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            Art.SetSize(img, (int)Math.Max(80, size * 2));
            Art.SetPath(img, art);
            grid.Children.Add(img);
        }
        return grid;
    }

    public static System.Windows.Shapes.Rectangle Bar(double width, double height, string brush = "Accent")
    {
        var r = new System.Windows.Shapes.Rectangle { Width = Math.Max(0, width), Height = height, HorizontalAlignment = HorizontalAlignment.Left };
        r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        return r;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A window in Phoenix's colours.</summary>
    public static Window Window(string title, double width, double height, bool resize = true)
    {
        var w = new Window
        {
            Title = title, Width = width, Height = height, ResizeMode = resize ? ResizeMode.CanResize : ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontSize = 13, UseLayoutRounding = true
        };
        w.SetResourceReference(Control.FontFamilyProperty, "TextFont");
        if (App.Win is { IsLoaded: true } owner) w.Owner = owner;
        w.SetResourceReference(Control.BackgroundProperty, "Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Fg");
        return w;
    }

    /// <summary>Loads a picture file off the interface thread, scaled down to a width, ready to show.</summary>
    public static Task<BitmapSource?> PictureAsync(string file, int width) => Task.Run(() =>
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = width;
            b.UriSource = new Uri(file);
            b.EndInit();
            b.Freeze();
            return (BitmapSource?)b;
        }
        catch { return null; }
    });
}
