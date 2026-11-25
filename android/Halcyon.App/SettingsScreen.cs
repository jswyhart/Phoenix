using Android.Content;
using Android.Views;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Droid;

/// <summary>Library locations, theme, visualizer, Discogs token, and the Jellyfin and Plex connections.</summary>
public static class SettingsScreen
{
    static Settings S => App.Settings;

    public static Page Build(MainActivity a)
    {
        var page = new Page(a, "settings");
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

            rows.Add(Row.Caption("SYNC WITH PC"));
            rows.Add(new Row
            {
                Title = "sync with pc…", Click = () => a.Push(SyncScreen.Build(a)),
                Sub = !App.Sync.IsPaired ? "copy music, likes and playlists from Phoenix on your PC over Wi-Fi"
                    : Fmt.Join(S.SyncHost, Fmt.Plural(App.Sync.State.Files.Count, "song") + " on this phone")
            });

            if (App.Sync.IsPaired)
                rows.Add(new Row { Title = "remote control for the pc…", Sub = "play, pause, skip and volume for Phoenix on " + S.SyncHost, Click = () => RemoteScreen.Open(a) });

            MoreScreens.SettingsRows(a, rows, page);

            rows.Add(Row.Caption("APPEARANCE"));
            rows.Add(new Row { Title = "theme", Trail = S.Theme, Click = () => { S.Theme = S.Theme == "dark" ? "light" : "dark"; S.Save(); a.ApplyThemeAndRestart(); } });
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

            rows.Add(Row.Caption("DISCOGS"));
            rows.Add(new Row
            {
                Title = "personal access token", Sub = S.DiscogsToken == "" ? "not set  ·  needed for album lookups" : "saved (encrypted on this device)",
                Click = () => Ui.Ask(a, "Discogs token", "paste your personal access token", "", t => { S.DiscogsToken = t.Trim(); Save(); }, true)
            });

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

            rows.Add(Row.Caption("ABOUT"));
            rows.Add(Row.Note("Phoenix 1.0.0 for Android. Sign-in tokens are encrypted with a key held in the Android Keystore. " +
                              "Edits to song and video info are kept in Phoenix's collection and survive rescans and server syncs."));
            return rows;
        });
    }
}
