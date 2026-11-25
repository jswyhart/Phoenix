using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

public record PlaylistRow(string Name, string Glyph, Playlist? Playlist, string? Auto, SmartPlaylist? Smart = null);

public partial class PlaylistsView : UserControl
{
    static readonly string[] AutoKinds = { "liked", "most played", "recently added", "recently played" };
    List<Track> _songs = new(), _baseSongs = new();
    PlaylistRow? _row;
    bool _loading;

    /// <summary>Filtering for the songs of the playlist on show (the column headings sort them).</summary>
    public ViewOptions Options { get; } = new("playlists", Array.Empty<(string, string)>(),
        new[] { ("", "everything"), ("liked", "liked"), ("unplayed", "never played") });

    bool Keep(Track t) => (Options.Show switch { "liked" => t.Rating > 0, "unplayed" => t.PlayCount == 0, _ => true }) && Options.Matches(t.Title, t.Artist, t.Album);

    public PlaylistsView()
    {
        InitializeComponent();
        Options.Margin = new Thickness(0, 0, 18, 0);
        SongBar.Children.Insert(0, Options);
        Options.Changed += () => { if (_row != null) Show(_row); };
        SongHdr.Changed += () => { if (_row != null) Show(_row); };
        // sorting is a view of the list: choosing another playlist starts from its own order again
        PlaylistList.SelectionChanged += (_, _) => { if (!_loading) SongHdr.Reset(); };
        TrackDrag.Attach(SongList, o => o is Track t ? new[] { t } : null, SongList);
        TrackDrag.Attach(PlaylistList, o => o is PlaylistRow row ? SongsOf(row) : null);
        App.Lib.Changed += Reload;
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        Reload();
    }

    /// <summary>The songs a row stands for: a playlist's own list, a built-in list, or an auto playlist's current matches.</summary>
    public static List<Track> SongsOf(PlaylistRow row) =>
        row.Playlist != null ? App.Lib.TracksOf(row.Playlist) : row.Smart != null ? App.Lib.Evaluate(row.Smart) : App.Lib.AutoPlaylist(row.Auto!);

    void Reload()
    {
        var rows = AutoKinds.Select(k => new PlaylistRow(k, "", null, k))
            .Concat(App.Settings.SmartPlaylists.Select(sp => new PlaylistRow(sp.Name, "", null, null, sp)))
            .Concat(App.Lib.Playlists.Select(p => new PlaylistRow(p.Name, "", p, null))).ToList();
        _loading = true;
        PlaylistList.ItemsSource = rows;
        var sel = _row == null ? rows[0] : rows.FirstOrDefault(r => r.Auto == _row.Auto && r.Playlist?.Id == _row.Playlist?.Id && r.Smart?.Id == _row.Smart?.Id) ?? rows[0];
        PlaylistList.SelectedItem = sel;
        _loading = false;
        Show(sel);
    }

    void Show(PlaylistRow row)
    {
        _row = row;
        _baseSongs = SongsOf(row);
        _songs = SongHdr.Sort.Apply(Options.Active ? _baseSongs.Where(Keep) : _baseSongs);
        SongCap.Text = (_songs.Count == _baseSongs.Count ? $"SONGS  {_songs.Count}" : $"SONGS  {_songs.Count} of {_baseSongs.Count}") + $"   ·   {Fmt.Time(_songs.Sum(t => t.DurationMs))}";
        RulesText.Visibility = row.Smart != null ? Visibility.Visible : Visibility.Collapsed;
        RulesText.Text = row.Smart != null ? Library.Describe(row.Smart) : "";
        SongList.ItemsSource = _songs;
    }

    void NewAuto_Click(object sender, RoutedEventArgs e)
    {
        var sp = new SmartPlaylist { Name = "auto playlist" };
        if (!SmartPlaylistDialog.Edit(sp, true)) return;
        App.Settings.SmartPlaylists.Add(sp);
        App.Settings.Save();
        _row = new PlaylistRow(sp.Name, "", null, null, sp);
        Reload();
    }

    /// <summary>For the self-test: the rows on show.</summary>
    public List<PlaylistRow> Rows => PlaylistList.Items.Cast<PlaylistRow>().ToList();
    public void Select(PlaylistRow row) { PlaylistList.SelectedItem = row; }
    public List<Track> ShownSongs => _songs;

