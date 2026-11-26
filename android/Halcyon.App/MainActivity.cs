using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Halcyon.Core;

namespace Halcyon.Droid;

[Activity(Label = "Phoenix", Name = "com.halcyon.player.MainActivity", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public class MainActivity : Activity
{
    /// <summary>The home menu: quickplay, the collection sections that are switched on, then settings.</summary>
    static List<string> Pivots => new[] { "quickplay" }.Concat(Settings.Tabs.Where(App.Settings.TabOn)).Append("settings").ToList();
    const int ReqMedia = 1, ReqAudio = 2, ReqBackup = 11, ReqRestore = 12;

    readonly List<Page> _stack = new();
    string _pivot = "";
    FrameLayout _content = null!;
    LinearLayout _mini = null!;
    View _topBar = null!;
    TextView _status = null!, _miniTitle = null!, _miniSub = null!;
    ImageView _miniArt = null!, _miniPlay = null!, _miniHeart = null!;
    ProgressBar _miniProgress = null!;
    NowPlayingView _now = null!;
    PictureViewer _viewer = null!;
    BookReader _reader = null!;
    string _miniArtFor = "\0";
    bool _resumed, _reloadQueued;
    Action _tick = null!;

    static Player P => App.Player;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Th.Apply(App.Settings.Theme, this);
        SetTheme(Th.Dark ? Android.Resource.Style.ThemeMaterialNoActionBar : Android.Resource.Style.ThemeMaterialLightNoActionBar);
        base.OnCreate(savedInstanceState);
        Window!.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Th.Bg));

        var root = new FrameLayout(this);
        root.SetFitsSystemWindows(true);
        root.SetBackgroundColor(Th.Bg);
        var main = new LinearLayout(this) { Orientation = Orientation.Vertical };

        // brand + status + search
        var brand = Ui.Text(this, "PHOENIX", 12, Th.Accent, Th.Medium);
        brand.LetterSpacing = 0.18f;
        brand.SetPadding(Th.Dp(16), 0, Th.Dp(12), 0);
        _status = Ui.Text(this, "", 12, Th.FgDim);
        var top = Ui.RowOf(this, brand);
        top.AddView(_status, Ui.Weight());
        top.AddView(Ui.Icon(this, IconKind.Search, Th.Fg, 20, () => Push(Screens.Search(this)), 11));
        _topBar = top;
        main.AddView(top, Ui.Fill());

        _content = new FrameLayout(this);
        main.AddView(_content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        main.AddView(BuildMiniPlayer(), Ui.Fill());
        root.AddView(main);

        _now = new NowPlayingView(this);
        root.AddView(_now, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _viewer = new PictureViewer(this);
        root.AddView(_viewer, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _reader = new BookReader(this);
        root.AddView(_reader, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(root);
        RegisterBack();
        // Dark icons on the light theme's status and navigation bars (the decor view exists only after SetContentView).
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            Window.InsetsController?.SetSystemBarsAppearance(Th.Dark ? 0 : (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars),
                (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars));

        App.Lib.Changed += OnLibraryChanged;
        App.Lib.RatingsChanged += OnRatingsChanged;
        App.Lib.TagsChanged += OnLibraryChanged;
        App.Lib.BookmarksChanged += OnLibraryChanged;
        P.CurrentChanged += OnPlayerChanged;
        P.StateChanged += OnPlayerChanged;
        P.QueueChanged += OnLibraryChanged;
        P.Error += OnPlayerError;
        App.Status += OnStatus;
        _tick = Tick;

        Navigate(Intent?.GetStringExtra("pivot") ?? "home");
        OnPlayerChanged();
        if (Intent?.GetBooleanExtra("nowplaying", false) == true) ShowNowPlaying();
        if (EnsureMediaPermission()) _ = App.RefreshCollectionAsync();
        _ = Halcyon.Services.Listening.FlushAsync();   // listens that could not be sent last time
        if (App.Settings.CheckUpdates && Fmt.Now - App.Settings.UpdateChecked > 20 * 3600) _ = CheckForUpdate(false);
    }

    // ---------- updates ----------

    public string VersionName
    {
        get { try { return PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName ?? "1.0.0"; } catch { return "1.0.0"; } }
    }

    /// <summary>Asks GitHub whether there is a newer Phoenix; if so, offers to open its download. Quiet unless asked for by hand.</summary>
    public async Task CheckForUpdate(bool byHand)
    {
        try
        {
            if (byHand) App.SetStatus("asking GitHub…");
            var release = await Halcyon.Services.Updates.CheckAsync(".apk");
            App.Settings.UpdateChecked = Fmt.Now;
            App.SetStatus("");
            var mine = Halcyon.Services.Updates.ParseVersion(VersionName) ?? new Version(1, 0, 0);
            if (release == null) { if (byHand) Ui.Alert(this, "No release has been published yet."); return; }
            if (release.Version <= mine) { if (byHand) Ui.Alert(this, $"You have the newest version ({VersionName})."); return; }
            if (!byHand) { App.SetStatus($"Phoenix {release.VersionText} is available: settings → updates"); return; }
            var where = release.FileUrl != "" ? release.FileUrl : release.Page;
            Ui.Confirm(this, $"Phoenix {release.VersionText} is available (you have {VersionName}).\n\n" +
                             (release.FileUrl != "" ? "Open the download? Android then asks whether to install it." : "It has no Android file attached; open its page?"),
                "Open", () => StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(where))));
        }
        catch (Exception ex) { App.SetStatus(""); if (byHand) Ui.Alert(this, "Couldn't check for updates.\n\n" + ex.Message); }
    }

    // ---------- backup and restore (Android's own file picker says where) ----------

    public void StartBackup()
    {
        var i = new Intent(Intent.ActionCreateDocument);
        i.AddCategory(Intent.CategoryOpenable);
        i.SetType("application/octet-stream");
        i.PutExtra(Intent.ExtraTitle, Halcyon.Services.Backup.SuggestedName);
        try { StartActivityForResult(i, ReqBackup); } catch (Exception ex) { Ui.Alert(this, ex.Message); }
    }

    public void StartRestore()
    {
        var i = new Intent(Intent.ActionOpenDocument);
        i.AddCategory(Intent.CategoryOpenable);
        i.SetType("*/*");
        try { StartActivityForResult(i, ReqRestore); } catch (Exception ex) { Ui.Alert(this, ex.Message); }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is not { } uri) return;
        try
        {
            if (requestCode == ReqBackup)
            {
                using var to = ContentResolver!.OpenOutputStream(uri) ?? throw new Exception("That place can't be written to.");
                using var memory = new MemoryStream();
                Halcyon.Services.Backup.Write(memory);
                memory.Position = 0;
                memory.CopyTo(to);
                Ui.Toast(this, $"Backed up ({Math.Max(1, memory.Length / 1024)} KB).");
            }
            else if (requestCode == ReqRestore)
            {
                using var from = ContentResolver!.OpenInputStream(uri) ?? throw new Exception("That file can't be read.");
                using var memory = new MemoryStream();
                from.CopyTo(memory);
                memory.Position = 0;
                var m = Halcyon.Services.Backup.Stage(memory);
                var when = DateTime.TryParse(m.Created, out var made) ? made.ToString("f") : m.Created;
                new AlertDialog.Builder(this)
                    .SetMessage($"Restore the backup made on {when} ({m.Device})?\n\nIt holds {m.Songs:N0} songs' details and {m.Playlists} playlists. The collection details and settings on this phone are replaced by the ones in the backup, and Phoenix restarts. Your music files are not touched.")!
                    .SetPositiveButton("Restore", (_, _) => Restart())!
                    .SetNegativeButton("Cancel", (_, _) => Halcyon.Services.Backup.Discard())!
                    .SetOnCancelListener(new CancelAction(Halcyon.Services.Backup.Discard))!
                    .Show();
            }
        }
        catch (Exception ex) { Halcyon.Services.Backup.Discard(); Ui.Alert(this, ex.Message); }
    }

    class CancelAction(Action act) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => act();
    }

    /// <summary>Closes Phoenix and starts it again (a restored library is put in place on the way back in).</summary>
    void Restart()
    {
        P.Stop();
        var launch = PackageManager!.GetLaunchIntentForPackage(PackageName!)!;
        launch.AddFlags(ActivityFlags.ClearTask | ActivityFlags.NewTask);
        StartActivity(launch);
        Java.Lang.Runtime.GetRuntime()!.Exit(0);
    }

    protected override void OnDestroy()
    {
        App.Lib.Changed -= OnLibraryChanged;
        App.Lib.RatingsChanged -= OnRatingsChanged;
        App.Lib.TagsChanged -= OnLibraryChanged;
        App.Lib.BookmarksChanged -= OnLibraryChanged;
        P.CurrentChanged -= OnPlayerChanged;
        P.StateChanged -= OnPlayerChanged;
        P.QueueChanged -= OnLibraryChanged;
        P.Error -= OnPlayerError;
        App.Status -= OnStatus;
        HalcyonApp.Engine.SetSurface(null);
        if (_back != null && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) { OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_back); _back = null; }
        base.OnDestroy();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.GetStringExtra("pivot") is { } pivot) Navigate(pivot);
        if (intent?.GetBooleanExtra("nowplaying", false) == true) ShowNowPlaying();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _resumed = true;
        if (_allFilesAsked && HasAllFilesAccess) { _allFilesAsked = false; _ = App.RefreshCollectionAsync(); }
        OnPlayerChanged();
        HalcyonApp.Main.RemoveCallbacks(_tick);
        HalcyonApp.Main.Post(_tick);
    }

    protected override void OnPause()
    {
        _resumed = false;
        HalcyonApp.Main.RemoveCallbacks(_tick);
        P.SaveEpisodePosition();
        _reader.Pause();
        try { App.Settings.Save(); } catch { }
        base.OnPause();
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        _now.SetLandscape(newConfig.Orientation == Android.Content.Res.Orientation.Landscape);
        // the phone switched between light and dark: a theme that follows it is redrawn
        if (App.Settings.Theme == "system" && Th.SystemIsDark(this) != Th.Dark) { Th.Apply("system", this); Recreate(); }
    }

    // ---------- navigation ----------

    Page Root(string pivot) => pivot switch
    {
        "music" => Screens.Music(this),
        "videos" => Screens.Videos(this),
        "pictures" => Screens.Pictures(this),
        "podcasts" => Screens.Podcasts(this),
        "books" => BookScreens.Books(this),
        "playlists" => Screens.Playlists(this),
        "radio" => MoreScreens.Radio(this),
        "settings" => SettingsScreen.Build(this),
        _ => Screens.Quickplay(this)
    };

    /// <summary>The home screen: the sections as one tall column of big lowercase words, like the menu of a Zune player.</summary>
    Page Home()
    {
        var page = new Page(this);
        return page.List(() => Pivots.Select(name => new Row
        {
            Kind = RowKind.Menu, Title = name,
            IsCurrent = () => _pivot == "" || name == _pivot,   // the section last visited stays bright, the rest recede
            Click = () => Navigate(name)
        }).ToList());
    }

    /// <summary>Goes to a section ("music", "videos"…) from the home menu, or back to the menu itself ("home").</summary>
    public void Navigate(string pivot)
    {
        _stack.Clear();
        _content.RemoveAllViews();
        if (Pivots.Contains(pivot)) _pivot = pivot;
        _stack.Add(Home());
        _content.AddView(_stack[0]);
        _topBar.Visibility = ViewStates.Visible;
        if (Pivots.Contains(pivot)) Push(Root(pivot));
    }

    public void Push(Page page)
    {
        if (_stack.Count > 0) _stack[^1].Visibility = ViewStates.Gone;
        _stack.Add(page);
        _content.AddView(page);
        _topBar.Visibility = ViewStates.Gone;
    }

    public void Pop()
    {
        if (_stack.Count <= 1) return;
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        _content.RemoveView(top);
        HideKeyboard();
        var now = _stack[^1];
        now.Visibility = ViewStates.Visible;
        now.Reload();
        if (_stack.Count == 1) _topBar.Visibility = ViewStates.Visible;
    }

    void HideKeyboard()
    {
        var imm = (Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService);
        imm?.HideSoftInputFromWindow(_content.WindowToken, 0);
    }

    /// <summary>What the system back button or gesture does: close the top-most thing, and only leave the app from the home menu.</summary>
    void GoBack()
    {
        if (_reader.IsOpen) _reader.Close();
        else if (_viewer.IsOpen) _viewer.Close();
        else if (_now.IsOpen) HideNowPlaying();
        else if (_stack.Count > 1) Pop();
        else MoveTaskToBack(true);   // keep playing in the background
    }

    // Android 13+ delivers back through this callback; apps built for Android 16 no longer get OnBackPressed at all.
    class BackCallback(MainActivity a) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => a.GoBack();
    }
    BackCallback? _back;

    void RegisterBack()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu || _back != null) return;
        _back = new BackCallback(this);
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, _back);   // 0 = PRIORITY_DEFAULT
    }

