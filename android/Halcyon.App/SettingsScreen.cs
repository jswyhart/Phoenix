using Android.Content;
using Android.Views;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>
/// Settings, arranged like the Windows app's: the groups SOFTWARE, PC and SERVERS across the top, the chosen group's
/// categories listed underneath, and each category's options on a page of its own.
/// </summary>
public static class SettingsScreen
{
    static Settings S => App.Settings;

    /// <summary>The groups and their categories: (id, name, what it holds).</summary>
    public static readonly Dictionary<string, (string Id, string Name, string About)[]> Groups = new()
    {
        ["software"] = new[]
        {
            ("collection", "Collection", "folders for music, videos, pictures and books; rescan"),
            ("metadata", "Metadata", "Discogs, for album details and covers"),
            ("playback", "Playback", "equalizer and volume levelling"),
            ("listening", "Listening", "stats, and sending listens to Last.fm or ListenBrainz"),
            ("lyrics", "Lyrics", "looking lyrics up on LRCLIB"),
            ("display", "Display", "theme, size, visualizer, and which sections are shown"),
            ("storage", "Storage", "back up and restore"),
            ("updates", "Updates", "look for a newer Phoenix")
        },
        ["pc"] = new[] { ("sync", "Sync options", "copy music from Phoenix on your PC, and use this phone as its remote") },
        ["servers"] = new[] { ("jellyfin", "Jellyfin", "a Jellyfin server's music, videos and books"), ("plex", "Plex", "a Plex Media Server's music and videos") }
    };

    static string _group = "software";
    static CancellationTokenSource? _autoEqScan, _aiDownload;
    static string _aiStatus = "";
    static string _autoEqStatus = "measures with the phone's own decoders, one song at a time";

    public static Page Build(MainActivity a)
    {
        var page = new Page(a, "settings");
        page.Chips(() => new[] { "SOFTWARE", "PC", "SERVERS" }, () => _group.ToUpperInvariant(), g => _group = g.ToLowerInvariant());
        page.Actions(("find a setting", () => Ui.Ask(a, "Find a setting", "for example: equalizer, theme, pairing", "", q => { if (q.Trim() != "") a.Push(Found(a, q)); })));
        return page.List(() => Groups[_group].Select(c => new Row
        {
            Title = c.Name, Sub = c.About,
            Click = () => a.Push(Category(a, c.Id, c.Name))
        }).ToList());
    }

