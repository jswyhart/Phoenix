using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>Every screen of the app, each built as a <see cref="Page"/> whose rows come from the shared catalog.</summary>
public static class Screens
{
    static Library Lib => App.Lib;
    static Player P => App.Player;

    // ---------- shared row builders ----------

    /// <summary>A row's tags as "#chill  #road trip", or "" when it has none.</summary>
    static string TagLine(string kind, long id) =>
        string.Join("  ", Lib.TagsOf(kind, id).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Select(t => "#" + t));

    /// <summary>The line of details, then the row's tags on a second line when it has any.</summary>
    static string WithTags(string details, string kind, long id) =>
        TagLine(kind, id) is { Length: > 0 } tags ? (details == "" ? tags : details + "\n" + tags) : details;

    /// <summary>
    /// One song. <paramref name="index"/> is its position in the list (shown as 1, 2, 3… down the list, not the album's
    /// track number); pass -1 for no number. Tags are shown after the artist and album.
    /// </summary>
    static Row SongRow(MainActivity a, Track t, Func<List<Track>> context, bool art, int index = -1, Playlist? playlist = null, bool showArtist = true) => new()
    {
        Title = t.Title,
        Sub = WithTags(Fmt.Join(showArtist ? t.Artist : "", showArtist ? t.Album : ""), Library.SongTag, t.Id),
        Lead = index >= 0 ? (index + 1).ToString() : "",
        Trail = t.DurationText,
        Art = t.ArtPath, ArtShape = art ? 1 : 0,
        Rate = t,
        IsCurrent = () => P.Current?.Track == t,
        Click = () => P.PlayTracks(context(), t),
        Long = () => Menus.ForTracks(a, new List<Track> { t }, context(), playlist)
    };

    static Row AlbumRow(MainActivity a, Album al) => new()
    {
        Title = al.Title, Sub = Fmt.Join(al.Artist, al.SourceBadge), Art = al.ArtPath, ArtShape = 1,
        Click = () => a.Push(AlbumPage(a, al.Key)),
        Long = () => Menus.ForAlbum(a, al)
    };

    static void PlayVideos(MainActivity a, List<VideoItem> list, VideoItem start)
    {
        P.Play(list.Select(Player.From), Math.Max(0, list.IndexOf(start)));
        a.ShowNowPlaying();
    }

    static Row VideoRow(MainActivity a, VideoItem v, Func<List<VideoItem>> context, bool episode) => new()
    {
        Title = episode ? v.RowTitle : v.DisplayTitle,
        Sub = WithTags(episode ? Fmt.Join(v.EpisodeCode, v.DurationText) : v.Subtitle, Library.VideoTag, v.Id),
        Art = v.ArtPath, ArtShape = 3,
        IsCurrent = () => P.Current is { IsVideo: true } c && c.Title == v.Title && c.ArtPath == v.ArtPath,
        Click = () => PlayVideos(a, context(), v),
        Long = () => Menus.ForVideos(a, new List<VideoItem> { v }, context())
    };

    static Row PosterTile(MainActivity a, VideoItem v) => new()
    {
        Title = v.Title, Sub = Fmt.Join(v.Year > 0 ? v.Year.ToString() : "", v.DurationText), Art = v.ArtPath,
        Click = () => PlayVideos(a, new List<VideoItem> { v }, v),
        Long = () => Menus.ForVideos(a, new List<VideoItem> { v })
    };

    // ---------- quickplay ----------

    public static Page Quickplay(MainActivity a)
    {
        Row Tile(QuickItem q) => new()
        {
            Title = q.Title, Sub = q.Subtitle, Art = q.ArtPath, ArtShape = 1, Click = q.Play,
            Long = q.Pin is { } pin ? () => Ui.Menu(a, q.Title, ("Play", q.Play), ("Unpin from quickplay", () => Lib.TogglePin(pin.Kind, pin.Key, pin.Title))) : null
        };
        void Section(List<Row> rows, string caption, List<QuickItem> items, string hint)
        {
            rows.Add(Row.Caption(caption));
            if (items.Count == 0) rows.Add(Row.Note(hint));
            else rows.AddRange(items.Select(Tile));   // each section is a list down the screen, not a sideways strip
        }
        var page = new Page(a, "quickplay") { ReloadOnRatings = true };
        page.Actions(("play liked", () => P.PlayTracks(Lib.Liked())), ("shuffle liked", () => P.PlayShuffled(Lib.Liked())), ("shuffle everything", () => P.PlayShuffled(Lib.Tracks)), ("listening stats", () => a.Push(MoreScreens.Stats(a))));
        return page.List(() =>
        {
            var rows = new List<Row>();
            if (Lib.Tracks.Count == 0 && Lib.Videos.Count == 0)
            {
                rows.Add(Row.Note("Your collection is empty. Copy music or videos onto this device, or connect a Jellyfin or Plex server in settings."));
                rows.Add(new Row { Title = "open settings", Click = () => a.Navigate("settings") });
                return rows;
            }
            Section(rows, "PINS", Catalog.Pins(), "Long-press an album, artist or playlist and choose “pin to quickplay”.");
            Section(rows, "NEW", Catalog.NewAlbums(), "Albums you add show up here.");
            Section(rows, "HISTORY", Catalog.History(), "Albums you play show up here.");
            Section(rows, "LIKED", Catalog.Liked(), "Tap the heart on a song to like it. Liked songs appear here, newest first.");
            return rows;
        });
    }

