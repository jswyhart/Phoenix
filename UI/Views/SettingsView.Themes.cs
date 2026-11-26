using System.IO;
using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>Settings: the theme, and your own themes (made in the designer, or files you import).</summary>
public partial class SettingsView
{
    bool _listingThemes;

    void RefreshThemes()
    {
        _listingThemes = true;
        ThemeBox.Items.Clear();
        var all = Themes.All();
        foreach (var t in all)
            ThemeBox.Items.Add(new ComboBoxItem { Content = t.Id == "system" ? "follow the system" : t.BuiltIn ? t.Name : $"{t.Name}   ({t.Base})", Tag = t.Id });
        // following the system: which theme for light and which for dark
        var following = Themes.Current.Id == "system";
        SystemThemes.Visibility = following ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (box, chosen) in new[] { (LightThemeBox, S.ThemeLight), (DarkThemeBox, S.ThemeDark) })
        {
            box.Items.Clear();
            foreach (var t in all.Where(t => t.Id != "system"))
                box.Items.Add(new ComboBoxItem { Content = t.BuiltIn ? t.Name : $"{t.Name}   ({t.Base})", Tag = t.Id });
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, chosen, StringComparison.OrdinalIgnoreCase))
                               ?? box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == (box == LightThemeBox ? "light" : "dark"));
        }
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, Themes.Current.Id, StringComparison.OrdinalIgnoreCase))
                                ?? ThemeBox.Items[0];
        _listingThemes = false;
        var mine = !Themes.Current.BuiltIn;
        ThemeDeleteBtn.IsEnabled = mine;
        ThemeNote.Text = following
            ? $"Phoenix shows the first while Windows is set to light for apps and the second while it is dark, and switches when Windows does (now: {(Themes.SystemIsDark ? "dark" : "light")})."
            : mine
            ? $"Saved as {Themes.Current.Id}.json in the themes folder. Edit it there by hand and Phoenix shows the change as soon as you save."
            : "Customise to make a theme of your own from this one: pick its colours and fonts while Phoenix shows them as you go.";
    }

    void ThemeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_listingThemes || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        UseTheme(id);
    }

    void SideTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_listingThemes || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string id } } box) return;
        if (box == LightThemeBox) S.ThemeLight = id; else S.ThemeDark = id;
        UseTheme("system");
    }

    void UseTheme(string id)
    {
        S.Theme = id;
        Keep();
        App.ApplyTheme(id);
        RefreshThemes();
    }

    void ThemeDesign_Click(object sender, RoutedEventArgs e)
    {
        ThemeDesigner.Open(Themes.Current, id => { if (id != null) UseTheme(id); else RefreshThemes(); });
    }

    void ThemeImport_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "Add a Phoenix theme", Filter = "Phoenix theme (*.json)|*.json|All files|*.*" };
        if (pick.ShowDialog() != true) return;
        Theme? t;
        try { t = Themes.Import(pick.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Phoenix", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (t == null) { MessageBox.Show("That file isn't a Phoenix theme.", "Phoenix", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        UseTheme(t.Id);
    }

    void ThemeExport_Click(object sender, RoutedEventArgs e)
    {
        var t = Themes.Current;
        var pick = new Microsoft.Win32.SaveFileDialog { Title = "Save this theme to share", FileName = Themes.NewId(t.Name) + ".json", Filter = "Phoenix theme (*.json)|*.json", DefaultExt = ".json" };
        if (pick.ShowDialog() != true) return;
        try { Themes.Export(t, pick.FileName); ThemeNote.Text = "Saved to " + pick.FileName; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Phoenix", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ThemeDelete_Click(object sender, RoutedEventArgs e)
    {
        var t = Themes.Current;
        if (t.BuiltIn) return;
        if (MessageBox.Show($"Delete the theme \"{t.Name}\"? Its file goes from the themes folder, and Phoenix goes back to {t.Base}.", "Phoenix",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Themes.Delete(t.Id);
        if (string.Equals(S.ThemeLight, t.Id, StringComparison.OrdinalIgnoreCase)) S.ThemeLight = "light";
        if (string.Equals(S.ThemeDark, t.Id, StringComparison.OrdinalIgnoreCase)) S.ThemeDark = "dark";
        UseTheme(t.Base);
    }

    void ThemeFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(Themes.Folder); System.Diagnostics.Process.Start("explorer.exe", $"\"{Themes.Folder}\""); }
        catch (Exception ex) { ThemeNote.Text = ex.Message; }
    }
}