    /// <summary>
    /// The settings whose words match, from every page: each word must appear in the setting, what it says under it, or
    /// the name of its page. Tapping one opens its page.
    /// </summary>
    static Page Found(MainActivity a, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var page = new Page(a, "settings found");
        page.Subtitle = () => $"“{query.Trim()}”";
        var found = new List<Row>();
        foreach (var (id, name, _) in Groups.Values.SelectMany(g => g))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Category(a, id, name).RowsNow())
            {
                if (r.Kind is not (RowKind.Item or RowKind.Caption) || r.Title == "" || !seen.Add(r.Title)) continue;
                var text = $"{r.Title} {r.Sub} {name}";
                if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                var title = r.Kind == RowKind.Caption ? char.ToUpperInvariant(r.Title[0]) + r.Title[1..].ToLowerInvariant() : r.Title;
                found.Add(new Row { Title = title, Sub = Fmt.Join(name, r.Sub), Click = () => a.Push(Category(a, id, name)) });
            }
        }
        return page.List(() => found, "Nothing matches. Try one word, like “equalizer”, “theme” or “sync”.");
    }

    /// <summary>One category's options.</summary>
    public static Page Category(MainActivity a, string category, string title)
    {
        var page = new Page(a, title);
        var note = "";

        void Save() { S.Save(); page.Reload(); }

        void Folders(List<Row> rows, string caption, List<string> list, string addLabel, Action changed)
        {
            rows.Add(Row.Caption(caption));
            rows.AddRange(list.Select(f => new Row
            {
                Title = f, Sub = Directory.Exists(f) ? "tap to remove" : "not found  ·  tap to remove",
                Click = () => Ui.Confirm(a, $"Stop including {f}?\n\nNothing is deleted from the device.", "Remove", () => { list.Remove(f); Save(); changed(); })
            }));
            rows.Add(new Row
            {
                Title = addLabel, Dim = true,
                Click = () => Ui.PickFolder(a, Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath, dir =>
                {
                    if (!list.Contains(dir)) list.Add(dir);
                    Save();
                    changed();
                })
            });
        }

        async Task Sync()
        {
            await App.Lib.SyncServersAsync(App.SetStatus);
            page.Reload();
        }

        void ConnectJellyfin()
        {
            var url = Ui.Input(a, "server address, e.g. 192.168.1.20:8096", S.JellyfinUrl);
            var user = Ui.Input(a, "user name", S.JellyfinUser);
            var pass = Ui.Input(a, "password", "", true);
            var form = Ui.Column(a, url, user, pass);
            form.SetPadding(Th.Dp(20), Th.Dp(8), Th.Dp(20), 0);
            new AlertDialog.Builder(a).SetTitle("Connect to Jellyfin")!.SetView(form)!
                .SetPositiveButton("Connect", async (_, _) =>
                {
                    try
                    {
                        note = "connecting to jellyfin…"; page.Reload();
                        await App.Jellyfin.LoginAsync(url.Text ?? "", (user.Text ?? "").Trim(), pass.Text ?? "");
                        note = ""; page.Reload();
                        await Sync();
                    }
                    catch (Exception ex) { note = ""; page.Reload(); Ui.Alert(a, "Couldn't connect to Jellyfin.\n\n" + ex.Message); }
                })!
                .SetNegativeButton("Cancel", (_, _) => { })!.Show();
        }

        async Task UsePlexServer(string url, string token)
        {
            string oldUrl = S.PlexUrl, oldToken = S.PlexToken;
            S.PlexUrl = url.Trim().TrimEnd('/');
            if (!S.PlexUrl.Contains("://")) S.PlexUrl = "http://" + S.PlexUrl;
            S.PlexToken = token.Trim();
            try
            {
                note = "connecting to plex…"; page.Reload();
                await App.Plex.TestAsync();
                note = "";
                Save();
                await Sync();
            }
            catch (Exception ex)
            {
                S.PlexUrl = oldUrl; S.PlexToken = oldToken;
                note = ""; page.Reload();
                Ui.Alert(a, "Couldn't reach that Plex server.\n\n" + ex.Message);
            }
        }

        // plex.tv PIN sign-in: the user approves this app in their browser; no password is typed here.
        async Task PlexSignIn()
        {
            try
            {
                var (id, _, url) = await App.Plex.CreatePinAsync();
                a.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)));
                note = "waiting for you to approve Phoenix in the browser…"; page.Reload();
                var token = "";
                for (var i = 0; i < 150 && token == ""; i++)
                {
                    await Task.Delay(2000);
                    try { token = await App.Plex.CheckPinAsync(id); } catch { }
                }
                note = ""; page.Reload();
                if (token == "") { Ui.Alert(a, "Plex sign-in timed out. Try again."); return; }
                var servers = await App.Plex.GetServersAsync(token);
                if (servers.Count == 0) { Ui.Alert(a, "No Plex servers were found on that account."); return; }
                Ui.Menu(a, "Choose a server", servers.Select(s => (s.ToString(), (Action)(() => _ = UsePlexServer(s.Uri, s.Token)))).ToArray());
            }
            catch (Exception ex) { note = ""; page.Reload(); Ui.Alert(a, "Plex sign-in failed.\n\n" + ex.Message); }
        }

        void PlexManual()
        {
            var url = Ui.Input(a, "server address, e.g. 192.168.1.20:32400", S.PlexUrl);
            var token = Ui.Input(a, "X-Plex-Token", "", true);
            var form = Ui.Column(a, url, token);
            form.SetPadding(Th.Dp(20), Th.Dp(8), Th.Dp(20), 0);
            new AlertDialog.Builder(a).SetTitle("Connect to Plex")!.SetView(form)!
                .SetPositiveButton("Connect", (_, _) => _ = UsePlexServer(url.Text ?? "", token.Text ?? ""))!
                .SetNegativeButton("Cancel", (_, _) => { })!.Show();
        }

        return page.List(() =>
        {
            var rows = new List<Row>();
            if (note != "") rows.Add(Row.Note(note));

            if (category == "collection")
            {
            Folders(rows, "MUSIC FOLDERS", S.MusicFolders, "add a music folder…", () => _ = App.RefreshCollectionAsync());
            Folders(rows, "VIDEO FOLDERS", S.VideoFolders, "add a video folder…", () => _ = App.RefreshCollectionAsync());
            Folders(rows, "PICTURE FOLDERS", S.PictureFolders, "add a picture folder…", () => { });
            Folders(rows, "BOOK FOLDERS (AUDIOBOOKS AND EBOOKS)", S.BookFolders, "add a book folder…", () =>
            {
                if (S.BookFolders.Count > 0 && !a.HasAllFilesAccess) a.AskAllFilesAccess();
                else _ = App.RefreshCollectionAsync();
            });
            rows.Add(Row.Caption("COLLECTION"));
            rows.Add(new Row
            {
                Title = "rescan now", Sub = Fmt.Join(Fmt.Plural(App.Lib.Tracks.Count, "song"), Fmt.Plural(App.Lib.Videos.Count, "video"), Fmt.Plural(App.Lib.Books.Count, "book")),
                Click = () => { if (a.EnsureMediaPermission()) _ = App.RefreshCollectionAsync(); }
            });
            }

            if (category == "sync")
            {
            rows.Add(Row.Caption("SYNC WITH PC"));
            rows.Add(new Row
            {
                Title = "sync with pc…", Click = () => a.Push(SyncScreen.Build(a)),
                Sub = !App.Sync.IsPaired ? "copy music, likes and playlists from Phoenix on your PC over Wi-Fi"
                    : Fmt.Join(S.SyncPcName != "" ? S.SyncPcName : S.SyncHost, Fmt.Plural(App.Sync.State.Files.Count, "song") + " on this phone",
                               S.SyncAuto ? "syncs by itself" : "")
            });

            if (App.Sync.IsPaired)
                rows.Add(new Row { Title = "remote control for the pc…", Sub = "play, pause, skip and volume for Phoenix on " + (S.SyncPcName != "" ? S.SyncPcName : S.SyncHost), Click = () => RemoteScreen.Open(a) });

            }

            if (category is "listening" or "storage" or "updates") MoreScreens.SettingsRows(a, rows, page, category);

            if (category == "display")
            {
            rows.Add(Row.Caption("APPEARANCE"));
            rows.Add(new Row
            {
                Title = "theme", Trail = S.Theme == "system" ? "follow the phone" : S.Theme,
                Sub = S.Theme == "system" ? $"light or dark as the phone is set (now {(Th.Dark ? "dark" : "light")})" : "",
                Click = () => Ui.Menu(a, "Theme",
                    ("light", () => { S.Theme = "light"; S.Save(); a.ApplyThemeAndRestart(); }),
                    ("dark", () => { S.Theme = "dark"; S.Save(); a.ApplyThemeAndRestart(); }),
                    ("follow the phone (light or dark as it is set)", () => { S.Theme = "system"; S.Save(); a.ApplyThemeAndRestart(); }))
            });
            rows.Add(new Row
            {
                Title = "size", Trail = S.UiSize switch { 1 => "small", 2 => "normal", 3 => "large", 4 => "larger", _ => "fit this screen" },
                Sub = "how big text, pictures and buttons are",
                Click = () => { S.UiSize = (S.UiSize + 1) % 5; S.Save(); a.ApplyThemeAndRestart(); }
            });
            rows.Add(new Row
            {
                Title = "visualizer", Trail = VisualizerOptions.Styles[Math.Clamp(S.Visualizer, 0, VisualizerOptions.Styles.Length - 1)],
                Sub = $"{VisualizerOptions.Palettes[Math.Clamp(S.VisualizerColor, 0, VisualizerOptions.Palettes.Length - 1)]} colours  ·  {VisualizerOptions.Sensitivities[Math.Clamp(S.VisualizerSensitivity, 0, 3)]} sensitivity  ·  shown on the now playing screen",
                Click = () => VisualizerPicker.Show(a, page.Reload)
            });
            MoreScreens.SettingsRows(a, rows, page, "sections");
            }

            if (category == "playback")
            {
            var engine = HalcyonApp.Engine;
            rows.Add(new Row
            {
                Title = "pick up where I left off", Trail = S.ResumeOnStart ? "on" : "off",
                Sub = "when Phoenix starts again, the queue is put back, paused where it was",
                Click = () => { S.ResumeOnStart = !S.ResumeOnStart; if (!S.ResumeOnStart) S.ResumeTracks = new(); S.Save(); page.Reload(); }
            });
            rows.Add(Row.Caption("EQUALIZER"));
            rows.Add(new Row { Title = "equalizer", Trail = S.EqEnabled ? "on" : "off", Sub = engine.EqTrouble, Click = () => { S.EqEnabled = !S.EqEnabled; S.Save(); engine.ApplyEqualizer(); page.Reload(); } });
            if (S.EqEnabled)
            {
                rows.Add(new Row
                {
                    Title = "preset", Trail = S.EqPreset,
                    Click = () => Ui.Menu(a, "Equalizer", EqPresets.All.Select(x => x.Name).Append("custom")
                        .Select(n => ((n == S.EqPreset ? "●  " : "○  ") + n, (Action)(() => { S.EqPreset = n; S.Save(); engine.ApplyEqualizer(); page.Reload(); }))).ToArray())
                });
                rows.Add(new Row { Title = "adjust the bands…", Sub = "31 Hz to 16 kHz; changing one makes the preset “custom”", Click = () => EqualizerDialog.Show(a, page.Reload) });
            }
            rows.Add(Row.Caption("AUTO EQUALIZER"));
            rows.Add(new Row
            {
                Title = "adjust each song by itself", Trail = S.AutoEq ? "on" : "off",
                Sub = "each song is measured once and the equalizer is set for it as it plays, on top of your own",
                Click = () => { S.AutoEq = !S.AutoEq; S.Save(); engine.ApplyEqualizer(); Halcyon.Services.AutoEq.EnsureMeasured(S, App.Player.Current?.Track); page.Reload(); }
            });
            if (S.AutoEq)
            {
                var profile = Halcyon.Services.AutoEq.Profiles.FirstOrDefault(x => x.Id == S.AutoEqProfile) ?? Halcyon.Services.AutoEq.Profiles[0];
                rows.Add(new Row
                {
                    Title = "sound", Trail = profile.Name, Sub = profile.About,
                    Click = () => Ui.Menu(a, "Sound", Halcyon.Services.AutoEq.Profiles.Select(x => ((x.Id == S.AutoEqProfile ? "●  " : "○  ") + $"{x.Name}: {x.About}",
                        (Action)(() => { S.AutoEqProfile = x.Id; S.Save(); engine.ApplyEqualizer(); page.Reload(); }))).ToArray())
                });
                var store = Halcyon.Services.AutoEq.Stores.FirstOrDefault(x => x.Id == S.AutoEqStore) ?? Halcyon.Services.AutoEq.Stores[0];
                rows.Add(new Row
                {
                    Title = "keep measurements", Sub = store.Name,
                    Click = () => Ui.Menu(a, "Keep measurements", Halcyon.Services.AutoEq.Stores.Select(x => ((x.Id == S.AutoEqStore ? "●  " : "○  ") + x.Name,
                        (Action)(() => { S.AutoEqStore = x.Id; S.Save(); page.Reload(); }))).ToArray())
                });
                rows.Add(new Row
                {
                    Title = _autoEqScan != null ? "stop measuring" : "measure all songs now", Sub = _autoEqStatus,
                    Click = () =>
                    {
                        if (_autoEqScan != null) { _autoEqScan.Cancel(); return; }
                        _autoEqScan = new CancellationTokenSource();
                        var cancel = _autoEqScan.Token;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var (done, measured) = await Halcyon.Services.AutoEq.ScanAsync(App.Lib.Tracks.ToList(), S.AutoEqStore,
                                    (d, total, m) => HalcyonApp.Main.Post(() => { _autoEqStatus = $"measuring {d + 1} of {total}  ·  {m} measured"; page.Reload(); }), cancel);
                                _autoEqStatus = done == 0 ? "every song is measured already" : $"measured {measured} of {done} songs";
                            }
                            catch (OperationCanceledException) { _autoEqStatus = "stopped; the songs measured so far are kept"; }
                            _autoEqScan = null;
                            HalcyonApp.Main.Post(() => { engine.ApplyEqualizer(); page.Reload(); });
                        });
                        page.Reload();
                    }
                });
            }
            if (S.AutoEq)
            {
                var have = Halcyon.Services.AutoEqAi.Have;
                rows.Add(new Row
                {
                    Title = "recognise each song's style with AI", Trail = S.AutoEqAi ? "on" : "off",
                    Sub = !S.AutoEqAi ? "YAMNet tells electronic, hip hop, rock, pop, acoustic, jazz and classical apart, so each gets a balance that suits it"
                        : _aiDownload != null ? _aiStatus
                        : have ? "runs on the phone's AI accelerator where it has one; songs measured before are listened to again"
                        : $"tap to download the model ({Halcyon.Services.AutoEqAi.Megabytes} MB)",
                    Click = () =>
                    {
                        if (S.AutoEqAi && !have && _aiDownload == null)
                        {
                            // the model, downloaded once
                            _aiDownload = new CancellationTokenSource();
                            var cancel = _aiDownload.Token;
                            _ = Task.Run(async () =>
                            {
                                try { await Halcyon.Services.AutoEqAi.DownloadAsync(f => HalcyonApp.Main.Post(() => { _aiStatus = $"downloading the model: {f:P0}"; page.Reload(); }), cancel); }
                                catch (Exception ex) { _aiStatus = "couldn't download the model: " + ex.Message; }
                                _aiDownload = null;
                                HalcyonApp.Main.Post(page.Reload);
                            });
                            page.Reload();
                            return;
                        }
                        S.AutoEqAi = !S.AutoEqAi;
                        S.Save();
                        engine.ApplyEqualizer();
                        page.Reload();
                    }
                });
            }
            rows.Add(Row.Caption("HEADPHONES AND SPEAKERS"));
            rows.Add(new Row
            {
                Title = "correct for my headphones", Trail = S.HeadphoneOn ? "on" : "off",
                Sub = S.HeadphoneName == "" ? "find yours by name in the AutoEq project's measurements" : $"{S.HeadphoneName} (measured by {S.HeadphoneSource})",
                Click = () =>
                {
                    if (S.HeadphoneName == "") { FindHeadphones(); return; }
                    S.HeadphoneOn = !S.HeadphoneOn; S.Save(); engine.ApplyEqualizer(); page.Reload();
                }
            });
            rows.Add(new Row { Title = "find my headphones or speakers…", Sub = "for example: sony wf-1000xm5, hd 600, airpods pro", Click = FindHeadphones });

            void FindHeadphones() => Ui.Ask(a, "Headphones or speakers", "name", "", async q =>
            {
                try
                {
                    var found = await Halcyon.Services.HeadphoneEq.SearchAsync(q);
                    if (found.Count == 0) { Ui.Toast(a, "AutoEq has nothing by that name; try fewer words"); return; }
                    Ui.Menu(a, "Choose yours", found.Select(m => (m.Label, (Action)(async () =>
                    {
                        try
                        {
                            var (gains, _) = await Halcyon.Services.HeadphoneEq.FetchAsync(m);
                            (S.HeadphoneName, S.HeadphoneSource, S.HeadphoneGains, S.HeadphoneOn) = (m.Name, m.Source, gains, true);
                            S.Save();
                            engine.ApplyEqualizer();
                            page.Reload();
                        }
                        catch (Exception ex) { Ui.Toast(a, "couldn't fetch that profile: " + ex.Message); }
                    }))).ToArray());
                }
                catch (Exception ex) { Ui.Toast(a, "couldn't reach AutoEq: " + ex.Message); }
            });

            rows.Add(Row.Caption("VOLUME LEVELLING"));
            rows.Add(new Row
            {
                Title = "even out loudness", Trail = S.Levelling ? "on" : "off",
                Sub = "plays every song at about the same loudness, from the ReplayGain tags in your songs; songs without them play as they are",
                Click = () => { S.Levelling = !S.Levelling; S.Save(); page.Reload(); }
            });
            if (S.Levelling)
                rows.Add(new Row
                {
                    Title = "level whole albums", Trail = S.LevelByAlbum ? "on" : "off",
                    Sub = "keeps an album's loud and quiet songs as they are to each other",
                    Click = () => { S.LevelByAlbum = !S.LevelByAlbum; S.Save(); page.Reload(); }
                });
            rows.Add(Row.Note("These apply from the next song. The equalizer is the phone's own, so how many bands it has depends on the phone; the ten bands here are read off at its bands."));
            }

            if (category == "lyrics")
            {
            rows.Add(Row.Caption("LYRICS"));
            rows.Add(new Row
            {
                Title = "find lyrics by itself", Trail = S.LyricsAuto ? "on" : "off",
                Sub = "when a song has none: sends its title, artist and album to LRCLIB, a free, open lyrics database",
                Click = () => { S.LyricsAuto = !S.LyricsAuto; S.Save(); page.Reload(); }
            });
            rows.Add(Row.Note("In now playing, tap “lyrics” on the picture to show the song's lyrics. They come from a .lrc or .txt file beside the song, its tags, LRCLIB, or the clipboard, and timed lyrics follow the song."));
            }

            if (category == "metadata")
            {
            rows.Add(Row.Caption("DISCOGS"));
            rows.Add(new Row
            {
                Title = "personal access token", Sub = S.DiscogsToken == "" ? "not set  ·  needed for album lookups" : "saved (encrypted on this device)",
                Click = () => Ui.Ask(a, "Discogs token", "paste your personal access token", "", t => { S.DiscogsToken = t.Trim(); Save(); }, true)
            });
            }

            if (category == "jellyfin")
            {
            rows.Add(Row.Caption("JELLYFIN"));
            if (App.Jellyfin.IsConnected)
            {
                rows.Add(new Row { Title = S.JellyfinUrl, Sub = $"signed in as {S.JellyfinUser}  ·  {App.Lib.Tracks.Count(t => t.Source == "jellyfin")} songs, {App.Lib.Videos.Count(v => v.Source == "jellyfin")} videos, {App.Lib.Books.Count(b => b.Source == "jellyfin")} books" });
                rows.Add(new Row { Title = "sync now", Click = () => _ = Sync() });
                rows.Add(new Row
                {
                    Title = "disconnect", Click = () => Ui.Confirm(a, "Disconnect from Jellyfin and remove its items from the collection?", "Disconnect", async () =>
                    {
                        App.Jellyfin.Disconnect();
                        await App.Lib.RemoveSourceAsync("jellyfin");
                        page.Reload();
                    })
                });
            }
            else rows.Add(new Row { Title = "connect…", Sub = "server address, user name and password", Click = ConnectJellyfin });
            }

            if (category == "plex")
            {
            rows.Add(Row.Caption("PLEX"));
            if (App.Plex.IsConnected)
            {
                rows.Add(new Row { Title = S.PlexUrl, Sub = $"{App.Lib.Tracks.Count(t => t.Source == "plex")} songs, {App.Lib.Videos.Count(v => v.Source == "plex")} videos" });
                rows.Add(new Row { Title = "sync now", Click = () => _ = Sync() });
                rows.Add(new Row
                {
                    Title = "disconnect", Click = () => Ui.Confirm(a, "Disconnect from Plex and remove its items from the collection?", "Disconnect", async () =>
                    {
                        S.PlexToken = ""; S.Save();
                        await App.Lib.RemoveSourceAsync("plex");
                        page.Reload();
                    })
                });
            }
            else
            {
                rows.Add(new Row { Title = "sign in with plex…", Sub = "approve Phoenix in your browser", Click = () => _ = PlexSignIn() });
                rows.Add(new Row { Title = "connect with address and token…", Click = PlexManual });
            }
            }

            if (category == "updates")
            {
            rows.Add(Row.Caption("ABOUT"));
            rows.Add(Row.Note($"Phoenix {a.VersionName} for Android. Sign-in tokens are encrypted with a key held in the Android Keystore. " +
                              "Edits to song and video info are kept in Phoenix's collection and survive rescans and server syncs."));
            }
            return rows;
        });
    }
}
