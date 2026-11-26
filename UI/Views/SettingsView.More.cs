using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Settings: where the data is kept, translation and lyrics, and the Spotify tab.</summary>
public partial class SettingsView
{
    bool _loadingMore;

    // ---------- storage ----------

    void LoadStorage()
    {
        StorageBox.Text = AppPaths.DataDir;
        StorageDefaultBtn.IsEnabled = !Storage.IsStandard && AppPaths.Override == null;
        StorageChangeBtn.IsEnabled = AppPaths.Override == null;
        StorageUsage.Text = "measuring…";
        _ = Task.Run(() => Storage.Size(AppPaths.DataDir)).ContinueWith(t => Dispatcher.BeginInvoke(() => StorageUsage.Text = $"Using {Storage.SizeText(t.Result)}."));
    }

    public string StorageShown => StorageBox.Text;

    void StorageOpen_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.DataDir}\""); } catch (Exception ex) { StorageStatus.Text = ex.Message; }
    }

    async void StorageChange_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where Phoenix keeps its library, covers and caches" };
        if (pick.ShowDialog(App.Win) != true) return;
        await MoveStorage(pick.FolderName);
    }

    async void StorageDefault_Click(object sender, RoutedEventArgs e) => await MoveStorage(AppPaths.StandardDir);

    async Task MoveStorage(string target)
    {
        if (string.Equals(target.TrimEnd('\\'), AppPaths.DataDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { StorageStatus.Text = "that is the folder in use now"; return; }
        var adopt = Storage.HasLibrary(target);
        var size = await Task.Run(() => Storage.Size(AppPaths.DataDir));
        var question = adopt
            ? $"{target}\n\nalready holds a Phoenix library. Phoenix will restart and use that one. The library you are using now stays where it is, untouched."
            : $"Move Phoenix's library, settings, covers and caches ({Storage.SizeText(size)}) to\n\n{target}\n\nPhoenix copies everything, restarts from the new folder, and then removes the old copies. Your music, videos and pictures are not moved.";
        if (MessageBox.Show(App.Win, question, "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        StorageChangeBtn.IsEnabled = StorageDefaultBtn.IsEnabled = false;
        try
        {
            App.Player.Stop();
            var failed = await Storage.MoveToAsync(target, s => Dispatcher.BeginInvoke(() => StorageStatus.Text = s));
            StorageStatus.Text = failed > 0 ? $"copied, except {failed} file(s) that were in use; restarting…" : "restarting…";
            await Task.Delay(failed > 0 ? 2500 : 400);
            Storage.Restart();
        }
        catch (Exception ex)
        {
            StorageStatus.Text = "nothing was changed: " + ex.Message;
            LoadStorage();
        }
    }

    // ---------- translation and lyrics ----------

    void LoadTranslation()
    {
        _loadingMore = true;
        if (TranslateLang.Items.Count == 0)
        {
            TranslateLang.ItemsSource = Translator.Languages.Select(l => l.Name).ToList();
            TranslateService.ItemsSource = Translator.Providers.Select(p => p.Name).ToList();
            Translator.TroubleChanged += ShowTranslateState;
        }
        var s = App.Settings;
        TranslateOnBox.IsChecked = s.TranslateOn;
        TranslateLang.SelectedIndex = Math.Max(0, Array.FindIndex(Translator.Languages, l => l.Code == s.TranslateTo));
        TranslateService.SelectedIndex = Math.Max(0, Array.FindIndex(Translator.Providers, p => p.Id == s.TranslateProvider));
        TranslateLatinBox.IsChecked = s.TranslateLatin;
        LibreUrlBox.Text = s.LibreTranslateUrl;
        LyricsAutoBox.IsChecked = s.LyricsAuto;
        _loadingMore = false;
        ShowTranslateState();
    }

    void ShowTranslateState()
    {
        var provider = Translator.Providers[Math.Max(0, TranslateService.SelectedIndex)];
        _loadingMore = true;
        TranslateKey.Password = Metadata.Key(provider.Id);
        _loadingMore = false;
        TranslateKeyRow.Visibility = provider.Id == "mymemory" ? Visibility.Collapsed : Visibility.Visible;
        LibreRow.Visibility = provider.Id == "libre" ? Visibility.Visible : Visibility.Collapsed;
        TranslateHelp.Text = provider.KeyHelp ?? "";
        TranslateKeyRow.Visibility = provider.Id is "mymemory" or "local" ? Visibility.Collapsed : Visibility.Visible;
        TranslateLatinBox.IsEnabled = provider.Id != "local";
        LocalPanel.Visibility = provider.Id == "local" ? Visibility.Visible : Visibility.Collapsed;
        if (provider.Id == "local") _ = ShowPacksAsync();
        TranslateStatus.Text = Translator.Trouble;
    }

    void TranslationChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingMore || !IsInitialized) return;
        var s = App.Settings;
        s.TranslateOn = TranslateOnBox.IsChecked == true;
        s.TranslateTo = Translator.Languages[Math.Max(0, TranslateLang.SelectedIndex)].Code;
        s.TranslateProvider = Translator.Providers[Math.Max(0, TranslateService.SelectedIndex)].Id;
        s.TranslateLatin = TranslateLatinBox.IsChecked == true;
        s.LibreTranslateUrl = LibreUrlBox.Text.Trim();
        s.LyricsAuto = LyricsAutoBox.IsChecked == true;
        if (ReferenceEquals(sender, TranslateKey)) s.ApiKeys[s.TranslateProvider] = TranslateKey.Password.Trim();
        if (!App.SmokeMode) s.Save();
        Translator.Reset();
        ShowTranslateState();
        App.Lib.NotifyChanged();          // lists redraw with (or without) translations
        App.Win.RefreshNowText();
    }

    void TranslationSelection(object sender, SelectionChangedEventArgs e) => TranslationChanged(sender, e);

    public int TranslateLanguageCount => TranslateLang.Items.Count;

    // ---------- language packs for translating on this PC ----------

    static readonly string[] PackLanguages = { "ja", "ko", "zh", "ru", "uk", "ar", "he", "el", "th", "hi" };   // the languages told apart by script
    bool _packBusy;

    async Task ShowPacksAsync()
    {
        PackFolderBox.Text = LocalTranslator.Folder;
        var size = LocalTranslator.Size();
        PackUsage.Text = size == 0 ? "No language packs are installed." : $"Language packs installed: {Storage.SizeText(size)}.";
        var to = App.Settings.TranslateTo;
        List<LocalTranslator.Pack> catalogue;
        try { catalogue = await LocalTranslator.CatalogueAsync(); }
        catch (Exception ex)
        {
            // offline: show what is installed, which keeps working
            PackList.Children.Clear();
            PackStatus.Text = "The list of language packs could not be fetched (" + ex.Message + "). Packs already installed still work.";
            return;
        }
        PackList.Children.Clear();
        foreach (var from in PackLanguages.Where(l => l != to))
        {
            var route = LocalTranslator.Route(catalogue, from, to);
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var name = new TextBlock { Text = LocalTranslator.LanguageName(from), Width = 110, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            if (route.Count == 0)
            {
                var none = new TextBlock { Text = $"no pack into {LocalTranslator.LanguageName(to)} is published", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                none.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
                row.Children.Add(none);
            }
            else
            {
                var missing = route.Where(p => !LocalTranslator.Installed(p)).ToList();
                var installed = missing.Count == 0;
                var act = new Button { Content = installed ? "REMOVE" : "DOWNLOAD", Style = (Style)FindResource("PillBtn"), Margin = new Thickness(0, 0, 12, 0), MinWidth = 96 };
                act.Click += async (_, _) => { if (installed) await RemovePackAsync(from, route); else await DownloadPacksAsync(missing); };
                row.Children.Add(act);
                var detail = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                    Text = installed ? "installed" + (route.Count == 2 ? " (through English)" : "")
                        : $"{missing.Sum(p => p.Size) >> 20} MB" + (route.Count == 2 ? " (goes through English: two packs)" : "")
                };
                detail.SetResourceReference(TextBlock.ForegroundProperty, installed ? "Accent" : "FgDim");
                row.Children.Add(detail);
            }
            PackList.Children.Add(row);
        }
    }

    public int PackRows => PackList.Children.Count;

    async Task DownloadPacksAsync(List<LocalTranslator.Pack> packs)
    {
        if (_packBusy) return;
        _packBusy = true;
        try
        {
            foreach (var p in packs) await LocalTranslator.DownloadAsync(p, s => Dispatcher.BeginInvoke(() => PackStatus.Text = s));
            PackStatus.Text = "language pack installed";
            Translator.Reset();
            App.Lib.NotifyChanged();
            App.Win.RefreshNowText();
        }
        catch (Exception ex) { PackStatus.Text = "couldn't download the language pack: " + ex.Message; }
        finally { _packBusy = false; }
        await ShowPacksAsync();
    }

    async Task RemovePackAsync(string from, List<LocalTranslator.Pack> route)
    {
        if (_packBusy) return;
        _packBusy = true;
        try
        {
            // the pack out of English may be shared with other languages: remove only this language's own pack
            await LocalTranslator.RemoveAsync(route[0].From, route[0].To);
            PackStatus.Text = $"{LocalTranslator.LanguageName(from)} pack removed";
        }
        finally { _packBusy = false; }
        await ShowPacksAsync();
    }

    async void PackFolderMove_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for the translation language packs" };
        if (pick.ShowDialog(App.Win) != true) return;
        await MovePacks(pick.FolderName);
    }

    async void PackFolderDefault_Click(object sender, RoutedEventArgs e) => await MovePacks("");

    async Task MovePacks(string target)
    {
        if (_packBusy) return;
        _packBusy = true;
        PackStatus.Text = "moving the language packs…";
        try { await LocalTranslator.MoveToAsync(target); PackStatus.Text = "language packs moved"; }
        catch (Exception ex) { PackStatus.Text = "nothing was moved: " + ex.Message; }
        finally { _packBusy = false; }
        await ShowPacksAsync();
    }

    // ---------- streaming services ----------

    void LoadWeb()
    {
        WebMusicPanel.Children.Clear();
        WebVideoPanel.Children.Clear();
        WebCustomPanel.Children.Clear();
        foreach (var service in WebServices.Presets)
        {
            var box = new CheckBox { Content = service.Name, IsChecked = WebServices.IsOn(service), VerticalAlignment = VerticalAlignment.Center, ToolTip = service.Url };
            var place = PlaceBox(service);
            box.Click += (_, _) => { WebServices.Set(service, box.IsChecked == true); place.IsEnabled = box.IsChecked == true; };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(place, Dock.Right);
            row.Children.Add(place);
            row.Children.Add(box);
            (service.Video ? WebVideoPanel : WebMusicPanel).Children.Add(row);
        }
        foreach (var custom in App.Settings.CustomServices.ToList())
        {
            var service = WebServices.Custom.FirstOrDefault(c => c.Id == "custom-" + custom.Id);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = "remove", Style = (Style)FindResource("TextBtn"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { App.Settings.CustomServices.Remove(custom); if (!App.SmokeMode) App.Settings.Save(); WebServices.RaiseChanged(); LoadWeb(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            if (service != null) { var place = PlaceBox(service); DockPanel.SetDock(place, Dock.Right); row.Children.Add(place); }
            var text = new TextBlock { Text = $"{custom.Name}   ·   {custom.Url}", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            WebCustomPanel.Children.Add(row);
        }
    }

    public int WebChoices => WebMusicPanel.Children.Count + WebVideoPanel.Children.Count;

    /// <summary>The "show in" choice beside a service: its own tab, under a collection, or in a tab of your own.</summary>
    ComboBox PlaceBox(WebService service)
    {
        var places = WebServices.Places();
        var box = new ComboBox { Width = 190, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), IsEnabled = WebServices.IsOn(service), ToolTip = "Where " + service.Name + " is shown in your collection",
            ItemsSource = places.Select(p => p.Name).ToList() };
        box.SelectedIndex = Math.Max(0, places.FindIndex(p => p.Place == WebServices.PlaceOf(service)));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) WebServices.SetPlace(service, places[box.SelectedIndex].Place); };
        return box;
    }

    void WebAdd_Click(object sender, RoutedEventArgs e) => AddCustomService(WebNameBox.Text, WebUrlBox.Text, WebKindBox.SelectedIndex == 1);

    public bool AddCustomService(string name, string url, bool video)
    {
        name = name.Trim();
        var address = WebServices.Address(url);
        if (name == "" || address == "") { WebStatus.Text = "give it a name and an address"; return false; }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file")) { WebStatus.Text = "that address is not a web page or a file"; return false; }
        if (uri.Scheme == "file" && !System.IO.File.Exists(uri.LocalPath)) { WebStatus.Text = "that file was not found"; return false; }
        App.Settings.CustomServices.Add(new CustomService { Name = name, Url = url.Trim(), Video = video });
        if (!App.SmokeMode) App.Settings.Save();
        WebNameBox.Text = WebUrlBox.Text = "";
        WebStatus.Text = video ? $"“{name}” is on the videos page; choose “show in” to move it" : $"“{name}” has a tab of its own; choose “show in” to move it";
        WebServices.RaiseChanged();
        LoadWeb();
        return true;
    }

    // ---------- when the window is closed ----------

    void RunInTray_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.RunInTray = RunInTrayBox.IsChecked == true;
        if (!App.SmokeMode) App.Settings.Save();
        Tray.Sync();
    }

    // ---------- quickplay ----------

    void QuickplayPlays_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.QuickplayPlays = QuickplayPlaysBox.IsChecked == true;
        if (!App.SmokeMode) App.Settings.Save();
    }

}
