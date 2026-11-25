using Android.Content;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>Empty choices for the filter / show / sort bar (a page that only filters, say).</summary>
public static class Opts
{
    public static readonly (string Key, string Label)[] None = Array.Empty<(string, string)>();
}

/// <summary>Screens brought over from the Windows app: radio, podcast sources, listening stats, and the player's extras.</summary>
public static class MoreScreens
{
    static Library Lib => App.Lib;
    static Player P => App.Player;
    static Settings S => App.Settings;

    // ---------- radio ----------

    static Row StationRow(MainActivity a, Station s, bool saved, Action changed) => new()
    {
        Title = s.Name, Sub = s.Subtitle, Art = s.Art, ArtShape = 1,
        Trail = !saved && Lib.Stations.Any(x => x.Url == s.Url) ? "saved" : "",
        IsCurrent = () => P.Current?.Station?.Url == s.Url,
        Click = () =>
        {
            if (saved) { P.PlayStation(s); return; }
            var have = Lib.Stations.Any(x => x.Url == s.Url);
            Ui.Menu(a, s.Name, ("Listen", () => P.PlayStation(s)),
                (have ? "Already in my stations" : "Add to my stations", () => { if (!have) { Lib.SaveStation(s); Ui.Toast(a, $"{s.Name} added to my stations"); changed(); } }));
        },
        Long = () =>
        {
            if (!saved) { if (Lib.Stations.All(x => x.Url != s.Url)) { Lib.SaveStation(s); Ui.Toast(a, $"{s.Name} added to my stations"); changed(); } return; }
            Ui.Menu(a, s.Name,
                ("Listen", () => P.PlayStation(s)),
                (Lib.IsPinned("radio", s.Url) ? "Unpin from quickplay" : "Pin to quickplay", () => Lib.TogglePin("radio", s.Url, s.Name)),
                ("Rename…", () => Ui.Ask(a, "Rename station", "Name", s.Name, name => { if (name.Trim() != "") { s.Name = name.Trim(); Lib.SaveStation(s); } })),
                ("Remove", () => Lib.RemoveStation(s)));
        }
    };