#pragma warning disable CS0672, CA1422
    public override void OnBackPressed() => GoBack();   // Android 12 and older
#pragma warning restore CS0672, CA1422

    public void ShowNowPlaying()
    {
        if (P.Current == null) return;
        HideKeyboard();
        _now.SetLandscape(Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape);
        _now.Open();
    }

    public void HideNowPlaying() => _now.Close();

    public void ShowReader(Book b) => _reader.Open(b);

    public void ShowViewer(List<PictureItem> pics, int index, bool slideshow) => _viewer.Open(pics, index, slideshow);

    public void ApplyThemeAndRestart() => Recreate();

    // ---------- mini player ----------

    View BuildMiniPlayer()
    {
        _miniArt = new ImageView(this);
        _miniArt.SetScaleType(ImageView.ScaleType.CenterCrop);
        _miniTitle = Ui.Text(this, "", 15, Th.Fg);
        _miniSub = Ui.Text(this, "", 12, Th.FgDim);
        _miniHeart = Ui.Icon(this, IconKind.Heart, Th.FgFaint, 20, () =>
        {
            if (P.Current?.Track is { } t) App.Lib.Rate(new[] { t }, t.Rating == 0 ? 1 : t.Rating == 1 ? -1 : 0);
        });
        _miniPlay = Ui.Icon(this, IconKind.Play, Th.Fg, 24, P.Toggle);
        var names = Ui.Column(this, _miniTitle, _miniSub);
        names.SetPadding(Th.Dp(12), 0, Th.Dp(4), 0);
        var row = Ui.RowOf(this);
        row.AddView(_miniArt, new LinearLayout.LayoutParams(Th.Dp(46), Th.Dp(46)));
        row.AddView(names, Ui.Weight());
        row.AddView(_miniHeart);
        row.AddView(_miniPlay);
        row.AddView(Ui.Icon(this, IconKind.Next, Th.Fg, 22, () => P.Next()));
        row.SetPadding(Th.Dp(10), Th.Dp(6), Th.Dp(4), Th.Dp(6));
        row.Background = Ui.Ripple();
        row.Clickable = true;
        row.Click += (_, _) => ShowNowPlaying();

        _miniProgress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        _miniProgress.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Th.Accent);
        _mini = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _mini.SetBackgroundColor(Th.BgAlt);
        _mini.AddView(_miniProgress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Th.Dp(2)));
        _mini.AddView(row, Ui.Fill());
        return _mini;
    }

    void UpdateMini()
    {
        var cur = P.Current;
        _mini.Visibility = cur == null ? ViewStates.Gone : ViewStates.Visible;
        if (cur == null) return;
        _miniTitle.Text = cur.Title;
        _miniSub.Text = Fmt.Join(cur.Subtitle, cur.Album);
        _miniPlay.SetImageDrawable(new IconDrawable(P.IsPlaying ? IconKind.Pause : IconKind.Play, Th.Fg));
        if (_miniArtFor != cur.ArtPath) { _miniArtFor = cur.ArtPath; Art.Into(_miniArt, cur.ArtPath, Th.Dp(96)); }
        var t = cur.Track;
        _miniHeart.Visibility = t == null ? ViewStates.Gone : ViewStates.Visible;
        if (t != null)
            _miniHeart.SetImageDrawable(new IconDrawable(t.Rating > 0 ? IconKind.HeartFull : t.Rating < 0 ? IconKind.HeartBroken : IconKind.Heart,
                t.Rating > 0 ? Th.Accent : t.Rating < 0 ? Th.FgDim : Th.FgFaint));
    }

    void Tick()
    {
        if (!_resumed) return;
        if (P.Current != null)
        {
            long pos = P.PositionMs, dur = P.DurationMs;
            _miniProgress.Progress = dur > 0 ? (int)Math.Clamp(pos * 1000 / dur, 0, 1000) : 0;
            _now.Tick();
        }
        HalcyonApp.Main.PostDelayed(_tick, 500);
    }

    // ---------- events from the shared core (all on the main thread) ----------

    void OnPlayerChanged()
    {
        UpdateMini();
        _now.Update();
        if (_stack.Count > 0) _stack[^1].Repaint();
    }

    void OnRatingsChanged()
    {
        UpdateMini();
        _now.Update();
        if (_stack.Count == 0) return;
        if (_stack[^1].ReloadOnRatings) _stack[^1].Reload(); else _stack[^1].Repaint();
    }

    void OnLibraryChanged()
    {
        // A scan reports in batches; fold bursts into one rebuild of the visible screen.
        if (_reloadQueued) return;
        _reloadQueued = true;
        HalcyonApp.Main.PostDelayed(() =>
        {
            _reloadQueued = false;
            if (_stack.Count > 0 && !IsDestroyed) _stack[^1].Reload();
        }, 150);
    }

    void OnPlayerError(string message) { if (message != "") Ui.Toast(this, message); }

    void OnStatus(string text) => HalcyonApp.Main.Post(() => { if (!IsDestroyed) _status.Text = text; });

    // ---------- permissions ----------

    static string[] MediaPermissions => Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
        ? new[] { Android.Manifest.Permission.ReadMediaAudio, Android.Manifest.Permission.ReadMediaVideo, Android.Manifest.Permission.ReadMediaImages, Android.Manifest.Permission.PostNotifications }
        : new[] { Android.Manifest.Permission.ReadExternalStorage };

    /// <summary>True when on-device media can be read; otherwise asks (the scan runs when the answer arrives).</summary>
    public bool EnsureMediaPermission()
    {
        var missing = MediaPermissions.Where(p => CheckSelfPermission(p) != Permission.Granted).ToArray();
        // Notifications are nice to have; only the media permissions gate scanning.
        if (missing.All(p => p == Android.Manifest.Permission.PostNotifications) && _askedMedia) return true;
        if (missing.Length == 0) return true;
        _askedMedia = true;
        RequestPermissions(missing, ReqMedia);
        return false;
    }
    bool _askedMedia, _allFilesAsked;

    /// <summary>EPUB and PDF files are not media, so Android only lets an app read them from shared storage with "all files access".</summary>
    public bool HasAllFilesAccess => Build.VERSION.SdkInt < BuildVersionCodes.R || Android.OS.Environment.IsExternalStorageManager;

    public void AskAllFilesAccess() => Ui.Confirm(this,
        "To read ebooks (EPUB and PDF) from your folders, Android needs you to allow Phoenix \"all files access\". " +
        "Phoenix only reads the folders you add. Audiobooks don't need it.\n\nOpen the setting now?", "Open setting", () =>
        {
            _allFilesAsked = true;
            try { StartActivity(new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + PackageName))); }
            catch { StartActivity(new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission)); }
        });

    public bool HasAudioPermission => CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;

    /// <summary>The visualizer taps the app's own audio output, which Android gates behind the microphone permission.</summary>
    public bool EnsureAudioPermission()
    {
        if (HasAudioPermission) return true;
        RequestPermissions(new[] { Android.Manifest.Permission.RecordAudio }, ReqAudio);
        return false;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == ReqMedia) _ = App.RefreshCollectionAsync();   // scans what it can; servers sync either way
        else if (requestCode == ReqAudio && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            if (App.Settings.Visualizer == 0) App.Settings.Visualizer = 1;
            _now.Update();
            if (_stack.Count > 0) _stack[^1].Reload();
        }
    }
}