    // ---------- sorting ----------

    /// <summary>
    /// The "sort" action of a song list: pick a column; picking it again reverses it, a third time goes back to the
    /// original order (the same cycle as clicking a column heading in the Windows app).
    /// </summary>
    static (string, Action) SortLink(MainActivity a, SongSort sort, Action changed)
    {
        var label = sort.Active ? $"sort: {SongSort.Keys.First(k => k.Key == sort.Key).Label} {(sort.Descending ? "▼" : "▲")}" : "sort";
        return (label, () => Ui.Menu(a, "Sort songs by",
            SongSort.Keys.Select(k => ((sort.Key == k.Key ? (sort.Descending ? "▼  " : "▲  ") : "") + k.Label, (Action)(() => { sort.Click(k.Key); changed(); })))
                .Append(("original order", () => { sort.Clear(); changed(); })).ToArray()));
    }

    // ---------- music ----------

    public static Page Music(MainActivity a)
    {
        var tab = "artists";
        var songSort = new SongSort();
        var view = new ViewChoice("music");
        var albumSorts = new[] { ("", "artist"), ("title", "album title"), ("year", "year"), ("added", "date added"), ("plays", "most played"), ("recent", "last played") };
        var shows = new[] { ("", "everything"), ("liked", "liked"), ("unplayed", "never played"), ("local", "on this phone"), ("server", "on servers") };
        bool KeepSong(Track t) => (view.Show switch { "liked" => t.Rating > 0, "unplayed" => t.PlayCount == 0, "local" => t.IsLocal, "server" => !t.IsLocal, _ => true }) && view.Matches(t.Title, t.Artist, t.Album);
        bool KeepAlbum(Album al) => (view.Show switch { "liked" => al.Tracks.Any(t => t.Rating > 0), "unplayed" => al.Tracks.All(t => t.PlayCount == 0), "local" => al.Source == "local", "server" => al.Source != "local", _ => true })
                                    && (view.Matches(al.Title, al.Artist) || al.Tracks.Any(t => view.Matches(t.Title)));
        var page = new Page(a, "music");
        page.Subtitle = () => Fmt.Join(Fmt.Plural(Catalog.Artists().Count, "artist"), Fmt.Plural(Lib.Albums.Count, "album"), Fmt.Plural(Lib.Tracks.Count, "song"));
        page.Chips(() => new[] { "artists", "genres", "albums", "songs" }, () => tab, t => tab = t);
        return page.Show(() =>
        {
            const string empty = "No music yet. Add songs to this device's Music folder or connect a server in settings.";
            var byName = StringComparer.OrdinalIgnoreCase;
            switch (tab)
            {
                case "genres":
                    page.Actions(page.Opts(view, Opts.None, Opts.None).ToArray());
                    return new Content(Catalog.Genres().Where(g => view.Matches(g)).Select(g => new Row
                    {
                        Title = g, Sub = Fmt.Plural(Catalog.AlbumsInGenre(g).Count, "album"),
                        Click = () => a.Push(new Page(a, g).Grid(() => Catalog.AlbumsInGenre(g).Select(al => AlbumRow(a, al)).ToList(), 150, 1f))
                    }).ToList(), Empty: empty);
                case "albums":
                    page.Actions(page.Opts(view, albumSorts, shows).ToArray());
                    var albums = view.Order(Lib.Albums.Where(KeepAlbum), key => key switch
                    {
                        "title" => l => l.OrderBy(x => x.Title, byName),
                        "year" => l => l.OrderBy(x => x.Year == 0 ? int.MaxValue : x.Year).ThenBy(x => x.Title, byName),
                        "added" => l => l.OrderByDescending(x => x.DateAdded),
                        "plays" => l => l.OrderByDescending(x => x.Tracks.Sum(t => t.PlayCount)).ThenBy(x => x.Title, byName),
                        "recent" => l => l.OrderByDescending(x => x.Tracks.Max(t => t.LastPlayed)),
                        _ => null
                    });
                    return new Content(albums.Select(al => AlbumRow(a, al)).ToList(), 150, 1f, Lib.Albums.Count == 0 ? empty : "No album matches.");
                case "songs":
                    var songs = songSort.Apply(Catalog.Songs().Where(KeepSong));
                    // the song list has its own sort (by column); "show" and "filter" come from the same bar as the albums
                    page.Actions(new[] { SortLink(a, songSort, () => page.Reload()) }.Concat(page.Opts(view, Opts.None, shows)).ToArray());
                    return new Content(songs.Select((t, i) => SongRow(a, t, () => songs, true, i)).ToList(), Empty: empty);
                default:
                    page.Actions(page.Opts(view, Opts.None, Opts.None).ToArray());
                    return new Content(Catalog.Artists().Where(name => view.Matches(name)).Select(name =>
                    {
                        var albums = Catalog.AlbumsOf(name);
                        return new Row
                        {
                            Title = name, Sub = Fmt.Join(Fmt.Plural(albums.Count, "album"), Fmt.Plural(albums.Sum(x => x.Tracks.Count), "song")),
                            Art = albums.Select(x => x.ArtPath).FirstOrDefault(p => p != "") ?? "", ArtShape = 1,
                            Click = () => a.Push(Artist(a, name)),
                            Long = () => Menus.ForArtist(a, name)
                        };
                    }).ToList(), Empty: empty);
            }
        });
    }

