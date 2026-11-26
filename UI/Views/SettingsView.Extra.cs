using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Settings: volume levelling, scrobbling, artist photos, backup and restore, library care, the remote, and updates.</summary>
public partial class SettingsView
{
    static void Keep() { if (!App.SmokeMode) S.Save(); }

    void Care_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("care");
    void ShowStats_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("stats");

    // ---------- volume levelling ----------

    void LoadLevelling()
    {
        LevelOn.IsChecked = S.Levelling;
        LevelAlbum.IsChecked = S.LevelByAlbum;
        LevelMeasureBox.IsChecked = S.LevelMeasure;
        LevelOn.IsEnabled = S.AudioEngine;
        LevelAlbum.IsEnabled = LevelMeasureBox.IsEnabled = S.Levelling && S.AudioEngine;
    }

    void Level_Click(object sender, RoutedEventArgs e)
    {
        S.Levelling = LevelOn.IsChecked == true;
        S.LevelByAlbum = LevelAlbum.IsChecked == true;
        S.LevelMeasure = LevelMeasureBox.IsChecked == true;
        Keep();
        App.Player.ApplyAudioSettings();   // the song that is playing changes over a second or two
        LoadLevelling();
    }

    // ---------- listening: the play log and scrobbling ----------

    void LoadListening()
    {
        RadioOn.IsChecked = S.RadioEnabled;
        LbOn.IsChecked = S.ScrobbleListenBrainz;
        LfmOn.IsChecked = S.ScrobbleLastFm;
        LbToken.Password = S.ApiKeys.GetValueOrDefault("listenbrainz", "");
        LfmKey.Password = S.ApiKeys.GetValueOrDefault("lastfm", "");
        LfmSecret.Password = S.ApiKeys.GetValueOrDefault("lastfm_secret", "");
        ShowListening();
    }

    void ShowListening()
    {
        LbStatus.Text = !Listening.ListenBrainzReady ? "no token entered" : S.ListenBrainzUser != "" ? $"connected as {S.ListenBrainzUser}" : "token entered (CHECK tells you whether it works)";
        LfmStatus.Text = Listening.LastFmReady ? $"connected as {S.LastFmUser}" : S.ApiKeys.GetValueOrDefault("lastfm", "") == "" || S.ApiKeys.GetValueOrDefault("lastfm_secret", "") == "" ? "enter the key and secret, then CONNECT" : "not connected yet: choose CONNECT";
        LfmSignOutBtn.IsEnabled = Listening.LastFmReady;
        ScrobbleResult.Text = Listening.LastResult;
    }

    void RadioOn_Click(object sender, RoutedEventArgs e) => SetTab("radio", RadioOn.IsChecked == true);

    // ---------- which collection tabs are shown ----------

    /// <summary>Switches a collection tab on or off (the last one left stays on). Returns whether it was done.</summary>
    public bool SetTab(string tab, bool on)
    {
        var done = S.SetTab(tab, on);
        Keep();
        App.Win.ShowTabs();
        App.Lib.NotifyChanged();   // quickplay shows or hides pinned stations
        LoadTabs();
        RadioOn.IsChecked = S.TabOn("radio");
        if (!done) App.Win.SetStatus("one tab has to stay on");
        return done;
    }

    void LoadTabs()
    {
        TabChecks.Children.Clear();
        foreach (var tab in Settings.Tabs)
        {
            var name = tab;
            var box = new CheckBox { Content = tab, IsChecked = S.TabOn(tab), Margin = new Thickness(0, 4, 26, 4), MinWidth = 96 };
            box.Click += (_, _) => SetTab(name, box.IsChecked == true);
            TabChecks.Children.Add(box);
        }
    }

    public List<CheckBox> TabBoxes => TabChecks.Children.OfType<CheckBox>().ToList();

