using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

public partial class PodcastsView : UserControl
{
    List<Episode> _episodes = new();
    Podcast? _podcast;

    public PodcastsView()
    {
        InitializeComponent();
        // a background refresh found something, or downloads changed
        PodcastSync.Changed += () => Reload(_podcast?.Id);
        Reload(null);
    }

    void Reload(long? selectId)
    {
        var pods = App.Lib.Podcasts;
        SeriesCap.Text = $"SERIES  {pods.Count}";
        SeriesList.ItemsSource = null;
        SeriesList.ItemsSource = pods;
        EmptyText.Visibility = pods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sel = pods.FirstOrDefault(p => p.Id == selectId) ?? pods.FirstOrDefault();
        SeriesList.SelectedItem = sel;
        if (sel == null) ShowPodcast(null);
    }

    void ShowPodcast(Podcast? p)
    {
        _podcast = p;
        PodTitle.Text = p?.Title ?? "";
        PodDesc.Text = p?.Description ?? "";
        _episodes = p == null ? new() : App.Lib.Db.LoadEpisodes(p.Id);
        EpCap.Text = p == null ? "" : $"EPISODES  {_episodes.Count}";
        EpisodeList.ItemsSource = _episodes;
    }

    void Series_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPodcast(SeriesList.SelectedItem as Podcast);

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var url = InputDialog.Ask("Add a podcast", "Podcast RSS feed address", "https://");
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
        try
        {
            App.Win.SetStatus("fetching podcast…");
            var (podcast, episodes) = await PodcastService.FetchAsync(url);
            App.Lib.Db.SavePodcast(podcast, episodes);
            App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
            App.Win.SetStatus("");
            Reload(podcast.Id);
        }
        catch (Exception ex)
        {
            App.Win.SetStatus("");
            MessageBox.Show(App.Win, "Couldn't add that podcast.\n\n" + ex.Message, "Phoenix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshBtn.IsEnabled = false;
        await PodcastSync.RefreshAllAsync(App.Win.SetStatus);
        RefreshBtn.IsEnabled = true;
        Reload(_podcast?.Id);
    }

    void Series_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (SeriesList.SelectedItem is not Podcast p) return;
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Play newest episode", () => { if (_episodes.Count > 0) Play(_episodes[0]); }));
        var unplayed = _episodes.Where(x => !x.Played && !x.IsVideo && x.File == "").ToList();
        m.Items.Add(Menus.Item(unplayed.Count == 0 ? "Download all unplayed" : $"Download all unplayed ({unplayed.Count})", async () =>
        {
            if (unplayed.Count > 10 && MessageBox.Show(App.Win, $"Download {unplayed.Count} episodes of “{p.Title}”?", "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var done = 0;
            foreach (var ep in unplayed)
            {
                App.Win.SetStatus($"downloading {++done} of {unplayed.Count}: {ep.Title}…");
                try { await PodcastSync.DownloadAsync(p, ep); } catch { }
            }
            App.Win.SetStatus("");
        }, unplayed.Count > 0));
        var have = _episodes.Where(x => x.File != "").ToList();
        m.Items.Add(Menus.Item(have.Count == 0 ? "Remove downloads" : $"Remove downloads ({have.Count})", () => { foreach (var ep in have) PodcastSync.RemoveDownload(ep); }, have.Count > 0));
        var keep = new MenuItem { Header = "Keep downloaded" };
        var own = App.Settings.PodcastKeepBySeries.TryGetValue(p.FeedUrl, out var ownKeep);
        MenuItem Keep(string text, int? n)
        {
            var mi = Menus.Item(text, async () => { PodcastSync.SetKeep(p, n); await PodcastSync.DownloadsAsync(App.Win.SetStatus); App.Win.SetStatus(""); Reload(p.Id); });
            mi.IsChecked = n == null ? !own : own && ownKeep == n;
            return mi;
        }
        keep.Items.Add(Keep($"As set in settings ({PodcastSync.KeepText(App.Settings.PodcastKeep)})", null));
        keep.Items.Add(new Separator());
        foreach (var n in new[] { 0, 1, 3, 5, 10, PodcastSync.All }) keep.Items.Add(Keep(PodcastSync.KeepText(n), n));
        m.Items.Add(keep);
        m.Items.Add(new Separator());
        m.Items.Add(Menus.Item("Mark all as played", () =>
        {
            foreach (var ep in _episodes.Where(x => !x.Played)) { ep.Played = true; ep.PositionMs = 0; App.Lib.Db.SaveEpisodeState(ep); ep.Raise(); }
            App.Lib.Db.ClearFresh(p.Id);
            App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
            Reload(p.Id);
        }, _episodes.Any(x => !x.Played)));
        m.Items.Add(Menus.Item("Unsubscribe", () =>
        {
            foreach (var file in App.Lib.Db.EpisodeFiles(p.Id)) { try { System.IO.File.Delete(file); } catch { } }
            App.Lib.Db.DeletePodcast(p.Id);
            App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
            Reload(null);
        }));
        Menus.Open(m, SeriesList);
    }

    void Play(Episode ep)
    {
        if (_podcast == null) return;
        App.Player.Play(new[] { Player.From(ep, _podcast) });
        if (ep.Fresh)
        {
            // listened to: no longer new
            ep.Fresh = false;
            App.Lib.Db.SaveEpisodeFresh(ep);
            ep.Raise();
            _podcast.NewCount = Math.Max(0, _podcast.NewCount - 1);
            SeriesList.Items.Refresh();
        }
    }

    void Episode_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Episode ep) Play(ep);
    }

    void Episode_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (EpisodeList.SelectedItem is not Episode ep) return;
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Play", () => Play(ep)));
        m.Items.Add(Menus.Item(ep.Played ? "Mark as unplayed" : "Mark as played", () =>
        {
            ep.Played = !ep.Played;
            ep.PositionMs = 0;
            App.Lib.Db.SaveEpisodeState(ep);
            ep.Raise();
            App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
            Reload(_podcast?.Id);
        }));
        if (ep.File != "")
        {
            m.Items.Add(Menus.Item("Remove download", () => PodcastSync.RemoveDownload(ep)));
            m.Items.Add(Menus.Item("Show in File Explorer", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ep.File}\"")));
        }
        else if (!ep.IsVideo) m.Items.Add(Menus.Item("Download", async () =>
        {
            App.Win.SetStatus($"downloading {ep.Title}…");
            try { if (_podcast != null) await PodcastSync.DownloadAsync(_podcast, ep); App.Win.SetStatus(""); }
            catch (Exception ex) { App.Win.SetStatus($"couldn't download \"{ep.Title}\": {ex.Message}"); }
        }));
        Menus.Open(m, EpisodeList);
    }
}
