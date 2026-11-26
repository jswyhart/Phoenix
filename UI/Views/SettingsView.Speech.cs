using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Settings → translation → lyrics: the speech model that writes lyrics, where it runs, and writing them for every song.</summary>
public partial class SettingsView
{
    bool _loadingSpeech;
    CancellationTokenSource? _speechDownload, _speechAll;

    void LoadSpeech()
    {
        _loadingSpeech = true;
        var s = App.Settings;
        SpeechModelBox.Items.Clear();
        var best = SpeechLyrics.Recommended();
        foreach (var m in SpeechLyrics.Models)
            SpeechModelBox.Items.Add(new ComboBoxItem { Content = $"{m.Name}   ({Size(m)}{(SpeechLyrics.Have(m) ? ", downloaded" : "")}{(m == best ? ", recommended here" : "")}): {m.Note}", Tag = m.Id });
        var chosen = SpeechLyrics.Find(s.LyricsModel) ?? best;
        SpeechModelBox.SelectedItem = SpeechModelBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == chosen.Id);
        SpeechGpuBox.Items.Clear();
        SpeechGpuBox.Items.Add(new ComboBoxItem { Content = "automatic", Tag = -2 });
        foreach (var g in SpeechLyrics.Gpus())
            SpeechGpuBox.Items.Add(new ComboBoxItem { Content = $"{g.Name}   ({g.Kind}{(g.Kind == "separate" && g.MemoryMb > 0 ? $", {g.MemoryMb / 1024.0:0} GB" : "")})", Tag = g.Index });
        SpeechGpuBox.Items.Add(new ComboBoxItem { Content = "the processor only", Tag = -1 });
        SpeechGpuBox.SelectedItem = SpeechGpuBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.LyricsGpu) ?? SpeechGpuBox.Items[0];
        LyricsListenBox.IsChecked = s.LyricsListen;
        _loadingSpeech = false;
        ShowSpeechState();
    }

    static string Size(SpeechLyrics.Model m) => m.Megabytes >= 1000 ? $"{m.Megabytes / 1024.0:0.0} GB" : $"{m.Megabytes} MB";

    SpeechLyrics.Model PickedModel => SpeechLyrics.Find(SpeechModelBox.SelectedItem is ComboBoxItem { Tag: string id } ? id : "") ?? SpeechLyrics.Models[2];

    void ShowSpeechState()
    {
        var m = PickedModel;
        var have = SpeechLyrics.Have(m);
        SpeechDownloadBtn.Content = _speechDownload != null ? "STOP" : "DOWNLOAD";
        SpeechDownloadBtn.Visibility = have && _speechDownload == null ? Visibility.Collapsed : Visibility.Visible;
        SpeechDeleteBtn.Visibility = have ? Visibility.Visible : Visibility.Collapsed;
        LyricsListenBox.IsEnabled = SpeechAllBtn.IsEnabled = have || _speechAll != null;
        SpeechAllBtn.Content = _speechAll != null ? "STOP" : "WRITE LYRICS FOR SONGS THAT HAVE NONE";
        if (_speechDownload == null && _speechAll == null)
            SpeechStatus.Text = have ? (SpeechLyrics.RanOn != "" ? $"ready; it last ran on {SpeechLyrics.RanOn}" : "ready") : $"download the model to use it ({Size(m)})";
    }

    void SpeechModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSpeech) return;
        // a model that is here is used straight away; one that isn't waits for DOWNLOAD
        if (SpeechLyrics.Have(PickedModel)) { App.Settings.LyricsModel = PickedModel.Id; if (!App.SmokeMode) App.Settings.Save(); }
        ShowSpeechState();
    }

    void SpeechGpu_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSpeech || SpeechGpuBox.SelectedItem is not ComboBoxItem { Tag: int gpu }) return;
        App.Settings.LyricsGpu = SpeechLyrics.GpuChoice = gpu;
        if (!App.SmokeMode) App.Settings.Save();
        SpeechLyrics.Unload();
        LoadSpeech();   // the recommended model depends on where it runs
    }

    void LyricsListen_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.LyricsListen = LyricsListenBox.IsChecked == true;
        if (!App.SmokeMode) App.Settings.Save();
    }

    async void SpeechDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_speechDownload != null) { _speechDownload.Cancel(); return; }
        var m = PickedModel;
        _speechDownload = new CancellationTokenSource();
        ShowSpeechState();
        try
        {
            await SpeechLyrics.DownloadAsync(m, p => Dispatcher.BeginInvoke(() => SpeechStatus.Text = $"downloading {m.Name}: {p:P0} of {Size(m)}"), _speechDownload.Token);
            App.Settings.LyricsModel = m.Id;
            if (!App.SmokeMode) App.Settings.Save();
            _speechDownload = null;
            LoadSpeech();
            App.Win.RefreshLyrics();   // the generated tab appears
        }
        catch (OperationCanceledException) { _speechDownload = null; ShowSpeechState(); }
        catch (Exception ex) { _speechDownload = null; ShowSpeechState(); SpeechStatus.Text = "couldn't download the model: " + ex.Message; }
    }

    void SpeechDelete_Click(object sender, RoutedEventArgs e)
    {
        var m = PickedModel;
        if (MessageBox.Show(App.Win, $"Delete the {m.Name} speech model ({Size(m)})? Lyrics it already wrote are kept.", "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SpeechLyrics.Delete(m);
        if (App.Settings.LyricsModel == m.Id) { App.Settings.LyricsModel = ""; if (!App.SmokeMode) App.Settings.Save(); }
        LoadSpeech();
        App.Win.RefreshLyrics();
    }

    /// <summary>Writes lyrics for every song in the collection that has none, one at a time; it can be stopped.</summary>
    async void SpeechAll_Click(object sender, RoutedEventArgs e)
    {
        if (_speechAll != null) { _speechAll.Cancel(); return; }
        var m = PickedModel;
        if (!SpeechLyrics.Have(m)) return;
        var songs = await Task.Run(() => App.Lib.Tracks.Where(t => t.IsLocal && Lyrics.For(t) == null && !Lyrics.HasGenerated(t)).ToList());
        if (songs.Count == 0) { SpeechStatus.Text = "every song has lyrics already"; return; }
        _speechAll = new CancellationTokenSource();
        var cancel = _speechAll.Token;
        ShowSpeechState();
        int done = 0, wrote = 0;
        try
        {
            foreach (var t in songs)
            {
                SpeechStatus.Text = $"listening to {done + 1} of {songs.Count}: {t.Title}   ({wrote} written so far)";
                try
                {
                    if (await SpeechLyrics.TranscribeAsync(t.Location, m, null, cancel, t.Title, t.Artist) is { } lrc) { Lyrics.SaveGenerated(t, lrc); wrote++; }
                }
                catch (OperationCanceledException) { throw; }
                catch { }   // a song that can't be read is skipped
                done++;
            }
            _speechAll = null;
            ShowSpeechState();
            SpeechStatus.Text = $"wrote lyrics for {wrote} of {songs.Count} songs (the rest had no words the model could hear) on {SpeechLyrics.RanOn}";
        }
        catch (OperationCanceledException) { _speechAll = null; ShowSpeechState(); SpeechStatus.Text = $"stopped; wrote lyrics for {wrote} songs"; }
        App.Win.RefreshLyrics();
    }
}
