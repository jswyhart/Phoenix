using Avalonia.Controls;
using Halcyon.Core;

namespace Halcyon.Desktop;

/// <summary>Shared context menus for songs, albums and artists.</summary>
public static class Menus
{
    static Player P => App.Player;

    static MenuItem AddToPlaylist(Func<List<Track>> tracks)
    {
        var items = new List<object>
        {
            Ui.MenuItem("New playlist…", async () =>
            {
                var name = await Dialog.Ask("New playlist", "Playlist name", "");
                if (!string.IsNullOrWhiteSpace(name)) App.Lib.AddToPlaylist(App.Lib.CreatePlaylist(name.Trim()), tracks());
            })
        };
        if (App.Lib.Playlists.Count > 0) items.Add(new Separator());
        items.AddRange(App.Lib.Playlists.Select(p => Ui.MenuItem(p.Name, () => App.Lib.AddToPlaylist(p, tracks()))));
        return Ui.Sub("Add to playlist", items);
    }

    public static ContextMenu ForTracks(List<Track> tracks, IEnumerable<Track>? context = null)
    {
        var album = Catalog.AlbumOf(tracks[0]);
        return Ui.Menu(
            Ui.MenuItem("Play", () => { if (context != null) P.PlayTracks(context, tracks[0]); else P.PlayTracks(tracks); }),
            Ui.MenuItem("Add to now playing", () => P.EnqueueTracks(tracks)),
            AddToPlaylist(() => tracks),
            new Separator(),
            Ui.Sub("Rate", new object[]
            {
                Ui.MenuItem("Like", () => App.Lib.Rate(tracks, 1)), Ui.MenuItem("Dislike", () => App.Lib.Rate(tracks, -1)), Ui.MenuItem("Clear rating", () => App.Lib.Rate(tracks, 0))
            }),
            Ui.MenuItem(tracks.Count == 1 ? "Edit song info…" : $"Edit info for {tracks.Count} songs…", () => new EditInfoDialog(tracks, false).ShowDialog(HalcyonApp.Win)),
            album == null ? null : Ui.MenuItem("Find album info on Discogs…", () => new DiscogsDialog(album).ShowDialog(HalcyonApp.Win)),
            tracks.Count == 1 && tracks[0].IsLocal ? Ui.MenuItem(Platform.IsMac ? "Show in Finder" : "Show in file manager", () => Platform.RevealFile(tracks[0].SourceId)) : null);
    }

    public static ContextMenu ForAlbum(Album a) => Ui.Menu(
        Ui.MenuItem("Play album", () => P.PlayTracks(a.Tracks)),
        Ui.MenuItem("Add to now playing", () => P.EnqueueTracks(a.Tracks)),
        AddToPlaylist(() => a.Tracks),
        Ui.MenuItem(App.Lib.IsPinned("album", a.Key) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("album", a.Key, a.Title)),
        new Separator(),
        Ui.MenuItem("Find album info on Discogs…", () => new DiscogsDialog(a).ShowDialog(HalcyonApp.Win)),
        Ui.MenuItem("Edit album info…", () => new EditInfoDialog(a.Tracks, true).ShowDialog(HalcyonApp.Win)));

    public static ContextMenu ForArtist(string artist)
    {
        var tracks = Catalog.AlbumsOf(artist).SelectMany(a => a.Tracks).ToList();
        return Ui.Menu(
            Ui.MenuItem("Play all", () => P.PlayTracks(tracks)),
            Ui.MenuItem("Shuffle", () => P.PlayShuffled(tracks)),
            Ui.MenuItem("Play smart mix", () => P.PlayTracks(App.Lib.SmartMix(artist))),
            Ui.MenuItem("Add to now playing", () => P.EnqueueTracks(tracks)),
            AddToPlaylist(() => tracks),
            Ui.MenuItem(App.Lib.IsPinned("artist", artist) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("artist", artist, artist)));
    }

    /// <summary>Videos play through VLC, which opens the server's original file directly; the server's own conversion is the fallback.</summary>
    public static PlayItem Item(VideoItem v)
    {
        var item = Player.From(v);
        if (v.Source == "jellyfin") item.Resolve = () => Task.FromResult(App.Jellyfin.DirectVideoUrl(v.Location));
        return item;
    }
}
