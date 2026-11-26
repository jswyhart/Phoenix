using System.Text.Json;
using System.Text.RegularExpressions;
using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>The rest of the screens: quickplay, videos, pictures, podcasts, books and their readers, playlists, search, servers.</summary>
public static partial class Api
{
    static void RegisterPages()
    {
        RegisterQuickplay();
        RegisterVideos();
        RegisterPictures();
        RegisterPodcasts();
        RegisterBooks();
        RegisterPlaylists();
        RegisterEditing();
        RegisterServers();
        RegisterVisualizer();
    }

    // ---------- quickplay ----------

    static object Quick(QuickItem q, object play) => new
    {
        title = q.Title, caption = q.Subtitle, art = q.ArtPath, play,
        pin = q.Pin == null ? null : new { kind = q.Pin.Kind, key = q.Pin.Key, title = q.Pin.Title }
    };

    static void RegisterQuickplay()
    {
        Rpc.On("quick.view", _ =>
        {
            var liked = Lib.Liked().Take(30).ToList();
            return new
            {
                empty = Lib.Tracks.Count == 0 && Lib.Videos.Count == 0,
                pins = Catalog.Pins().Select(q => Quick(q, new { type = q.Pin!.Kind, key = q.Pin.Key })),
                fresh = Lib.Albums.OrderByDescending(a => a.DateAdded).Take(12).Select(a => new { title = a.Title, caption = a.Artist, art = a.ArtPath, play = new { type = "album", key = a.Key }, pin = (object?)null }),
                history = Lib.Tracks.Where(t => t.LastPlayed > 0).OrderByDescending(t => t.LastPlayed).Select(t => t.AlbumKey).Distinct().Take(12)
                    .Select(AlbumByKey).Where(a => a != null).Select(a => new { title = a!.Title, caption = a.Artist, art = a.ArtPath, play = new { type = "album", key = a.Key }, pin = (object?)null }),
                liked = liked.Select(t => new { title = t.Title, caption = t.Artist, art = t.ArtPath, play = new { type = "liked", key = t.Id.ToString() }, pin = (object?)null })
            };
        });
        Rpc.On("quick.play", p =>
        {
            var key = p.Str("key");
            switch (p.Str("type"))
            {
                case "album": if (AlbumByKey(key) is { } a) P.PlayTracks(a.Tracks); break;
                case "artist": P.PlayTracks(Catalog.AlbumsOf(key).SelectMany(x => x.Tracks)); break;
                case "playlist": if (Lib.Playlists.FirstOrDefault(x => x.Id.ToString() == key) is { } pl) P.PlayTracks(Lib.TracksOf(pl)); break;
                // a liked song plays and carries on down the liked list
                case "liked": if (long.TryParse(key, out var id) && Lib.ById.TryGetValue(id, out var t)) P.PlayTracks(Lib.Liked(), t); break;
                case "likedAll": P.PlayTracks(Lib.Liked()); break;
                case "likedShuffle": P.PlayShuffled(Lib.Liked()); break;
                case "track": if (long.TryParse(key, out var tid) && Lib.ById.TryGetValue(tid, out var one)) P.PlayTracks(new[] { one }); break;
                case "radio": if (S.TabOn("radio") && Lib.Stations.FirstOrDefault(st => st.Url == key) is { } station) P.PlayStation(station); break;
            }
        });
    }

    // ---------- videos ----------

