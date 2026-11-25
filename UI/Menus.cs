using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Shared play actions and context menus.</summary>
public static class Menus
{
    public static void Play(IEnumerable<Track> tracks, Track? start = null)
    {
        var list = tracks.ToList();
        App.Player.Play(list.Select(Player.From), start == null ? 0 : Math.Max(0, list.IndexOf(start)));
    }

    public static void PlayShuffled(IEnumerable<Track> tracks)
    {
        var rnd = new Random();
        Play(tracks.Where(t => t.Rating >= 0).OrderBy(_ => rnd.Next()));
    }

    /// <summary>The "Info source" submenu: which metadata source an album uses when missing info is filled in.</summary>
    public static MenuItem InfoSource(Album a)
    {
        var root = new MenuItem { Header = "Info source" };
        var now = Metadata.SourceOf(a);
        MenuItem Choice(string header, string id, bool enabled = true)
        {
            var mi = Item(header, () =>
            {
                Metadata.SetSource(a, id);
                App.Win.SetStatus(id == Metadata.Auto ? $"“{a.Title}” uses your sources by weight" : id == Metadata.Never ? $"“{a.Title}” is left alone when missing info is filled in" : $"“{a.Title}” uses {Metadata.Find(id)?.Name} only");
            }, enabled);
            mi.IsChecked = now == id;
            return mi;
        }
        root.Items.Add(Choice("Automatic (by weight)", Metadata.Auto));
        root.Items.Add(new Separator());
        // only the sources switched on in settings → metadata are offered (plus the one this album is tied to, if it was switched off since)
        foreach (var s in InfoSources(a)) root.Items.Add(Choice(s.Ready ? s.Name : s.Name + " (needs its key)", s.Id, s.Ready));
        root.Items.Add(new Separator());
        root.Items.Add(Choice("Never fill in automatically", Metadata.Never));
        return root;
    }

    /// <summary>The sources the "Info source" menu lists for an album: those switched on in settings.</summary>
    public static List<IMetaSource> InfoSources(Album a)
    {
        var tied = Metadata.SourceOf(a);
        return Metadata.Sources.Where(s => Metadata.Setting(s.Id).Enabled || s.Id == tied).ToList();
    }

