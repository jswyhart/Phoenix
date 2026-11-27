using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;
using Microsoft.Win32;

namespace Halcyon.UI;

public partial class SettingsView : UserControl
{
    static Settings S => App.Settings;

    /// <summary>The folder lists would otherwise swallow the mouse wheel; pass it to the page unless the list itself can still scroll that way.</summary>
    void Page_Wheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // SmoothScroll now passes the wheel on from a list that cannot scroll to the page around it, and glides
        if (!e.Handled) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != PageScroll; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is not ScrollViewer inner) continue;
            var up = e.Delta > 0;
            if (inner.ScrollableHeight > 0 && (up ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight)) return;
        }
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }
    CancellationTokenSource? _autoMatch;

    // SOFTWARE / PHONE / SERVERS, each with its categories and the panel a category shows
    static readonly Dictionary<string, (string name, string panel)[]> Categories = new()
    {
        ["software"] = new[] { ("Collection", "PanelCollection"), ("Rip", "PanelRip"), ("Burn", "PanelBurn"), ("Metadata", "PanelMetadata"), ("Playback", "PanelPlayback"), ("Listening", "PanelListening"), ("Podcasts", "PanelPodcasts"), ("Translation", "PanelTranslation"), ("Display", "PanelDisplay"), ("Storage", "PanelStorage"), ("Updates", "PanelAbout") },
        ["phone"] = new[] { ("Sync options", "PanelPhone") },
        ["servers"] = new[] { ("Jellyfin", "PanelJellyfin"), ("Plex", "PanelPlex"), ("Streaming", "PanelWeb") }
    };
    string _pivot = "software";
    bool _settingUp;

    /// <summary>Shows one of the three groups and, optionally, a category inside it (by name).</summary>
    public void ShowCategory(string pivot, string? category = null)
    {
        _pivot = Categories.ContainsKey(pivot) ? pivot : "software";
        PivotSoftware.IsChecked = _pivot == "software";
        PivotPhone.IsChecked = _pivot == "phone";
        PivotServers.IsChecked = _pivot == "servers";
        var items = Categories[_pivot].Select(c => c.name).ToList();
        CategoryList.ItemsSource = items;
        var index = category == null ? 0 : Math.Max(0, items.FindIndex(n => string.Equals(n, category, StringComparison.OrdinalIgnoreCase)));
        CategoryList.SelectedIndex = index;
    }

    void Pivot_Click(object sender, RoutedEventArgs e) => ShowCategory((string)((FrameworkElement)sender).Tag);

    void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedIndex < 0) return;
        var show = Categories[_pivot][CategoryList.SelectedIndex].panel;
        foreach (var panel in Categories.Values.SelectMany(v => v).Select(c => c.panel))
            ((FrameworkElement)FindName(panel)).Visibility = panel == show ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        if (show == "PanelDisplay") { LoadDisplay(); LoadBackdrops(); }
        if (show == "PanelPlayback") { LoadPlayback(); LoadLevelling(); }
        if (show == "PanelListening") LoadListening();
        if (show == "PanelAbout") LoadAbout();
        if (show == "PanelMetadata") BuildSources();
        if (show == "PanelStorage") LoadStorage();
        if (show == "PanelTranslation") LoadTranslation();
        if (show == "PanelWeb") LoadWeb();
        if (show == "PanelPodcasts") LoadPodcasts();
        if (show == "PanelPhone") RefreshSync();
    }

    void Ok_Click(object sender, RoutedEventArgs e) => App.Win.GoBackOr("quickplay");
    bool _loadingBurn;

    void LoadBurn()
    {
        _loadingBurn = true;
        var kind = Math.Clamp(S.BurnKind, 0, 2);
        BurnAudioRadio.IsChecked = kind == 0;
        BurnMp3Radio.IsChecked = kind == 1;
        BurnDataRadio.IsChecked = kind == 2;
        BurnKbpsBox.SelectedItem = BurnKbpsBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.BurnMp3Kbps) ?? BurnKbpsBox.Items[2];
        _loadingBurn = false;
    }

    void BurnKind_Click(object sender, RoutedEventArgs e)
    {
        S.BurnKind = int.Parse((string)((FrameworkElement)sender).Tag);
        S.Save();
    }

    void BurnKbps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBurn || BurnKbpsBox.SelectedItem is not ComboBoxItem { Tag: int kbps }) return;
        S.BurnMp3Kbps = kbps;
        S.Save();
    }

    void ViewBurnList_Click(object sender, RoutedEventArgs e) { App.Win.Navigate("disc"); App.Win.Page<DiscView>("disc").ShowPivot("burn"); }
    void ViewPhone_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("phone");

    void LoadDisplay()
    {
        QuickplayPlaysBox.IsChecked = App.Settings.QuickplayPlays;
        RunInTrayBox.IsChecked = App.Settings.RunInTray;
        NowPanelBox.IsChecked = App.Settings.NowPanel;
        _settingUp = true;
        VizStyle.ItemsSource = Visualizer.Styles;
        VizColour.ItemsSource = Visualizer.Palettes;
        VizSensitivity.ItemsSource = Visualizer.Sensitivities;
        VizStyle.SelectedIndex = Math.Clamp(S.Visualizer, 0, Visualizer.Styles.Length - 1);
        VizColour.SelectedIndex = Math.Clamp(S.VisualizerColor, 0, Visualizer.Palettes.Length - 1);
        VizSensitivity.SelectedIndex = Math.Clamp(S.VisualizerSensitivity, 0, Visualizer.Sensitivities.Length - 1);
        _settingUp = false;
    }

    void Viz_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingUp || VizStyle.SelectedIndex < 0 || VizColour.SelectedIndex < 0 || VizSensitivity.SelectedIndex < 0) return;
        S.VisualizerSensitivity = VizSensitivity.SelectedIndex;
        App.Win.SetVisualizer(VizStyle.SelectedIndex, VizColour.SelectedIndex);
        S.Save();
    }

    public SettingsView()
    {
        InitializeComponent();
        foreach (var i in CdAudio.FormatOrder)
        {
            var index = i;
            var rb = new RadioButton { Content = CdAudio.Formats[i], GroupName = "ripFormat", IsChecked = S.RipFormat == i, Margin = new Thickness(0, 0, 0, 8) };
            rb.SetResourceReference(ForegroundProperty, "Fg");
            rb.Click += (_, _) => { S.RipFormat = index; S.Save(); };
            RipFormats.Children.Add(rb);
        }
        foreach (var kbps in new[] { 128, 192, 256, 320 }) BurnKbpsBox.Items.Add(new ComboBoxItem { Content = $"{kbps} kbps", Tag = kbps });
        LoadBurn();
        ShowCategory("software");
        RefreshFolders();
        RefreshThemes();
        WriteTags.IsChecked = S.WriteTagsToFiles;
        JfUrl.Text = S.JellyfinUrl;
        JfUser.Text = S.JellyfinUser;
        PlexUrl.Text = S.PlexUrl;
        PlexToken.Password = S.PlexToken;
        App.Lib.Changed += RefreshStatus;
        RefreshStatus();
        SyncEnabled.IsChecked = S.SyncEnabled;
        RefreshSync();
        PhonePairing.Changed += () => Dispatcher.BeginInvoke(() => { if (PanelPhone.Visibility == Visibility.Visible) RefreshSync(); });
        // the QR code's token runs out after ten minutes: a fresh one while the page is open
        _qrTimer.Tick += (_, _) => { if (PanelPhone.IsVisible) RefreshSync(); else _qrTimer.Stop(); };
    }

    readonly System.Windows.Threading.DispatcherTimer _qrTimer = new() { Interval = TimeSpan.FromMinutes(4) };

    /// <summary>The QR code a phone scans to pair, and the phones paired so far.</summary>
    void ShowPairing()
    {
        var on = S.SyncEnabled && App.Sync.IsRunning;
        SyncPairPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { _qrTimer.Stop(); return; }
        if (PhonePairing.QrUrl(SyncServer.Port) is { } url)
        {
            var png = PhonePairing.QrPng(url);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.StreamSource = new System.IO.MemoryStream(png);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            SyncQr.Source = img;
            SyncQr.ToolTip = url[..url.IndexOf('#')];
        }
        _qrTimer.Start();
        PairedPhones.Children.Clear();
        var phones = PhonePairing.Phones.OrderByDescending(p => p.LastSeen).ToList();
        if (phones.Count == 0)
            PairedPhones.Children.Add(new TextBlock { Text = "none yet", Style = (Style)FindResource("Note"), Margin = new Thickness(0) });
        foreach (var phone in phones)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
            var forget = Make.Button("FORGET", "PillBtn", () =>
            {
                if (MessageBox.Show(App.Win, $"Forget “{phone.Name}”? It will have to pair again to sync with this PC. Music already on the phone stays there.", "Phoenix",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                PhonePairing.Forget(phone.Id);
                RefreshSync();
            }, "This phone has to pair again to sync", new Thickness(12, 0, 0, 0));
            DockPanel.SetDock(forget, Dock.Right);
            row.Children.Add(forget);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = phone.Name, FontSize = 14 });
            var when = phone.LastSync > 0 ? $"last synced {Fmt.Ago(phone.LastSync)}" + (phone.LastSummary != "" ? $" ({phone.LastSummary})" : "")
                     : phone.LastSeen > 0 ? $"paired {Fmt.Ago(phone.Paired)}, last seen {Fmt.Ago(phone.LastSeen)}" : $"paired {Fmt.Ago(phone.Paired)}";
            var sub = new TextBlock { Text = when, TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            text.Children.Add(sub);
            row.Children.Add(text);
            PairedPhones.Children.Add(row);
        }
    }

    void RefreshSync()
    {
        ShowPairing();
        SyncNewCodeBtn.IsEnabled = S.SyncEnabled;
        RemoteAddress.Text = S.SyncEnabled && App.Sync.IsRunning
            ? string.Join("   or   ", SyncServer.LocalAddresses().Select(a => $"http://{a}:{SyncServer.Port}/remote")) : "Turn sync on above to use the remote.";
        if (!S.SyncEnabled) { SyncStatus.Text = "off"; return; }
        if (!App.Sync.IsRunning)
        {
            SyncStatus.Text = "couldn't start: " + (App.Sync.LastError ?? "unknown error");
            return;
        }
        var addresses = SyncServer.LocalAddresses();
        SyncStatus.Text = $"pairing code  {S.SyncCode}\n" +
            (addresses.Count == 0 ? "this PC is not on a network" : "this PC's address  " + string.Join("  or  ", addresses)) +
            "\nIf the phone can't find this PC, check both are on the same Wi-Fi, and allow Phoenix through Windows Firewall for private networks.";
    }

    void SyncEnabled_Click(object sender, RoutedEventArgs e)
    {
        S.SyncEnabled = SyncEnabled.IsChecked == true;
        S.Save();
        if (S.SyncEnabled) App.Sync.Start(); else App.Sync.Stop();
        RefreshSync();
    }

    void SyncNewCode_Click(object sender, RoutedEventArgs e)
    {
        S.SyncCode = SyncServer.NewCode();
        S.Save();
        RefreshSync();
    }

    void RefreshStatus()
    {
        var jf = App.Lib.Tracks.Count(t => t.Source == "jellyfin");
        var jfv = App.Lib.Videos.Count(v => v.Source == "jellyfin");
        JfSyncBtn.IsEnabled = JfDisconnectBtn.IsEnabled = App.Jellyfin.IsConnected;
        if (JfStatus.Text == "" || JfStatus.Text.StartsWith("connected") || JfStatus.Text.StartsWith("not connected"))
            JfStatus.Text = App.Jellyfin.IsConnected ? $"connected as {S.JellyfinUser}  ·  {jf} songs, {jfv} videos" : "not connected";
        var px = App.Lib.Tracks.Count(t => t.Source == "plex");
        var pxv = App.Lib.Videos.Count(v => v.Source == "plex");
        PlexDisconnectBtn.IsEnabled = App.Plex.IsConnected;
        if (PlexStatus.Text == "" || PlexStatus.Text.StartsWith("connected") || PlexStatus.Text.StartsWith("not connected"))
            PlexStatus.Text = App.Plex.IsConnected ? $"connected  ·  {px} songs, {pxv} videos" : "not connected";
    }

    // ---------- folders ----------

    List<string> Folders(string kind) => kind switch { "music" => S.MusicFolders, "video" => S.VideoFolders, "book" => S.BookFolders, _ => S.PictureFolders };
    ListBox FolderBox(string kind) => kind switch { "music" => MusicFolders, "video" => VideoFolders, "book" => BookFolders, _ => PictureFolders };

    void RefreshFolders()
    {
        MusicFolders.ItemsSource = S.MusicFolders.ToList();
        VideoFolders.ItemsSource = S.VideoFolders.ToList();
        PictureFolders.ItemsSource = S.PictureFolders.ToList();
        BookFolders.ItemsSource = S.BookFolders.ToList();
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var kind = (string)((FrameworkElement)sender).Tag;
        var dlg = new OpenFolderDialog { Title = $"Choose a {kind} folder", Multiselect = true };
        if (dlg.ShowDialog(App.Win) != true) return;
        var list = Folders(kind);
        foreach (var f in dlg.FolderNames)
            if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        FoldersChanged(kind);
    }

    void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        var kind = (string)((FrameworkElement)sender).Tag;
        if (FolderBox(kind).SelectedItem is not string f) return;
        Folders(kind).Remove(f);
        FoldersChanged(kind);
    }

    void FoldersChanged(string kind)
    {
        S.Save();
        RefreshFolders();
        if (kind == "picture") _ = App.Win.Page<PicturesView>("pictures").ReloadAsync();
        else Rescan_Click(this, new RoutedEventArgs());
    }

    async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanBtn.IsEnabled = false;
        await App.RefreshCollectionAsync(s => ScanStatus.Text = s);
        RefreshStatus();
        ScanStatus.Text = $"{App.Lib.Tracks.Count} songs and {App.Lib.Videos.Count} videos in your collection";
        RescanBtn.IsEnabled = true;
    }

    // ---------- jellyfin ----------

    async void JfConnect_Click(object sender, RoutedEventArgs e)
    {
        if (JfUrl.Text.Trim() == "" || JfUser.Text.Trim() == "") { JfStatus.Text = "enter the server address and user name"; return; }
        JfConnectBtn.IsEnabled = false;
        try
        {
            JfStatus.Text = "signing in…";
            await App.Jellyfin.LoginAsync(JfUrl.Text, JfUser.Text.Trim(), JfPassword.Password);
            JfPassword.Clear();
            JfUrl.Text = S.JellyfinUrl;
            await SyncJellyfin();
        }
        catch (Exception ex) { JfStatus.Text = "couldn't connect: " + ex.Message; }
        finally { JfConnectBtn.IsEnabled = true; RefreshStatus(); }
    }

    async void JfSync_Click(object sender, RoutedEventArgs e)
    {
        try { await SyncJellyfin(); }
        catch (Exception ex) { JfStatus.Text = "sync failed: " + ex.Message; }
        RefreshStatus();
    }

    async Task SyncJellyfin()
    {
        JfSyncBtn.IsEnabled = false;
        var (tracks, videos) = await App.Jellyfin.FetchAsync(s => JfStatus.Text = s);
        JfStatus.Text = "saving…";
        await App.Lib.ReplaceSourceAsync("jellyfin", tracks, videos);
        await App.Lib.ReplaceBooksAsync("jellyfin", await App.Jellyfin.FetchBooksAsync(s => JfStatus.Text = s));
        JfStatus.Text = "";
    }

    async void JfDisconnect_Click(object sender, RoutedEventArgs e)
    {
        App.Jellyfin.Disconnect();
        await App.Lib.RemoveSourceAsync("jellyfin");
        JfStatus.Text = "";
        RefreshStatus();
    }

    // ---------- plex ----------

    async void PlexSignIn_Click(object sender, RoutedEventArgs e)
    {
        PlexSignInBtn.IsEnabled = false;
        try
        {
            var (id, _, url) = await App.Plex.CreatePinAsync();
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            PlexStatus.Text = "approve Phoenix in the browser window that just opened…";
            var token = "";
            for (var i = 0; i < 90 && token == ""; i++)
            {
                await Task.Delay(2000);
                token = await App.Plex.CheckPinAsync(id);
            }
            if (token == "") { PlexStatus.Text = "sign-in timed out — try again"; return; }
            var servers = await App.Plex.GetServersAsync(token);
            if (servers.Count == 0) { PlexStatus.Text = "signed in, but no Plex Media Server was found on this account"; return; }
            PlexServers.ItemsSource = servers;
            PlexServers.Visibility = Visibility.Visible;
            PlexServers.SelectedIndex = 0;
            PlexStatus.Text = "signed in — pick a server connection, then choose “connect & sync”";
        }
        catch (Exception ex) { PlexStatus.Text = "sign-in failed: " + ex.Message; }
        finally { PlexSignInBtn.IsEnabled = true; }
    }

    void PlexServers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlexServers.SelectedItem is not PlexServer s) return;
        PlexUrl.Text = s.Uri;
        PlexToken.Password = s.Token;
    }

    async void PlexConnect_Click(object sender, RoutedEventArgs e)
    {
        if (PlexUrl.Text.Trim() == "" || PlexToken.Password.Trim() == "") { PlexStatus.Text = "enter the server address and token, or sign in with Plex"; return; }
        PlexConnectBtn.IsEnabled = false;
        try
        {
            S.PlexUrl = JellyfinClient.NormalizeUrl(PlexUrl.Text);
            S.PlexToken = PlexToken.Password.Trim();
            PlexUrl.Text = S.PlexUrl;
            PlexStatus.Text = "connecting…";
            await App.Plex.TestAsync();
            S.Save();
            var (tracks, videos) = await App.Plex.FetchAsync(s => PlexStatus.Text = s);
            PlexStatus.Text = "saving…";
            await App.Lib.ReplaceSourceAsync("plex", tracks, videos);
            PlexStatus.Text = "";
        }
        catch (Exception ex) { PlexStatus.Text = "couldn't connect: " + ex.Message; }
        finally { PlexConnectBtn.IsEnabled = true; RefreshStatus(); }
    }

    async void PlexDisconnect_Click(object sender, RoutedEventArgs e)
    {
        S.PlexToken = "";
        S.Save();
        PlexToken.Clear();
        await App.Lib.RemoveSourceAsync("plex");
        PlexStatus.Text = "";
        RefreshStatus();
    }
}
