using Android.Views;
using Halcyon.Core;

namespace Halcyon.Droid;

/// <summary>Long-press menus and the edit-info dialogs (the phone counterparts of the Windows context menus).</summary>
public static class Menus
{
    static Library Lib => App.Lib;
    static Player P => App.Player;

    static void AddToPlaylist(MainActivity a, Func<List<Track>> tracks)
    {
        var items = new List<(string, Action)>
        {
            ("New playlist…", () => Ui.Ask(a, "New playlist", "Playlist name", "", name =>
            {
                if (!string.IsNullOrWhiteSpace(name)) Lib.AddToPlaylist(Lib.CreatePlaylist(name.Trim()), tracks());
            }))
        };
        foreach (var p in Lib.Playlists) items.Add((p.Name, () => Lib.AddToPlaylist(p, tracks())));
        Ui.Menu(a, "Add to playlist", items.ToArray());
    }

    /// <summary>
    /// Edits your own tags on songs, videos or books (kind is Library.SongTag, VideoTag or BookTag). The box shows the tags
    /// every selected item has; saving adds the new ones and removes the ones you took out.
    /// </summary>
    public static void EditTags(MainActivity a, string kind, List<long> ids, string title)
    {
        var common = Lib.CommonTags(kind, ids);
        var input = Ui.Input(a, "tags, separated by commas", string.Join(", ", common));
        var inUse = Lib.AllTags().Take(10).Select(t => $"{t.Tag} ({t.Count})").ToList();
        var form = Ui.Column(a);
        form.SetPadding(Th.Dp(20), Th.Dp(8), Th.Dp(20), 0);
        form.AddView(input);
        if (ids.Count > 1) form.AddView(Ui.Text(a, "Shows the tags every selected item has. New tags are added to all of them.", 12, Th.FgFaint, null, 0));
        if (inUse.Count > 0) form.AddView(Ui.Text(a, "In use: " + string.Join(", ", inUse), 12, Th.FgFaint, null, 0));
        new AlertDialog.Builder(a).SetTitle(title)!.SetView(form)!
            .SetPositiveButton("Save", (_, _) =>
            {
                var wanted = Library.ParseTags(input.Text ?? "");
                Lib.ApplyTags(kind, ids, wanted.Except(common, StringComparer.OrdinalIgnoreCase), common.Except(wanted, StringComparer.OrdinalIgnoreCase));
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
        input.SetSelection(input.Text?.Length ?? 0);
    }

    public static void ForTracks(MainActivity a, List<Track> tracks, IEnumerable<Track>? context = null, Playlist? inPlaylist = null)
    {
        var items = new List<(string, Action)>
        {
            ("Play", () => { if (context != null) P.PlayTracks(context, tracks[0]); else P.PlayTracks(tracks); }),
            ("Add to now playing", () => P.EnqueueTracks(tracks)),
            ("Add to playlist…", () => AddToPlaylist(a, () => tracks)),
            ("Like", () => Lib.Rate(tracks, 1)),
            ("Dislike", () => Lib.Rate(tracks, -1)),
            ("Clear rating", () => Lib.Rate(tracks, 0)),
            (tracks.Count == 1 ? "Edit song info…" : $"Edit info for {tracks.Count} songs…", () => EditTracks(a, tracks, false)),
            (tracks.Count == 1 ? "Tags…" : $"Tags for {tracks.Count} songs…", () => EditTags(a, Library.SongTag, tracks.Select(t => t.Id).ToList(), tracks.Count == 1 ? "Tags: " + tracks[0].Title : $"Tags for {tracks.Count} songs")),
        };
        var album = Catalog.AlbumOf(tracks[0]);
        if (album != null)
        {
            items.Add(("Go to album", () => a.Push(Screens.AlbumPage(a, album.Key))));
            items.Add(("Find album info on Discogs…", () => a.Push(Screens.Discogs(a, album))));
        }
        if (inPlaylist != null)
            items.Add(("Remove from playlist", () =>
            {
                foreach (var t in tracks) inPlaylist.TrackIds.Remove(t.Id);
                Lib.SavePlaylist(inPlaylist);
            }));
        Ui.Menu(a, tracks.Count == 1 ? tracks[0].Title : null, items.ToArray());
    }

    public static void ForAlbum(MainActivity a, Album al)
    {
        Ui.Menu(a, al.Title,
            ("Play album", () => P.PlayTracks(al.Tracks)),
            ("Shuffle", () => P.PlayShuffled(al.Tracks)),
            ("Add to now playing", () => P.EnqueueTracks(al.Tracks)),
            ("Add to playlist…", () => AddToPlaylist(a, () => al.Tracks)),
            (Lib.IsPinned("album", al.Key) ? "Unpin from quickplay" : "Pin to quickplay", () => Lib.TogglePin("album", al.Key, al.Title)),
            ("Find album info on Discogs…", () => a.Push(Screens.Discogs(a, al))),
            ("Edit album info…", () => EditTracks(a, al.Tracks, true)),
            ("Tags for the album's songs…", () => EditTags(a, Library.SongTag, al.Tracks.Select(t => t.Id).ToList(), "Tags: " + al.Title)));
    }

    public static void ForArtist(MainActivity a, string artist)
    {
        var tracks = Catalog.AlbumsOf(artist).SelectMany(x => x.Tracks).ToList();
        Ui.Menu(a, artist,
            ("Play all", () => P.PlayTracks(tracks)),
            ("Shuffle", () => P.PlayShuffled(tracks)),
            ("Play smart mix", () => P.PlayTracks(Lib.SmartMix(artist))),
            ("Add to now playing", () => P.EnqueueTracks(tracks)),
            ("Add to playlist…", () => AddToPlaylist(a, () => tracks)),
            (Lib.IsPinned("artist", artist) ? "Unpin from quickplay" : "Pin to quickplay", () => Lib.TogglePin("artist", artist, artist)));
    }

    public static void ForVideos(MainActivity a, List<VideoItem> videos, List<VideoItem>? context = null)
    {
        Ui.Menu(a, videos.Count == 1 ? videos[0].DisplayTitle : null,
            ("Play", () =>
            {
                if (videos.Count == 1 && context != null && context.Contains(videos[0])) P.Play(context.Select(Player.From), context.IndexOf(videos[0]));
                else P.Play(videos.Select(Player.From));
                a.ShowNowPlaying();
            }),
            ("Add to now playing", () => P.Enqueue(videos.Select(Player.From))),
            (videos.Count == 1 ? "Edit video info…" : $"Edit info for {videos.Count} videos…", () => EditVideos(a, videos)),
            ("Tags…", () => EditTags(a, Library.VideoTag, videos.Select(v => v.Id).ToList(), videos.Count == 1 ? "Tags: " + videos[0].DisplayTitle : $"Tags for {videos.Count} videos")));
    }

    public static void ForVideoGroup(MainActivity a, string tab, VideoGroup g, Action<string> renamed)
    {
        var list = Catalog.GroupMembers(tab, g.Key);
        Ui.Menu(a, g.Name,
            ("Play all", () => { P.Play(list.Select(Player.From)); a.ShowNowPlaying(); }),
            ("Rename…", () => Ui.Ask(a, "Rename", "New name (use an existing name to merge the two)", g.Name, name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                Catalog.RenameGroup(tab, g.Key, name);
                renamed(name.Trim());
            })),
            ($"Edit info for all {list.Count}…", () => EditVideos(a, list)),
            ($"Tags for all {list.Count}…", () => EditTags(a, Library.VideoTag, list.Select(v => v.Id).ToList(), "Tags: " + g.Name)));
    }

    // ---------- edit dialogs ----------

    /// <summary>The value every item shares, or "" when they differ.</summary>
    static string Common<T>(IEnumerable<T> items, Func<T, string> get)
    {
        var values = items.Select(get).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : "";
    }

    internal static (LinearLayout form, Func<string, EditText> field) Form(MainActivity a)
    {
        var form = new LinearLayout(a) { Orientation = Orientation.Vertical };
        form.SetPadding(Th.Dp(20), Th.Dp(8), Th.Dp(20), 0);
        return (form, Add);

        EditText Add(string spec)
        {
            // spec: "label|initial|number?"
            var parts = spec.Split('|');
            form.AddView(Ui.Text(a, parts[0], 12, Th.FgDim));
            var e = Ui.Input(a, "", parts[1], false, parts.Length > 2);
            form.AddView(e, Ui.Fill());
            return e;
        }
    }

    internal static void ShowForm(MainActivity a, string title, View form, Action save)
    {
        var scroll = new ScrollView(a);
        scroll.AddView(form);
        new AlertDialog.Builder(a).SetTitle(title)!.SetView(scroll)!
            .SetPositiveButton("Save", (_, _) => save())!.SetNegativeButton("Cancel", (_, _) => { })!.Show();
    }

    /// <summary>
    /// Edits one song, or several at once. With several, a field left blank keeps each song's own value.
    /// Changes to server songs are kept here and survive syncs.
    /// </summary>
    public static void EditTracks(MainActivity a, List<Track> tracks, bool albumMode)
    {
        var single = tracks.Count == 1;
        var (form, field) = Form(a);
        var title = single ? field($"Title|{tracks[0].Title}") : null;
        var artist = field($"Artist|{Common(tracks, t => t.Artist)}");
        var albumArtist = field($"Album artist|{Common(tracks, t => t.AlbumArtist)}");
        var album = field($"Album|{Common(tracks, t => t.Album)}");
        var genre = field($"Genre|{Common(tracks, t => t.Genre)}");
        var year = field($"Year|{Common(tracks, t => t.Year > 0 ? t.Year.ToString() : "")}|n");
        var no = single ? field($"Track number|{tracks[0].TrackNoText}|n") : null;
        if (!single) form.AddView(Ui.Text(a, "Blank fields keep each song's own value.", 12, Th.FgFaint));

        ShowForm(a, albumMode ? "Edit album info" : single ? "Edit song info" : $"Edit {tracks.Count} songs", form, () =>
        {
            static string V(EditText? e) => e?.Text?.Trim() ?? "";
            foreach (var t in tracks)
            {
                if (title != null && V(title) != "") t.Title = V(title);
                if (V(artist) != "") t.Artist = V(artist);
                if (V(albumArtist) != "") t.AlbumArtist = V(albumArtist);
                if (V(album) != "") t.Album = V(album);
                if (V(genre) != "" || single) t.Genre = V(genre);
                if (int.TryParse(V(year), out var y)) t.Year = y; else if (single) t.Year = 0;
                if (no != null) t.TrackNo = int.TryParse(V(no), out var n) ? n : 0;
            }
            var failed = Lib.SaveTracks(tracks);
            if (failed > 0) Ui.Toast(a, $"{failed} file(s) could not be rewritten; the changes are kept in the collection.");
        });
    }

    /// <summary>Edits or regroups videos: what kind they are, which series / season / collection they belong to.</summary>
    public static void EditVideos(MainActivity a, List<VideoItem> videos)
    {
        var single = videos.Count == 1;
        var (form, field) = Form(a);
        var title = single ? field($"Title|{videos[0].Title}") : null;
        form.AddView(Ui.Text(a, "Kind", 12, Th.FgDim));
        var kinds = new[] { "(keep)", "movie", "episode", "video" };
        var kind = new Spinner(a) { Adapter = new ArrayAdapter<string>(a, Android.Resource.Layout.SimpleSpinnerDropDownItem, single ? kinds[1..] : kinds) };
        var commonKind = Common(videos, v => v.Kind);
        kind.SetSelection(single ? Math.Max(0, Array.IndexOf(kinds, videos[0].Kind) - 1) : Math.Max(0, Array.IndexOf(kinds, commonKind)));
        form.AddView(kind, Ui.Fill());
        var series = field($"Series (or folder)|{Common(videos, v => v.Series)}");
        var season = field($"Season|{Common(videos, v => v.Season > 0 ? v.Season.ToString() : "")}|n");
        var episode = single ? field($"Episode|{(videos[0].Episode > 0 ? videos[0].Episode.ToString() : "")}|n") : null;
        var year = field($"Year|{Common(videos, v => v.Year > 0 ? v.Year.ToString() : "")}|n");
        var collection = field($"Collection|{Common(videos, v => v.Collection)}");
        if (!single) form.AddView(Ui.Text(a, "Blank fields keep each video's own value.", 12, Th.FgFaint));

        ShowForm(a, single ? "Edit video info" : $"Edit {videos.Count} videos", form, () =>
        {
            static string V(EditText? e) => e?.Text?.Trim() ?? "";
            var k = kind.SelectedItem?.ToString() ?? "(keep)";
            foreach (var v in videos)
            {
                if (title != null && V(title) != "") v.Title = V(title);
                if (k != "(keep)") v.Kind = k;
                if (V(series) != "" || single) v.Series = V(series);
                if (int.TryParse(V(season), out var s)) v.Season = s; else if (single) v.Season = 0;
                if (episode != null) v.Episode = int.TryParse(V(episode), out var e) ? e : 0;
                if (int.TryParse(V(year), out var y)) v.Year = y; else if (single) v.Year = 0;
                if (V(collection) != "" || single) v.Collection = V(collection);
            }
            Lib.SaveVideos(videos);
        });
    }
}