    void LoadCustomTabs()
    {
        CustomTabRows.Children.Clear();
        foreach (var tab in S.CustomTabs.ToList())
        {
            var t = tab;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = Make.Button("remove", "TextBtn", () => { WebServices.RemoveTab(t); LoadWeb(); }, "The services in it go back to where they were before", new Thickness(12, 0, 0, 0));
            var rename = Make.Button("rename", "TextBtn", () =>
            {
                var name = InputDialog.Ask("Rename tab", "Name", t.Name);
                if (!string.IsNullOrWhiteSpace(name)) { WebServices.RenameTab(t, name); LoadWeb(); }
            }, null, new Thickness(12, 0, 0, 0));
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(rename, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(rename);
            var count = WebServices.Under(t.Page).Count;
            row.Children.Add(Make.Text($"{t.Name}   ·   {(count == 0 ? "empty" : count == 1 ? "1 service" : $"{count} services")}", 13));
            CustomTabRows.Children.Add(row);
        }
    }

    void AddTab_Click(object sender, RoutedEventArgs e) => AddTab(NewTabName.Text);

    /// <summary>Adds a collection tab of your own (also used by the self-test).</summary>
    public CustomTab? AddTab(string name)
    {
        name = name.Trim();
        if (name == "") { App.Win.SetStatus("give the tab a name"); return null; }
        var tab = WebServices.AddTab(name);
        NewTabName.Text = "";
        LoadWeb();   // the new tab is offered under each service's "show in"
        App.Win.SetStatus($"“{name}” added to your collection tabs; choose it under a service's “show in”");
        return tab;
    }

    void Scrobble_Click(object sender, RoutedEventArgs e)
    {
        S.ScrobbleListenBrainz = LbOn.IsChecked == true;
        S.ScrobbleLastFm = LfmOn.IsChecked == true;
        Keep();
        ShowListening();
    }

    void ScrobbleKeys_Lost(object sender, RoutedEventArgs e)
    {
        void Set(string id, string value) { if (value.Trim() == "") S.ApiKeys.Remove(id); else S.ApiKeys[id] = value.Trim(); }
        var lb = S.ApiKeys.GetValueOrDefault("listenbrainz", "");
        Set("listenbrainz", LbToken.Password);
        if (lb != S.ApiKeys.GetValueOrDefault("listenbrainz", "")) S.ListenBrainzUser = "";
        Set("lastfm", LfmKey.Password);
        Set("lastfm_secret", LfmSecret.Password);
        Keep();
        ShowListening();
    }

    async void LbCheck_Click(object sender, RoutedEventArgs e)
    {
        ScrobbleKeys_Lost(sender, e);
        if (!Listening.ListenBrainzReady) return;
        LbStatus.Text = "asking ListenBrainz…";
        try
        {
            S.ListenBrainzUser = await Listening.ListenBrainzCheckAsync(S.ApiKeys["listenbrainz"]);
            Keep();
            LbStatus.Text = S.ListenBrainzUser != "" ? $"connected as {S.ListenBrainzUser}" : "ListenBrainz does not accept that token";
        }
        catch (Exception ex) { LbStatus.Text = "couldn't reach ListenBrainz: " + ex.Message; }
    }

    async void LfmConnect_Click(object sender, RoutedEventArgs e)
    {
        ScrobbleKeys_Lost(sender, e);
        if (S.ApiKeys.GetValueOrDefault("lastfm", "") == "" || S.ApiKeys.GetValueOrDefault("lastfm_secret", "") == "") { LfmStatus.Text = "enter the API key and the shared secret first"; return; }
        LfmConnectBtn.IsEnabled = false;
        try
        {
            var token = await Listening.LastFmBeginAsync();
            LfmStatus.Text = "allow Phoenix in the browser window that just opened…";
            var name = "";
            for (var i = 0; i < 90 && name == ""; i++)
            {
                await Task.Delay(2000);
                name = await Listening.LastFmFinishAsync(token);
            }
            LfmStatus.Text = name == "" ? "that took too long: choose CONNECT again" : $"connected as {name}";
            if (name != "") { S.ScrobbleLastFm = true; LfmOn.IsChecked = true; Keep(); }
        }
        catch (Exception ex) { LfmStatus.Text = "couldn't connect: " + ex.Message; }
        finally { LfmConnectBtn.IsEnabled = true; LfmSignOutBtn.IsEnabled = Listening.LastFmReady; }
    }

    void LfmSignOut_Click(object sender, RoutedEventArgs e)
    {
        Listening.LastFmSignOut();
        S.ScrobbleLastFm = false;
        LfmOn.IsChecked = false;
        Keep();
        ShowListening();
    }

    // ---------- artist photos ----------

    void LoadBackdrops()
    {
        LoadTabs();
        BackdropsBox.IsChecked = S.Backdrops;
        var (artists, bytes) = Backdrops.Usage();
        BackdropUsage.Text = artists == 0 ? "No photos kept yet." : $"Photos of {artists} artist{(artists == 1 ? "" : "s")} kept, using {Storage.SizeText(bytes)}.";
    }

    void Backdrops_Click(object sender, RoutedEventArgs e)
    {
        S.Backdrops = BackdropsBox.IsChecked == true;
        Keep();
        App.Win.RefreshBackdrop();
    }

    void BackdropsClear_Click(object sender, RoutedEventArgs e)
    {
        App.Win.BackdropLayer.Show(new List<string>());
        Backdrops.Clear();
        App.Win.RefreshBackdrop();
        LoadBackdrops();
    }

    // ---------- backup and restore ----------

    async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.SaveFileDialog { Title = "Save a backup of Phoenix", FileName = Backup.SuggestedName, Filter = $"Phoenix backup (*{Backup.Extension})|*{Backup.Extension}", DefaultExt = Backup.Extension };
        if (pick.ShowDialog(App.Win) != true) return;
        try
        {
            var size = await Backup.CreateAsync(pick.FileName, BackupCovers.IsChecked == true, text => Dispatcher.BeginInvoke(() => BackupStatus.Text = text));
            BackupStatus.Text = $"backed up to {pick.FileName} ({Storage.SizeText(size)})";
        }
        catch (Exception ex) { BackupStatus.Text = "couldn't make the backup: " + ex.Message; }
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "Restore Phoenix from a backup", Filter = $"Phoenix backup (*{Backup.Extension})|*{Backup.Extension}|All files|*.*" };
        if (pick.ShowDialog(App.Win) != true) return;
        var m = Backup.Read(pick.FileName);
        if (m == null) { BackupStatus.Text = "that file is not a Phoenix backup"; return; }
        var when = DateTime.TryParse(m.Created, out var made) ? made.ToString("f") : m.Created;
        var text = $"Restore the backup made on {when}?\n\nIt holds {m.Songs:N0} songs' details and {m.Playlists} playlists{(m.Covers ? ", with covers" : "")}.\n\n" +
                   "Your library and settings as they are now are replaced by the ones in the backup. Phoenix restarts to do it; the library in use now is kept beside it as “library.before-restore.db”. Your music files are not touched.";
        if (MessageBox.Show(App.Win, text, "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Backup.Stage(pick.FileName);
            Storage.Restart();
        }
        catch (Exception ex) { BackupStatus.Text = "couldn't restore: " + ex.Message; }
    }

