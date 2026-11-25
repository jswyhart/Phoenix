using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

public class QuickItem
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string ArtPath { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Action Play { get; init; } = () => { };
    /// <summary>Goes to the item in the collection (set for albums).</summary>
    public Action? Open { get; init; }
    public Pin? Pin { get; init; }
}

public partial class QuickplayView : UserControl
{
    public QuickplayView()
    {
        InitializeComponent();
        App.Lib.Changed += Reload;
        App.Lib.RatingsChanged += () => { if (IsVisible) Reload(); };
        App.Player.CurrentChanged += () => { if (IsVisible) Reload(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        Reload();
    }

    static QuickItem FromAlbum(Album a, Pin? pin = null) =>
        new() { Title = a.Title, Subtitle = a.Artist, ArtPath = a.ArtPath, Play = () => Menus.Play(a.Tracks), Open = () => OpenAlbum(a), Pin = pin };

    static void OpenAlbum(Album a)
    {
        App.Win.Navigate("music");
        App.Win.Page<MusicView>("music").OpenAlbum(a);
    }

    void Reload()
    {
        var lib = App.Lib;
        var empty = lib.Tracks.Count == 0 && lib.Videos.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Sections.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        var pins = new List<QuickItem>();
        foreach (var pin in lib.Pins)
        {
            switch (pin.Kind)
            {
                case "album" when lib.Albums.FirstOrDefault(a => a.Key == pin.Key) is { } album:
                    pins.Add(FromAlbum(album, pin));
                    break;
                case "artist":
                    var albums = lib.Albums.Where(a => string.Equals(a.Artist, pin.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (albums.Count > 0)
                        pins.Add(new QuickItem
                        {
                            Title = pin.Title, Subtitle = "artist", Glyph = "", Pin = pin,
                            ArtPath = albums.Select(a => a.ArtPath).FirstOrDefault(p => p != "") ?? "",
                            Play = () => Menus.Play(albums.SelectMany(a => a.Tracks))
                        });
                    break;
                case "radio" when App.Settings.RadioEnabled && lib.Stations.FirstOrDefault(st => st.Url == pin.Key) is { } station:
                    pins.Add(new QuickItem { Title = station.Name, Subtitle = "radio", Glyph = "\uE7F6", Pin = pin, ArtPath = station.Art, Play = () => Halcyon.Services.Radio.Play(station) });
                    break;
                case "playlist" when lib.Playlists.FirstOrDefault(p => p.Id.ToString() == pin.Key) is { } pl:
                    var tracks = lib.TracksOf(pl);
                    pins.Add(new QuickItem
                    {
                        Title = pl.Name, Subtitle = "playlist", Glyph = "", Pin = pin,
                        ArtPath = tracks.Select(t => t.ArtPath).FirstOrDefault(p => p != "") ?? "",
                        Play = () => Menus.Play(lib.TracksOf(pl))
                    });
                    break;
            }
        }
        PinsList.ItemsSource = pins;
        PinsHint.Visibility = pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        NewList.ItemsSource = lib.Albums.OrderByDescending(a => a.DateAdded).Take(12).Select(a => FromAlbum(a)).ToList();

        var history = lib.Tracks.Where(t => t.LastPlayed > 0).OrderByDescending(t => t.LastPlayed)
            .Select(t => t.AlbumKey).Distinct().Take(12)
            .Select(k => lib.Albums.FirstOrDefault(a => a.Key == k)).Where(a => a != null).Select(a => FromAlbum(a!)).ToList();
        HistoryList.ItemsSource = history;
        HistoryHint.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Liked songs, most recently liked first; a tile plays that song and carries on down the list.
        var liked = lib.Liked();
        LikedList.ItemsSource = liked.Take(30).Select(t => new QuickItem
        {
            Title = t.Title, Subtitle = t.Artist, ArtPath = t.ArtPath, Play = () => Menus.Play(lib.Liked(), t)
        }).ToList();
        LikedHint.Visibility = liked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayLikedBtn.Visibility = ShuffleLikedBtn.Visibility = liked.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void PlayLiked_Click(object sender, RoutedEventArgs e) => Menus.Play(App.Lib.Liked());
    void ShuffleLiked_Click(object sender, RoutedEventArgs e) => Menus.PlayShuffled(App.Lib.Liked());

    void Tile_Click(object sender, RoutedEventArgs e) => Activate((QuickItem)((FrameworkElement)sender).DataContext);

    /// <summary>What a click on a tile does: open the album, or play it if that was chosen in settings (other tiles always play).</summary>
    public static void Activate(QuickItem item)
    {
        if (item.Open != null && !App.Settings.QuickplayPlays) item.Open(); else item.Play();
    }

    public List<QuickItem> NewItems => NewList.Items.Cast<QuickItem>().ToList();

    void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = (QuickItem)((FrameworkElement)sender).DataContext;
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Play", item.Play));
        if (item.Open != null) m.Items.Add(Menus.Item("Open album", item.Open));
        if (item.Pin is { } pin) m.Items.Add(Menus.Item("Unpin from quickplay", () => App.Lib.TogglePin(pin.Kind, pin.Key, pin.Title)));
        Menus.Open(m, (UIElement)sender);
    }

    void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("settings");
    void Stats_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("stats");
}