    public static Page Artist(MainActivity a, string name)
    {
        List<Track> All() => Catalog.AlbumsOf(name).SelectMany(x => x.Tracks).ToList();
        var page = new Page(a, name);
        page.Actions(("play all", () => P.PlayTracks(All())), ("shuffle", () => P.PlayShuffled(All())),
            ("smart mix", () => P.PlayTracks(Lib.SmartMix(name))), ("more…", () => Menus.ForArtist(a, name)));
        return page.Grid(() => Catalog.AlbumsOf(name).Select(al => new Row
        {
            Title = al.Title, Sub = Fmt.Join(al.Year > 0 ? al.Year.ToString() : "", al.SourceBadge), Art = al.ArtPath,
            Click = () => a.Push(AlbumPage(a, al.Key)), Long = () => Menus.ForAlbum(a, al)
        }).ToList(), 150, 1f, "Nothing by this artist is left in the collection.");
    }

    public static Page AlbumPage(MainActivity a, string key)
    {
        // Editing an album changes its key, so follow it by one of its songs.
        var anchor = Lib.Albums.FirstOrDefault(x => x.Key == key)?.Tracks.FirstOrDefault()?.Id ?? -1;
        Album? Find() => Lib.Albums.FirstOrDefault(x => x.Tracks.Any(t => t.Id == anchor));
        var page = new Page(a, Find()?.Title ?? "album");
        page.TitleSource = () => Find()?.Title ?? "album";
        page.Subtitle = () => Find() is { } al
            ? Fmt.Join(al.Artist, al.Year > 0 ? al.Year.ToString() : "", al.Genre, Fmt.Plural(al.Tracks.Count, "song"), Fmt.Time(al.Tracks.Sum(t => t.DurationMs)), al.SourceBadge) : "";
        page.Actions(("play", () => { if (Find() is { } al) P.PlayTracks(al.Tracks); }), ("shuffle", () => { if (Find() is { } al) P.PlayShuffled(al.Tracks); }),
            ("discogs", () => { if (Find() is { } al) a.Push(Discogs(a, al)); }), ("more…", () => { if (Find() is { } al) Menus.ForAlbum(a, al); }));
        return page.List(() =>
        {
            var al = Find();
            if (al == null) return new List<Row>();
            var rows = new List<Row> { new() { Title = al.Title, Sub = al.Artist, Art = al.ArtPath, ArtShape = 1, Click = () => P.PlayTracks(al.Tracks), Long = () => Menus.ForAlbum(a, al) } };
            var multiDisc = al.Tracks.Select(t => t.DiscNo).Distinct().Count() > 1;
            var disc = -1;
            var n = 0;
            foreach (var t in al.Tracks)
            {
                if (multiDisc && t.DiscNo != disc) { disc = t.DiscNo; rows.Add(Row.Caption($"DISC {disc}")); }
                var row = SongRow(a, t, () => Find()?.Tracks ?? new(), false, n++, null, false);
                if (!string.Equals(t.Artist, al.Artist, StringComparison.OrdinalIgnoreCase)) row.Sub = WithTags(t.Artist, Library.SongTag, t.Id);
                rows.Add(row);
            }
            return rows;
        }, "This album is no longer in the collection.");
    }

    // ---------- videos ----------

