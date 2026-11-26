using System.Text.Json;
using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// What the Windows app has beyond the basics, for the Mac and Linux window: collection tabs that can be switched off,
/// internet radio, podcast directories, sorting and filtering, the sleep timer and playback speed, listening stats and
/// scrobbling, backup and restore.
/// </summary>
public static partial class Api
{
    static void RegisterExtras()
    {
        RegisterTabs();
        RegisterRadio();
        RegisterPodcastDirectory();
        RegisterPlayerExtras();
        RegisterListening();
        RegisterBackup();
        RegisterWeb();
        RegisterCare();
        RegisterMetadata();
        Updates.Register();
    }

    // ---------- sorting and filtering ----------

    /// <summary>
    /// How a page is sorted and filtered right now: what the window sent (remembered for next time), or what was chosen
    /// last time. The typed filter is never remembered.
    /// </summary>
    public sealed class View
    {
        public string Sort = "", Show = "", Text = "";
        public bool Desc;
        public bool Active => Sort != "" || Desc || Show != "" || Text != "";

        public bool Matches(params string?[] fields)
        {
            if (Text.Trim() == "") return true;
            foreach (var word in Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!fields.Any(f => f != null && f.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        public List<T> Order<T>(IEnumerable<T> items, Func<string, Func<IEnumerable<T>, IEnumerable<T>>?> keys)
        {
            var list = (keys(Sort)?.Invoke(items) ?? items).ToList();
            if (Desc) list.Reverse();
            return list;
        }

        public object Shape => new { sort = Sort, desc = Desc, show = Show, text = Text };
    }

    static View ViewOf(JsonElement p, string id)
    {
        var v = new View();
        var o = p.Obj("view");
        if (o.ValueKind == JsonValueKind.Object)
        {
            v.Sort = o.Str("sort"); v.Desc = o.Bool("desc"); v.Show = o.Str("show"); v.Text = o.Str("text").Trim();
            var line = $"{v.Sort}|{(v.Desc ? "1" : "0")}|{v.Show}";
            if (S.Views.GetValueOrDefault(id) != line) { S.Views[id] = line; Save(); }
        }
        else if (S.Views.TryGetValue(id, out var saved) && saved.Split('|') is { Length: 3 } parts)
        {
            v.Sort = parts[0]; v.Desc = parts[1] == "1"; v.Show = parts[2];
        }
        return v;
    }

    /// <summary>The choices a page's sorting and filtering bar offers, as the Windows app has them.</summary>
    static readonly Dictionary<string, object> ViewOptions = new()
    {
        ["music"] = new
        {
            sorts = Pairs(("", "artist"), ("title", "album title"), ("year", "year"), ("added", "date added"), ("plays", "most played"), ("recent", "last played")),
            shows = Pairs(("", "everything"), ("liked", "liked"), ("unplayed", "never played"), ("local", "on this computer"), ("server", "on servers"))
        },
        ["videos"] = new { sorts = Pairs(("", "title"), ("year", "year"), ("added", "date added"), ("length", "length")), shows = Pairs(("", "everything"), ("local", "on this computer"), ("server", "on servers")) },
        ["books"] = new
        {
            sorts = Pairs(("", "author"), ("title", "title"), ("added", "date added"), ("opened", "last opened"), ("progress", "progress")),
            shows = Pairs(("", "everything"), ("new", "not started"), ("started", "in progress"), ("done", "finished"), ("comic", "comics"), ("epub", "EPUB"), ("pdf", "PDF"))
        },
        ["pictures"] = new { sorts = Pairs(("", "name"), ("date", "date")), shows = Pairs() },
        ["playlists"] = new { sorts = Pairs(), shows = Pairs(("", "everything"), ("liked", "liked"), ("unplayed", "never played")) },
        ["podcasts"] = new { sorts = Pairs(("", "title"), ("new", "new episodes"), ("author", "author")), shows = Pairs(("", "all episodes"), ("unplayed", "unplayed"), ("video", "video"), ("audio", "audio")) },
        ["radio"] = new { sorts = Pairs(("", "name"), ("country", "country"), ("genre", "genre")), shows = Pairs() },
    };

    static object[] Pairs(params (string key, string label)[] items) => items.Select(i => (object)new { key = i.key, label = i.label }).ToArray();

    static bool KeepTrack(View v, Track t) =>
        (v.Show switch { "liked" => t.Rating > 0, "unplayed" => t.PlayCount == 0, "local" => t.IsLocal, "server" => !t.IsLocal, _ => true }) && v.Matches(t.Title, t.Artist, t.Album);

    /// <summary>The music page's albums, after the bar's choices: an album is shown when it fits by name or one of its songs does.</summary>
    static List<Album> ArrangeAlbums(View v, IEnumerable<Album> albums)
    {
        if (!v.Active) return albums.ToList();
        albums = v.Show switch
        {
            "liked" => albums.Where(a => a.Tracks.Any(t => t.Rating > 0)),
            "unplayed" => albums.Where(a => a.Tracks.All(t => t.PlayCount == 0)),
            "local" => albums.Where(a => a.Source == "local"),
            "server" => albums.Where(a => a.Source != "local"),
            _ => albums
        };
        if (v.Text != "") albums = albums.Where(a => v.Matches(a.Title, a.Artist) || a.Tracks.Any(t => v.Matches(t.Title)));
        return v.Order(albums, sort => sort switch
        {
            "title" => list => list.OrderBy(a => a.Title, NoCase),
            "year" => list => list.OrderBy(a => a.Year == 0 ? int.MaxValue : a.Year).ThenBy(a => a.Title, NoCase),
            "added" => list => list.OrderByDescending(a => a.DateAdded),
            "plays" => list => list.OrderByDescending(a => a.Tracks.Sum(t => t.PlayCount)).ThenBy(a => a.Title, NoCase),
            "recent" => list => list.OrderByDescending(a => a.Tracks.Count == 0 ? 0 : a.Tracks.Max(t => t.LastPlayed)),
            _ => null
        });
    }

    static bool KeepVideo(View v, VideoItem x) => (v.Show switch { "local" => x.Source == "local", "server" => x.Source != "local", _ => true }) && v.Matches(x.Title, x.Series, x.Collection);

    static List<VideoItem> ArrangeVideos(View v, IEnumerable<VideoItem> videos, bool sort = true) => v.Order(videos.Where(x => KeepVideo(v, x)), key => !sort ? null : key switch
    {
        "year" => list => list.OrderBy(x => x.Year == 0 ? int.MaxValue : x.Year).ThenBy(x => x.Title, NoCase),
        "added" => list => list.OrderByDescending(x => x.DateAdded),
        "length" => list => list.OrderByDescending(x => x.DurationMs),
        _ => null
    });

    static bool KeepBook(View v, Book b) => (v.Show switch
    {
        "new" => b.Progress <= 0.004 && !b.Finished, "started" => b.Progress > 0.004 && !b.Finished, "done" => b.Finished,
        "comic" => b.Format == "comic", "epub" => b.Format == "epub", "pdf" => b.Format == "pdf", _ => true
    }) && v.Matches(b.Title, b.Author, b.Series);

    // ---------- collection tabs ----------

    static object TabsState() => new
    {
        tabs = Settings.Tabs.Select(t => new { key = t, on = S.TabOn(t) }),
        custom = CustomTabsShape(),
        own = WebServices.OwnTabs.Select(w => new { id = w.Id, name = w.Name, page = w.Page }).ToList()
    };

    static object CustomTabsShape() => S.CustomTabs.Select(t => new { id = t.Id, name = t.Name, page = t.Page }).ToList();

    static void RegisterTabs()
    {
        Rpc.On("tabs.get", _ => TabsState());
        Rpc.On("tabs.set", p =>
        {
            var tab = p.Str("tab");
            if (!Settings.Tabs.Contains(tab)) throw new InvalidOperationException("there is no such tab");
            if (!S.SetTab(tab, p.Bool("on"))) throw new InvalidOperationException("One tab has to stay on.");
            // radio switched off: a station that is playing stops, as on Windows
            if (!S.TabOn("radio") && P.Current?.Station != null) P.Stop();
            Save();
            Rpc.Emit("tabs", TabsState());
            Rpc.Emit("web", WebState());   // a service under a tab that went away moves to a tab of its own
            return TabsState();
        });
        Rpc.On("view.options", p => ViewOptions.GetValueOrDefault(p.Str("page")));
        Rpc.On("view.get", p => ViewOf(default, p.Str("page")).Shape);
    }

    // ---------- radio ----------

    static object StationShape(Station s) => new { id = s.Id, name = s.Name, url = s.Url, art = s.Art, tags = s.Tags, country = s.Country, subtitle = s.Subtitle, saved = Lib.Stations.Any(x => x.Url == s.Url), pinned = Lib.IsPinned("radio", s.Url) };

    static Station StationFrom(JsonElement p) => new()
    {
        Name = p.Str("name").Trim(), Url = p.Str("url").Trim(), Art = p.Str("art"), Tags = p.Str("tags"), Country = p.Str("country")
    };

    static async Task<object> Stations(Func<Task<List<Station>>> ask) => (await ask()).Select(StationShape).ToList();

    static void RegisterRadio()
    {
        Rpc.On("radio.mine", p =>
        {
            var v = ViewOf(p, "radio");
            var list = v.Order(Lib.Stations.Where(st => v.Matches(st.Name, st.Tags, st.Country)), key => key switch
            {
                "country" => l => l.OrderBy(st => st.Country == "" ? "zz" : st.Country).ThenBy(st => st.Name, NoCase),
                "genre" => l => l.OrderBy(st => st.Tags == "" ? "zz" : st.Tags, NoCase).ThenBy(st => st.Name, NoCase),
                _ => null
            });
            return new { total = Lib.Stations.Count, stations = list.Select(StationShape), view = v.Shape };
        });
        Rpc.On("radio.popular", async _ => await Stations(Radio.PopularAsync));
        Rpc.On("radio.search", async p => await Stations(() => Radio.SearchAsync(p.Str("text"))));
        Rpc.On("radio.groups", async p => (p.Str("kind") == "countries" ? await Radio.CountriesAsync() : await Radio.GenresAsync())
            .Select(g => new { name = g.Name, key = g.Key, count = g.Count }).ToList());
        Rpc.On("radio.group", async p => await Stations(() => p.Str("kind") == "countries" ? Radio.ByCountryAsync(p.Str("key")) : Radio.ByGenreAsync(p.Str("key"))));
        Rpc.On("radio.save", p =>
        {
            var s = p.Has("address") ? Radio.FromAddress(p.Str("name"), p.Str("address")) : StationFrom(p);
            if (s == null || s.Url == "" || s.Name == "") throw new InvalidOperationException("That is not a web address.");
            Lib.SaveStation(s);
            return StationShape(s);
        });
        Rpc.On("radio.rename", p =>
        {
            if (Lib.Stations.FirstOrDefault(x => x.Id == p.Long("id")) is not { } s || p.Str("name").Trim() == "") return;
            s.Name = p.Str("name").Trim();
            Lib.SaveStation(s);
        });
        Rpc.On("radio.remove", p => { if (Lib.Stations.FirstOrDefault(x => x.Id == p.Long("id")) is { } s) Lib.RemoveStation(s); });
        Rpc.On("radio.play", p =>
        {
            if (!S.TabOn("radio")) throw new InvalidOperationException("Radio is switched off in settings.");
            var s = Lib.Stations.FirstOrDefault(x => x.Url == p.Str("url")) ?? StationFrom(p);
            if (s.Url == "") return;
            P.PlayStation(s);
        });
    }

    // ---------- podcast directories ----------

    static object HitShape(PodcastHit h) => new { title = h.Title, author = h.Author, art = h.Art, feedUrl = h.FeedUrl, detail = h.Detail, appleId = h.AppleId, subtitle = h.Subtitle, have = HavePodcast(h) };

    static PodcastHit HitFrom(JsonElement p) => new(p.Str("title"), p.Str("author"), p.Str("art"), p.Str("feedUrl"), p.Str("detail"), p.Str("appleId"));

    static bool HavePodcast(PodcastHit h) => (h.FeedUrl != "" && Lib.Podcasts.Any(p => string.Equals(p.FeedUrl, h.FeedUrl, StringComparison.OrdinalIgnoreCase)))
        || Lib.Podcasts.Any(p => string.Equals(p.Title, h.Title, StringComparison.OrdinalIgnoreCase) && (h.Author == "" || string.Equals(p.Author, h.Author, StringComparison.OrdinalIgnoreCase)));

    static object SourcesShape() => new
    {
        sources = PodcastDirectory.Sources().Select(s => new { id = s.Id, name = s.Name, note = s.Note, hasGenres = s.HasGenres, yours = s.Yours }),
        genres = PodcastDirectory.AppleGenres.Select(g => new { name = g.Name, id = g.Id })
    };

    static void RegisterPodcastDirectory()
    {
        Rpc.On("pdir.sources", _ => SourcesShape());
        Rpc.On("pdir.top", async p => (await PodcastDirectory.TopAsync(p.Str("source", "apple"), p.Int("genre"))).Select(HitShape).ToList());
        Rpc.On("pdir.search", async p => (await PodcastDirectory.SearchAsync(p.Str("source", "apple"), p.Str("text"))).Select(HitShape).ToList());
        Rpc.On("pdir.subscribe", async p =>
        {
            var hit = HitFrom(p);
            App.SetStatus($"subscribing to {hit.Title}…");
            try
            {
                var (podcast, episodes) = await PodcastService.FetchAsync(await PodcastDirectory.FeedAsync(hit));
                Lib.Db.SavePodcast(podcast, episodes);
                Lib.Podcasts = Lib.Db.LoadPodcasts();
                return (object?)new { id = podcast.Id, title = podcast.Title, video = episodes.Count > 0 && episodes.All(e => e.IsVideo) };
            }
            finally { App.SetStatus(""); }
        });
        Rpc.On("pdir.addSource", p =>
        {
            var url = p.Str("url").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("That is not a web address.");
            S.PodcastSources.Add(new PodcastSource { Name = p.Str("name").Trim() == "" ? uri.Host : p.Str("name").Trim(), Url = url });
            Save();
            return new { id = "own:" + (S.PodcastSources.Count - 1), all = SourcesShape() };
        });
        Rpc.On("pdir.removeSource", p =>
        {
            var id = p.Str("id");
            if (id.StartsWith("own:") && int.TryParse(id[4..], out var i) && i >= 0 && i < S.PodcastSources.Count) S.PodcastSources.RemoveAt(i);
            Save();
            return SourcesShape();
        });
        // podcasts you subscribe to, as an OPML list other podcast apps read, and back
        Rpc.On("podcasts.exportOpml", p => { File.WriteAllText(p.Str("file"), PodcastDirectory.ToOpml(Lib.Podcasts)); return Lib.Podcasts.Count; });
        Rpc.On("podcasts.importOpml", async p =>
        {
            var hits = PodcastDirectory.ParseOpml(await File.ReadAllTextAsync(p.Str("file")));
            int added = 0, failed = 0;
            foreach (var h in hits.Where(h => !HavePodcast(h)))
            {
                App.SetStatus($"subscribing to {h.Title}…");
                try
                {
                    var (podcast, episodes) = await PodcastService.FetchAsync(h.FeedUrl);
                    Lib.Db.SavePodcast(podcast, episodes);
                    added++;
                }
                catch { failed++; }
            }
            Lib.Podcasts = Lib.Db.LoadPodcasts();
            App.SetStatus("");
            return new { added, failed, already = hits.Count - added - failed };
        });
    }

    // ---------- sleep timer and speed ----------

    static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3 };

    static void RegisterPlayerExtras()
    {
        Rpc.On("player.sleep", p =>
        {
            if (p.Bool("atEnd")) P.SetSleep(null, atEnd: true);
            else if (p.Int("minutes") > 0) P.SetSleep(TimeSpan.FromMinutes(p.Int("minutes")));
            else P.SetSleep(null);
            Rpc.Emit("player", PlayerState());
        });
        Rpc.On("player.speed", p =>
        {
            if (!P.CanSpeed) throw new InvalidOperationException("Radio plays at its own speed.");
            P.Speed = Math.Clamp(p.Num("value", 1), 0.5, 3);
            Save();
            Rpc.Emit("player", PlayerState());
        });
        Rpc.On("player.speeds", _ => Speeds);
    }

    // ---------- listening: stats and scrobbling ----------

    static object TopShape(ListenStats.Top t) => new
    {
        name = t.Name, detail = t.Detail, time = ListenStats.Hours(t.Ms), ms = t.Ms, plays = t.Plays, art = t.Art,
        play = t.Item switch
        {
            Track track => new { type = "track", key = track.Id.ToString() },
            Album album => new { type = "album", key = album.Key },
            string artist when Catalog.AlbumsOf(artist).Count > 0 => new { type = "artist", key = artist },
            _ => null
        }
    };

    static object ListeningState() => new
    {
        lastFm = S.ScrobbleLastFm, listenBrainz = S.ScrobbleListenBrainz,
        lastFmReady = Listening.LastFmReady, listenBrainzReady = Listening.ListenBrainzReady,
        lastFmUser = S.LastFmUser, listenBrainzUser = S.ListenBrainzUser,
        hasLastFmKeys = S.Keys.GetValueOrDefault("lastfm", "") != "" && S.Keys.GetValueOrDefault("lastfm_secret", "") != "",
        result = Listening.LastResult
    };

    static void RegisterListening()
    {
        Rpc.On("stats.periods", _ => new[] { DateTime.Now.Year.ToString(), (DateTime.Now.Year - 1).ToString(), "last 30 days", "all time" });
        Rpc.On("stats.view", p =>
        {
            static long Unix(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();
            var now = DateTime.Now;
            var period = p.Str("period", now.Year.ToString());
            var s = period == "all time" ? ListenStats.AllTime()
                : period == "last 30 days" ? ListenStats.FromLog("the last 30 days", Unix(now.Date.AddDays(-29)), Fmt.Now + 1)
                : period == (now.Year - 1).ToString() ? ListenStats.FromLog(period, Unix(new DateTime(now.Year - 1, 1, 1)), Unix(new DateTime(now.Year, 1, 1)), byMonth: true)
                : ListenStats.FromLog(now.Year.ToString(), Unix(new DateTime(now.Year, 1, 1)), Fmt.Now + 1, byMonth: true);
            return new
            {
                title = s.Title, empty = s.Plays == 0 && s.Ms == 0, hours = ListenStats.Hours(s.Ms),
                ms = s.Ms, plays = s.Plays, artistCount = s.Artists, albumCount = s.Albums, songCount = s.Songs,
                over = s.Over.Select(o => new { label = o.Label, ms = o.Ms, text = o.Ms > 0 ? ListenStats.Hours(o.Ms) : "" }),
                summary = Fmt.Join(Fmt.Plural(s.Plays, "play"), Fmt.Plural(s.Artists, "artist"), Fmt.Plural(s.Albums, "album"), Fmt.Plural(s.Songs, "song")),
                artists = s.TopArtists.Select(TopShape), albums = s.TopAlbums.Select(TopShape), songs = s.TopSongs.Select(TopShape), note = s.Note
            };
        });
        Rpc.On("listening.state", _ => ListeningState());
        Rpc.On("listening.set", p =>
        {
            if (p.Has("lastFm")) S.ScrobbleLastFm = p.Bool("lastFm") && Listening.LastFmReady;
            if (p.Has("listenBrainz")) S.ScrobbleListenBrainz = p.Bool("listenBrainz") && Listening.ListenBrainzReady;
            Save();
            if (S.ScrobbleLastFm || S.ScrobbleListenBrainz) _ = Listening.FlushAsync();
            return ListeningState();
        });
        Rpc.On("listening.lbToken", async p =>
        {
            var token = p.Str("token").Trim();
            if (token == "")
            {
                S.Keys.Remove("listenbrainz"); S.ListenBrainzUser = ""; S.ScrobbleListenBrainz = false;
                Save();
                return new { ok = true, text = "token removed", state = ListeningState() };
            }
            var user = await Listening.ListenBrainzCheckAsync(token);
            if (user == "") return new { ok = false, text = "ListenBrainz does not accept that token.", state = ListeningState() };
            S.Keys["listenbrainz"] = token;
            S.ListenBrainzUser = user;
            S.ScrobbleListenBrainz = true;
            Save();
            return new { ok = true, text = "connected as " + user, state = ListeningState() };
        });
        Rpc.On("listening.lfmKeys", p =>
        {
            if (p.Str("key").Trim() != "") S.Keys["lastfm"] = p.Str("key").Trim();
            if (p.Str("secret").Trim() != "") S.Keys["lastfm_secret"] = p.Str("secret").Trim();
            Save();
            return ListeningState();
        });
        // signing in to Last.fm: the browser asks you to allow Phoenix; the window then asks here until it is allowed
        Rpc.On("listening.lfmBegin", async _ =>
        {
            if (S.Keys.GetValueOrDefault("lastfm", "") == "" || S.Keys.GetValueOrDefault("lastfm_secret", "") == "") throw new InvalidOperationException("Enter the API key and secret first.");
            var (token, url) = await Listening.LastFmBeginAsync();
            return new { token, url };
        });
        Rpc.On("listening.lfmFinish", async p =>
        {
            var name = Program.SmokeMode ? "" : await Listening.LastFmFinishAsync(p.Str("token"));
            if (name != "") { S.ScrobbleLastFm = true; Save(); }
            return new { name, state = ListeningState() };
        });
        Rpc.On("listening.lfmSignOut", _ => { Listening.LastFmSignOut(); return ListeningState(); });
    }

    // ---------- backup and restore ----------

    static void RegisterBackup()
    {
        Rpc.On("backup.name", _ => Backup.SuggestedName);
        Rpc.On("backup.write", p =>
        {
            var file = p.Str("file");
            if (!Path.IsPathRooted(file)) throw new InvalidOperationException("Choose where to save the backup.");
            using (var to = File.Create(file)) Backup.Write(to);
            return $"backed up to {file}  ({new FileInfo(file).Length / 1024.0 / 1024.0:0.#} MB)";
        });
        // the library can't be swapped while it is open: the restore is put in place when Phoenix starts again
        Rpc.On("backup.stage", p =>
        {
            using var from = File.OpenRead(p.Str("file"));
            var m = Backup.Stage(from);
            return new { created = m.Created, device = m.Device, songs = m.Songs, playlists = m.Playlists };
        });
        Rpc.On("backup.discard", _ => Backup.Discard());
        Rpc.On("storage.state", _ =>
        {
            long size = 0;
            try { size = new DirectoryInfo(DataDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            return new { folder = DataDir, used = size >= 1 << 30 ? $"{size / (double)(1 << 30):0.0} GB used" : $"{size / (double)(1 << 20):0} MB used" };
        });
    }

    static string DataDir => Program.DataDir;
}