    void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && PlaylistList.SelectedItem is PlaylistRow row) Show(row);
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask("New playlist", "Playlist name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = App.Lib.CreatePlaylist(name.Trim());
        _row = new PlaylistRow(p.Name, "", p, null);
        Reload();
    }

    void Play_Click(object sender, RoutedEventArgs e) => Menus.Play(_songs);
    void Shuffle_Click(object sender, RoutedEventArgs e) => Menus.PlayShuffled(_songs);
    void Playlist_DoubleClick(object sender, MouseButtonEventArgs e) => Menus.Play(_songs);

    void Playlist_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistList.SelectedItem is not PlaylistRow row) return;
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Play", () => Menus.Play(_songs)));
        m.Items.Add(Menus.Item("Add to now playing", () => Menus.Enqueue(_songs)));
        m.Items.Add(Menus.Item("Add to burn list", () => { foreach (var t in _songs) App.BurnList.Add(t); }));
        if (row.Smart is { } sp)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Menus.Item("Edit rules…", () =>
            {
                if (!SmartPlaylistDialog.Edit(sp, false)) return;
                App.Settings.Save();
                _row = new PlaylistRow(sp.Name, "", null, null, sp);
                Reload();
            }));
            m.Items.Add(Menus.Item("Save as a playlist", () =>
            {
                // a copy that stays as it is now
                var copy = App.Lib.CreatePlaylist(sp.Name);
                App.Lib.AddToPlaylist(copy, _baseSongs);
                App.Win.SetStatus($"saved “{sp.Name}” as a playlist of {_baseSongs.Count} songs");
            }));
            m.Items.Add(Menus.Item("Delete auto playlist", () =>
            {
                if (MessageBox.Show(App.Win, $"Delete the auto playlist “{sp.Name}”? The songs stay in your collection.", "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                App.Settings.SmartPlaylists.Remove(sp);
                App.Settings.Save();
                _row = null;
                Reload();
            }));
        }
        if (row.Playlist is { } p)
        {
            var key = p.Id.ToString();
            m.Items.Add(Menus.Item(App.Lib.IsPinned("playlist", key) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("playlist", key, p.Name)));
            m.Items.Add(new Separator());
            m.Items.Add(Menus.Item("Rename…", () =>
            {
                var name = InputDialog.Ask("Rename playlist", "Playlist name", p.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                p.Name = name.Trim();
                App.Lib.SavePlaylist(p);
            }));
            m.Items.Add(Menus.Item("Delete playlist", () =>
            {
                if (MessageBox.Show(App.Win, $"Delete the playlist “{p.Name}”? The songs stay in your collection.", "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _row = null;
                App.Lib.DeletePlaylist(p);
            }));
        }
        Menus.Open(m, PlaylistList);
    }

    void Song_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) Menus.Play(_songs, t);
    }

    void Song_RightClick(object sender, MouseButtonEventArgs e)
    {
        var sel = SongList.SelectedItems.Cast<Track>().ToList();
        if (sel.Count == 0) return;
        var m = Menus.ForTracks(sel, sel.Count == 1 ? _songs : null);
        if (_row?.Playlist is { } p)
        {
            var sorted = SongHdr.Sort.Active;
            var indexes = SongList.SelectedItems.Cast<Track>().Select(t => _songs.IndexOf(t)).OrderBy(i => i).ToList();
            var chosen = SongList.SelectedItems.Cast<Track>().Select(t => t.Id).ToList();
            m.Items.Add(new Separator());
            if (sorted)
            {
                // the list is showing a sort, not the playlist's own order: moving songs only makes sense in that order
                m.Items.Add(Menus.Item("Keep this order in the playlist", () =>
                {
                    p.TrackIds = _songs.Select(t => t.Id).ToList();
                    SongHdr.Reset();
                    App.Lib.SavePlaylist(p);
                }));
            }
            else
            {
                m.Items.Add(Menus.Item("Move up", () => Move(p, indexes, -1), indexes[0] > 0));
                m.Items.Add(Menus.Item("Move down", () => Move(p, indexes, 1), indexes[^1] < _songs.Count - 1));
            }
            m.Items.Add(Menus.Item("Remove from playlist", () =>
            {
                var ids = p.TrackIds.ToList();
                foreach (var id in chosen) ids.Remove(id);   // first occurrence of each selected song
                p.TrackIds = ids;
                App.Lib.SavePlaylist(p);
            }));
        }
        Menus.Open(m, SongList);
    }

    void Move(Playlist p, List<int> indexes, int delta)
    {
        var ids = _songs.Select(t => t.Id).ToList();
        foreach (var i in delta < 0 ? indexes : indexes.AsEnumerable().Reverse())
            (ids[i], ids[i + delta]) = (ids[i + delta], ids[i]);
        p.TrackIds = ids;
        App.Lib.SavePlaylist(p);
        foreach (var i in indexes) SongList.SelectedItems.Add(_songs[i + delta]);
    }
}
