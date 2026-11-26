using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

public partial class MainWindow : Window
{
    static readonly string[] CollectionPages = { "music", "videos", "pictures", "podcasts", "books", "playlists", "radio", "disc", "phone", "mixview" };
    readonly Dictionary<string, FrameworkElement> _pages = new();
    readonly Stack<string> _back = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Smtc _smtc = new();
    string _page = "";
    string _collPage = Settings.Tabs.FirstOrDefault(t => App.Settings.TabOn(t)) ?? "music";
    bool _dragging, _settingSeek, _full;
    WindowState _stateBeforeFull;
    MiniWindow? _mini;

    static Player P => App.Player;
    public bool IsNowOpen => NowLayer.Visibility == Visibility.Visible;
    public string CurrentPage => _page;

    public MainWindow()
    {
        InitializeComponent();
        NowColours();
        Themes.Changed += NowColours;
        P.Attach(Media);
        P.CurrentChanged += OnCurrentChanged;
        P.StateChanged += RefreshTransport;
        P.Error += SetStatus;
        QueueList.ItemsSource = P.Queue;
        Volume.Value = App.Settings.Volume;

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };
        // In full screen the controls and pointer get out of the way until the mouse moves again.
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (!_full || Transport.IsMouseOver) return;
            Transport.Visibility = BackNowBtn.Visibility = VizBtn.Visibility = FullBtn.Visibility = Visibility.Collapsed;
            Cursor = Cursors.None;
        };
        MouseMove += (_, e) =>
        {
            if (!_full) return;
            var pos = e.GetPosition(this);
            if (pos == _lastMouse) return;      // WPF also raises MouseMove when the layout changes under a still pointer
            _lastMouse = pos;
            ShowFullscreenControls();
        };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized && !_full ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        SourceInitialized += (_, _) => _smtc.Init(new WindowInteropHelper(this).Handle);
        Closing += (_, e) =>
        {
            // run on in the notification area, if that is switched on: the window only hides
            if (Tray.HideInstead()) { e.Cancel = true; return; }
            Tray.Dispose();
            P.SaveEpisodePosition();
            P.StopCast(resumeHere: false);
            App.Settings.Shuffle = P.Shuffle;
            App.Settings.Repeat = P.Repeat;
        };

        BuildTaskbarButtons();
        Tray.Sync();
        ShowTabs();
        ShowWebTabs();
        WebServices.Changed += () => Dispatcher.BeginInvoke(ShowWebTabs);
        ShowQueueTab(App.Settings.ShowLyrics, App.Settings.ShowGenerated);
        ApplyNowPanel();
        ApplyBackground();
        Navigate("quickplay");
        RefreshTransport();
    }

    // ---------- taskbar: previous / play / next on the window's thumbnail ----------

    System.Windows.Shell.ThumbButtonInfo? _thumbPlay;
    ImageSource? _thumbPlayIcon, _thumbPauseIcon;

    ImageSource GlyphIcon(string glyph)
    {
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("Icons"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16, Brushes.White, 1.0);
        var drawing = new GeometryDrawing(Brushes.White, null, text.BuildGeometry(new Point(0, 0)));
        // a fixed square frame so every glyph sits centred at the same size
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(-2, -2, 20, 20))));
        group.Children.Add(drawing);
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    void BuildTaskbarButtons()
    {
        try
        {
            _thumbPlayIcon = GlyphIcon("\uE768");
            _thumbPauseIcon = GlyphIcon("\uE769");
            var prev = new System.Windows.Shell.ThumbButtonInfo { ImageSource = GlyphIcon("\uE892"), Description = "Previous" };
            _thumbPlay = new System.Windows.Shell.ThumbButtonInfo { ImageSource = _thumbPlayIcon, Description = "Play" };
            var next = new System.Windows.Shell.ThumbButtonInfo { ImageSource = GlyphIcon("\uE893"), Description = "Next" };
            prev.Click += (_, _) => P.Previous();
            _thumbPlay.Click += (_, _) => P.Toggle();
            next.Click += (_, _) => P.Next();
            Taskbar.ThumbButtonInfos = new System.Windows.Shell.ThumbButtonInfoCollection { prev, _thumbPlay, next };
        }
        catch { }
    }

    public int TaskbarButtonCount => Taskbar.ThumbButtonInfos?.Count ?? 0;

    // ---------- now playing: the queue or the lyrics ----------

    Lyrics? _lyrics;
    PlayItem? _lyricsFor;
    int _lyricLine = -1;

    void QueueTab_Click(object sender, RoutedEventArgs e) => ShowQueueTab(false);
    void LyricsTab_Click(object sender, RoutedEventArgs e) => ShowQueueTab(true);
    void GeneratedTab_Click(object sender, RoutedEventArgs e) => ShowQueueTab(true, generated: true);

    /// <summary>Now playing's side pane: the queue, the song's lyrics, or the lyrics the speech model wrote.</summary>
    public void ShowQueueTab(bool lyrics, bool generated = false)
    {
        var canGenerate = SpeechModel != null;
        generated &= lyrics && canGenerate;
        App.Settings.ShowLyrics = lyrics;
        App.Settings.ShowGenerated = generated;
        GeneratedTab.Visibility = canGenerate ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = lyrics ? Visibility.Collapsed : Visibility.Visible;
        LyricsPane.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
        QueueTabText.Opacity = lyrics ? 0.4 : 1;
        LyricsTabText.Opacity = lyrics && !generated ? 1 : 0.4;
        GeneratedTabText.Opacity = generated ? 1 : 0.4;
        _lyricsFor = null;
        LoadLyrics();
    }

    bool ShowingGenerated => App.Settings.ShowLyrics && App.Settings.ShowGenerated;

    /// <summary>The picture of your own behind the window (settings → display), or none.</summary>
    public void ApplyBackground()
    {
        var file = App.Settings.BackgroundPicture;
        if (file == "" || !System.IO.File.Exists(file)) { BackPicture.Source = null; BackPicture.Visibility = Visibility.Collapsed; return; }
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // the file can be replaced while shown
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 2560;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            BackPicture.Source = bmp;
            BackPicture.Opacity = Math.Clamp(App.Settings.BackgroundStrength, 5, 90) / 100.0;
            BackPicture.Visibility = Visibility.Visible;
        }
        catch { BackPicture.Source = null; BackPicture.Visibility = Visibility.Collapsed; }
    }

    public bool BarNowShown => BarNow.Visibility == Visibility.Visible;

    /// <summary>A panel behind the queue and lyrics (settings → display), so they read clearly over the visualizer.</summary>
    public void ApplyNowPanel()
    {
        if (App.Settings.NowPanel) QueueBack.SetResourceReference(Border.BackgroundProperty, "NowPanelBg");
        else QueueBack.Background = null;
    }

    public Lyrics? ShownLyrics => _lyrics;
    public int ShownLyricLine => _lyricLine;

    void LoadLyrics()
    {
        if (!App.Settings.ShowLyrics || !IsNowOpen) return;
        var cur = P.Current;
        if (ReferenceEquals(cur, _lyricsFor) && cur != null) return;
        _lyricsFor = cur;
        _lyricLine = -1;
        var gen = ShowingGenerated;
        var track = cur?.Track;
        var listeningHere = _listening != null && _listeningTo == track && track != null;
        _lyrics = listeningHere && gen ? _heard : gen ? Lyrics.Generated(track) : Lyrics.For(track);
        LyricsList.ItemsSource = _lyrics?.Lines;
        LyricsEmpty.Visibility = _lyrics == null ? Visibility.Visible : Visibility.Collapsed;
        LyricsEmpty.Text = cur == null ? "" : track == null ? "Lyrics are shown for songs."
            : gen ? "The speech model hasn't written lyrics for this song yet.\n\nChoose “listen” below and it writes down what is sung, line by line, as it hears it."
            : "No lyrics for this song yet.\n\nUse “find online”, “import file” or “paste” below, put a .lrc or .txt file with the same name beside the song, or add lyrics to its tags.";
        LyricsNote.Text = listeningHere && gen ? "the speech model is listening…" : _lyrics == null ? "" : _lyrics.Synced ? _lyrics.Source + "   ·   double-click a line to jump to it" : _lyrics.Source;
        // each tab has its own buttons
        LyricsFindBtn.Visibility = LyricsImportBtn.Visibility = LyricsPasteBtn.Visibility = gen ? Visibility.Collapsed : Visibility.Visible;
        LyricsListenBtn.Visibility = gen ? Visibility.Visible : Visibility.Collapsed;
        LyricsFindBtn.IsEnabled = LyricsImportBtn.IsEnabled = LyricsPasteBtn.IsEnabled = track != null;
        LyricsListenBtn.IsEnabled = track != null && SpeechModel != null;
        LyricsListenBtn.Content = listeningHere ? "stop listening" : track != null && Lyrics.HasGenerated(track) ? "listen again" : "listen";
        LyricsRemoveBtn.Visibility = track != null && !listeningHere && (gen ? Lyrics.HasGenerated(track) : Lyrics.HasStored(track)) ? Visibility.Visible : Visibility.Collapsed;
        LyricsRemoveBtn.ToolTip = gen ? "Remove what the speech model wrote for this song" : "Remove the lyrics you imported for this song";
        if (_lyrics is { Lines.Count: > 0 }) LyricsList.ScrollIntoView(_lyrics.Lines[0]);
        // look it up by itself, if that was asked for in settings (once per song per session)
        if (track != null && _lyricsAsked.Add(track.Id) && Lyrics.For(track) == null)
        {
            if (App.Settings.LyricsAuto) _ = FindThenListenAsync(track);
            else if (App.Settings.LyricsListen && SpeechModel != null && !Lyrics.HasGenerated(track)) _ = ListenAsync(track, quiet: true);
        }
    }

    public void RefreshLyrics() => ShowQueueTab(App.Settings.ShowLyrics, App.Settings.ShowGenerated);

    async Task FindThenListenAsync(Track track)
    {
        await FindLyricsAsync(track, quiet: true);
        if (App.Settings.LyricsListen && SpeechModel != null && Lyrics.For(track) == null && !Lyrics.HasGenerated(track) && P.Current?.Track == track) await ListenAsync(track, quiet: true);
    }

    Lyrics? _heard;   // what the speech model has written so far, while it listens

    /// <summary>The speech model chosen in settings, when it has been downloaded.</summary>
    static Halcyon.Services.SpeechLyrics.Model? SpeechModel =>
        Halcyon.Services.SpeechLyrics.Find(App.Settings.LyricsModel) is { } m && Halcyon.Services.SpeechLyrics.Have(m) ? m : null;

    CancellationTokenSource? _listening;
    Track? _listeningTo;

    /// <summary>The speech model listens to the song and writes its lyrics; lines show as they are written.</summary>
    async Task ListenAsync(Track track, bool quiet)
    {
        if (SpeechModel is not { } model) { LyricsNote.Text = "download a speech model in settings → translation first"; return; }
        _listening?.Cancel();
        var cancel = new CancellationTokenSource();
        _listening = cancel;
        _listeningTo = track;
        var lines = new List<string>();
        _heard = null;
        void Show()
        {
            _heard = Lyrics.Parse(string.Join("\n", lines), "the speech model is listening…");
            if (P.Current?.Track != track || !ShowingGenerated || _heard == null) return;
            _lyrics = _heard;
            _lyricLine = -1;
            LyricsList.ItemsSource = _heard.Lines;
            LyricsEmpty.Visibility = Visibility.Collapsed;
        }
        if (ShowingGenerated) ReloadLyrics();
        try
        {
            var source = track.IsLocal ? track.Location : Player.From(track).Url;
            var lrc = await Halcyon.Services.SpeechLyrics.TranscribeAsync(source, model, (ms, text) => Dispatcher.BeginInvoke(() =>
            {
                lines.Add($"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000 / 10:00}]{text}");
                Show();
            }), cancel.Token, track.Title, track.Artist);
            Finish();
            if (lrc == null) { if (P.Current?.Track == track && ShowingGenerated) LyricsNote.Text = "the speech model heard no words in this song"; return; }
            Lyrics.SaveGenerated(track, lrc);
            if (P.Current?.Track == track) ReloadLyrics();
        }
        catch (OperationCanceledException) { Finish(); if (P.Current?.Track == track) ReloadLyrics(); }
        catch (Exception ex) { Finish(); if (P.Current?.Track == track && (ShowingGenerated || !quiet)) { ReloadLyrics(); LyricsNote.Text = "the speech model couldn't listen: " + ex.Message; } }

        void Finish()
        {
            if (_listening != cancel) return;
            _listening = null;
            _listeningTo = null;
            _heard = null;
        }
    }

    async void LyricsListen_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current?.Track is not { } track) return;
        if (_listening != null && _listeningTo == track) { _listening.Cancel(); return; }
        await ListenAsync(track, quiet: false);
    }

    readonly HashSet<long> _lyricsAsked = new();

    void ReloadLyrics() { _lyricsFor = null; LoadLyrics(); }

    /// <summary>Stores lyrics for the song that is playing and shows them.</summary>
    public void ImportLyrics(Track track, string text)
    {
        if (Lyrics.Parse(text, "imported") == null) { LyricsNote.Text = "that does not look like lyrics"; return; }
        Lyrics.Save(track, text);
        if (P.Current?.Track == track) ReloadLyrics();
    }

    async Task FindLyricsAsync(Track track, bool quiet)
    {
        if (!quiet) LyricsNote.Text = "looking on LRCLIB…";
        try
        {
            var text = await Halcyon.Services.LyricsFinder.FindAsync(track);
            if (text == null) { if (!quiet && P.Current?.Track == track) LyricsNote.Text = "LRCLIB has no lyrics for this song"; return; }
            ImportLyrics(track, text);
        }
        catch (Exception ex) { if (!quiet) LyricsNote.Text = "couldn't reach LRCLIB: " + ex.Message; }
    }

    async void LyricsFind_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current?.Track is { } track) await FindLyricsAsync(track, quiet: false);
    }

    void LyricsImport_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current?.Track is not { } track) return;
        var pick = new Microsoft.Win32.OpenFileDialog { Title = $"Lyrics for “{track.Title}”", Filter = "Lyrics (*.lrc;*.txt)|*.lrc;*.txt|All files|*.*" };
        if (pick.ShowDialog(this) != true) return;
        try { ImportLyrics(track, System.IO.File.ReadAllText(pick.FileName)); }
        catch (Exception ex) { LyricsNote.Text = ex.Message; }
    }

    void LyricsPaste_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current?.Track is not { } track) return;
        var text = "";
        try { text = Clipboard.GetText(); } catch { }
        if (string.IsNullOrWhiteSpace(text)) { LyricsNote.Text = "the clipboard has no text"; return; }
        ImportLyrics(track, text);
    }

    void LyricsRemove_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current?.Track is not { } track) return;
        if (ShowingGenerated) Lyrics.RemoveGenerated(track); else Lyrics.Remove(track);
        ReloadLyrics();
    }

    void FollowLyrics(double positionMs)
    {
        if (_lyrics is not { Synced: true } ly || LyricsPane.Visibility != Visibility.Visible || !IsNowOpen) return;
        var i = ly.LineAt((long)positionMs + 200);
        if (i == _lyricLine) return;
        if (_lyricLine >= 0 && _lyricLine < ly.Lines.Count) ly.Lines[_lyricLine].IsNow = false;
        _lyricLine = i;
        if (i < 0) return;
        ly.Lines[i].IsNow = true;
        // keep a few lines of what comes next in view below the current one
        LyricsList.ScrollIntoView(ly.Lines[Math.Min(ly.Lines.Count - 1, i + 4)]);
        LyricsList.ScrollIntoView(ly.Lines[Math.Max(0, i - 1)]);
        LyricsList.ScrollIntoView(ly.Lines[i]);
    }

    void Lyrics_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LyricLine { Ms: >= 0 } line) P.Seek(TimeSpan.FromMilliseconds(line.Ms));
        e.Handled = true;
    }

    // ---------- navigation ----------

    FrameworkElement Create(string page) => page switch
    {
        "quickplay" => new QuickplayView(),
        "music" => new MusicView(),
        "videos" => new VideosView(),
        "pictures" => new PicturesView(),
        "podcasts" => new PodcastsView(),
        "books" => new BooksView(),
        "playlists" => new PlaylistsView(),
        "disc" => new DiscView(),
        "phone" => new PhoneView(),
        "search" => new SearchView(),
        "settings" => new SettingsView(),
        "stats" => new StatsView(),
        "radio" => new RadioView(),
        "care" => new CareView(),
        "mixview" => new MixviewPage(),
        _ when page.StartsWith("web:") && WebServices.Find(page[4..]) is { } service => HookWeb(new WebServiceView(service)),
        _ when page.StartsWith("tab:") => new CustomTabView(page),
        _ => throw new ArgumentException(page)
    };

    public T Page<T>(string page) where T : FrameworkElement =>
        (T)(_pages.TryGetValue(page, out var v) ? v : _pages[page] = Create(page));

    public void Navigate(string page, bool push = true)
    {
        if (IsNowOpen) ShowNow(false);
        if (page == _page) return;
        if (push && _page != "") _back.Push(_page);
        _page = page;
        var web = page.StartsWith("web:") ? WebServices.Find(page[4..]) : null;
        if (page.StartsWith("web:") && (web == null || !WebServices.IsOn(web))) { page = "music"; web = null; }
        if (Settings.Tabs.Contains(page) && !App.Settings.TabOn(page)) page = FirstTab;   // a tab that is switched off
        if (page.StartsWith("tab:") && App.Settings.CustomTabs.All(t => t.Page != page)) page = FirstTab;   // a tab of your own that was removed
        _page = page;
        var isColl = CollectionPages.Contains(page) || web != null || page.StartsWith("tab:");
        if (isColl) _collPage = page;
        Host.Content = Page<FrameworkElement>(page);

        NavQuick.IsChecked = page is "quickplay" or "stats";
        NavColl.IsChecked = isColl || page == "search";
        NavSettings.IsChecked = page is "settings" or "care";
        SubNav.Visibility = isColl ? Visibility.Visible : Visibility.Collapsed;
        // a service shown under a collection lights that collection's tab; one with a tab of its own lights its own
        var lit = web != null && WebServices.PlaceOf(web) is var place && place != WebServices.OwnTab ? place : page;
        foreach (RadioButton rb in SubNav.Children) rb.IsChecked = (string)rb.Tag == lit;
        BackBtn.IsEnabled = _back.Count > 0;
    }

    void NavBig_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        Navigate(tag == "collection" ? (Settings.Tabs.Contains(_collPage) && !App.Settings.TabOn(_collPage) ? FirstTab : _collPage) : tag);
    }

    void Logo_Click(object sender, RoutedEventArgs e) => Navigate("quickplay");

    void NavSub_Click(object sender, RoutedEventArgs e) => Navigate((string)((FrameworkElement)sender).Tag);

    // ---------- the phone / disc / playlist icons ----------

    static string TargetOf(object sender) => ((FrameworkElement)sender).Name switch { "PhoneTarget" => "phone", "DiscTarget" => "disc", _ => "playlist" };

    void DropTarget_Click(object sender, RoutedEventArgs e)
    {
        switch (TargetOf(sender))
        {
            case "phone": Navigate("phone"); break;
            case "disc": Navigate("disc"); break;
            default: Navigate("playlists"); break;
        }
    }

    void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (TrackDrag.Tracks(e.Data) is not { } tracks) return;
        ((Button)sender).Tag = "over";
        DropHint.Text = TargetOf(sender) switch
        {
            "phone" => $"sync {Songs(tracks.Count)} with your phone",
            "disc" => $"add {Songs(tracks.Count)} to the burn list",
            _ => $"add {Songs(tracks.Count)} to a playlist"
        };
    }

    static string Songs(int n) => n == 1 ? "1 song" : $"{n} songs";

    void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        ((Button)sender).Tag = null;
        DropHint.Text = "";
    }

    void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TrackDrag.Tracks(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void DropTarget_Drop(object sender, DragEventArgs e)
    {
        ((Button)sender).Tag = null;
        DropHint.Text = "";
        if (TrackDrag.Tracks(e.Data) is { } tracks) DropOn(TargetOf(sender), tracks, (Button)sender);
        e.Handled = true;
    }

    /// <summary>What dropping songs on one of the three icons does.</summary>
    public void DropOn(string target, List<Track> tracks, UIElement? anchor = null)
    {
        switch (target)
        {
            case "phone":
                var added = PhoneView.Add(tracks);
                var skipped = tracks.Count(t => t.Source != "local");
                SetStatus(added == 0 && skipped > 0 ? "only music stored on this PC can be copied to the phone"
                    : added == 0 ? "already on the phone sync list"
                    : $"{Songs(added)} added to the phone sync list" + (skipped > 0 ? $" ({skipped} on a server left out)" : ""));
                break;
            case "disc":
                foreach (var t in tracks) App.BurnList.Add(t);
                SetStatus($"{App.BurnList.Count} songs on the burn list");
                break;
            default:
                // choose which playlist, as the Zune software asks when there is more than one
                var m = new ContextMenu();
                m.Items.Add(Menus.Item("New playlist…", () =>
                {
                    var name = InputDialog.Ask("New playlist", "Playlist name", "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    App.Lib.AddToPlaylist(App.Lib.CreatePlaylist(name.Trim()), tracks);
                    SetStatus($"{Songs(tracks.Count)} added to “{name.Trim()}”");
                }));
                if (App.Lib.Playlists.Count > 0) m.Items.Add(new Separator());
                foreach (var p in App.Lib.Playlists)
                {
                    var playlist = p;
                    m.Items.Add(Menus.Item(p.Name, () => { App.Lib.AddToPlaylist(playlist, tracks); SetStatus($"{Songs(tracks.Count)} added to “{playlist.Name}”"); }));
                }
                Menus.Open(m, anchor ?? PlaylistTarget);
                break;
        }
    }

    /// <summary>Goes back a page, or to <paramref name="fallback"/> when there is nowhere to go back to.</summary>
    public void GoBackOr(string fallback)
    {
        if (_back.Count > 0) Navigate(_back.Pop(), push: false); else Navigate(fallback);
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count > 0) Navigate(_back.Pop(), push: false);
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Puts a control in the window without showing it (the offline translator's hidden browser view needs a window to live in).</summary>
    public void AddHidden(UIElement element) => Root.Children.Add(element);
    public void RemoveHidden(UIElement element) => Root.Children.Remove(element);

    // ---------- streaming services in the player bar ----------

    WebServiceView? _remote;      // the web player that last started playing
    bool _remoteShown;

    /// <summary>The web player while it is what is being listened to, else null.</summary>
    WebServiceView? RemoteNow => _remote is { Active: true } ? _remote : null;
    public bool ShowingWebInBar => _remoteShown;
    public string BarTitleShown => Tr.Shown(BarTitle);
    public bool ShuffleShown => ShuffleBtn.Visibility == Visibility.Visible;
    public string ShuffleTip => ShuffleBtn.ToolTip as string ?? "";
    public string BarInfoShown => BarInfo.Visibility == Visibility.Visible ? BarInfo.Text : "";

    /// <summary>The small technical line under the artist: format and bitrate, or picture size and frame rate for video.</summary>
    void ShowInfo(string text)
    {
        _infoText = text;
        ComposeInfo();
    }

    string _infoText = "";
    bool? _moreLit;

    /// <summary>The small line under the artist: the technical details, then anything out of the ordinary (casting, sleep timer, speed).</summary>
    void ComposeInfo()
    {
        var mode = ModeText;
        var text = string.Join("   ·   ", new[] { _infoText, mode }.Where(x => x != ""));
        // this runs four times a second: touch the controls only when something changed
        if (BarInfo.Text != text)
        {
            BarInfo.Text = text;
            BarInfo.Visibility = text == "" ? Visibility.Collapsed : Visibility.Visible;
        }
        var lit = mode != "";
        if (lit == _moreLit) return;
        _moreLit = lit;
        MoreBtn.SetResourceReference(ForegroundProperty, lit ? "Accent" : "Fg");
    }

    /// <summary>What is switched on from the "more" button, in words.</summary>
    public string ModeText => string.Join("  ·  ", new[]
    {
        P.Cast != null ? "playing on " + P.Cast.Name : "", P.SleepText, P.CanSpeed && Math.Abs(P.Speed - 1) > 0.01 ? $"{P.Speed:0.##}× speed" : ""
    }.Where(x => x != ""));

    // ---------- the "more" button: sleep timer, speed, casting ----------

    void More_Click(object sender, RoutedEventArgs e) => Menus.Open(MoreMenu(), MoreBtn);

    public ContextMenu MoreMenu()
    {
        var m = new ContextMenu();
        var sleep = new MenuItem { Header = P.SleepOn ? $"Sleep timer  ({P.SleepText.Replace("sleep: ", "").Replace("sleep ", "")})" : "Sleep timer" };
        var off = Menus.Item("Off", () => P.SetSleep(null));
        off.IsChecked = !P.SleepOn;
        sleep.Items.Add(off);
        foreach (var minutes in new[] { 15, 30, 45, 60, 90, 120 })
        {
            var n = minutes;
            sleep.Items.Add(Menus.Item($"{n} minutes", () => P.SetSleep(TimeSpan.FromMinutes(n))));
        }
        var atEnd = Menus.Item("When this one ends", () => P.SetSleep(null, atEnd: true), P.Current != null);
        atEnd.IsChecked = P.SleepAtEnd;
        sleep.Items.Add(atEnd);
        m.Items.Add(sleep);

        var speed = new MenuItem { Header = P.CanSpeed ? "Playback speed" : "Playback speed (podcasts, audiobooks, video)", IsEnabled = P.CanSpeed };
        foreach (var rate in new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3 })
        {
            var r = rate;
            var item = Menus.Item(r == 1 ? "1× (normal)" : $"{r:0.##}×", () => { P.Speed = r; App.Settings.Save(); });
            item.IsChecked = Math.Abs(P.Speed - r) < 0.01;
            speed.Items.Add(item);
        }
        m.Items.Add(speed);
        m.Items.Add(new Separator());
        if (P.Cast is { } device) m.Items.Add(Menus.Item($"Stop playing on {device.Name}", () => P.StopCast()));
        m.Items.Add(Menus.Item(P.Cast != null ? "Play on a different device…" : "Play on another device…", () =>
        {
            if (CastDialog.Ask() is not { } target) return;
            SetStatus($"connecting to {target.Name}…");
            P.StartCast(target.Connect());
            SetStatus(P.Current == null ? $"{target.Name} is ready: what you play next plays there" : "");
        }));
        m.Items.Add(new Separator());
        var track = P.Current?.Track;
        var album = track == null ? null : App.Lib.Albums.FirstOrDefault(a => a.Key == track.AlbumKey);
        m.Items.Add(Menus.Item("Mixview for this album", () => MixviewPage.Open(album, album!.Artist), album != null));
        m.Items.Add(Menus.Item("Listening stats", () => Navigate("stats")));
        return m;
    }

    /// <summary>The first collection tab that is switched on.</summary>
    static string FirstTab => Settings.Tabs.FirstOrDefault(App.Settings.TabOn) ?? "music";

    /// <summary>
    /// Shows the collection tabs that are switched on in settings and hides the rest. If the page on show was
    /// switched off, the first tab that is still on takes its place; radio switched off also stops a station.
    /// </summary>
    public void ShowTabs()
    {
        foreach (var tab in SubNav.Children.OfType<RadioButton>().Where(rb => rb.Tag is string t && Settings.Tabs.Contains(t)))
            tab.Visibility = App.Settings.TabOn((string)tab.Tag) ? Visibility.Visible : Visibility.Collapsed;
        if (Settings.Tabs.Contains(_collPage) && !App.Settings.TabOn(_collPage)) _collPage = FirstTab;
        if (Settings.Tabs.Contains(_page) && !App.Settings.TabOn(_page)) Navigate(FirstTab);
        if (!App.Settings.TabOn("radio") && P.Current?.Station != null) P.Stop();
    }

    public void ShowRadioTab() => ShowTabs();

    /// <summary>The collection tabs on show right now, in order.</summary>
    public List<string> TabsShown => SubNav.Children.OfType<RadioButton>().Where(rb => rb.Visibility == Visibility.Visible && rb.Tag is string t && Settings.Tabs.Contains(t)).Select(rb => (string)rb.Tag).ToList();

    public bool RadioTabShown => NavRadio.Visibility == Visibility.Visible;

    /// <summary>Sets the volume as if the slider had been moved (used by the phone remote).</summary>
    public void SetVolume(double value) => Volume.Value = Math.Clamp(value, 0, 1);

    public void ToggleMute() => Mute_Click(this, new RoutedEventArgs());

    // ---------- artist photos behind now playing ----------

    string _backdropFor = "";

    async void UpdateBackdrop()
    {
        var cur = P.Current;
        var artist = App.Settings.Backdrops && IsNowOpen && cur is { IsVideo: false, Track: not null } ? cur.Track!.Artist : "";
        if (artist == _backdropFor) return;
        _backdropFor = artist;
        var files = artist == "" ? new List<string>() : await Backdrops.ForAsync(artist);
        if (artist != _backdropFor) return;   // the song changed while the pictures were being fetched
        Backdrop.Show(files);
        UpdateVideoSurface();
    }

    public Backdrop BackdropLayer => Backdrop;
    /// <summary>Looks at the backdrop setting again (after it was changed).</summary>
    public void RefreshBackdrop() { _backdropFor = "\u0001"; UpdateBackdrop(); }

    async void ShowLocalInfo(PlayItem? item)
    {
        if (item == null) { ShowInfo(""); return; }
        var text = await Task.Run(() => MediaInfo.For(item));
        if (ReferenceEquals(P.Current, item) && RemoteNow == null) ShowInfo(text);
    }
    public bool RepeatShown => RepeatBtn.Visibility == Visibility.Visible;
    public bool HeartShown => HeartBtn.Visibility == Visibility.Visible;
    public bool SeekShown => Seek.Visibility == Visibility.Visible;

    WebServiceView HookWeb(WebServiceView view)
    {
        view.Changed += ShowRemote;
        view.TookOver += taker =>
        {
            // one thing plays at a time: the others stop and hand over the controls
            foreach (var other in _pages.Values.OfType<WebServiceView>().Where(o => !ReferenceEquals(o, taker))) { if (other.IsPlayingAudio) other.Pause(); other.SetActive(false); }
            _remote = taker;
            P.Remote = taker;
            taker.SetVolume(Volume.Value);
            taker.Muted = P.Muted;
        };
        return view;
    }

    /// <summary>
    /// Puts the web player's song and state in the player bar while it is playing, showing only the controls it can
    /// really be worked with, and Phoenix's own back when it is not.
    /// </summary>
    void ShowRemote()
    {
        if (RemoteNow is not { } web)
        {
            if (!_remoteShown) return;
            _remoteShown = false;
            Seek.Visibility = ShuffleBtn.Visibility = RepeatBtn.Visibility = HeartBtn.Visibility = Visibility.Visible;
            OnCurrentChanged();
            return;
        }
        _remoteShown = true;
        BarNow.Visibility = Visibility.Visible;
        var st = web.State;
        var name = web.Service.Name;
        Tr.SetText(BarTitle, st.Title != "" ? st.Title : name);
        Tr.SetText(BarArtist, string.Join("  ·  ", new[] { st.Artist, name }.Where(x => x != "" && x != st.Title)));
        Art.SetPath(BarArt, st.Art == "" ? null : st.Art);
        BookmarkBtn.Visibility = Visibility.Collapsed;
        ShowInfo(web.BitrateText);
        // only what this page can do is offered
        ShuffleBtn.Visibility = st.Shuffle != null ? Visibility.Visible : Visibility.Collapsed;
        RepeatBtn.Visibility = st.Repeat != null ? Visibility.Visible : Visibility.Collapsed;
        HeartBtn.Visibility = st.Liked != null ? Visibility.Visible : Visibility.Hidden;
        Seek.Visibility = st.CanSeek && st.Duration > 0 ? Visibility.Visible : Visibility.Hidden;
        RefreshTransport();
        ShowRemoteClock();
    }

    void ShowRemoteClock()
    {
        if (RemoteNow is not { } web || _dragging) return;
        var st = web.State;
        PosText.Text = st.Duration > 0 ? (st.Position < 1 ? "0:00" : Fmt.Time((long)(st.Position * 1000))) : "";
        DurText.Text = st.Duration > 0 ? Fmt.Time((long)(st.Duration * 1000)) : "";
        _settingSeek = true;
        Seek.Maximum = st.Duration > 0 ? st.Duration : 1;
        Seek.Value = Math.Min(st.Position, Seek.Maximum);
        _settingSeek = false;
    }

    /// <summary>
    /// After the collection's own tabs: your tabs of your own, then the streaming services that have a tab of their
    /// own. Services placed under a collection are shown by that collection's page instead.
    /// </summary>
    public void ShowWebTabs()
    {
        foreach (var old in SubNav.Children.OfType<RadioButton>().Where(rb => rb.Tag is string t && (t.StartsWith("web:") || t.StartsWith("tab:"))).ToList()) SubNav.Children.Remove(old);
        var lit = _page.StartsWith("web:") && WebServices.Find(_page[4..]) is { } shown && WebServices.PlaceOf(shown) is var place && place != WebServices.OwnTab ? place : _page;
        foreach (var custom in App.Settings.CustomTabs)
        {
            var tab = new RadioButton { Content = custom.Name.ToLowerInvariant(), Tag = custom.Page, GroupName = "sub", Style = (Style)FindResource("NavSection"), IsChecked = lit == custom.Page };
            tab.Click += NavSub_Click;
            SubNav.Children.Add(tab);
        }
        foreach (var service in WebServices.OwnTabs)
        {
            var tab = new RadioButton { Content = service.Name.ToLowerInvariant(), Tag = service.Page, GroupName = "sub", Style = (Style)FindResource("NavSection"), IsChecked = lit == service.Page };
            tab.Click += NavSub_Click;
            SubNav.Children.Add(tab);
        }
        // the lit tab follows a service that was just moved
        foreach (RadioButton rb in SubNav.Children) rb.IsChecked = (string)rb.Tag == lit;
        // a tab of your own that was removed is left
        if (_page.StartsWith("tab:") && App.Settings.CustomTabs.All(t => t.Page != _page)) Navigate(FirstTab);
        if (_collPage.StartsWith("tab:") && App.Settings.CustomTabs.All(t => t.Page != _collPage)) _collPage = FirstTab;
        // a service that was switched off stops playing, and its page is left
        foreach (var (page, view) in _pages.Where(kv => kv.Value is WebServiceView).Select(kv => (kv.Key, (WebServiceView)kv.Value)).ToList())
        {
            if (WebServices.Find(page[4..]) is { } still && WebServices.IsOn(still)) continue;
            if (view.IsPlayingAudio) view.Pause();
            view.SetActive(false);
            if (_page == page) Navigate("music");
            if (_collPage == page) _collPage = "music";
        }
    }

    public List<string> WebTabs => SubNav.Children.OfType<RadioButton>().Where(rb => rb.Tag is string t && t.StartsWith("web:")).Select(rb => (string)rb.Tag).ToList();
    /// <summary>The tabs of your own on show (for the self-test).</summary>
    public List<string> CustomTabsShown => SubNav.Children.OfType<RadioButton>().Where(rb => rb.Tag is string t && t.StartsWith("tab:")).Select(rb => (string)rb.Tag).ToList();
    /// <summary>The collection tab lit right now (for the self-test).</summary>
    public string LitTab => SubNav.Children.OfType<RadioButton>().FirstOrDefault(rb => rb.IsChecked == true)?.Tag as string ?? "";

    bool _webFull;
    WindowState _stateBeforeWebFull;

    /// <summary>A video in a web page asked for the full screen: the page takes the whole display, without Phoenix's own bars.</summary>
    public void SetWebFullscreen(bool on)
    {
        if (on == _webFull || _full) return;
        _webFull = on;
        if (on)
        {
            _stateBeforeWebFull = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeWebFull;
        }
        HeaderArea.Visibility = Transport.Visibility = Caption.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Root.Margin = WindowState == WindowState.Maximized && !on ? new Thickness(7) : new Thickness(0);
    }

    /// <summary>Redraws the names of what is playing (after the translation settings change).</summary>
    public void RefreshNowText()
    {
        foreach (var block in new[] { BarTitle, BarArtist, NowTitle, NowArtist, NowAlbum })
        {
            var text = Tr.GetText(block);
            Tr.SetText(block, null);
            Tr.SetText(block, text);
        }
    }

    /// <summary>Types into the search box (and so runs the search).</summary>
    public void SearchFor(string text)
    {
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
    }

    // ---------- search ----------

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _searchTimer.Stop(); RunSearch(); }
    }

    void RunSearch()
    {
        var q = SearchBox.Text.Trim();
        if (q == "") { if (_page == "search" && _back.Count > 0) Navigate(_back.Pop(), push: false); return; }
        Navigate("search");
        Page<SearchView>("search").Search(q);
    }

    // ---------- now playing ----------

    /// <summary>The now playing screen has colours of its own: inside it, the usual names stand for the theme's Now... colours.</summary>
    void NowColours()
    {
        var t = Themes.Current;
        foreach (var (inside, key) in new[] { ("Fg", "NowFg"), ("FgDim", "NowFgDim"), ("FgFaint", "NowFgFaint"), ("BgAlt", "NowBgAlt"), ("Line", "NowLine"), ("Hover", "NowHover"), ("Selected", "NowSelected") })
        {
            var b = new SolidColorBrush(t.Get(key));
            b.Freeze();
            NowLayer.Resources[inside] = b;
        }
    }

    public void ShowNow(bool show)
    {
        if (!show && _full) ToggleFullscreen();
        NowLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var view in _pages.Values.OfType<WebServiceView>()) view.Covered = show;
        foreach (Button b in Caption.Children)
        {
            if (show) b.SetResourceReference(ForegroundProperty, "NowFg"); else b.ClearValue(ForegroundProperty);
        }
        if (show) { BuildMosaic(); QueueList.ScrollIntoView(P.Current); LoadLyrics(); }
        UpdateVideoSurface();
        UpdateBackdrop();
    }

    void UpdateVideoSurface()
    {
        var video = P.Current?.IsVideo == true;
        var viz = IsNowOpen && !video && App.Settings.Visualizer > 0;
        Media.Visibility = video ? Visibility.Visible : Visibility.Hidden;
        NowInfo.Visibility = MosaicShade.Visibility = video ? Visibility.Hidden : Visibility.Visible;
        var photos = IsNowOpen && !video && Backdrop.HasPictures;
        Backdrop.Visibility = photos ? Visibility.Visible : Visibility.Collapsed;
        Mosaic.Visibility = video || viz || photos ? Visibility.Hidden : Visibility.Visible;
        VizBtn.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        Viz.Mode = App.Settings.Visualizer;
        Viz.Palette = App.Settings.VisualizerColor;
        Viz.Sensitivity = App.Settings.VisualizerSensitivity;
        // Windowed, the visualizer sits behind the song info; full screen it gets the whole display.
        Viz.Margin = new Thickness(0, _full ? 0 : 48, 0, 0);
        Viz.Opacity = _full ? 1 : 0.9;
        Viz.Visibility = viz ? Visibility.Visible : Visibility.Collapsed;
        if (viz) Viz.Start(); else Viz.Stop();   // only listens while it is on screen
    }

    public Visualizer VisualizerControl => Viz;

    async Task UpdateCoverColorAsync(string? art) => Viz.CoverColor = await Visualizer.DominantAsync(art);

    void Viz_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        for (var i = 0; i < Visualizer.Styles.Length; i++)
        {
            var style = i;
            var item = Menus.Item(Visualizer.Styles[i], () => SetVisualizer(style, App.Settings.VisualizerColor));
            item.IsChecked = App.Settings.Visualizer == i;
            m.Items.Add(item);
            if (i == 0) m.Items.Add(new Separator());
        }
        m.Items.Add(new Separator());
        var colours = new MenuItem { Header = "Colours" };
        for (var i = 0; i < Visualizer.Palettes.Length; i++)
        {
            var palette = i;
            var item = Menus.Item(Visualizer.Palettes[i], () => SetVisualizer(App.Settings.Visualizer, palette));
            item.IsChecked = App.Settings.VisualizerColor == i;
            colours.Items.Add(item);
        }
        m.Items.Add(colours);
        var sensitivity = new MenuItem { Header = "Sensitivity" };
        for (var i = 0; i < Visualizer.Sensitivities.Length; i++)
        {
            var level = i;
            var item = Menus.Item(Visualizer.Sensitivities[i], () => { App.Settings.VisualizerSensitivity = level; UpdateVideoSurface(); });
            item.IsChecked = App.Settings.VisualizerSensitivity == i;
            sensitivity.Items.Add(item);
        }
        m.Items.Add(sensitivity);
        m.Items.Add(new Separator());
        m.Items.Add(Menus.Item(_full ? "Exit full screen (Esc)" : "Full screen (F11)", ToggleFullscreen));
        Menus.Open(m, VizBtn);
    }

    public void SetVisualizer(int style, int palette)
    {
        App.Settings.Visualizer = style;
        App.Settings.VisualizerColor = palette;
        UpdateVideoSurface();
    }

    void CycleVisualizer() => SetVisualizer((App.Settings.Visualizer + 1) % Visualizer.Styles.Length, App.Settings.VisualizerColor);

    void BuildMosaic()
    {
        var rnd = new Random();
        var arts = App.Lib.Albums.Where(a => a.ArtPath != "").Select(a => a.ArtPath).Distinct().OrderBy(_ => rnd.Next()).Take(40).ToList();
        Mosaic.Children.Clear();
        if (arts.Count == 0) return;
        for (var i = 0; i < 40; i++)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            Art.SetSize(img, 240);
            Art.SetPath(img, arts[i % arts.Count]);
            Mosaic.Children.Add(img);
        }
    }

    void OpenNow_Click(object sender, RoutedEventArgs e) => ShowNow(!IsNowOpen);
    void CloseNow_Click(object sender, RoutedEventArgs e) => ShowNow(false);

    void NowLayer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !QueuePane.IsMouseOver) ToggleFullscreen();
    }

    void Full_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    Point _lastMouse;

    void ShowFullscreenControls()
    {
        Cursor = null;
        Transport.Visibility = BackNowBtn.Visibility = FullBtn.Visibility = Visibility.Visible;
        VizBtn.Visibility = P.Current?.IsVideo == true ? Visibility.Collapsed : Visibility.Visible;
        _idleTimer.Stop();
        if (_full) _idleTimer.Start();
    }

    public bool IsFullscreen => _full;
    public void ToggleFullscreenForTest() => ToggleFullscreen();

    void Queue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PlayItem item) P.PlayAt(P.Queue.IndexOf(item));
    }

    void ToggleFullscreen()
    {
        if (!_full)
        {
            _full = true;
            _stateBeforeFull = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Caption.Visibility = Visibility.Collapsed;
            Grid.SetRowSpan(NowLayer, 3);
            // Full screen is about the picture or the visualizer: the queue and big text step aside for music.
            QueuePane.Visibility = Visibility.Collapsed;
        }
        else
        {
            _full = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFull;
            Caption.Visibility = Visibility.Visible;
            Grid.SetRowSpan(NowLayer, 2);
            QueuePane.Visibility = Visibility.Visible;
        }
        Root.Margin = WindowState == WindowState.Maximized && !_full ? new Thickness(7) : new Thickness(0);
        ShowFullscreenControls();
        UpdateVideoSurface();
    }

    // ---------- transport ----------

    void OnCurrentChanged()
    {
        var cur = P.Current;
        BookmarkBtn.Visibility = cur?.Book != null ? Visibility.Visible : Visibility.Collapsed;
        _ = UpdateCoverColorAsync(cur?.ArtPath);
        ShowLocalInfo(cur);
        Tr.SetText(BarTitle, cur?.Title ?? "nothing playing");
        // nothing playing: the bar's middle stays empty (its place is kept, so the controls don't move)
        BarNow.Visibility = cur == null && !_remoteShown ? Visibility.Hidden : Visibility.Visible;
        Tr.SetText(BarArtist, cur?.Subtitle ?? "");
        Art.SetPath(BarArt, cur?.ArtPath);
        Tr.SetText(NowTitle, cur?.Title ?? "");
        Tr.SetText(NowArtist, cur?.Subtitle ?? "");
        Tr.SetText(NowAlbum, cur?.Album ?? "");
        Art.SetPath(NowArt, cur?.ArtPath);
        if (cur?.IsVideo == true && !IsNowOpen && _mini == null) ShowNow(true);
        else UpdateVideoSurface();
        if (IsNowOpen && cur != null) QueueList.ScrollIntoView(cur);
        LoadLyrics();
        UpdateBackdrop();
        DjText.Visibility = P.DjSeed != null ? Visibility.Visible : Visibility.Collapsed;
        DjText.Text = P.DjSeed is { } seed ? $"smart dj  ·  music like “{seed.Title}”" : "";
        _smtc.Update(cur, P.IsPlaying);
        RefreshTransport();
    }

    void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current is not { Book: { } book } cur) return;
        var m = App.Lib.AddBookmark(book, cur.BookPart, Math.Round(P.Position.TotalMilliseconds));
        SetStatus($"bookmarked {m.Label}");
    }

    public void RefreshTransport()
    {
        // while a web player is what is playing, the buttons show its state
        var remote = RemoteNow;
        var playing = remote != null ? remote.State.Playing : P.IsPlaying;
        var shuffle = remote != null ? (remote.State.Shuffle ?? 0) > 0 : P.Shuffle;
        var smart = remote?.State.Shuffle == 2;
        var repeat = remote != null ? remote.State.Repeat ?? 0 : P.Repeat;
        PlayBtn.Content = playing ? "" : "";
        // Spotify's third shuffle setting, smart shuffle, shows the arrows with a star beside them
        ShuffleBtn.Content = smart ? "\uE8B1\uE735" : "\uE8B1";
        ShuffleBtn.FontSize = smart ? 11 : 14;
        ShuffleBtn.ToolTip = remote == null ? "Shuffle" : smart ? "Smart shuffle (press for shuffle off)" : shuffle ? "Shuffle (press for smart shuffle)" : "Shuffle off (press for shuffle)";
        ShuffleBtn.Opacity = shuffle ? 1 : 0.35;
        ShuffleBtn.SetResourceReference(ForegroundProperty, shuffle ? "Accent" : "Fg");
        RepeatBtn.Content = repeat == 2 ? "" : "";
        RepeatBtn.Opacity = repeat > 0 ? 1 : 0.35;
        RepeatBtn.SetResourceReference(ForegroundProperty, repeat > 0 ? "Accent" : "Fg");
        MuteBtn.Content = P.Muted ? "" : "";
        if (remote != null)
        {
            HeartBtn.IsEnabled = remote.State.Liked != null;
            HeartBtn.Content = remote.State.Liked == true ? "\uEB52" : "";
            HeartBtn.SetResourceReference(ForegroundProperty, remote.State.Liked == true ? "Accent" : "Fg");
        }
        else
        {
            var t = P.Current?.Track;
            HeartBtn.IsEnabled = t != null;
            HeartBtn.Content = t?.RatingGlyph ?? "";
            HeartBtn.SetResourceReference(ForegroundProperty, t is { Rating: not 0 } ? "Accent" : "Fg");
        }
        _smtc.Update(P.Current, P.IsPlaying);
        if (_thumbPlay != null)
        {
            _thumbPlay.ImageSource = P.IsPlaying ? _thumbPauseIcon : _thumbPlayIcon;
            _thumbPlay.Description = P.IsPlaying ? "Pause" : "Play";
        }
    }

    int _ticks;

    void Tick()
    {
        // Every ten seconds, note where a playing audiobook or podcast is, so a crash or power cut loses little.
        if (++_ticks % 40 == 0 && P.IsPlaying) P.SaveEpisodePosition();
        P.SleepTick();
        ComposeInfo();
        if (RemoteNow != null) { ShowRemoteClock(); return; }
        if (P.Current == null)
        {
            PosText.Text = DurText.Text = "";
            if (!_dragging) { _settingSeek = true; Seek.Value = 0; _settingSeek = false; }
            return;
        }
        var dur = P.Duration.TotalSeconds;
        var pos = P.Position.TotalSeconds;
        DurText.Text = Fmt.Time((long)(dur * 1000));
        FollowLyrics(pos * 1000);
        if (_dragging) return;
        PosText.Text = pos < 1 ? "0:00" : Fmt.Time((long)(pos * 1000));
        _settingSeek = true;
        Seek.Maximum = dur > 0 ? dur : 1;
        Seek.Value = Math.Min(pos, Seek.Maximum);
        _settingSeek = false;
    }

    void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingSeek) return;
        if (_dragging) PosText.Text = Fmt.Time((long)(Seek.Value * 1000));
        else if (RemoteNow is { } web) web.SeekTo(Seek.Value);
        else P.Seek(TimeSpan.FromSeconds(Seek.Value));
    }

    void Seek_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _dragging = true;

    void Seek_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _dragging = false;
        if (RemoteNow is { } web) web.SeekTo(Seek.Value); else P.Seek(TimeSpan.FromSeconds(Seek.Value));
    }

    void Play_Click(object sender, RoutedEventArgs e) => P.Toggle();
    void Prev_Click(object sender, RoutedEventArgs e) => P.Previous();
    void Next_Click(object sender, RoutedEventArgs e) => P.Next();
    void Shuffle_Click(object sender, RoutedEventArgs e) { if (RemoteNow is { } sp) { sp.ToggleShuffle(); return; } P.Shuffle = !P.Shuffle; RefreshTransport(); }
    void Repeat_Click(object sender, RoutedEventArgs e) { if (RemoteNow is { } sp) { sp.CycleRepeat(); return; } P.Repeat = (P.Repeat + 1) % 3; RefreshTransport(); }
    void Mute_Click(object sender, RoutedEventArgs e)
    {
        P.Muted = !P.Muted;
        foreach (var view in _pages.Values.OfType<WebServiceView>()) view.Muted = P.Muted;   // one mute button for everything
    }
    void Heart_Click(object sender, RoutedEventArgs e) { if (RemoteNow is { } web) web.ToggleLike(); else Cmd.Rate.Execute(P.Current?.Track); }

    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || Media == null) return;
        P.Volume = Volume.Value;
        RemoteNow?.SetVolume(Volume.Value);   // one volume slider for everything
    }

    // ---------- window ----------

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Mini_Click(object sender, RoutedEventArgs e)
    {
        if (_full) ToggleFullscreen();
        _mini = new MiniWindow();
        _mini.Closed += (_, _) => { _mini = null; Show(); Activate(); };
        _mini.Show();
        Hide();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or PasswordBox;
        switch (e.Key)
        {
            case Key.Space when !typing: P.Toggle(); e.Handled = true; break;
            case Key.MediaPlayPause: P.Toggle(); e.Handled = true; break;
            case Key.MediaNextTrack: P.Next(); e.Handled = true; break;
            case Key.MediaPreviousTrack: P.Previous(); e.Handled = true; break;
            case Key.Right when !typing && Keyboard.Modifiers == ModifierKeys.Control: P.Next(); e.Handled = true; break;
            case Key.Left when !typing && Keyboard.Modifiers == ModifierKeys.Control: P.Previous(); e.Handled = true; break;
            case Key.F11: if (!IsNowOpen) ShowNow(true); ToggleFullscreen(); e.Handled = true; break;
            case Key.V when !typing && IsNowOpen && P.Current?.IsVideo != true: CycleVisualizer(); e.Handled = true; break;
            case Key.C when !typing && IsNowOpen && P.Current?.IsVideo != true:
                SetVisualizer(App.Settings.Visualizer, (App.Settings.VisualizerColor + 1) % Visualizer.Palettes.Length); e.Handled = true; break;
            case Key.Escape when _full: ToggleFullscreen(); e.Handled = true; break;
            case Key.Escape when IsNowOpen: ShowNow(false); e.Handled = true; break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control: SearchBox.Focus(); e.Handled = true; break;
        }
    }
}