    public static Page Videos(MainActivity a)
    {
        var tab = "";
        var view = new ViewChoice("videos");
        var sorts = new[] { ("", "title"), ("year", "year"), ("added", "date added"), ("length", "length") };
        var shows = new[] { ("", "everything"), ("local", "on this phone"), ("server", "on servers") };
        List<VideoItem> Arrange(IEnumerable<VideoItem> videos) => view.Order(
            videos.Where(v => (view.Show switch { "local" => v.Source == "local", "server" => v.Source != "local", _ => true }) && view.Matches(v.Title, v.Series, v.Collection)), key => key switch
            {
                "year" => l => l.OrderBy(v => v.Year == 0 ? int.MaxValue : v.Year).ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase),
                "added" => l => l.OrderByDescending(v => v.DateAdded),
                "length" => l => l.OrderByDescending(v => v.DurationMs),
                _ => null
            });
        var page = new Page(a, "videos");
        page.Subtitle = Catalog.VideoCounts;
        page.Chips(Catalog.VideoTabs, () => tab, t => tab = t);
        return page.Show(() =>
        {
            var tabs = Catalog.VideoTabs();
            if (!tabs.Contains(tab)) tab = tabs[0];
            const string empty = "No videos yet. Add some to this device's Movies folder or connect a server in settings.";
            switch (tab)
            {
                case "movies":
                    page.Actions(page.Opts(view, sorts, shows).ToArray());
                    return new Content(Arrange(Catalog.Movies()).Select(v => PosterTile(a, v)).ToList(), 108, 1.5f, empty);
                case "shows":
                case "collections":
                case "folders":
                    var kind = tab;
                    page.Actions(page.Opts(view, Opts.None, Opts.None).ToArray());
                    return new Content(Catalog.VideoGroups(kind).Where(g => view.Matches(g.Name)).Select(g => new Row
                    {
                        Title = g.Name, Sub = Fmt.Plural(g.Count, kind == "shows" ? "episode" : kind == "collections" ? "movie" : "video"),
                        Art = g.Art, ArtShape = kind == "folders" ? 3 : 2,
                        Click = () => a.Push(VideoGroupPage(a, kind, g.Key)),
                        Long = () => Menus.ForVideoGroup(a, kind, g, _ => { })
                    }).ToList(), Empty: empty);
                default:
                    page.Actions(page.Opts(view, sorts, shows).ToArray());
                    var all = Arrange(Catalog.AllVideos());
                    return new Content(all.Select(v => VideoRow(a, v, () => new List<VideoItem> { v }, false)).ToList(), Empty: empty);
            }
        });
    }

    /// <summary>One series (with season chips), collection or folder.</summary>
    public static Page VideoGroupPage(MainActivity a, string tab, string key)
    {
        int? season = null;
        var page = new Page(a, key == "" ? "unknown" : key);
        page.TitleSource = () => key == "" ? "unknown" : key;
        List<VideoItem> Members() => Catalog.GroupMembers(tab, key).Where(v => tab != "shows" || season == null || v.Season == season).ToList();
        void More() => Menus.ForVideoGroup(a, tab, new VideoGroup(key, "", Catalog.GroupMembers(tab, key).Count, key), name => { key = name; season = null; page.Reload(); });
        page.Actions(("play all", () => { var m = Members(); if (m.Count > 0) PlayVideos(a, m, m[0]); }), ("more…", More));
        if (tab == "shows")
            page.Chips(() =>
                {
                    var seasons = Catalog.Seasons(key);
                    return seasons.Count > 1 ? new[] { "all seasons" }.Concat(seasons.Select(Catalog.SeasonName)) : Array.Empty<string>();
                },
                () => season == null ? "all seasons" : Catalog.SeasonName(season.Value),
                name => season = name == "all seasons" ? null : Catalog.Seasons(key).Cast<int?>().FirstOrDefault(s => Catalog.SeasonName(s!.Value) == name));
        page.Subtitle = () => Fmt.Plural(Members().Count, tab == "shows" ? "episode" : tab == "collections" ? "movie" : "video");
        return page.Show(() =>
        {
            if (tab == "shows" && season != null && !Catalog.Seasons(key).Contains(season.Value)) season = null;
            var members = Members();
            return tab == "collections"
                ? new Content(members.Select(v => PosterTile(a, v)).ToList(), 108, 1.5f, "Nothing here any more.")
                : new Content(members.Select(v => VideoRow(a, v, Members, tab == "shows")).ToList(), Empty: "Nothing here any more.");
        });
    }

    // ---------- pictures ----------

    public static Page Pictures(MainActivity a)
    {
        var folders = new List<PicFolder>();
        var loaded = false;
        var page = new Page(a, "pictures");
        page.List(() => folders.Select(f => new Row
        {
            Title = f.Name, Sub = Fmt.Plural(f.Count, "picture"), ArtShape = 1,
            Art = Catalog.PicturesIn(f).FirstOrDefault()?.ArtPath ?? "",
            Click = () => a.Push(PictureGrid(a, f))
        }).ToList(), "");
        _ = LoadAsync();
        return page;

        async Task LoadAsync()
        {
            var roots = App.Settings.PictureFolders.ToList();
            var list = await Task.Run(() => Catalog.PictureFolders(roots));
            // Photo libraries on connected servers sit alongside the local folders.
            foreach (var fetch in new Func<Task<List<RemotePhoto>>>[] { App.Jellyfin.FetchPhotosAsync, App.Plex.FetchPhotosAsync })
                try
                {
                    foreach (var g in (await fetch()).GroupBy(p => p.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                        list.Add(new PicFolder(g.Key, "", g.Count(), g.Select(p => new PictureItem(p.Name, p.FullUrl, p.ThumbUrl)).ToList()));
                }
                catch { /* server offline: show what we have */ }
            folders = list;
            loaded = true;
            page.Show(() => new Content(folders.Select(f => new Row
            {
                Title = f.Name, Sub = Fmt.Plural(f.Count, "picture"), ArtShape = 1,
                Art = Catalog.PicturesIn(f).FirstOrDefault()?.ArtPath ?? "",
                Click = () => a.Push(PictureGrid(a, f))
            }).ToList(), Empty: loaded ? "No pictures found. Add folders in settings." : ""));
        }
    }

    static Page PictureGrid(MainActivity a, PicFolder f)
    {
        var everything = Catalog.PicturesIn(f);
        var view = new ViewChoice("pictures");
        static DateTime DateOf(PictureItem p) { try { return File.Exists(p.File) ? File.GetLastWriteTimeUtc(p.File) : DateTime.MinValue; } catch { return DateTime.MinValue; } }
        List<PictureItem> Shown() => view.Order(everything.Where(p => view.Matches(p.Name)), key => key == "date" ? l => l.OrderByDescending(DateOf) : null);
        var page = new Page(a, f.Name);
        page.Subtitle = () => Shown().Count == everything.Count ? Fmt.Plural(everything.Count, "picture") : $"{Shown().Count} of {everything.Count} pictures";
        return page.Show(() =>
        {
            var pics = Shown();
            page.Actions(new[] { ("slideshow", (Action)(() => a.ShowViewer(pics, 0, true))) }.Concat(page.Opts(view, new[] { ("", "name"), ("date", "date") }, Opts.None)).ToArray());
            return new Content(pics.Select((p, i) => new Row { Art = p.ArtPath, Click = () => a.ShowViewer(pics, i, false) }).ToList(), 104, 1f, "No pictures here.");
        });
    }

    // ---------- podcasts ----------

    public static Page Podcasts(MainActivity a)
    {
        var page = new Page(a, "podcasts");
        var busy = false;
        var tab = "my podcasts";
        var view = new ViewChoice("podcasts");
        var browser = new MoreScreens.PodcastBrowser();
        page.Chips(() => new[] { "my podcasts", "sources" }, () => tab, t => { tab = t; if (t == "sources" && !browser.Opened) browser.Browse(page.Reload); });
        void MineActions() => page.Actions(new (string, Action)[] {
            ("add podcast", () => Ui.Ask(a, "Add a podcast", "Podcast RSS feed address", "https://", async url =>
            {
                if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
                try
                {
                    App.SetStatus("fetching podcast…");
                    var (podcast, episodes) = await PodcastService.FetchAsync(url);
                    Lib.Db.SavePodcast(podcast, episodes);
                    Lib.Podcasts = Lib.Db.LoadPodcasts();
                    App.SetStatus("");
                    page.Reload();
                }
                catch (Exception ex) { App.SetStatus(""); Ui.Alert(a, "Couldn't add that podcast.\n\n" + ex.Message); }
            })),
            ("refresh all", async () =>
            {
                if (busy) return;
                busy = true;
                var failed = 0;
                foreach (var p in Lib.Podcasts.ToList())
                {
                    App.SetStatus($"refreshing {p.Title}…");
                    try { var (podcast, episodes) = await PodcastService.FetchAsync(p.FeedUrl); Lib.Db.SavePodcast(podcast, episodes); }
                    catch { failed++; }
                }
                Lib.Podcasts = Lib.Db.LoadPodcasts();
                App.SetStatus(failed > 0 ? $"{failed} podcast feed(s) could not be refreshed" : "");
                busy = false;
                page.Reload();
            }) }.Concat(page.Opts(view, new[] { ("", "title"), ("author", "author") }, Opts.None)).ToArray());
        return page.Show(() =>
        {
            if (tab == "sources") return MoreScreens.PodcastSources(a, page, browser);
            MineActions();
            var byName = StringComparer.OrdinalIgnoreCase;
            var mine = view.Order(Lib.Podcasts.Where(p => view.Matches(p.Title, p.Author)), key => key == "author" ? l => l.OrderBy(p => p.Author == "" ? "\uFFFF" : p.Author, byName).ThenBy(p => p.Title, byName) : null);
            return new Content(mine.Select(p => new Row
        {
            Title = p.Title, Sub = p.Author, Art = p.ArtUrl, ArtShape = 1,
            Click = () => a.Push(PodcastPage(a, p)),
            Long = () => Ui.Menu(a, p.Title,
                ("Play newest episode", () => { var eps = Lib.Db.LoadEpisodes(p.Id); if (eps.Count > 0) P.Play(new[] { Player.From(eps[0], p) }); }),
                ("Unsubscribe", () => { Lib.Db.DeletePodcast(p.Id); Lib.Podcasts = Lib.Db.LoadPodcasts(); page.Reload(); }))
        }).ToList(), Empty: Lib.Podcasts.Count == 0 ? "No podcasts yet.\n\nFind some under “sources”, or tap “add podcast” and paste a feed address." : "No podcast matches the filter.");
        });
    }

    static Page PodcastPage(MainActivity a, Podcast p)
    {
        var episodes = Lib.Db.LoadEpisodes(p.Id);
        var view = new ViewChoice("episodes");
        var shows = new[] { ("", "all episodes"), ("unplayed", "unplayed"), ("video", "video"), ("audio", "audio") };
        List<Episode> Shown() => episodes.Where(e => (view.Show switch { "unplayed" => !e.Played, "video" => e.IsVideo, "audio" => !e.IsVideo, _ => true }) && view.Matches(e.Title, e.Description)).ToList();
        var page = new Page(a, p.Title);
        page.Subtitle = () =>
        {
            var video = episodes.Count(e => e.IsVideo);
            return Fmt.Join(p.Author, Shown().Count == episodes.Count ? Fmt.Plural(episodes.Count, "episode") : $"{Shown().Count} of {episodes.Count} episodes",
                video == 0 ? "" : video == episodes.Count ? "a video podcast" : $"{video} in video");
        };
        return page.List(() =>
        {
            page.Actions(page.Opts(view, Opts.None, shows).ToArray());
            var rows = new List<Row>();
            if (p.Description != "") rows.Add(Row.Note(p.Description));
            rows.AddRange(Shown().Select(e => new Row
            {
                Title = e.Title, Dim = e.Played,
                Sub = Fmt.Join(e.DateText, e.DurationText, e.IsVideo ? "video" : "", e.Played ? "played" : e.PositionMs > 0 ? "resume at " + Fmt.Time(e.PositionMs) : ""),
                IsCurrent = () => P.Current?.Episode == e,
                Click = () => { P.Play(new[] { Player.From(e, p) }); if (e.IsVideo) a.ShowNowPlaying(); },
                Long = () => Ui.Menu(a, e.Title,
                    ("Play", () => P.Play(new[] { Player.From(e, p) })),
                    (e.Played ? "Mark as unplayed" : "Mark as played", () =>
                    {
                        e.Played = !e.Played;
                        e.PositionMs = 0;
                        Lib.Db.SaveEpisodeState(e);
                        page.Reload();
                    }))
            }));
            return rows;
        }, "This feed has no episodes.");
    }

    // ---------- playlists ----------

    public static Page Playlists(MainActivity a)
    {
        var page = new Page(a, "playlists") { ReloadOnRatings = true };
        page.Actions(("new playlist", () => Ui.Ask(a, "New playlist", "Playlist name", "", name =>
        {
            if (!string.IsNullOrWhiteSpace(name)) Lib.CreatePlaylist(name.Trim());
        })));
        return page.List(() =>
        {
            var rows = new List<Row> { Row.Caption("AUTOMATIC") };
            rows.AddRange(Catalog.AutoPlaylists.Select(kind => new Row
            {
                Title = kind, Sub = Fmt.Plural(Lib.AutoPlaylist(kind).Count, "song"),
                Click = () => a.Push(PlaylistPage(a, kind, () => Lib.AutoPlaylist(kind), null))
            }));
            rows.Add(Row.Caption("YOUR PLAYLISTS"));
            if (Lib.Playlists.Count == 0) rows.Add(Row.Note("Long-press songs, albums or artists and choose “add to playlist”."));
            rows.AddRange(Lib.Playlists.Select(pl => new Row
            {
                Title = pl.Name, Sub = Fmt.Plural(Lib.TracksOf(pl).Count, "song"),
                Art = Lib.TracksOf(pl).Select(t => t.ArtPath).FirstOrDefault(x => x != "") ?? "", ArtShape = 1,
                Click = () => a.Push(PlaylistPage(a, pl.Name, () => Lib.TracksOf(pl), pl)),
                Long = () => Ui.Menu(a, pl.Name,
                    ("Play", () => P.PlayTracks(Lib.TracksOf(pl))),
                    ("Shuffle", () => P.PlayShuffled(Lib.TracksOf(pl))),
                    ("Rename…", () => Ui.Ask(a, "Rename playlist", "Playlist name", pl.Name, name =>
                    {
                        if (string.IsNullOrWhiteSpace(name)) return;
                        pl.Name = name.Trim();
                        Lib.SavePlaylist(pl);
                    })),
                    (Lib.IsPinned("playlist", pl.Id.ToString()) ? "Unpin from quickplay" : "Pin to quickplay", () => Lib.TogglePin("playlist", pl.Id.ToString(), pl.Name)),
                    ("Delete", () => Ui.Confirm(a, $"Delete the playlist “{pl.Name}”? The songs stay in your collection.", "Delete", () => Lib.DeletePlaylist(pl))))
            }));
            return rows;
        });
    }

    static Page PlaylistPage(MainActivity a, string title, Func<List<Track>> tracks, Playlist? playlist)
    {
        var page = new Page(a, title) { ReloadOnRatings = playlist == null };
        var sort = new SongSort();
        var view = new ViewChoice("playlists");
        var shows = new[] { ("", "everything"), ("liked", "liked"), ("unplayed", "never played") };
        List<Track> Sorted() => sort.Apply(tracks().Where(t => (view.Show switch { "liked" => t.Rating > 0, "unplayed" => t.PlayCount == 0, _ => true }) && view.Matches(t.Title, t.Artist, t.Album)));
        if (playlist != null) page.TitleSource = () => playlist.Name;
        page.Subtitle = () => Fmt.Join(Fmt.Plural(tracks().Count, "song"), Fmt.Time(tracks().Sum(t => t.DurationMs)));
        void SetActions() => page.Actions(new (string, Action)[] { ("play", () => P.PlayTracks(Sorted())), ("shuffle", () => P.PlayShuffled(Sorted())), SortLink(a, sort, () => page.Reload()) }
            .Concat(page.Opts(view, Opts.None, shows)).ToArray());
        SetActions();
        return page.List(() =>
        {
            SetActions();
            var list = Sorted();
            return list.Select((t, i) => SongRow(a, t, Sorted, true, i, playlist)).ToList();
        }, playlist == null ? "Nothing here yet." : "This playlist is empty. Long-press songs and choose “add to playlist”.");
    }

    // ---------- now playing queue ----------

    public static Page Queue(MainActivity a)
    {
        var page = new Page(a, "now playing");
        page.Subtitle = () => Fmt.Plural(P.Queue.Count, "item");
        page.Actions(("stop and clear", () => { P.Play(Array.Empty<PlayItem>()); a.Pop(); }));
        return page.List(() => P.Queue.Select((item, i) => new Row
        {
            Title = item.Title, Sub = Fmt.Join(item.Subtitle, item.Album), Trail = item.DurationText, Art = item.ArtPath, ArtShape = 1,
            IsCurrent = () => item.IsCurrent,
            Click = () => P.PlayAt(P.Queue.IndexOf(item)),
            Long = () => Ui.Menu(a, item.Title, ("Play", () => P.PlayAt(P.Queue.IndexOf(item))), ("Remove from now playing", () => P.RemoveAt(P.Queue.IndexOf(item))))
        }).ToList(), "Nothing is playing.");
    }

    // ---------- search ----------

    public static Page Search(MainActivity a)
    {
        var query = "";
        var page = new Page(a, "search");
        var box = Ui.Input(a, "words, \"phrases\", #tags  (# lists your tags)");
        box.TextChanged += (_, _) => { query = box.Text ?? ""; page.Reload(); };
        page.Above(box);
        box.RequestFocus();
        return page.List(() =>
        {
            var rows = new List<Row>();
            if (query.Trim() == "") return rows;
            var found = Lib.Search(query);
            var (albums, songs, videos, books) = (found.Albums, found.Songs, found.Videos, found.Books);
            // tags worth tapping next: a tap puts that #tag into the search box
            if (found.Tags.Count > 0)
            {
                rows.Add(Row.Caption(query.Trim() == "#" ? $"YOUR TAGS  {found.Tags.Count}" : "TAGS"));
                rows.AddRange(found.Tags.Select(t => new Row
                {
                    Title = "#" + t.Tag, Sub = Fmt.Plural(t.Count, "item"),
                    Click = () => { box.Text = TagText.Query(t.Tag); box.SetSelection(box.Text?.Length ?? 0); }
                }));
            }
            if (albums.Count > 0) { rows.Add(Row.Caption($"ALBUMS  {albums.Count}")); rows.AddRange(albums.Select(al => AlbumRow(a, al))); }
            if (songs.Count > 0) { rows.Add(Row.Caption($"SONGS  {songs.Count}")); rows.AddRange(songs.Select((t, i) => SongRow(a, t, () => songs, true, i))); }
            if (videos.Count > 0) { rows.Add(Row.Caption($"VIDEOS  {videos.Count}")); rows.AddRange(videos.Select(v => VideoRow(a, v, () => new List<VideoItem> { v }, false))); }
            if (books.Count > 0)
            {
                rows.Add(Row.Caption($"BOOKS  {books.Count}"));
                rows.AddRange(books.Select(b => new Row
                {
                    Title = b.Title, Sub = Fmt.Join(b.AuthorOrUnknown, b.IsAudio ? "audiobook" : b.Format, TagLine(Library.BookTag, b.Id)), Art = b.ArtPath, ArtShape = 2,
                    Click = () => BookScreens.Open(a, b), Long = () => BookScreens.Menu(a, b)
                }));
            }
            if (rows.Count == 0) rows.Add(Row.Note($"Nothing matches “{query.Trim()}”."));
            return rows;
        });
    }

    // ---------- discogs ----------

    /// <summary>Looks an album up on Discogs and applies the chosen release's album info, track names and cover.</summary>
    public static Page Discogs(MainActivity a, Album album)
    {
        List<DiscogsResult>? results = null;
        DiscogsRelease? release = null;
        var note = "";
        bool info = true, titles = true, cover = true, busy = false;
        var tracks = album.Tracks.ToList();

        var page = new Page(a, "discogs");
        var artist = Ui.Input(a, "artist", album.Artist == "Unknown Artist" ? "" : album.Artist);
        var title = Ui.Input(a, "album", album.Title == "Unknown Album" ? "" : album.Title);
        page.Above(artist);
        page.Above(title);

        async Task SearchAsync()
        {
            if (busy) return;
            busy = true; release = null; results = null; note = "searching discogs…";
            page.Reload();
            try { results = await App.Discogs.SearchAsync(artist.Text ?? "", title.Text ?? ""); note = results.Count == 0 ? "No releases found. Try fewer words." : ""; }
            catch (Exception ex) { note = ex.Message; }
            busy = false;
            page.Reload();
        }

        async Task OpenAsync(DiscogsResult r)
        {
            if (busy) return;
            busy = true; note = "loading release…";
            page.Reload();
            try { release = await App.Discogs.GetReleaseAsync(r.Id); note = ""; }
            catch (Exception ex) { note = ex.Message; }
            busy = false;
            page.Reload();
        }

        async Task ApplyAsync()
        {
            if (busy || release == null) return;
            busy = true; note = "applying…";
            page.Reload();
            try
            {
                var failed = await Lib.ApplyReleaseAsync(new Album { Key = album.Key, Source = album.Source, Artist = album.Artist, Title = album.Title, Tracks = tracks },
                    release, info, titles && release.Tracks.Count == tracks.Count, cover);
                Ui.Toast(a, failed > 0 ? $"Applied. {failed} file(s) could not be rewritten; the changes are kept in the collection." : "Album info applied.");
                busy = false;
                a.Pop();
            }
            catch (Exception ex) { busy = false; note = ex.Message; page.Reload(); }
        }

        page.Actions(("search", () => _ = SearchAsync()), ("apply", () => _ = ApplyAsync()), ("back to results", () => { release = null; page.Reload(); }));
        page.List(() =>
        {
            var rows = new List<Row>();
            if (note != "") rows.Add(Row.Note(note));
            if (release is { } rel)
            {
                static string Box(bool on) => on ? "☑  " : "☐  ";
                var match = rel.Tracks.Count == tracks.Count;
                rows.Add(Row.Caption("RELEASE"));
                rows.Add(new Row { Title = rel.Title, Sub = Fmt.Join(rel.Artist, rel.Year > 0 ? rel.Year.ToString() : "", rel.Genre, rel.Style), Art = rel.CoverUrl, ArtShape = 1 });
                rows.Add(Row.Caption("APPLY"));
                rows.Add(new Row { Title = Box(info) + "album info", Sub = "title, artist, year, genre", Click = () => { info = !info; page.Reload(); } });
                rows.Add(new Row
                {
                    Title = Box(titles && match) + "track names", Dim = !match,
                    Sub = match ? "rename the songs in order" : $"not possible: the release has {rel.Tracks.Count} tracks, this album has {tracks.Count}",
                    Click = () => { if (match) { titles = !titles; page.Reload(); } }
                });
                rows.Add(new Row { Title = Box(cover) + "cover art", Sub = rel.CoverUrl == "" ? "this release has no picture" : "replace the album cover", Dim = rel.CoverUrl == "", Click = () => { cover = !cover; page.Reload(); } });
                rows.Add(Row.Caption($"TRACKS  {rel.Tracks.Count}"));
                rows.AddRange(rel.Tracks.Select((t, i) => new Row { Lead = t.Position, Title = t.Title, Trail = t.Duration, Sub = match && tracks[i].Title != t.Title ? "now: " + tracks[i].Title : "" }));
            }
            else if (results != null)
            {
                if (results.Count > 0) rows.Add(Row.Caption($"RELEASES  {results.Count}"));
                rows.AddRange(results.Select(r => new Row { Title = r.Title, Sub = r.Details, Art = r.Thumb, ArtShape = 1, Click = () => _ = OpenAsync(r) }));
            }
            return rows;
        });
        if (App.Discogs.HasToken) _ = SearchAsync();
        else { note = "Add your Discogs personal access token in settings first."; page.Reload(); }
        return page;
    }
}
