using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Desktop;

/// <summary>Where the media lives (folders and servers), the look of the app, and the Discogs token.</summary>
public class SettingsView : UserControl
{
    static Settings S => App.Settings;

    readonly Dictionary<string, ListBox> _boxes = new();
    readonly TextBlock _scanStatus = Ui.Note(""), _jfStatus = Ui.Note(""), _plexStatus = Ui.Note(""), _discogsStatus = Ui.Note("");
    readonly TextBox _jfUrl = new() { Watermark = "http://192.168.1.10:8096" }, _jfUser = new() { Watermark = "user name" },
        _jfPass = new() { PasswordChar = '•', Watermark = "password" },
        _plexUrl = new() { Watermark = "http://192.168.1.10:32400" }, _plexToken = new() { PasswordChar = '•', Watermark = "Plex token" },
        _discogs = new() { PasswordChar = '•', Watermark = "personal access token" };
    readonly ComboBox _plexServers = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
    readonly RadioButton _light = new() { Content = "light", GroupName = "theme" }, _dark = new() { Content = "dark", GroupName = "theme" };
    readonly CheckBox _writeTags = new() { Content = "write edited song info into the music files" };
    readonly Button _rescan, _jfConnect, _jfSync, _jfDisconnect, _plexSignIn, _plexConnect, _plexDisconnect;

