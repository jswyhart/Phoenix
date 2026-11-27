using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

public record OtherRow(string Glyph, string Title, string Kind, object Item);

public partial class SearchView : UserControl
{
    List<Track> _songs = new(), _baseSongs = new();
    string _query = "";

    public SearchView()
    {
        InitializeComponent();
        AlbumGrid.ItemClicked += o => { AlbumGrid.SelectedItem = o; ShowSongs(((Album)o).Tracks); };
        AlbumGrid.ItemActivated += o => Menus.Play(((Album)o).Tracks);
        TrackDrag.Attach(SongList, o => o is Track t ? new[] { t } : null, SongList);
        TrackDrag.Attach(AlbumGrid, o => o is TileCell { Item: Album a } ? a.Tracks : null);
        AlbumGrid.ItemContext += (o, el) => Menus.Open(Menus.ForAlbum((Album)o), el);
        AlbumGrid.DrawWith(TileStyle.Album, Menus.AlbumLook);
        SongHdr.Changed += () => ShowSongs(_baseSongs);
        App.Lib.Changed += () => { if (_query != "") Search(_query); };
        App.Lib.TagsChanged += () => { if (_query != "") Search(_query); };
    }

    void ShowSongs(List<Track> songs)
    {
        _baseSongs = songs;
        _songs = SongHdr.Sort.Apply(songs);
        SongCap.Text = $"SONGS  {_songs.Count}";
        SongList.ItemsSource = _songs;
    }

    public void Search(string query)
    {
        _query = query;
        var r = App.Lib.Search(query);
        HeaderText.Text = query.Trim() == "#" ? "your tags" : $"results for “{query}”";
        AlbumCap.Text = $"ALBUMS  {r.Albums.Count}";
        AlbumGrid.SelectedItem = null;
        AlbumGrid.SetItems(r.Albums);
        ShowSongs(r.Songs);

        var other = r.Videos.Select(v => new OtherRow("", v.DisplayTitle, v.Kind == "episode" ? "episode" : v.Kind == "movie" ? "movie" : "video", v))
            .Concat(r.Books.Select(b => new OtherRow(b.IsAudio ? "" : "", b.Title, b.IsAudio ? "audiobook" : "ebook", b))).ToList();
        OtherList.ItemsSource = other;
        OtherCap.Text = $"VIDEOS & BOOKS  {other.Count}";
        OtherRow.Height = other.Count == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        // tags the search touches: click one to narrow to exactly that tag
        TagChips.Children.Clear();
        foreach (var (tag, count) in r.Tags)
        {
            var b = new Button { Content = $"#{tag}  {count}", Style = (Style)FindResource("PillBtn"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            var q = TagText.Query(tag);
            b.Click += (_, _) => App.Win.SearchFor(q);
            TagChips.Children.Add(b);
        }
        TagChips.Visibility = r.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var nothing = r.Albums.Count == 0 && r.Songs.Count == 0 && other.Count == 0 && r.Tags.Count == 0;
        NothingText.Text = nothing ? NothingFound(query) : "";
        NothingText.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the search page says when nothing in the collection matches.</summary>
    public static string NothingFound(string query) =>
        $"Nothing in your collection matches “{query.Trim()}”.\n\nTry fewer words, or part of a name. Search looks at titles, artists, albums, genres and your search tags (#tag finds one tag); streaming services and podcasts you haven't added are searched on their own pages.";

    void Song_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) Menus.Play(_songs, t);
    }

    void Song_RightClick(object sender, MouseButtonEventArgs e)
    {
        var sel = SongList.SelectedItems.Cast<Track>().ToList();
        if (sel.Count > 0) Menus.Open(Menus.ForTracks(sel, sel.Count == 1 ? _songs : null), SongList);
    }

    void Other_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        switch ((e.OriginalSource as FrameworkElement)?.DataContext is OtherRow row ? row.Item : null)
        {
            case VideoItem v: App.Player.Play(new[] { Player.From(v) }); break;
            case Book b: BooksView.OpenBook(b); break;
        }
    }

    void Other_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (OtherList.SelectedItem is not OtherRow row) return;
        var m = new ContextMenu();
        switch (row.Item)
        {
            case VideoItem v:
                m.Items.Add(Menus.Item("Play", () => App.Player.Play(new[] { Player.From(v) })));
                m.Items.Add(Menus.SearchTags("Edit search tags (#)…", () => new EditTagsDialog(Library.VideoTag, new[] { v.Id }, $"“{v.Title}”").ShowDialog()));
                break;
            case Book b:
                m.Items.Add(Menus.Item(b.IsAudio ? "Listen" : "Read", () => BooksView.OpenBook(b)));
                m.Items.Add(Menus.SearchTags("Edit search tags (#)…", () => new EditTagsDialog(Library.BookTag, new[] { b.Id }, $"“{b.Title}”").ShowDialog()));
                break;
        }
        Menus.Open(m, OtherList);
    }
}