    /// <summary>Internet radio: "my stations", and "sources" to browse the station directory and add from it.</summary>
    public static Page Radio(MainActivity a)
    {
        var tab = "my stations";
        var view = new ViewChoice("radio");
        var way = "";                           // "", "popular", "genres", "countries", "found"
        var title = "";
        var note = "";
        List<Station> results = new();
        List<Radio.Group>? groups = null;
        var asked = 0;
        var page = new Page(a, "radio");

        async Task Load(string what, Func<Task<List<Station>>> ask)
        {
            var mine = ++asked;
            title = what; groups = null; results = new(); note = "asking radio-browser.info…";
            page.Reload();
            try { var found = await ask(); if (mine != asked) return; results = found; note = found.Count == 0 ? "No stations found." : ""; }
            catch (Exception ex) { if (mine != asked) return; note = "The station directory can't be reached: " + ex.Message; }
            page.Reload();
        }

        async Task LoadGroups(string kind)
        {
            var mine = ++asked;
            way = kind; title = kind.ToUpperInvariant(); results = new(); groups = new(); note = "asking radio-browser.info…";
            page.Reload();
            try { var found = kind == "genres" ? await Halcyon.Services.Radio.GenresAsync() : await Halcyon.Services.Radio.CountriesAsync(); if (mine != asked) return; groups = found; note = ""; }
            catch (Exception ex) { if (mine != asked) return; note = "The station directory can't be reached: " + ex.Message; }
            page.Reload();
        }

        void Popular() { way = "popular"; _ = Load("POPULAR", Halcyon.Services.Radio.PopularAsync); }

        page.Chips(() => new[] { "my stations", "sources" }, () => tab, t => { tab = t; if (t == "sources" && way == "") Popular(); });
        page.Subtitle = () => tab == "sources" ? "the Radio Browser directory (radio-browser.info)" : Fmt.Plural(Lib.Stations.Count, "station");
        return page.Show(() =>
        {
            var rows = new List<Row>();
            if (tab == "my stations")
            {
                var links = new List<(string, Action)>
                {
                    ("add by address", () => Ui.Ask(a, "Add a station", "stream address (http://… or https://…)", "https://", url =>
                        Ui.Ask(a, "Add a station", "name", "", name =>
                        {
                            if (Halcyon.Services.Radio.FromAddress(name, url) is { } st) Lib.SaveStation(st); else Ui.Toast(a, "That is not a web address.");
                        })))
                };
                links.AddRange(page.Opts(view, new[] { ("", "name"), ("country", "country"), ("genre", "genre") }, Opts.None));
                page.Actions(links.ToArray());
                var stations = view.Order(Lib.Stations.Where(st => view.Matches(st.Name, st.Tags, st.Country)), key => key switch
                {
                    "country" => l => l.OrderBy(st => st.Country == "" ? "zz" : st.Country).ThenBy(st => st.Name, StringComparer.OrdinalIgnoreCase),
                    "genre" => l => l.OrderBy(st => st.Tags == "" ? "zz" : st.Tags, StringComparer.OrdinalIgnoreCase).ThenBy(st => st.Name, StringComparer.OrdinalIgnoreCase),
                    _ => null
                });
                rows.AddRange(stations.Select(st => StationRow(a, st, true, page.Reload)));
                return new Content(rows, Empty: Lib.Stations.Count == 0 ? "No stations yet.\n\nFind some under “sources”, or tap “add by address” and paste a stream's address." : "No station matches the filter.");
            }
            page.Actions(("popular", Popular), ("genres", () => _ = LoadGroups("genres")), ("countries", () => _ = LoadGroups("countries")),
                ("search", () => Ui.Ask(a, "Search stations", "a station's name", "", text =>
                {
                    if (text.Trim() == "") return;
                    way = "found";
                    _ = Load($"“{text.Trim().ToUpperInvariant()}”", () => Halcyon.Services.Radio.SearchAsync(text));
                })));
            if (note != "") rows.Add(Row.Note(note));
            if (groups != null)
            {
                var kind = way;
                if (groups.Count > 0) rows.Add(Row.Caption($"{title}  {groups.Count}"));
                rows.AddRange(groups.Select(g => new Row
                {
                    Title = g.Name, Trail = g.Count.ToString("N0"),
                    Click = () => _ = Load(g.Name.ToUpperInvariant(), () => kind == "countries" ? Halcyon.Services.Radio.ByCountryAsync(g.Key) : Halcyon.Services.Radio.ByGenreAsync(g.Key))
                }));
            }
            else
            {
                if (results.Count > 0) rows.Add(Row.Caption($"{title}  {results.Count}"));
                rows.AddRange(results.Select(st => StationRow(a, st, false, page.Reload)));
                if (results.Count > 0) rows.Add(Row.Note("Tap a station to listen or add it to your stations; long-press adds it straight away."));
            }
            return new Content(rows);
        });
    }

    // ---------- podcast sources ----------

    /// <summary>Fetches a podcast's feed and adds it to "my podcasts".</summary>
    public static async Task SubscribeAsync(MainActivity a, PodcastHit hit, Action done)
    {
        try
        {
            App.SetStatus($"subscribing to {hit.Title}…");
            var (podcast, episodes) = await PodcastService.FetchAsync(await PodcastDirectory.FeedAsync(hit));
            Lib.Db.SavePodcast(podcast, episodes);
            Lib.Podcasts = Lib.Db.LoadPodcasts();
            App.SetStatus("");
            Ui.Toast(a, $"Subscribed to {podcast.Title}" + (episodes.Count > 0 && episodes.All(e => e.IsVideo) ? " (a video podcast)" : ""));
            done();
        }
        catch (Exception ex) { App.SetStatus(""); Ui.Alert(a, $"Couldn't subscribe to {hit.Title}.\n\n{ex.Message}"); }
    }

