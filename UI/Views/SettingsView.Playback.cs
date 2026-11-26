using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Settings: playback (crossfade, equalizer) and podcasts.</summary>
public partial class SettingsView
{
    static readonly int[] KeepChoices = { 0, 1, 3, 5, 10, PodcastSync.All };
    readonly List<Slider> _bands = new();
    readonly List<TextBlock> _bandValues = new();
    bool _loadingPlayback;

    void BuildPlayback()
    {
        if (_bands.Count > 0) return;
        for (var i = 0; i < AudioEngine.BandFrequencies.Length; i++)
        {
            var f = AudioEngine.BandFrequencies[i];
            // a horizontal slider turned on its end: up is louder
            var slider = new Slider { Minimum = -12, Maximum = 12, Width = 150, SmallChange = 0.5, LargeChange = 2, LayoutTransform = new RotateTransform(-90), Tag = i, HorizontalAlignment = HorizontalAlignment.Center };
            slider.ValueChanged += Band_Changed;
            var value = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            var label = new TextBlock { Text = f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
            var column = new StackPanel { Width = 46 };
            column.Children.Add(value);
            column.Children.Add(slider);
            column.Children.Add(label);
            EqBands.Children.Add(column);
            _bands.Add(slider);
            _bandValues.Add(value);
        }
        EqPreset.ItemsSource = AudioEngine.Presets.Select(p => p.Name).Append("custom").ToList();
        AutoEqProfileBox.ItemsSource = AutoEq.Profiles.Select(p => $"{p.Name}: {p.About}").ToList();
        AutoEqStoreBox.ItemsSource = AutoEq.Stores.Select(x => x.Name).ToList();
        PodcastKeep.ItemsSource = KeepChoices.Select(n => PodcastSync.KeepText(n) + (n > 0 ? " of each series" : "")).ToList();
    }

    void LoadPlayback()
    {
        BuildPlayback();
        _loadingPlayback = true;
        var s = App.Settings;
        EngineOn.IsChecked = s.AudioEngine;
        CrossfadeSlider.Value = Math.Clamp(s.Crossfade, 0, 12);
        EqOn.IsChecked = s.EqEnabled;
        if (s.EqGains.Length != _bands.Count) s.EqGains = new double[_bands.Count];
        for (var i = 0; i < _bands.Count; i++) _bands[i].Value = Math.Clamp(s.EqGains[i], -12, 12);
        EqPreset.SelectedItem = ((System.Collections.IEnumerable)EqPreset.ItemsSource).Cast<string>().Contains(s.EqPreset) ? s.EqPreset : "custom";
        AutoEqBox.IsChecked = s.AutoEq;
        AutoEqProfileBox.SelectedIndex = Math.Max(0, Array.FindIndex(AutoEq.Profiles, p => p.Id == s.AutoEqProfile));
        AutoEqStoreBox.SelectedIndex = Math.Max(0, Array.FindIndex(AutoEq.Stores, x => x.Id == s.AutoEqStore));
        _loadingPlayback = false;
        ShowPlaybackState();
    }

    void ShowPlaybackState()
    {
        var s = App.Settings;
        CrossfadeText.Text = s.Crossfade < 0.5 ? "off: songs play back to back with no gap" : $"{s.Crossfade:0} second{(s.Crossfade >= 1.5 ? "s" : "")}: the end of one song blends into the start of the next";
        for (var i = 0; i < _bands.Count; i++) _bandValues[i].Text = (_bands[i].Value > 0 ? "+" : "") + _bands[i].Value.ToString("0.#");
        EqBands.IsEnabled = EqPreset.IsEnabled = EqResetBtn.IsEnabled = s.EqEnabled && s.AudioEngine;
        EqBands.Opacity = EqBands.IsEnabled ? 1 : 0.4;
        CrossfadeSlider.IsEnabled = EqOn.IsEnabled = AutoEqBox.IsEnabled = s.AudioEngine;
        AutoEqProfileBox.IsEnabled = AutoEqStoreBox.IsEnabled = s.AutoEq && s.AudioEngine;
        AutoEqScanBtn.Content = _autoEqScan != null ? "STOP" : "MEASURE ALL SONGS NOW";
        if (_autoEqScan == null && App.Player.Current?.Track is { } now)
            AutoEqStatus.Text = !s.AutoEq ? "" : AutoEq.LevelsFor(now) is { } l
                ? $"now playing: {string.Join("  ", AutoEq.Gains(l, s.AutoEqProfile).Select((g, i) => $"{(AutoEq.Bands[i] >= 1000 ? $"{AutoEq.Bands[i] / 1000:0.#}k" : $"{AutoEq.Bands[i]:0}")} {g:+0.#;-0.#;0}"))}"
                : "the song playing hasn't been measured yet";
    }

    void Engine_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AudioEngine = EngineOn.IsChecked == true;
        ShowPlaybackState();
        App.Win.SetStatus("applies from the next song");
    }

    void Crossfade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingPlayback || !IsInitialized) return;
        App.Settings.Crossfade = Math.Round(CrossfadeSlider.Value);
        App.Player.ApplyAudioSettings();
        ShowPlaybackState();
    }

    CancellationTokenSource? _autoEqScan;

    void AutoEq_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoEq = AutoEqBox.IsChecked == true;
        if (!App.SmokeMode) App.Settings.Save();
        App.Player.ApplyEqualizer();
        AutoEq.EnsureMeasured(App.Settings, App.Player.Current?.Track);
        ShowPlaybackState();
    }

    void AutoEqProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlayback || AutoEqProfileBox.SelectedIndex < 0) return;
        App.Settings.AutoEqProfile = AutoEq.Profiles[AutoEqProfileBox.SelectedIndex].Id;
        if (!App.SmokeMode) App.Settings.Save();
        App.Player.ApplyEqualizer();
        ShowPlaybackState();
    }

    void AutoEqStore_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlayback || AutoEqStoreBox.SelectedIndex < 0) return;
        App.Settings.AutoEqStore = AutoEq.Stores[AutoEqStoreBox.SelectedIndex].Id;
        if (!App.SmokeMode) App.Settings.Save();
    }

    /// <summary>Measures every song in the collection that hasn't been yet, one at a time; it can be stopped.</summary>
    async void AutoEqScan_Click(object sender, RoutedEventArgs e)
    {
        if (_autoEqScan != null) { _autoEqScan.Cancel(); return; }
        _autoEqScan = new CancellationTokenSource();
        ShowPlaybackState();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var songs = App.Lib.Tracks.ToList();
            var (done, measured) = await AutoEq.ScanAsync(songs, App.Settings.AutoEqStore,
                (d, total, m) => Dispatcher.BeginInvoke(() => AutoEqStatus.Text = total == 0 ? "every song is measured already" : $"measuring {d + 1} of {total}   ({m} measured so far)"),
                _autoEqScan.Token);
            AutoEqStatus.Text = done == 0 ? "every song is measured already" : $"measured {measured} of {done} songs in {watch.Elapsed.TotalSeconds:0} s";
        }
        catch (OperationCanceledException) { AutoEqStatus.Text = "stopped; the songs measured so far are kept"; }
        finally
        {
            _autoEqScan = null;
            App.Player.ApplyEqualizer();
            AutoEqScanBtn.Content = "MEASURE ALL SONGS NOW";
        }
    }

    /// <summary>For the self-test: measure everything and wait.</summary>
    public Task<(int Done, int Measured)> MeasureAllForTest() => AutoEq.ScanAsync(App.Lib.Tracks.ToList(), App.Settings.AutoEqStore, null, CancellationToken.None);

    void Eq_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.EqEnabled = EqOn.IsChecked == true;
        App.Player.ApplyAudioSettings();
        ShowPlaybackState();
    }

    void EqPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlayback || EqPreset.SelectedItem is not string name) return;
        App.Settings.EqPreset = name;
        var preset = AudioEngine.Presets.FirstOrDefault(p => p.Name == name);
        if (preset.Gains == null) return;   // "custom": the sliders stay as they are
        _loadingPlayback = true;
        for (var i = 0; i < _bands.Count; i++) { _bands[i].Value = preset.Gains[i]; App.Settings.EqGains[i] = preset.Gains[i]; }
        _loadingPlayback = false;
        App.Player.ApplyAudioSettings();
        ShowPlaybackState();
    }

    void Band_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingPlayback) return;
        var slider = (Slider)sender;
        App.Settings.EqGains[(int)slider.Tag] = Math.Round(slider.Value * 2) / 2;
        // moving a band by hand leaves the preset behind
        _loadingPlayback = true;
        App.Settings.EqPreset = "custom";
        EqPreset.SelectedItem = "custom";
        _loadingPlayback = false;
        App.Player.ApplyAudioSettings();
        ShowPlaybackState();
    }

    void EqReset_Click(object sender, RoutedEventArgs e) => EqPreset.SelectedItem = "flat";

    /// <summary>For the self-test: the equalizer band sliders.</summary>
    public IReadOnlyList<Slider> EqSliders => _bands;

    // ---------- podcasts ----------

    void LoadPodcasts()
    {
        BuildPlayback();
        _loadingPlayback = true;
        PodcastAuto.IsChecked = App.Settings.PodcastAutoRefresh;
        PodcastKeep.SelectedIndex = Math.Max(0, Array.IndexOf(KeepChoices, App.Settings.PodcastKeep));
        PodcastRemovePlayed.IsChecked = App.Settings.PodcastRemovePlayed;
        _loadingPlayback = false;
        ShowPodcastFolder();
    }

    void ShowPodcastFolder()
    {
        PodcastFolderBox.Text = PodcastSync.Folder;
        var (count, bytes) = PodcastSync.Usage();
        PodcastUsage.Text = count == 0 ? "No episodes are downloaded."
            : $"{count} episode{(count == 1 ? "" : "s")} downloaded, using {(bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB")}.";
    }

    public int PodcastKeepChoices => PodcastKeep.Items.Count;
    public string PodcastFolderShown => PodcastFolderBox.Text;

    void PodcastAuto_Click(object sender, RoutedEventArgs e) => App.Settings.PodcastAutoRefresh = PodcastAuto.IsChecked == true;

    async void PodcastKeep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlayback || PodcastKeep.SelectedIndex < 0) return;
        App.Settings.PodcastKeep = KeepChoices[PodcastKeep.SelectedIndex];
        if (App.SmokeMode) return;
        App.Settings.Save();
        await PodcastSync.DownloadsAsync(App.Win.SetStatus);
        App.Win.SetStatus("");
        ShowPodcastFolder();
    }

    async void PodcastRemovePlayed_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.PodcastRemovePlayed = PodcastRemovePlayed.IsChecked == true;
        if (App.SmokeMode) return;
        App.Settings.Save();
        await PodcastSync.DownloadsAsync(App.Win.SetStatus);
        App.Win.SetStatus("");
        ShowPodcastFolder();
    }

    void PodcastFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for podcast downloads" };
        if (pick.ShowDialog(App.Win) != true) return;
        App.Settings.PodcastFolder = pick.FolderName;
        App.Settings.Save();
        ShowPodcastFolder();
    }

    void PodcastFolderDefault_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.PodcastFolder = "";
        if (!App.SmokeMode) App.Settings.Save();
        ShowPodcastFolder();
    }

    void PodcastFolderOpen_Click(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(PodcastSync.Folder); System.Diagnostics.Process.Start("explorer.exe", $"\"{PodcastSync.Folder}\""); }
        catch (Exception ex) { App.Win.SetStatus(ex.Message); }
    }

    void PodcastRemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var (count, _) = PodcastSync.Usage();
        if (count == 0) return;
        if (MessageBox.Show(App.Win, $"Remove all {count} downloaded episodes? Your subscriptions and what you have played are kept, and episodes can be downloaded again.",
                "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        PodcastSync.RemoveAll();
        ShowPodcastFolder();
    }
}