    static async void Identify(Track t)
    {
        App.Win.SetStatus($"listening to “{t.Title}”…");
        try
        {
            var found = await Metadata.IdentifyAsync(t.Location);
            App.Win.SetStatus("");
            if (found == null) { MessageBox.Show(App.Win, "AcoustID does not know this recording.", "Phoenix", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var text = $"This sounds like:\n\n{found.Title}\n{found.Artist}" + (found.Album != "" ? $"\n{found.Album}" : "") + $"\n\n({found.Score:P0} sure)\n\nUse these details for the song?";
            if (MessageBox.Show(App.Win, text, "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            t.Title = found.Title;
            if (found.Artist != "") { if (t.AlbumArtist == "" || t.AlbumArtist == t.Artist) t.AlbumArtist = found.Artist; t.Artist = found.Artist; }
            if (found.Album != "" && (t.Album == "" || t.Album == "Unknown Album")) t.Album = found.Album;
            var failed = App.Lib.SaveTracks(new[] { t });
            App.Win.SetStatus(failed > 0 ? "saved in Phoenix, but the file could not be updated" : $"now “{t.Title}” by {t.Artist}");
        }
        catch (Exception ex) { App.Win.SetStatus("couldn't identify the song: " + ex.Message); }
    }

    /// <summary>Starts an endless mix of music like the given song.</summary>
    public static void SmartDj(Track? seed)
    {
        if (seed == null) return;
        App.Player.PlaySmartDj(seed);
        App.Win.SetStatus(App.Player.Queue.Count > 1 ? $"smart dj: music like “{seed.Title}” by {seed.Artist}" : "smart dj found nothing else like that in your collection");
    }

    /// <summary>A song to seed a mix from: one you like if there is one, never one you dislike.</summary>
    static Track? Pick(IReadOnlyList<Track> tracks)
    {
        var rnd = new Random();
        var liked = tracks.Where(t => t.Rating > 0).ToList();
        var ok = liked.Count > 0 ? liked : tracks.Where(t => t.Rating >= 0).ToList();
        return ok.Count > 0 ? ok[rnd.Next(ok.Count)] : null;
    }

    public static void Enqueue(IEnumerable<Track> tracks) => App.Player.Enqueue(tracks.Select(Player.From));

    static MenuItem AddToBurnList(Func<IEnumerable<Track>> tracks) => Item("Add to burn list", () =>
    {
        foreach (var t in tracks()) App.BurnList.Add(t);
        App.Win.SetStatus($"{App.BurnList.Count} songs on the burn list (collection → disc)");
    });

    public static MenuItem Item(string header, Action click, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    /// <summary>What an album's tile shows in a cover grid.</summary>
    public static TileLook AlbumLook(object o) => o is Album a ? new TileLook(a.ArtPath, a.Title, a.Subtitle, "\uE8D6", a.SourceBadge) : default;

    public static void Open(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    static MenuItem AddToPlaylist(Func<List<Track>> tracks)
    {
        var root = new MenuItem { Header = "Add to playlist" };
        root.Items.Add(Item("New playlist…", () =>
        {
            var name = InputDialog.Ask("New playlist", "Playlist name", "");
            if (!string.IsNullOrWhiteSpace(name)) App.Lib.AddToPlaylist(App.Lib.CreatePlaylist(name.Trim()), tracks());
        }));
        if (App.Lib.Playlists.Count > 0) root.Items.Add(new Separator());
        foreach (var p in App.Lib.Playlists) root.Items.Add(Item(p.Name, () => App.Lib.AddToPlaylist(p, tracks())));
        return root;
    }

    static void Rate(List<Track> tracks, int rating) => App.Lib.Rate(tracks, rating);

    public static ContextMenu ForTracks(List<Track> tracks, IEnumerable<Track>? context = null)
    {
        var m = new ContextMenu();
        m.Items.Add(Item("Play", () => { if (context != null) Play(context, tracks[0]); else Play(tracks); }));
        m.Items.Add(Item("Add to now playing", () => Enqueue(tracks)));
        m.Items.Add(Item("Start Smart DJ", () => SmartDj(tracks[0])));
        m.Items.Add(AddToPlaylist(() => tracks));
        m.Items.Add(AddToBurnList(() => tracks));
        m.Items.Add(new Separator());
        var rate = new MenuItem { Header = "Rate" };
        rate.Items.Add(Item("Like", () => Rate(tracks, 1)));
        rate.Items.Add(Item("Dislike", () => Rate(tracks, -1)));
        rate.Items.Add(Item("Clear rating", () => Rate(tracks, 0)));
        m.Items.Add(rate);
        m.Items.Add(Item(tracks.Count == 1 ? "Edit song info…" : $"Edit info for {tracks.Count} songs…", () => new EditInfoDialog(tracks, false).ShowDialog()));
        m.Items.Add(Item(tracks.Count == 1 ? "Edit tags…" : $"Edit tags for {tracks.Count} songs…",
            () => new EditTagsDialog(Library.SongTag, tracks.Select(t => t.Id), tracks.Count == 1 ? $"“{tracks[0].Title}”" : $"{tracks.Count} songs").ShowDialog()));
        m.Items.Add(Item("Tag editor…", () => new TagEditorWindow(tracks).ShowDialog()));
        var album = App.Lib.Albums.FirstOrDefault(a => a.Key == tracks[0].AlbumKey);
        if (album != null) m.Items.Add(Item("Find album info…", () => new DiscogsDialog(album).ShowDialog()));
        if (album != null) m.Items.Add(Item("Mixview", () => MixviewPage.Open(album, album.Artist)));
        if (tracks.Count == 1 && tracks[0].IsLocal && Metadata.CanIdentify) m.Items.Add(Item("Identify by sound…", () => Identify(tracks[0])));
        if (tracks.Count == 1 && tracks[0].IsLocal)
            m.Items.Add(Item("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{tracks[0].SourceId}\"")));
        return m;
    }

    public static ContextMenu ForAlbum(Album a)
    {
        var m = new ContextMenu();
        m.Items.Add(Item("Play album", () => Play(a.Tracks)));
        m.Items.Add(Item("Add to now playing", () => Enqueue(a.Tracks)));
        m.Items.Add(Item("Start Smart DJ", () => SmartDj(Pick(a.Tracks)), a.Tracks.Count > 0));
        m.Items.Add(AddToPlaylist(() => a.Tracks));
        m.Items.Add(AddToBurnList(() => a.Tracks));
        m.Items.Add(Item(App.Lib.IsPinned("album", a.Key) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("album", a.Key, a.Title)));
        m.Items.Add(Item("Mixview", () => MixviewPage.Open(a, a.Artist)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Find album info…", () => new DiscogsDialog(a).ShowDialog()));
        m.Items.Add(InfoSource(a));
        m.Items.Add(Item("Edit album info…", () => new EditInfoDialog(a.Tracks, true).ShowDialog()));
        m.Items.Add(Item("Tag editor…", () => new TagEditorWindow(a.Tracks).ShowDialog()));
        m.Items.Add(Item("Edit tags for all songs…", () => new EditTagsDialog(Library.SongTag, a.Tracks.Select(t => t.Id), $"“{a.Title}” ({a.Tracks.Count} songs)").ShowDialog()));
        return m;
    }

    public static ContextMenu ForArtist(string artist)
    {
        var tracks = App.Lib.Albums.Where(a => string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase)).SelectMany(a => a.Tracks).ToList();
        var m = new ContextMenu();
        m.Items.Add(Item("Play all", () => Play(tracks)));
        m.Items.Add(Item("Shuffle", () => PlayShuffled(tracks)));
        m.Items.Add(Item("Start Smart DJ", () => SmartDj(Pick(tracks)), tracks.Count > 0));
        m.Items.Add(Item("Add to now playing", () => Enqueue(tracks)));
        m.Items.Add(AddToPlaylist(() => tracks));
        m.Items.Add(Item(App.Lib.IsPinned("artist", artist) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("artist", artist, artist)));
        m.Items.Add(Item("Mixview", () => MixviewPage.Open(null, artist)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Tag editor…", () => new TagEditorWindow(tracks).ShowDialog(), tracks.Count > 0));
        return m;
    }
}