    static bool Have(PodcastHit h) => (h.FeedUrl != "" && Lib.Podcasts.Any(p => string.Equals(p.FeedUrl, h.FeedUrl, StringComparison.OrdinalIgnoreCase)))
                                      || Lib.Podcasts.Any(p => string.Equals(p.Title, h.Title, StringComparison.OrdinalIgnoreCase) && (h.Author == "" || string.Equals(p.Author, h.Author, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What the "sources" side of the podcasts page shows: its state lives here between redraws.</summary>
    public class PodcastBrowser
    {
        public string Source = "apple", Title = "", Note = "";
        public int Genre;
        public List<PodcastHit> Hits = new();
        public bool Opened;
        int _asked;

        public string SourceName => PodcastDirectory.Sources().FirstOrDefault(s => s.Id == Source)?.Name ?? "Apple Podcasts";

        public async Task Load(string title, Func<Task<List<PodcastHit>>> ask, Action redraw)
        {
            var mine = ++_asked;
            Opened = true;
            Title = title; Hits = new(); Note = $"asking {SourceName}…";
            redraw();
            try { var found = await ask(); if (mine != _asked) return; Hits = found; Note = found.Count == 0 ? "No podcasts found." : ""; }
            catch (Exception ex) { if (mine != _asked) return; Note = $"{SourceName} can't be read: {ex.Message}"; }
            redraw();
        }

        public void Browse(Action redraw)
        {
            var genre = PodcastDirectory.AppleGenres.FirstOrDefault(g => g.Id == Genre).Name;
            string source = Source; var id = Genre;
            _ = Load(genre != null ? genre.ToUpperInvariant() : Source == "apple" ? "TOP PODCASTS" : SourceName.ToUpperInvariant(), () => PodcastDirectory.TopAsync(source, id), redraw);
        }
    }

    /// <summary>The action links and rows of the podcasts page's "sources" side.</summary>
    public static Content PodcastSources(MainActivity a, Page page, PodcastBrowser b)
    {
        var sources = PodcastDirectory.Sources();
        if (sources.All(s => s.Id != b.Source)) b.Source = "apple";
        var current = sources.First(s => s.Id == b.Source);
        var links = new List<(string, Action)>
        {
            ($"source: {current.Name}", () => Ui.Menu(a, "Browse podcasts from",
                sources.Select(s => ((s.Id == b.Source ? "●  " : "") + s.Name, (Action)(() => { b.Source = s.Id; b.Genre = 0; b.Browse(page.Reload); }))).ToArray()))
        };
        if (current.HasGenres)
            links.Add(("genres", () => Ui.Menu(a, "Apple Podcasts charts",
                new[] { ("top podcasts", (Action)(() => { b.Genre = 0; b.Browse(page.Reload); })) }
                    .Concat(PodcastDirectory.AppleGenres.Select(g => (g.Name, (Action)(() => { b.Genre = g.Id; b.Browse(page.Reload); })))).ToArray())));
        links.Add(("search", () => Ui.Ask(a, $"Search {current.Name}", "a podcast's name", "", text =>
        {
            if (text.Trim() == "") return;
            var source = b.Source;
            _ = b.Load($"“{text.Trim().ToUpperInvariant()}”", () => PodcastDirectory.SearchAsync(source, text), page.Reload);
        })));
        links.Add(("add your own", () => Ui.Ask(a, "Add a source of your own", "address of an OPML list of podcasts", "https://", url =>
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { Ui.Toast(a, "That is not a web address."); return; }
            Ui.Ask(a, "Add a source of your own", "a name for it", uri.Host, name =>
            {
                S.PodcastSources.Add(new PodcastSource { Name = name.Trim() == "" ? uri.Host : name.Trim(), Url = url.Trim() });
                S.Save();
                b.Source = "own:" + (S.PodcastSources.Count - 1);
                b.Genre = 0;
                b.Browse(page.Reload);
            });
        })));
        if (current.Yours)
            links.Add(("remove this source", () => Ui.Confirm(a, $"Remove “{current.Name}” from your sources? Podcasts you subscribed to from it stay.", "Remove", () =>
            {
                if (int.TryParse(current.Id[4..], out var i) && i < S.PodcastSources.Count) S.PodcastSources.RemoveAt(i);
                S.Save();
                b.Source = "apple"; b.Hits = new(); b.Title = ""; b.Opened = false;
                page.Reload();
            })));
        page.Actions(links.ToArray());

        var rows = new List<Row>();
        if (b.Note != "") rows.Add(Row.Note(b.Note));
        if (b.Hits.Count > 0) rows.Add(Row.Caption($"{b.Title}  {b.Hits.Count}"));
        rows.AddRange(b.Hits.Select(h => new Row
        {
            Title = h.Title, Sub = h.Subtitle, Art = h.Art, ArtShape = 1, Trail = Have(h) ? "subscribed" : "", Dim = Have(h),
            Click = () =>
            {
                if (Have(h)) { Ui.Toast(a, "Already in my podcasts."); return; }
                Ui.Confirm(a, $"Subscribe to “{h.Title}”?", "Subscribe", () => _ = SubscribeAsync(a, h, page.Reload));
            }
        }));
        if (b.Hits.Count > 0) rows.Add(Row.Note("Tap a podcast to subscribe to it."));
        return new Content(rows);
    }

    // ---------- listening stats ----------

    public static Page Stats(MainActivity a)
    {
        var period = DateTime.Now.Year.ToString();
        var page = new Page(a, "listening");
        string[] Periods() => new[] { DateTime.Now.Year.ToString(), (DateTime.Now.Year - 1).ToString(), "last 30 days", "all time" };
        page.Chips(Periods, () => period, p => period = p);
        return page.List(() =>
        {
            long Unix(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();
            var now = DateTime.Now;
            var s = period == "all time" ? ListenStats.AllTime()
                : period == "last 30 days" ? ListenStats.FromLog("the last 30 days", Unix(now.Date.AddDays(-29)), Fmt.Now + 1)
                : period == (now.Year - 1).ToString() ? ListenStats.FromLog(period, Unix(new DateTime(now.Year - 1, 1, 1)), Unix(new DateTime(now.Year, 1, 1)))
                : ListenStats.FromLog(period, Unix(new DateTime(now.Year, 1, 1)), Fmt.Now + 1);
            var rows = new List<Row>();
            if (s.Plays == 0 && s.Ms == 0)
            {
                rows.Add(Row.Note($"Nothing listened to in {s.Title} yet."));
                if (s.Note != "") rows.Add(Row.Note(s.Note));
                return rows;
            }
            rows.Add(new Row { Title = ListenStats.Hours(s.Ms) + " listened", Sub = Fmt.Join(Fmt.Plural(s.Plays, "play"), Fmt.Plural(s.Artists, "artist"), Fmt.Plural(s.Albums, "album"), Fmt.Plural(s.Songs, "song")) });
            void Section(string caption, List<ListenStats.Top> tops)
            {
                if (tops.Count == 0) return;
                rows.Add(Row.Caption(caption));
                rows.AddRange(tops.Select((t, i) => new Row
                {
                    Lead = (i + 1).ToString(), Title = t.Name, Art = t.Art, ArtShape = 1,
                    Sub = Fmt.Join(t.Detail, ListenStats.Hours(t.Ms), Fmt.Plural(t.Plays, "play")),
                    Click = () =>
                    {
                        switch (t.Item)
                        {
                            case Track track: P.PlayTracks(new[] { track }); break;
                            case Album album: a.Push(Screens.AlbumPage(a, album.Key)); break;
                            case string artist when Catalog.AlbumsOf(artist).Count > 0: a.Push(Screens.Artist(a, artist)); break;
                        }
                    }
                }));
            }
            Section("TOP ARTISTS", s.TopArtists);
            Section("TOP ALBUMS", s.TopAlbums);
            Section("TOP SONGS", s.TopSongs);
            if (s.Note != "") rows.Add(Row.Note(s.Note));
            return rows;
        });
    }

    // ---------- the player's extras: sleep timer and speed ----------

    public static void SleepMenu(MainActivity a)
    {
        var items = new List<(string, Action)> { ((P.SleepOn ? "" : "●  ") + "Off", () => P.SetSleep(null)) };
        foreach (var minutes in new[] { 15, 30, 45, 60, 90, 120 })
        {
            var n = minutes;
            items.Add(($"{n} minutes", () => { P.SetSleep(TimeSpan.FromMinutes(n)); Ui.Toast(a, $"Phoenix will pause in {n} minutes."); }));
        }
        if (P.Current != null) items.Add(((P.SleepAtEnd ? "●  " : "") + "When this one ends", () => P.SetSleep(null, atEnd: true)));
        Ui.Menu(a, P.SleepOn ? $"Sleep timer ({P.SleepText.Replace("sleep: ", "").Replace("sleep ", "")})" : "Sleep timer", items.ToArray());
    }

    public static void SpeedMenu(MainActivity a)
    {
        if (!P.CanSpeed) { Ui.Toast(a, "Radio plays at its own speed."); return; }
        var kept = P.Current is { Episode: not null } or { Book: not null };
        Ui.Menu(a, kept ? "Playback speed (remembered for this " + (P.Current!.Book != null ? "book)" : "podcast)") : "Playback speed",
            new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3 }.Select(r =>
                ((Math.Abs(P.Speed - r) < 0.01 ? "●  " : "") + (r == 1 ? "1× (normal)" : $"{r:0.##}×"), (Action)(() => { P.Speed = r; S.Save(); }))).ToArray());
    }

    /// <summary>The menu behind the "more" button of the now playing screen.</summary>
    public static void PlayerMenu(MainActivity a) => Ui.Menu(a, null,
        (P.SleepOn ? $"Sleep timer ({P.SleepText.Replace("sleep: ", "").Replace("sleep ", "")})…" : "Sleep timer…", () => SleepMenu(a)),
        (P.CanSpeed && Math.Abs(P.Speed - 1) > 0.01 ? $"Playback speed ({P.Speed:0.##}×)…" : "Playback speed…", () => SpeedMenu(a)),
        ("Listening stats", () => { a.HideNowPlaying(); a.Push(Stats(a)); }));

    /// <summary>What is out of the ordinary about playback right now, in words ("sleep in 12 min  ·  1.5× speed").</summary>
    public static string ModeText => Fmt.Join(P.SleepText, P.CanSpeed && Math.Abs(P.Speed - 1) > 0.01 ? $"{P.Speed:0.##}× speed" : "");

    // ---------- scrobbling, updates and backup (rows for the settings screen) ----------

    public static void SettingsRows(MainActivity a, List<Row> rows, Page page)
    {
        void Save() { S.Save(); page.Reload(); }
        static string Box(bool on) => on ? "☑  " : "☐  ";

        rows.Add(Row.Caption("COLLECTION SECTIONS"));
        rows.Add(Row.Note("Choose which sections the home menu shows. A section that is off is only hidden: nothing in it is removed."));
        foreach (var tab in Settings.Tabs)
        {
            var name = tab;
            rows.Add(new Row
            {
                Title = Box(S.TabOn(name)) + name,
                Click = () =>
                {
                    if (!S.SetTab(name, !S.TabOn(name))) { Ui.Toast(a, "One section has to stay on."); return; }
                    if (!S.TabOn("radio") && P.Current?.Station != null) P.Stop();
                    Save();
                }
            });
        }

        rows.Add(Row.Caption("LISTENING"));
        rows.Add(new Row { Title = "listening stats…", Sub = "hours, plays and your top artists, albums and songs", Click = () => a.Push(Stats(a)) });
        rows.Add(new Row
        {
            Title = Box(S.ScrobbleListenBrainz) + "send my listens to ListenBrainz",
            Sub = !Listening.ListenBrainzReady ? "needs your user token (below)" : S.ListenBrainzUser != "" ? "connected as " + S.ListenBrainzUser : "token saved",
            Click = () => { if (!Listening.ListenBrainzReady) { Ui.Toast(a, "Enter your ListenBrainz token first."); return; } S.ScrobbleListenBrainz = !S.ScrobbleListenBrainz; Save(); }
        });
        rows.Add(new Row
        {
            Title = "ListenBrainz user token", Sub = Listening.ListenBrainzReady ? "saved (encrypted on this device)  ·  tap to change" : "from listenbrainz.org → settings",
            Click = () => Ui.Ask(a, "ListenBrainz token", "paste your user token", "", async token =>
            {
                token = token.Trim();
                if (token == "") { S.Keys.Remove("listenbrainz"); S.ListenBrainzUser = ""; S.ScrobbleListenBrainz = false; Save(); return; }
                S.Keys["listenbrainz"] = token;
                Save();
                try
                {
                    S.ListenBrainzUser = await Listening.ListenBrainzCheckAsync(token);
                    if (S.ListenBrainzUser == "") Ui.Alert(a, "ListenBrainz does not accept that token."); else { S.ScrobbleListenBrainz = true; Ui.Toast(a, "Connected as " + S.ListenBrainzUser); }
                }
                catch (Exception ex) { Ui.Alert(a, "Couldn't reach ListenBrainz.\n\n" + ex.Message); }
                Save();
            }, true)
        });
        rows.Add(new Row
        {
            Title = Box(S.ScrobbleLastFm) + "send my listens to Last.fm",
            Sub = Listening.LastFmReady ? "connected as " + S.LastFmUser : "needs an API key and secret, then “connect” (below)",
            Click = () => { if (!Listening.LastFmReady) { Ui.Toast(a, "Connect to Last.fm first."); return; } S.ScrobbleLastFm = !S.ScrobbleLastFm; Save(); }
        });
        rows.Add(new Row
        {
            Title = "Last.fm API key and secret", Sub = S.Keys.GetValueOrDefault("lastfm", "") != "" && S.Keys.GetValueOrDefault("lastfm_secret", "") != "" ? "saved (encrypted on this device)  ·  tap to change" : "from last.fm/api/account/create (free)",
            Click = () => Ui.Ask(a, "Last.fm API key", "paste the API key", "", key =>
                Ui.Ask(a, "Last.fm shared secret", "paste the shared secret", "", secret =>
                {
                    if (key.Trim() == "" || secret.Trim() == "") return;
                    S.Keys["lastfm"] = key.Trim(); S.Keys["lastfm_secret"] = secret.Trim();
                    Save();
                }, true), true)
        });
        rows.Add(Listening.LastFmReady
            ? new Row { Title = "disconnect from Last.fm", Click = () => { Listening.LastFmSignOut(); page.Reload(); } }
            : new Row
            {
                Title = "connect to Last.fm…", Sub = "opens Last.fm in your browser, where you allow Phoenix",
                Click = async () =>
                {
                    if (S.Keys.GetValueOrDefault("lastfm", "") == "" || S.Keys.GetValueOrDefault("lastfm_secret", "") == "") { Ui.Toast(a, "Enter the API key and secret first."); return; }
                    try
                    {
                        var (token, url) = await Listening.LastFmBeginAsync();
                        a.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)));
                        App.SetStatus("waiting for you to allow Phoenix on Last.fm…");
                        var name = "";
                        for (var i = 0; i < 90 && name == ""; i++) { await Task.Delay(2000); name = await Listening.LastFmFinishAsync(token); }
                        App.SetStatus("");
                        if (name == "") Ui.Alert(a, "That took too long. Choose “connect to Last.fm” again."); else { S.ScrobbleLastFm = true; Ui.Toast(a, "Connected as " + name); }
                        Save();
                    }
                    catch (Exception ex) { App.SetStatus(""); Ui.Alert(a, "Couldn't connect to Last.fm.\n\n" + ex.Message); }
                }
            });
        if (Listening.LastResult != "") rows.Add(Row.Note(Listening.LastResult));

        rows.Add(Row.Caption("BACKUP"));
        rows.Add(Row.Note("A backup is one file holding your likes, play counts and play log, playlists, bookmarks and reading progress, podcasts, stations, pins, tags and settings. Your music and other files are not in it."));
        rows.Add(new Row { Title = "back up now…", Sub = "choose where to save the file", Click = a.StartBackup });
        rows.Add(new Row { Title = "restore from a backup…", Sub = "replaces the collection details and settings on this phone", Click = a.StartRestore });

        rows.Add(Row.Caption("UPDATES"));
        rows.Add(new Row { Title = Box(S.CheckUpdates) + "look for a newer Phoenix once a day", Sub = "asks GitHub for the list of releases; nothing about you is sent", Click = () => { S.CheckUpdates = !S.CheckUpdates; Save(); } });
        rows.Add(new Row { Title = "check now", Sub = $"this is Phoenix {a.VersionName}", Click = () => _ = a.CheckForUpdate(true) });
    }
}