    // ---------- about and updates ----------

    void LoadAbout()
    {
        AboutVersion.Text = $"Phoenix {Updater.CurrentText}  ·  {(Updater.Arch == "arm64" ? "ARM64" : "64-bit")}  ·  {(Updater.IsInstalled ? "installed" : "portable copy")}";
        UpdatesOn.IsChecked = S.CheckUpdates;
        AutoUpdateOn.IsChecked = S.AutoUpdate;
        KeepPreviousOn.IsChecked = S.KeepPrevious;
        ShowUpdate(Updater.Latest, checkedNow: false);
        ShowRevert();
    }

    void UpdatesOn_Click(object sender, RoutedEventArgs e)
    {
        S.CheckUpdates = UpdatesOn.IsChecked == true;
        S.AutoUpdate = AutoUpdateOn.IsChecked == true;
        S.KeepPrevious = KeepPreviousOn.IsChecked == true;
        if (!S.KeepPrevious) Updater.ForgetKept();
        Keep();
        ShowRevert();
    }

    void ShowRevert()
    {
        // updating by itself needs the daily look, and an installed copy to update
        AutoUpdateOn.IsEnabled = S.CheckUpdates && Updater.IsInstalled;
        RevertBtn.IsEnabled = Updater.IsInstalled;
        var kept = Updater.Kept;
        RevertBtn.Content = kept != null ? $"GO BACK TO {kept.Value.Version}" : "GO BACK ONE VERSION";
        RevertStatus.Text = !Updater.IsInstalled ? "This is a portable copy: to update or go back, unpack another version's zip over it."
            : Updater.Waiting is { } w ? $"Phoenix {w.Version} is downloaded and will be installed when you close Phoenix."
            : kept != null ? $"Phoenix {kept.Value.Version} is kept and can be put back without downloading."
            : S.UpdateSkipped != "" ? $"You went back from, or skipped, {S.UpdateSkipped}: it will not be installed by itself. CHECK NOW still offers it." : "";
    }