    static VideoItem? VideoById(long id) => Lib.Videos.FirstOrDefault(v => v.Id == id);
    static List<VideoItem> Videos(JsonElement p)
    {
        var byId = Lib.Videos.ToDictionary(v => v.Id);
        return p.Longs("ids").Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    /// <summary>Videos play through VLC, which opens the server's original file directly; the server's own conversion is the fallback.</summary>
    static PlayItem VideoPlayItem(VideoItem v)
    {
        var item = Player.From(v);
        if (v.Source == "jellyfin") item.Resolve = () => Task.FromResult(App.Jellyfin.DirectVideoUrl(v.Location));
        return item;
    }

    static object VideoRow(VideoItem v, string title, string caption) => new
    {
        id = v.Id, title, caption, art = v.ArtPath, rowTitle = v.RowTitle, code = v.EpisodeCode, dur = v.DurationMs, local = v.Source == "local"
    };

    static void RegisterVideos()
    {
        Rpc.On("videos.view", p =>
        {
            var tabs = Catalog.VideoTabs();
            var tab = p.Str("tab");
            var view = ViewOf(p, "videos");
            if (!tabs.Contains(tab)) tab = tabs[0];
            var group = p.StrOrNull("group");
            int? season = p.Has("season") ? p.Int("season") : null;
            var grouped = tab is "shows" or "collections" or "folders";

            List<VideoGroup>? groups = null;
            if (grouped)
            {
                groups = Catalog.VideoGroups(tab);
                if (tab != "shows") groups = groups.Select(g => g with { Art = "" }).ToList();   // posters only make sense for series
                group = (groups.FirstOrDefault(g => string.Equals(g.Key, group, StringComparison.OrdinalIgnoreCase)) ?? groups.FirstOrDefault())?.Key;
            }
            else group = null;

            List<(string name, int? season)>? seasons = null;
            if (tab == "shows" && group != null)
            {
                seasons = Catalog.Seasons(group).Select(s => (Catalog.SeasonName(s), (int?)s)).ToList();
                if (seasons.Count > 1) seasons.Insert(0, ("all seasons", null));
                var chosen = seasons.Any(s => s.season == season) ? season : seasons.FirstOrDefault().season;
                season = chosen;
            }
            else season = null;

            string cap, layout;
            IEnumerable<object> items;
            switch (tab)
            {
                case "shows":
                case "folders":
                    var rows = group == null ? new List<VideoItem>() : ArrangeVideos(view, Catalog.GroupMembers(tab, group).Where(v => tab != "shows" || season == null || v.Season == season), sort: tab != "shows");
                    cap = (tab == "shows" ? "EPISODES" : "VIDEOS") + $"  {rows.Count}";
                    layout = "rows";
                    items = rows.Select(v => VideoRow(v, v.DisplayTitle, v.DurationText));
                    break;
                case "movies":
                case "collections":
                    var movies = ArrangeVideos(view, tab == "movies" ? Catalog.Movies() : group == null ? new List<VideoItem>() : Catalog.GroupMembers("collections", group));
                    cap = $"MOVIES  {movies.Count}";
                    layout = "poster";
                    items = movies.Select(v => VideoRow(v, v.Title, Fmt.Join(v.Year > 0 ? v.Year.ToString() : "", v.DurationText)));
                    break;
                default:
                    var all = ArrangeVideos(view, Catalog.AllVideos());
                    cap = $"VIDEOS  {all.Count}";
                    layout = "wide";
                    items = all.Select(v => VideoRow(v, v.DisplayTitle, v.DurationText));
                    break;
            }
            return new
            {
                tabs, tab, counts = Catalog.VideoCounts(), empty = Lib.Videos.Count == 0,
                groups = groups?.Select(g => new { name = g.Name, art = g.Art, count = g.Count, key = g.Key }),
                groupCap = (tab == "shows" ? "SERIES" : tab == "collections" ? "COLLECTIONS" : "FOLDERS") + $"  {groups?.Count ?? 0}",
                group,
                seasons = seasons?.Select(s => new { name = s.name, season = s.season }),
                season, cap, layout, items, view = view.Shape
            };
        });
        Rpc.On("play.videos", p =>
        {
            var list = Videos(p);
            var start = p.LongOrNull("start");
            P.Play(list.Select(VideoPlayItem), start == null ? 0 : Math.Max(0, list.FindIndex(v => v.Id == start)));
        });
        Rpc.On("queue.videos", p => P.Enqueue(Videos(p).Select(VideoPlayItem)));
        Rpc.On("video.path", p => VideoById(p.Long("id")) is { Source: "local" } v ? v.SourceId : null);
        Rpc.On("video.groupIds", p => Catalog.GroupMembers(p.Str("tab"), p.Str("key")).Select(v => v.Id));
        Rpc.On("video.renameGroup", p => Catalog.RenameGroup(p.Str("tab"), p.Str("key"), p.Str("name")));
    }

    // ---------- pictures ----------

    static List<PicFolder> _picFolders = new();

    static void RegisterPictures()
    {
        Rpc.On("pictures.folders", async _ =>
        {
            var roots = S.PictureFolders.ToList();
            var folders = await Task.Run(() => Catalog.PictureFolders(roots));
            // Photo libraries on connected servers sit alongside the local folders.
            foreach (var fetch in new Func<Task<List<RemotePhoto>>>[] { App.Jellyfin.FetchPhotosAsync, App.Plex.FetchPhotosAsync })
                try
                {
                    foreach (var g in (await fetch()).GroupBy(x => x.Folder).OrderBy(g => g.Key, NoCase))
                        folders.Add(new PicFolder(g.Key, "", g.Count(), g.Select(x => new PictureItem(x.Name, x.FullUrl, x.ThumbUrl)).ToList()));
                }
                catch { /* server offline: show what we have */ }
            _picFolders = folders;
            return folders.Select(f => new { name = f.Name, count = f.Count });
        });
        Rpc.On("pictures.items", async p =>
        {
            var i = p.Int("index");
            if (i < 0 || i >= _picFolders.Count) return Array.Empty<object>();
            var f = _picFolders[i];
            var found = await Task.Run(() => Catalog.PicturesIn(f));
            var v = ViewOf(p, "pictures");
            static long Date(PictureItem x) { try { return File.Exists(x.File) ? File.GetLastWriteTimeUtc(x.File).Ticks : 0; } catch { return 0; } }
            var pics = v.Order(found.Where(x => v.Matches(x.Name)), key => key == "date" ? list => list.OrderByDescending(Date) : null);
            return pics.Select(x => (object)new { name = x.Name, file = x.File, art = x.ArtPath }).ToArray();
        });
    }

    // ---------- podcasts ----------

    static readonly Dictionary<long, List<Episode>> EpisodeLists = new();

    static List<Episode> EpisodesOf(long podcastId, bool fresh = false)
    {
        if (fresh || !EpisodeLists.TryGetValue(podcastId, out var list)) EpisodeLists[podcastId] = list = Lib.Db.LoadEpisodes(podcastId);
        return list;
    }

    static void RegisterPodcasts()
    {
        Rpc.On("podcasts.list", p =>
        {
            var v = ViewOf(p, "podcasts");
            var unplayed = Lib.Podcasts.ToDictionary(x => x.Id, x => EpisodesOf(x.Id).Count(e => !e.Played));
            return v.Order(Lib.Podcasts, key => key switch
            {
                "new" => list => list.OrderByDescending(x => unplayed[x.Id]).ThenBy(x => x.Title, NoCase),
                "author" => list => list.OrderBy(x => x.Author == "" ? "\uFFFF" : x.Author, NoCase).ThenBy(x => x.Title, NoCase),
                _ => null
            }).Select(x => new { id = x.Id, title = x.Title, author = x.Author, description = x.Description, art = x.ArtUrl, unplayed = unplayed[x.Id] });
        });
        Rpc.On("podcasts.episodes", p =>
        {
            var v = ViewOf(p, "podcasts");
            var all = EpisodesOf(p.Long("id"), true);
            var shown = all.Where(e => (v.Show switch { "unplayed" => !e.Played, "downloaded" => e.File != "", "video" => e.IsVideo, "audio" => !e.IsVideo, _ => true }) && v.Matches(e.Title, e.Description)).ToList();
            var video = all.Count(e => e.IsVideo);
            return new
            {
                total = all.Count, video = video == 0 ? "" : video == all.Count ? "a video podcast" : $"{video} in video",
                episodes = shown.Select(e => new { id = e.Id, title = e.Title, date = e.DateText, dur = e.DurationMs, played = e.Played, description = e.Description, isVideo = e.IsVideo, downloaded = e.File != "", manual = e.Manual })
            };
        });
        Rpc.On("podcasts.add", async p =>
        {
            App.SetStatus("fetching podcast…");
            try
            {
                var (podcast, episodes) = await PodcastService.FetchAsync(p.Str("url").Trim());
                Lib.Db.SavePodcast(podcast, episodes);
                Lib.Podcasts = Lib.Db.LoadPodcasts();
                return (object?)podcast.Id;
            }
            finally { App.SetStatus(""); }
        });
        Rpc.On("podcasts.refresh", async _ =>
        {
            // new episodes, then the downloads the settings ask for
            var (_, failed) = await PodcastSync.RefreshAllAsync(App.SetStatus);
            EpisodeLists.Clear();
            return (object?)failed;
        });
        Rpc.On("podcasts.remove", p =>
        {
            Lib.Db.DeletePodcast(p.Long("id"));
            Lib.Podcasts = Lib.Db.LoadPodcasts();
            EpisodeLists.Remove(p.Long("id"));
        });
        Rpc.On("podcasts.play", p =>
        {
            var pod = Lib.Podcasts.FirstOrDefault(x => x.Id == p.Long("podcast"));
            if (pod == null) return;
            var list = EpisodesOf(pod.Id);
            var ep = p.Has("episode") ? list.FirstOrDefault(e => e.Id == p.Long("episode")) : list.FirstOrDefault();
            if (ep != null) P.Play(new[] { Player.From(ep, pod) });
        });
        Rpc.On("podcasts.togglePlayed", p =>
        {
            if (EpisodesOf(p.Long("podcast")).FirstOrDefault(e => e.Id == p.Long("episode")) is not { } ep) return;
            ep.Played = !ep.Played;
            ep.PositionMs = 0;
            Lib.Db.SaveEpisodeState(ep);
            ep.Raise();
        });
    }

    // ---------- books ----------

    static Book? BookById(long id) => Lib.Books.FirstOrDefault(b => b.Id == id);
    static readonly Dictionary<long, (Epub epub, string dir)> OpenEpubs = new();

    static async Task<string> BookFile(Book b, string what)
    {
        var file = b.Source == "jellyfin" ? await App.Jellyfin.DownloadBookAsync(b) : b.SourceId;
        if (!File.Exists(file)) throw new FileNotFoundException($"The {what} file is no longer where it was.");
        return file;
    }

    static Bookmark? Mark(JsonElement p) => BookById(p.Long("book")) is { } b ? Lib.BookmarksOf(b).FirstOrDefault(m => m.Id == p.Long("mark")) : null;

    /// <summary>The readable text of a chapter: paragraphs separated by blank lines, markup removed.</summary>
    public static string HtmlText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|h[1-6]|li|blockquote|tr)\s*>|<br\s*/?>", "\n\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t ]+", " ");
        s = Regex.Replace(s, @" ?\n ?", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    static void RegisterBooks()
    {
        Rpc.On("books.view", p =>
        {
            var tab = p.Str("tab", "all");
            var author = p.StrOrNull("author");
            var v = ViewOf(p, "books");
            IEnumerable<Book> inTab = tab switch
            {
                "audio" => Lib.Books.Where(b => b.IsAudio),
                "ebook" => Lib.Books.Where(b => !b.IsAudio),
                "progress" => Lib.Books.Where(b => b.Progress > 0 && !b.Finished),
                _ => Lib.Books
            };
            var authors = inTab.Select(b => b.AuthorOrUnknown).Distinct(NoCase).OrderBy(a => a, NoCase).ToList();
            if (author != null && !authors.Contains(author, NoCase)) author = null;
            var books = inTab.Where(b => author == null || string.Equals(b.AuthorOrUnknown, author, StringComparison.OrdinalIgnoreCase));
            books = tab == "progress"
                ? books.OrderByDescending(b => b.LastOpened)
                : books.OrderBy(b => b.AuthorOrUnknown, NoCase).ThenBy(b => b.Series, NoCase).ThenBy(b => b.Title, NoCase);
            books = v.Order(books.Where(b => KeepBook(v, b)), key => key switch
            {
                "title" => l => l.OrderBy(b => b.Title, NoCase),
                "added" => l => l.OrderByDescending(b => b.DateAdded),
                "opened" => l => l.OrderByDescending(b => b.LastOpened),
                "progress" => l => l.OrderByDescending(b => b.Finished ? 1 : b.Progress).ThenBy(b => b.Title, NoCase),
                _ => null
            });
            return new
            {
                empty = Lib.Books.Count == 0,
                counts = Fmt.Join(Fmt.Plural(Lib.Books.Count(b => b.IsAudio), "audiobook"), Fmt.Plural(Lib.Books.Count(b => !b.IsAudio), "ebook")),
                authors, author, view = v.Shape,
                books = books.Select(b => new
                {
                    id = b.Id, title = b.Title, art = b.ArtPath,
                    caption = Fmt.Join(b.AuthorOrUnknown, b.IsAudio ? Fmt.Time(b.DurationMs) : b.Format, b.ProgressText)
                })
            };
        });
        Rpc.On("book.info", p => BookById(p.Long("id")) is { } b ? new
        {
            id = b.Id, title = b.Title, author = b.Author, series = b.Series, isAudio = b.IsAudio, format = b.Format, local = b.Source == "local",
            started = b.Progress > 0, finished = b.Finished, progressText = b.ProgressText, marks = Lib.BookmarkCount(b),
            playingNow = b.IsAudio && P.Current?.Book == b, part = b.Part
        } : null);
        Rpc.On("book.open", async p =>
        {
            if (BookById(p.Long("id")) is not { } b) throw new InvalidOperationException("That book is no longer in the collection.");
            var restart = p.Bool("restart");
            if (b.IsAudio) { P.PlayBook(b, restart); return new { kind = "audio", file = "" }; }
            if (restart) Lib.SetBookFinished(b, false);
            if (b.Format != "pdf") return new { kind = b.Format == "comic" ? "comic" : "epub", file = "" };
            // PDFs go to the system's own viewer
            var file = await BookFile(b, "book");
            b.LastOpened = Fmt.Now;
            if (b.Progress <= 0) b.Progress = 0.01;   // a PDF can't report a page, so just record that it was opened
            Lib.Db.SaveBookState(b);
            b.Raise();
            return new { kind = "pdf", file };
        });
        Rpc.On("book.finished", p => { if (BookById(p.Long("id")) is { } b) Lib.SetBookFinished(b, p.Bool("value")); });
        Rpc.On("book.save", p =>
        {
            if (BookById(p.Long("id")) is not { } b) return;
            if (p.Str("title").Trim() != "") b.Title = p.Str("title").Trim();
            b.Author = p.Str("author").Trim();
            b.Series = p.Str("series").Trim();
            Lib.SaveBookInfo(b);
        });
        Rpc.On("book.path", p => BookById(p.Long("id")) is { Source: "local" } b ? b.SourceId : null);
        Rpc.On("book.bookmarkHere", _ => P.Current is { Book: { } book } cur ? Lib.AddBookmark(book, cur.BookPart, P.PositionMs).Label : null);

        // ---- comics ----
        Rpc.On("comic.open", async p =>
        {
            if (BookById(p.Long("id")) is not { } b) throw new InvalidOperationException("That comic is no longer in the collection.");
            var file = await BookFile(b, "comic");
            var dir = Path.Combine(AppPaths.DataDir, "books", b.Id.ToString());
            var pages = await Task.Run(() => Comic.Pages(file, dir));
            if (pages.Count == 0) throw new InvalidDataException("The comic has no pages.");
            b.LastOpened = Fmt.Now;
            return new { title = b.Title, author = b.Author, pages, page = b.Finished ? 0 : Math.Clamp(b.Part, 0, pages.Count - 1), marks = Lib.BookmarkCount(b) };
        });
        Rpc.On("comic.show", p =>
        {
            if (BookById(p.Long("id")) is not { } b) return;
            int page = p.Int("page"), count = Math.Max(1, p.Int("count"));
            b.Part = page;
            b.Progress = (page + 1.0) / count;
            b.Finished = page == count - 1;
            Lib.Db.SaveBookState(b);
            b.Raise();
        });

        // ---- ebooks ----
        Rpc.On("epub.open", async p =>
        {
            if (BookById(p.Long("id")) is not { } b) throw new InvalidOperationException("That book is no longer in the collection.");
            var file = await BookFile(b, "book");
            var epub = await Task.Run(() => Epub.Open(file));
            if (epub.Chapters.Count == 0) throw new InvalidDataException("The book has no readable chapters.");
            var dir = Path.Combine(AppPaths.DataDir, "books", b.Id.ToString());
            var stamp = Path.Combine(dir, ".extracted");
            var signature = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != signature)
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                await Task.Run(() => Epub.Extract(file, dir));
                File.WriteAllText(stamp, signature);
            }
            OpenEpubs[b.Id] = (epub, dir);
            b.LastOpened = Fmt.Now;
            return new
            {
                title = b.Title, author = b.Author, chapters = epub.Chapters.Select(c => c.Title),
                chapter = b.Finished ? 0 : Math.Clamp(b.Part, 0, epub.Chapters.Count - 1), fraction = b.Finished ? 0 : b.Fraction,
                fontSize = S.ReaderFontSize, marks = Lib.BookmarkCount(b)
            };
        });
        Rpc.On("epub.chapter", p =>
        {
            if (!OpenEpubs.TryGetValue(p.Long("id"), out var open)) throw new InvalidOperationException("The book is not open.");
            var index = Math.Clamp(p.Int("index"), 0, open.epub.Chapters.Count - 1);
            string text;
            try { text = HtmlText(File.ReadAllText(Path.Combine(open.dir, open.epub.Chapters[index].Href.Replace('/', Path.DirectorySeparatorChar)))); }
            catch (Exception ex) { text = "This chapter can't be read: " + ex.Message; }
            return new { index, text, title = open.epub.Chapters[index].Title };
        });
        Rpc.On("epub.save", p =>
        {
            if (BookById(p.Long("id")) is not { } b || !OpenEpubs.TryGetValue(b.Id, out var open)) return "";
            var chapters = open.epub.Chapters.Count;
            var chapter = Math.Clamp(p.Int("chapter"), 0, chapters - 1);
            var fraction = Math.Clamp(p.Num("fraction"), 0, 1);
            b.Part = chapter;
            b.Fraction = fraction;
            b.Progress = Math.Clamp((chapter + fraction) / chapters, 0, 1);
            b.Finished = chapter == chapters - 1 && fraction > 0.97;
            Lib.Db.SaveBookState(b);
            b.Raise();
            return $"chapter {chapter + 1} of {chapters}   ·   {Math.Max(1, (int)(b.Progress * 100))}%";
        });
        Rpc.On("epub.close", p => { OpenEpubs.Remove(p.Long("id")); });

        // ---- bookmarks ----
        Rpc.On("bookmarks.list", p => BookById(p.Long("book")) is { } b
            ? Lib.BookmarksOf(b).Select(m => (object)new { id = m.Id, label = m.Label, created = m.CreatedText, part = m.Part, position = m.Position }).ToArray()
            : Array.Empty<object>());
        Rpc.On("bookmarks.add", p =>
        {
            if (BookById(p.Long("book")) is not { } b) return null;
            var m = Lib.AddBookmark(b, p.Int("part"), p.Num("position"), p.Str("label"));
            return new { id = m.Id, label = m.Label, count = Lib.BookmarkCount(b) };
        });
        Rpc.On("bookmarks.rename", p => { if (Mark(p) is { } m) Lib.RenameBookmark(m, p.Str("name")); });
        Rpc.On("bookmarks.delete", p => { if (Mark(p) is { } m) Lib.DeleteBookmark(m); });
        // takes the book to a bookmark: plays the audiobook from there, or tells the window where to open the reader
        Rpc.On("bookmarks.go", p =>
        {
            if (BookById(p.Long("book")) is not { } b || Mark(p) is not { } m || !Lib.MoveToBookmark(m)) return null;
            if (b.IsAudio) { P.PlayBookAt(b, m.Part, (long)m.Position); return new { kind = "audio", part = m.Part, position = m.Position }; }
            return new { kind = b.Format == "comic" ? "comic" : b.Format == "epub" ? "epub" : "other", part = m.Part, position = m.Position };
        });
    }

    // ---------- playlists and search ----------

    static Playlist? PlaylistById(long id) => Lib.Playlists.FirstOrDefault(x => x.Id == id);

    static void RegisterPlaylists()
    {
        Rpc.On("playlists.list", _ => Catalog.AutoPlaylists.Select(k => new { name = k, id = (long?)null, auto = (string?)k, smart = (string?)null, rules = "", pinned = false })
            .Concat(Lib.Playlists.Select(x => new { name = x.Name, id = (long?)x.Id, auto = (string?)null, smart = (string?)null, rules = "", pinned = Lib.IsPinned("playlist", x.Id.ToString()) }))
            // auto playlists of your own, made by rules
            .Concat(S.SmartPlaylists.Select(x => new { name = x.Name, id = (long?)null, auto = (string?)null, smart = (string?)x.Id, rules = Library.Describe(x), pinned = false })));
        Rpc.On("playlists.songs", p =>
        {
            var songs = p.LongOrNull("id") is { } id ? (PlaylistById(id) is { } pl ? Lib.TracksOf(pl) : new List<Track>())
                : p.StrOrNull("smart") is { } smart ? (S.SmartPlaylists.FirstOrDefault(x => x.Id == smart) is { } sp ? Lib.Evaluate(sp) : new List<Track>())
                : Lib.AutoPlaylist(p.Str("auto"));
            var v = ViewOf(p, "playlists");
            var count = songs.Count;
            songs = songs.Where(t => (v.Show switch { "liked" => t.Rating > 0, "unplayed" => t.PlayCount == 0, _ => true }) && v.Matches(t.Title, t.Artist, t.Album)).ToList();
            var sort = new SongSort();
            if (p.Str("sortKey") != "") sort.Set(p.Str("sortKey"), p.Bool("sortDesc"));
            songs = sort.Apply(songs);
            return new { songs = songs.Select(Song), total = Fmt.Time(songs.Sum(t => t.DurationMs)), all = count, view = v.Shape };
        });
        Rpc.On("playlist.create", p =>
        {
            var pl = Lib.CreatePlaylist(p.Str("name").Trim());
            var tracks = Tracks(p);
            if (tracks.Count > 0) Lib.AddToPlaylist(pl, tracks);
            return (object?)pl.Id;
        });
        Rpc.On("playlist.add", p => { if (PlaylistById(p.Long("id")) is { } pl) Lib.AddToPlaylist(pl, Tracks(p)); });
        Rpc.On("playlist.rename", p => { if (PlaylistById(p.Long("id")) is { } pl && p.Str("name").Trim() != "") { pl.Name = p.Str("name").Trim(); Lib.SavePlaylist(pl); } });
        Rpc.On("playlist.delete", p => { if (PlaylistById(p.Long("id")) is { } pl) Lib.DeletePlaylist(pl); });
        Rpc.On("playlist.setOrder", p => { if (PlaylistById(p.Long("id")) is { } pl) { pl.TrackIds = p.Longs("ids"); Lib.SavePlaylist(pl); } });

        Rpc.On("search", p =>
        {
            var (albums, songs, _) = Catalog.Search(p.Str("query"));
            return new { albums = albums.Select(Tile), songs = songs.Select(Song) };
        });
        Rpc.On("album.songs", p => AlbumByKey(p.StrOrNull("key")) is { } a ? a.Tracks.Select(Song) : Enumerable.Empty<object>());
    }

    // ---------- editing: song info, video info, Discogs ----------

    static readonly Dictionary<int, DiscogsRelease> Releases = new();

    static void RegisterEditing()
    {
        Rpc.On("tracks.info", p =>
        {
            var tracks = Tracks(p);
            if (tracks.Count == 0) return null;
            var f = tracks[0];
            string Shared(Func<Track, string> get) => tracks.All(t => get(t) == get(f)) ? get(f) : "";
            return new
            {
                count = tracks.Count, source = f.Source, title = f.Title, trackNo = f.TrackNo > 0 ? f.TrackNo.ToString() : "",
                artist = Shared(t => t.Artist), album = Shared(t => t.Album), albumArtist = Shared(t => t.AlbumArtist), genre = Shared(t => t.Genre),
                year = Shared(t => t.Year > 0 ? t.Year.ToString() : "")
            };
        });
        // one song, or the shared fields of several songs / a whole album; blank shared fields are left unchanged
        Rpc.On("tracks.save", p =>
        {
            var tracks = Tracks(p);
            var single = tracks.Count == 1 && !p.Bool("album");
            foreach (var t in tracks)
            {
                void Apply(string field, Action<string> set) { var v = p.Str(field).Trim(); if (v != "" || single) set(v); }
                if (single)
                {
                    if (p.Str("title").Trim() != "") t.Title = p.Str("title").Trim();
                    t.TrackNo = int.TryParse(p.Str("trackNo"), out var n) ? n : 0;
                }
                Apply("artist", v => t.Artist = v == "" ? "Unknown Artist" : v);
                Apply("albumName", v => t.Album = v == "" ? "Unknown Album" : v);
                Apply("albumArtist", v => t.AlbumArtist = v == "" ? t.Artist : v);
                Apply("genre", v => t.Genre = v);
                Apply("year", v => t.Year = int.TryParse(v, out var y) ? y : 0);
            }
            return (object?)Lib.SaveTracks(tracks);
        });

        Rpc.On("videos.info", p =>
        {
            var videos = Videos(p);
            if (videos.Count == 0) return null;
            var f = videos[0];
            string Shared(Func<VideoItem, string> get) => videos.All(v => get(v) == get(f)) ? get(f) : "";
            static string Num(int n) => n > 0 ? n.ToString() : "";
            return new
            {
                count = videos.Count, source = f.Source, title = f.Title, kind = Shared(v => v.Kind == "" ? "video" : v.Kind),
                series = Shared(v => v.Series), collection = Shared(v => v.Collection), season = Shared(v => Num(v.Season)),
                episode = Num(f.Episode), year = Shared(v => Num(v.Year))
            };
        });
        Rpc.On("videos.save", p =>
        {
            var videos = Videos(p);
            var single = videos.Count == 1;
            foreach (var v in videos)
            {
                if (p.Str("kind") is "movie" or "episode" or "video") v.Kind = p.Str("kind");
                if (single)
                {
                    if (p.Str("title").Trim() != "") v.Title = p.Str("title").Trim();
                    v.Episode = int.TryParse(p.Str("episode"), out var e) ? e : 0;
                }
                void Apply(string field, Action<string> set) { var t = p.Str(field).Trim(); if (single || t != "") set(t); }
                Apply("series", s => v.Series = s);
                Apply("season", s => v.Season = int.TryParse(s, out var n) ? n : 0);
                Apply("year", s => v.Year = int.TryParse(s, out var n) ? n : 0);
                Apply("collection", s => v.Collection = s);
            }
            Lib.SaveVideos(videos);
        });

        Rpc.On("discogs.saveToken", p =>
        {
            S.DiscogsToken = p.Str("token").Trim();
            Save();
            return S.DiscogsToken == "" ? "token removed" : "token saved — right-click any album and choose “Find album info on Discogs”";
        });
        Rpc.On("discogs.album", p => AlbumByKey(p.StrOrNull("key")) is { } a ? new
        {
            key = a.Key, artist = a.Artist == "Unknown Artist" ? "" : a.Artist, title = a.Title == "Unknown Album" ? "" : a.Title, source = a.Source,
            tracks = a.Tracks.Select(t => t.Title), hasToken = App.Discogs.HasToken
        } : null);
        Rpc.On("discogs.search", async p => (await App.Discogs.SearchAsync(p.Str("artist").Trim(), p.Str("album").Trim()))
            .Select(r => (object)new { id = r.Id, title = r.Title, details = r.Details, thumb = r.Thumb }).ToArray());
        Rpc.On("discogs.release", async p =>
        {
            var rel = await App.Discogs.GetReleaseAsync(p.Int("id"));
            Releases[rel.Id] = rel;
            return new
            {
                id = rel.Id, title = rel.Title, artist = rel.Artist, cover = rel.CoverUrl,
                meta = Fmt.Join(rel.Year > 0 ? rel.Year.ToString() : "", rel.Genre, rel.Style), tracks = rel.Tracks.Select(t => t.Title)
            };
        });
        Rpc.On("discogs.apply", async p =>
        {
            if (AlbumByKey(p.StrOrNull("key")) is not { } album || !Releases.TryGetValue(p.Int("id"), out var rel)) throw new InvalidOperationException("Pick a release first.");
            return (object?)await Lib.ApplyReleaseAsync(album, rel, p.Bool("albumInfo"), p.Bool("titles"), p.Bool("cover"));
        });
    }

    // ---------- servers ----------

    static List<PlexServer> _plexServers = new();

    static object ServerStatus()
    {
        int jf = Lib.Tracks.Count(t => t.Source == "jellyfin"), jfv = Lib.Videos.Count(v => v.Source == "jellyfin");
        int px = Lib.Tracks.Count(t => t.Source == "plex"), pxv = Lib.Videos.Count(v => v.Source == "plex");
        return new
        {
            jellyfin = App.Jellyfin.IsConnected ? $"connected as {S.JellyfinUser}  ·  {jf} songs, {jfv} videos" : "not connected",
            plex = App.Plex.IsConnected ? $"connected  ·  {px} songs, {pxv} videos" : "not connected",
            collection = $"{Lib.Tracks.Count} songs and {Lib.Videos.Count} videos in your collection",
            settings = SettingsState()
        };
    }

    static void RegisterServers()
    {
        void Say(string which, string text) => Rpc.Emit("server.status", new { which, text });

        async Task SyncJellyfin()
        {
            var (tracks, videos) = await App.Jellyfin.FetchAsync(s => Say("jellyfin", s));
            Say("jellyfin", "saving…");
            await Lib.ReplaceSourceAsync("jellyfin", tracks, videos);
            await Lib.ReplaceBooksAsync("jellyfin", await App.Jellyfin.FetchBooksAsync(s => Say("jellyfin", s)));
        }

        Rpc.On("servers.status", _ => ServerStatus());
        Rpc.On("jellyfin.connect", async p =>
        {
            Say("jellyfin", "signing in…");
            await App.Jellyfin.LoginAsync(p.Str("url"), p.Str("user").Trim(), p.Str("password"));
            await SyncJellyfin();
            return ServerStatus();
        });
        Rpc.On("jellyfin.sync", async _ => { await SyncJellyfin(); return ServerStatus(); });
        Rpc.On("jellyfin.disconnect", async _ =>
        {
            App.Jellyfin.Disconnect();
            await Lib.RemoveSourceAsync("jellyfin");
            return ServerStatus();
        });

        // Plex sign-in: the browser approves a pin; the account's servers (and their tokens) stay here in the helper
        Rpc.On("plex.pin", async _ =>
        {
            var (id, _, url) = await App.Plex.CreatePinAsync();
            return new { id, url };
        });
        Rpc.On("plex.check", async p =>
        {
            var token = await App.Plex.CheckPinAsync(p.Long("id"));
            if (token == "") return null;
            _plexServers = await App.Plex.GetServersAsync(token);
            return new { servers = _plexServers.Select(s => new { label = s.ToString(), uri = s.Uri }) };
        });
        Rpc.On("plex.connect", async p =>
        {
            var token = p.Str("token").Trim();
            if (token == "" && p.Has("server") && p.Int("server") >= 0 && p.Int("server") < _plexServers.Count) token = _plexServers[p.Int("server")].Token;
            if (token == "") token = S.PlexToken;
            if (p.Str("url").Trim() == "" || token == "") throw new InvalidOperationException("enter the server address and token, or sign in with Plex");
            S.PlexUrl = JellyfinClient.NormalizeUrl(p.Str("url"));
            S.PlexToken = token;
            Say("plex", "connecting…");
            await App.Plex.TestAsync();
            Save();
            var (tracks, videos) = await App.Plex.FetchAsync(s => Say("plex", s));
            Say("plex", "saving…");
            await Lib.ReplaceSourceAsync("plex", tracks, videos);
            return ServerStatus();
        });
        Rpc.On("plex.disconnect", async _ =>
        {
            S.PlexToken = "";
            Save();
            await Lib.RemoveSourceAsync("plex");
            return ServerStatus();
        });
    }

    // ---------- visualizer feed ----------

    static Timer? _vizTimer;

    static void RegisterVisualizer()
    {
        // while a visualizer is on screen: thirty times a second, the spectrum bars, their peak caps and the raw wave
        Rpc.On("viz.enable", p =>
        {
            var on = p.Bool("on");
            Program.Engine.AnalysisEnabled = on;
            _vizTimer?.Dispose();
            _vizTimer = null;
            if (!on) return;
            _vizTimer = MainLoop.Every(33, () =>
            {
                if (_vizTimer == null) return;
                var sp = Program.Engine.Spectrum;
                sp.Sensitivity = S.VisualizerSensitivity;
                sp.Update();
                var scope = sp.Scope;
                var wave = new byte[scope.Length];
                for (var i = 0; i < wave.Length; i++) wave[i] = (byte)(sbyte)Math.Clamp((int)Math.Round(scope[i] * 127), -127, 127);
                Rpc.Emit("viz", new { bars = sp.Bars.Select(b => Math.Round(b, 3)), peaks = sp.Peaks.Select(b => Math.Round(b, 3)), scope = Convert.ToBase64String(wave) });
            });
        });
    }
}