    public SettingsView()
    {
        _rescan = Ui.Pill("RESCAN COLLECTION NOW", Rescan);
        _jfConnect = Ui.Pill("CONNECT & SYNC", JfConnect);
        _jfSync = Ui.Pill("SYNC NOW", JfSync);
        _jfDisconnect = Ui.Pill("DISCONNECT", JfDisconnect);
        _plexSignIn = Ui.Pill("SIGN IN WITH PLEX…", PlexSignIn);
        _plexConnect = Ui.Pill("CONNECT & SYNC", PlexConnect);
        _plexDisconnect = Ui.Pill("DISCONNECT", PlexDisconnect);

        _light.IsChecked = S.Theme != "dark";
        _dark.IsChecked = S.Theme == "dark";
        _light.Click += (_, _) => SetTheme("light");
        _dark.Click += (_, _) => SetTheme("dark");
        _writeTags.IsChecked = S.WriteTagsToFiles;
        _writeTags.Click += (_, _) => { S.WriteTagsToFiles = _writeTags.IsChecked == true; S.Save(); };
        _jfUrl.Text = S.JellyfinUrl; _jfUser.Text = S.JellyfinUser;
        _plexUrl.Text = S.PlexUrl; _plexToken.Text = S.PlexToken;
        _discogs.Text = S.DiscogsToken;
        _plexServers.SelectionChanged += (_, _) =>
        {
            if (_plexServers.SelectedItem is not PlexServer s) return;
            _plexUrl.Text = s.Uri;
            _plexToken.Text = s.Token;
        };

        // the panels, one per category (laid out like the Windows app: SOFTWARE / SERVERS across the top,
        // categories down the left, the chosen one on the right, OK at the bottom)
        _panels["Collection"] = Ui.Column(
            Ui.H2("collection"),
            Ui.Note("Halcyon watches these folders, and any servers you connect, for music, videos, pictures and books. Everything is checked for changes each time Halcyon starts. Files stay where they are."),
            Folders("music", "MUSIC FOLDERS", 84), Folders("video", "VIDEO FOLDERS", 64), Folders("picture", "PICTURE FOLDERS", 64), Folders("book", "BOOK FOLDERS (AUDIOBOOKS, EBOOKS AND COMICS)", 64),
            Ui.Row(_rescan).With(r => r.Margin = new Thickness(0, 16, 0, 0)), _scanStatus);
        _panels["Metadata"] = Ui.Column(
            Ui.H2("metadata"),
            Ui.Note("Album details, track names and cover art come from the Discogs database. Create a personal access token at discogs.com → Settings → Developers and paste it here."),
            Field("DISCOGS PERSONAL ACCESS TOKEN", _discogs), _writeTags.With(c => c.Margin = new Thickness(0, 4, 0, 4)),
            Ui.Row(Ui.Pill("SAVE TOKEN", SaveDiscogs)).With(r => r.Margin = new Thickness(0, 8, 0, 0)), _discogsStatus);
        _panels["Display"] = Ui.Column(
            Ui.H2("display"),
            Ui.Label("BACKGROUND"), Ui.Row(_light.With(r => r.Margin = new Thickness(0, 0, 18, 0)), _dark));
        _panels["Jellyfin"] = Ui.Column(
            Ui.H2("jellyfin"),
            Ui.Note("Add a Jellyfin server to your collection like a folder: its music, movies, shows, collections, photos and books appear alongside your own and stream from the server."),
            Field("SERVER ADDRESS", _jfUrl),
            Ui.Cells("*,10,*", "Auto", Field("USER NAME", _jfUser), Field("PASSWORD", _jfPass).At(2)),
            Ui.Row(_jfConnect, _jfSync, _jfDisconnect).With(r => r.Margin = new Thickness(0, 10, 0, 0)), _jfStatus);
        _panels["Plex"] = Ui.Column(
            Ui.H2("plex"),
            Ui.Note("Add a Plex Media Server to your collection like a folder. Sign in with Plex to find your servers, or enter an address and token yourself."),
            Ui.Row(_plexSignIn), _plexServers, Field("SERVER ADDRESS", _plexUrl), Field("TOKEN", _plexToken),
            Ui.Row(_plexConnect, _plexDisconnect).With(r => r.Margin = new Thickness(0, 10, 0, 0)), _plexStatus);

        var (pivots, selectPivot, _) = Ui.Tabs("settingsPivot", new[] { ("software", "SOFTWARE"), ("servers", "SERVERS") }, p => ShowCategory(p));
        foreach (var rb in pivots.Children.OfType<RadioButton>()) { rb.Classes.Remove("tab"); rb.Classes.Add("pivot"); }
        _selectPivot = selectPivot;
        pivots.Margin = new Thickness(0, 0, 0, 18);
        _categories.SelectionChanged += (_, _) => ShowPanel();
        _categories.Margin = new Thickness(-8, 0, 24, 0);
        var page = new Grid { MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 18, 10) };
        foreach (var panel in _panels.Values) page.Children.Add(panel);
        _scroll.Content = page;
        var ok = Ui.Pill("OK", () => HalcyonApp.Win.GoBackOr("quickplay")).With(b => { b.MinWidth = 96; b.Margin = default; });
        ToolTip.SetTip(ok, "Changes are saved as you make them");
        Content = Ui.Cells("230,*", "Auto,*,Auto",
            pivots.At(0, 0, 2), _categories.At(0, 1), _scroll.At(1, 1),
            Ui.Row(ok).With(r => { r.HorizontalAlignment = HorizontalAlignment.Right; r.Margin = new Thickness(0, 10, 0, 0); }).At(0, 2, 2))
            .With(g => g.Margin = new Thickness(28, 6, 28, 14));
        ShowCategory("software");
        App.Lib.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshStatus);
        RefreshFolders();
        RefreshStatus();
    }

    static readonly Dictionary<string, string[]> Groups = new()
    {
        ["software"] = new[] { "Collection", "Metadata", "Display" },
        ["servers"] = new[] { "Jellyfin", "Plex" }
    };
    readonly Dictionary<string, Control> _panels = new();
    readonly ListBox _categories = new() { ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((name, _) => new TextBlock { Text = name, FontSize = 15, Height = 34, Padding = new Thickness(0, 7, 0, 0) }) };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    Action<string> _selectPivot = _ => { };
    string _pivot = "software";

    /// <summary>Shows one of the groups and, optionally, a category inside it.</summary>
    public void ShowCategory(string pivot, string? category = null)
    {
        _pivot = Groups.ContainsKey(pivot) ? pivot : "software";
        _selectPivot(_pivot);
        var names = Groups[_pivot];
        _categories.ItemsSource = names;
        _categories.SelectedIndex = category == null ? 0 : Math.Max(0, Array.FindIndex(names, n => string.Equals(n, category, StringComparison.OrdinalIgnoreCase)));
        ShowPanel();
    }

    void ShowPanel()
    {
        if (_categories.SelectedItem is not string chosen) return;
        foreach (var (name, panel) in _panels) panel.IsVisible = name == chosen;
        _scroll.Offset = default;
    }

    /// <summary>Which category is on screen (for the self-test).</summary>
    public string Shown => _categories.SelectedItem as string ?? "";

    static Control Field(string label, Control input)
    {
        input.Margin = new Thickness(0, 2, 0, 8);
        return Ui.Column(Ui.Label(label), input);
    }

    // ---------- folders ----------

    static List<string> FolderList(string kind) => kind switch { "music" => S.MusicFolders, "video" => S.VideoFolders, "book" => S.BookFolders, _ => S.PictureFolders };

    Control Folders(string kind, string caption, double height)
    {
        var box = new ListBox { Height = height, ItemTemplate = Ui.Template("PathRow") };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        box.Classes.Add("boxed");
        _boxes[kind] = box;
        return Ui.Column(Ui.Cap(caption).With(c => c.Margin = new Thickness(0, 14, 0, 4)), box,
            Ui.Row(Ui.Pill("ADD FOLDER", () => AddFolder(kind)), Ui.Pill("REMOVE", () => RemoveFolder(kind))).With(r => r.Margin = new Thickness(0, 6, 0, 0)));
    }

    void RefreshFolders()
    {
        foreach (var (kind, box) in _boxes) box.ItemsSource = FolderList(kind).ToList();
    }

    async void AddFolder(string kind)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Choose a {kind} folder", AllowMultiple = true });
        var list = FolderList(kind);
        foreach (var f in picked)
        {
            var path = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        }
        FoldersChanged(kind);
    }

    void RemoveFolder(string kind)
    {
        if (_boxes[kind].SelectedItem is not string f) return;
        FolderList(kind).Remove(f);
        FoldersChanged(kind);
    }

    void FoldersChanged(string kind)
    {
        S.Save();
        RefreshFolders();
        if (kind == "picture") _ = HalcyonApp.Win.Page<PicturesView>("pictures").ReloadAsync();
        else Rescan();
    }

    async void Rescan()
    {
        _rescan.IsEnabled = false;
        _scanStatus.Text = "scanning…";
        await App.RefreshCollectionAsync();
        RefreshStatus();
        _scanStatus.Text = $"{App.Lib.Tracks.Count} songs and {App.Lib.Videos.Count} videos in your collection";
        _rescan.IsEnabled = true;
    }

    void SetTheme(string name)
    {
        S.Theme = name;
        S.Save();
        HalcyonApp.ApplyTheme(name);
    }

    void RefreshStatus()
    {
        var jf = App.Lib.Tracks.Count(t => t.Source == "jellyfin");
        var jfv = App.Lib.Videos.Count(v => v.Source == "jellyfin");
        _jfSync.IsEnabled = _jfDisconnect.IsEnabled = App.Jellyfin.IsConnected;
        if (_jfStatus.Text is null or "" || _jfStatus.Text.StartsWith("connected") || _jfStatus.Text.StartsWith("not connected"))
            _jfStatus.Text = App.Jellyfin.IsConnected ? $"connected as {S.JellyfinUser}  ·  {jf} songs, {jfv} videos" : "not connected";
        var px = App.Lib.Tracks.Count(t => t.Source == "plex");
        var pxv = App.Lib.Videos.Count(v => v.Source == "plex");
        _plexDisconnect.IsEnabled = App.Plex.IsConnected;
        if (_plexStatus.Text is null or "" || _plexStatus.Text.StartsWith("connected") || _plexStatus.Text.StartsWith("not connected"))
            _plexStatus.Text = App.Plex.IsConnected ? $"connected  ·  {px} songs, {pxv} videos" : "not connected";
    }

    // ---------- discogs ----------

    void SaveDiscogs()
    {
        S.DiscogsToken = (_discogs.Text ?? "").Trim();
        S.Save();
        _discogsStatus.Text = S.DiscogsToken == "" ? "token removed" : "token saved — right-click any album and choose “Find album info on Discogs”";
    }

    // ---------- jellyfin ----------

    async void JfConnect()
    {
        if ((_jfUrl.Text ?? "").Trim() == "" || (_jfUser.Text ?? "").Trim() == "") { _jfStatus.Text = "enter the server address and user name"; return; }
        _jfConnect.IsEnabled = false;
        try
        {
            _jfStatus.Text = "signing in…";
            await App.Jellyfin.LoginAsync(_jfUrl.Text!, _jfUser.Text!.Trim(), _jfPass.Text ?? "");
            _jfPass.Text = "";
            _jfUrl.Text = S.JellyfinUrl;
            await SyncJellyfin();
        }
        catch (Exception ex) { _jfStatus.Text = "couldn't connect: " + ex.Message; }
        finally { _jfConnect.IsEnabled = true; RefreshStatus(); }
    }

    async void JfSync()
    {
        try { await SyncJellyfin(); }
        catch (Exception ex) { _jfStatus.Text = "sync failed: " + ex.Message; }
        RefreshStatus();
    }

    async Task SyncJellyfin()
    {
        _jfSync.IsEnabled = false;
        var (tracks, videos) = await App.Jellyfin.FetchAsync(s => _jfStatus.Text = s);
        _jfStatus.Text = "saving…";
        await App.Lib.ReplaceSourceAsync("jellyfin", tracks, videos);
        await App.Lib.ReplaceBooksAsync("jellyfin", await App.Jellyfin.FetchBooksAsync(s => _jfStatus.Text = s));
        _jfStatus.Text = "";
    }

    async void JfDisconnect()
    {
        App.Jellyfin.Disconnect();
        await App.Lib.RemoveSourceAsync("jellyfin");
        _jfStatus.Text = "";
        RefreshStatus();
    }

    // ---------- plex ----------

    async void PlexSignIn()
    {
        _plexSignIn.IsEnabled = false;
        try
        {
            var (id, _, url) = await App.Plex.CreatePinAsync();
            Platform.OpenUrl(url);
            _plexStatus.Text = "approve Halcyon in the browser window that just opened…";
            var token = "";
            for (var i = 0; i < 90 && token == ""; i++)
            {
                await Task.Delay(2000);
                token = await App.Plex.CheckPinAsync(id);
            }
            if (token == "") { _plexStatus.Text = "sign-in timed out — try again"; return; }
            var servers = await App.Plex.GetServersAsync(token);
            if (servers.Count == 0) { _plexStatus.Text = "signed in, but no Plex Media Server was found on this account"; return; }
            _plexServers.ItemsSource = servers;
            _plexServers.IsVisible = true;
            _plexServers.SelectedIndex = 0;
            _plexStatus.Text = "signed in — pick a server connection, then choose “connect & sync”";
        }
        catch (Exception ex) { _plexStatus.Text = "sign-in failed: " + ex.Message; }
        finally { _plexSignIn.IsEnabled = true; }
    }

    async void PlexConnect()
    {
        if ((_plexUrl.Text ?? "").Trim() == "" || (_plexToken.Text ?? "").Trim() == "") { _plexStatus.Text = "enter the server address and token, or sign in with Plex"; return; }
        _plexConnect.IsEnabled = false;
        try
        {
            S.PlexUrl = JellyfinClient.NormalizeUrl(_plexUrl.Text!);
            S.PlexToken = _plexToken.Text!.Trim();
            _plexUrl.Text = S.PlexUrl;
            _plexStatus.Text = "connecting…";
            await App.Plex.TestAsync();
            S.Save();
            var (tracks, videos) = await App.Plex.FetchAsync(s => _plexStatus.Text = s);
            _plexStatus.Text = "saving…";
            await App.Lib.ReplaceSourceAsync("plex", tracks, videos);
            _plexStatus.Text = "";
        }
        catch (Exception ex) { _plexStatus.Text = "couldn't connect: " + ex.Message; }
        finally { _plexConnect.IsEnabled = true; RefreshStatus(); }
    }

    async void PlexDisconnect()
    {
        S.PlexToken = "";
        S.Save();
        _plexToken.Text = "";
        await App.Lib.RemoveSourceAsync("plex");
        _plexStatus.Text = "";
        RefreshStatus();
    }
}