    async void Revert_Click(object sender, RoutedEventArgs e)
    {
        var to = Updater.Kept?.Version;
        var text = (to != null ? $"Go back to Phoenix {to}?" : "Go back to the version before this one? It is downloaded from Phoenix's releases on GitHub.")
                   + $" Phoenix closes and the earlier version's installer runs. Your library and settings stay as they are. Phoenix {Updater.CurrentText} will not be installed again by itself.";
        if (MessageBox.Show(App.Win, text, "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RevertBtn.IsEnabled = false;
        try { await Updater.RevertAsync(status => Dispatcher.BeginInvoke(() => RevertStatus.Text = status)); }
        catch (Exception ex) { RevertStatus.Text = "couldn't go back: " + ex.Message; RevertBtn.IsEnabled = true; }
    }

    void ShowUpdate(Updater.Release? release, bool checkedNow)
    {
        var newer = release is { IsNewer: true };
        UpdateInstallBtn.Visibility = newer && Updater.IsInstalled && release!.InstallerUrl != "" ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageBtn.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        UpdateSkipBtn.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotes.Text = newer ? release!.Notes.Trim() : "";
        UpdateNotes.Visibility = UpdateNotes.Text == "" ? Visibility.Collapsed : Visibility.Visible;   // no empty gap when there is nothing to say
        if (newer)
            UpdateStatus.Text = $"Phoenix {release!.VersionText} is available." + (Updater.IsInstalled ? "" : "  This is a portable copy: download the new zip from the release page and unpack it over this one.");
        else if (checkedNow) UpdateStatus.Text = release == null ? "No release has been published yet." : $"You have the newest version ({Updater.CurrentText}).";
        else UpdateStatus.Text = "";
    }

    async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckBtn.IsEnabled = false;
        UpdateStatus.Text = "asking GitHub…";
        try
        {
            var release = await Updater.CheckAsync();
            S.UpdateChecked = Fmt.Now;
            ShowUpdate(release, checkedNow: true);
        }
        catch (Exception ex) { UpdateStatus.Text = "couldn't check: " + ex.Message; }
        finally { UpdateCheckBtn.IsEnabled = true; }
    }

    async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Latest is not { IsNewer: true } release) return;
        UpdateInstallBtn.IsEnabled = false;
        try
        {
            if (S.KeepPrevious)
            {
                UpdateStatus.Text = $"keeping Phoenix {Updater.CurrentText} so you can go back…";
                try { await Updater.KeepCurrentAsync(); } catch { }   // not being able to keep it does not stop the update
            }
            var file = await Updater.DownloadAsync(release, done => Dispatcher.BeginInvoke(() => UpdateStatus.Text = $"downloading Phoenix {release.VersionText}… {done:P0}"));
            UpdateStatus.Text = "starting the installer…";
            App.Settings.Save();
            Updater.Install(file);
        }
        catch (Exception ex) { UpdateStatus.Text = "couldn't update: " + ex.Message; UpdateInstallBtn.IsEnabled = true; }
    }

    void UpdatePage_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Updater.Latest?.Page ?? Updater.ReleasesPage) { UseShellExecute = true }); } catch { }
    }

    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Latest is not { } release) return;
        S.UpdateSkipped = release.VersionText;
        Keep();
        UpdateStatus.Text = $"You won't be told about {release.VersionText} again when Phoenix starts.";
    }
}
