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

    /// <summary>Sorting for the series, and what to show of a series' episodes (with a filter by name).</summary>
    public ViewOptions Options { get; } = new("podcasts",
        new[] { ("", "title"), ("new", "new episodes"), ("author", "author") },
        new[] { ("", "all episodes"), ("unplayed", "unplayed"), ("downloaded", "downloaded"), ("video", "video"), ("audio", "audio") });

    public string Tab { get; private set; } = "mine";
    public List<Episode> ShownEpisodes { get; private set; } = new();
    public List<Podcast> ShownSeries => SeriesList.Items.Cast<Podcast>().ToList();
    PodcastSourcesPane? _sources;
    public PodcastSourcesPane Sources => _sources ??= MakeSources();

    PodcastSourcesPane MakeSources()
    {
        var pane = new PodcastSourcesPane();
        pane.Subscribed += id => Reload(id);
        SourcesPane.Children.Add(pane);
        return pane;
    }

    /// <summary>Shows "my podcasts" or "sources".</summary>
    public void ShowTab(string tab)
    {
        Tab = tab == "sources" ? "sources" : "mine";
        TabBar.Children.Clear();
        foreach (var (id, label) in new[] { ("mine", "my podcasts"), ("sources", "sources") })
        {
            var b = Make.Button(label, "TextBtn", () => ShowTab(id), id == "sources" ? "Browse podcast directories and subscribe" : null, new Thickness(0, 0, 22, 0));
            b.Opacity = id == Tab ? 1 : 0.5;
            b.FontSize = 15;
            TabBar.Children.Add(b);
        }
        Options.Visibility = Tab == "mine" ? Visibility.Visible : Visibility.Collapsed;
        TabBar.Children.Add(Options);
        MinePane.Visibility = Tab == "mine" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPane.Visibility = Tab == "sources" ? Visibility.Visible : Visibility.Collapsed;
        if (Tab == "sources") Sources.Shown();
    }

    public PodcastsView()
    {
        InitializeComponent();
        ShowTab("mine");
        Options.Changed += () => Reload(_podcast?.Id);
        // a background refresh found something, or downloads changed
        PodcastSync.Changed += () => Reload(_podcast?.Id);
        Reload(null);
    }

    void Reload(long? selectId)
    {
        var byName = StringComparer.OrdinalIgnoreCase;
        var pods = Options.Order(App.Lib.Podcasts, key => key switch
        {
            "new" => list => list.OrderByDescending(p => p.NewCount).ThenBy(p => p.Title, byName),
            "author" => list => list.OrderBy(p => p.Author == "" ? "\uFFFF" : p.Author, byName).ThenBy(p => p.Title, byName),
            _ => null
        });
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
        // the bar's "show" and filter choose which of the series' episodes are listed
        ShownEpisodes = _episodes.Where(e => (Options.Show switch { "unplayed" => !e.Played, "downloaded" => e.File != "", "video" => e.IsVideo, "audio" => !e.IsVideo, _ => true })
                                             && Options.Matches(e.Title, e.Description)).ToList();
        var video = _episodes.Count(e => e.IsVideo);
        EpCap.Text = p == null ? "" : (ShownEpisodes.Count == _episodes.Count ? $"EPISODES  {_episodes.Count}" : $"EPISODES  {ShownEpisodes.Count} of {_episodes.Count}")
            + (video == 0 ? "" : video == _episodes.Count ? "   ·   a video podcast" : $"   ·   {video} in video");
        EpisodeList.ItemsSource = ShownEpisodes;
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
        else m.Items.Add(Menus.Item(ep.IsVideo ? "Download the video" : "Download", async () =>
        {
            App.Win.SetStatus($"downloading {ep.Title}…");
            try { if (_podcast != null) await PodcastSync.DownloadAsync(_podcast, ep); App.Win.SetStatus(""); }
            catch (Exception ex) { App.Win.SetStatus($"couldn't download \"{ep.Title}\": {ex.Message}"); }
        }));
        Menus.Open(m, EpisodeList);
    }
}
