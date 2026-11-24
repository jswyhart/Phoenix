using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Halcyon.Core;

namespace Halcyon.Desktop;

public partial class MainWindow : Window
{
    static readonly string[] CollectionPages = { "music", "videos", "pictures", "podcasts", "books", "playlists" };
    readonly Dictionary<string, Control> _pages = new();
    readonly Stack<string> _back = new();
    readonly DispatcherTimer _timer, _searchTimer, _idleTimer;
    string _page = "", _collPage = "music";
    bool _dragging, _settingSeek, _full;
    WindowState _stateBeforeFull;
    Point _lastMouse;
    int _ticks;

    static Player P => App.Player;
    public bool IsNowOpen => NowLayer.IsVisible;
    public bool IsFullscreen => _full;
    public Viz Visualizer => VizView;

    public MainWindow()
    {
        InitializeComponent();
        Frames = new VideoFrames(HalcyonApp.Engine.MediaPlayer, VideoImage);
        P.CurrentChanged += OnCurrentChanged;
        P.StateChanged += RefreshTransport;
        P.QueueChanged += () => QueueList.ItemsSource = P.Queue.ToList();
        P.Error += SetStatus;
        App.Lib.RatingsChanged += RefreshTransport;
        App.Status += s => Dispatcher.UIThread.Post(() => SetStatus(s));
        Volume.Value = App.Settings.Volume;
        Volume.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) P.Volume = Volume.Value; };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _searchTimer!.Stop(); RunSearch(); });
        _searchTimer.Stop();
        SearchBox.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        // In full screen the controls and pointer get out of the way until the mouse moves again (music only:
        // nothing can be drawn over the native video surface, so with video the controls simply stay).
        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _idleTimer!.Stop();
            if (!_full || P.Current?.IsVideo == true || Transport.IsPointerOver) return;
            Transport.IsVisible = false;
            Cursor = new Cursor(StandardCursorType.None);
        });
        _idleTimer.Stop();
        PointerMoved += (_, e) =>
        {
            if (!_full) return;
            var pos = e.GetPosition(this);
            if (pos == _lastMouse) return;
            _lastMouse = pos;
            ShowFullscreenControls();
        };

        // The seek bar follows playback except while it is being dragged.
        Seek.AddHandler(PointerPressedEvent, (_, _) => _dragging = true, RoutingStrategies.Tunnel);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => { if (_dragging) { _dragging = false; P.Seek((long)(Seek.Value * 1000)); } }, RoutingStrategies.Tunnel);
        Seek.PointerCaptureLost += (_, _) => _dragging = false;
        Seek.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && _dragging && !_settingSeek) PosText.Text = Fmt.Clock((long)(Seek.Value * 1000)); };

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Navigate("quickplay");
        RefreshTransport();
    }

    // ---------- navigation ----------

    Control Create(string page) => page switch
    {
        "quickplay" => new QuickplayView(),
        "music" => new MusicView(),
        "videos" => new VideosView(),
        "pictures" => new PicturesView(),
        "podcasts" => new PodcastsView(),
        "books" => new BooksView(),
        "playlists" => new PlaylistsView(),
        "search" => new SearchView(),
        "settings" => new SettingsView(),
        _ => throw new ArgumentException(page)
    };

    public T Page<T>(string page) where T : Control => (T)(_pages.TryGetValue(page, out var v) ? v : _pages[page] = Create(page));

    public void Navigate(string page, bool push = true)
    {
        if (IsNowOpen) ShowNow(false);
        if (page == _page) return;
        if (push && _page != "") _back.Push(_page);
        _page = page;
        var isColl = CollectionPages.Contains(page);
        if (isColl) _collPage = page;
        Host.Content = Page<Control>(page);

        NavQuick.IsChecked = page == "quickplay";
        NavColl.IsChecked = isColl || page == "search";
        NavSettings.IsChecked = page == "settings";
        SubNav.IsVisible = isColl;
        foreach (var rb in SubNav.Children.OfType<RadioButton>()) rb.IsChecked = (string?)rb.Tag == page;
        BackBtn.IsEnabled = _back.Count > 0;
    }

    void NavBig_Click(object? sender, RoutedEventArgs e)
    {
        var tag = (string)((Control)sender!).Tag!;
        Navigate(tag == "collection" ? _collPage : tag);
    }

    void NavSub_Click(object? sender, RoutedEventArgs e) => Navigate((string)((Control)sender!).Tag!);

    void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_back.Count > 0) Navigate(_back.Pop(), push: false);
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Where the playing video's frames are drawn.</summary>
    public VideoFrames Frames { get; private set; } = null!;

    Geometry Icon(string key) => this.TryFindResource(key, out var g) && g is Geometry geo ? geo : new StreamGeometry();

    /// <summary>Goes back a page, or to <paramref name="fallback"/> when there is nowhere to go back to.</summary>
    public void GoBackOr(string fallback)
    {
        if (_back.Count > 0) Navigate(_back.Pop(), push: false); else Navigate(fallback);
    }

    void Logo_Click(object? sender, RoutedEventArgs e) => Navigate("quickplay");
    void Playlists_Click(object? sender, RoutedEventArgs e) => Navigate("playlists");

    void RunSearch()
    {
        var q = (SearchBox.Text ?? "").Trim();
        if (q == "") { if (_page == "search" && _back.Count > 0) Navigate(_back.Pop(), push: false); return; }
        Navigate("search");
        Page<SearchView>("search").Search(q);
    }

    // ---------- now playing ----------

    public void ShowNow(bool show)
    {
        if (!show && _full) ToggleFullscreen();
        NowLayer.IsVisible = show;
        if (show) { BuildMosaic(); if (P.Current != null) QueueList.ScrollIntoView(P.Current); }
        UpdateSurface();
    }

    void UpdateSurface()
    {
        var video = P.Current?.IsVideo == true;
        var viz = IsNowOpen && !video && App.Settings.Visualizer > 0;
        Video.IsVisible = video && IsNowOpen;
        NowInfo.IsVisible = MosaicShade.IsVisible = !video;
        QueuePane.IsVisible = !_full;
        Mosaic.IsVisible = !video && !viz;
        VizBtn.IsVisible = !video;
        VizView.Mode = App.Settings.Visualizer;
        VizView.Palette = App.Settings.VisualizerColor;
        VizView.Sensitivity = App.Settings.VisualizerSensitivity;
        VizView.Margin = new Thickness(0, _full ? 0 : 44, 0, 0);
        VizView.IsVisible = viz;
        if (viz) VizView.Start(); else VizView.Stop();   // only analyses while it is on screen
    }

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

    void OpenNow_Click(object? sender, RoutedEventArgs e) => ShowNow(!IsNowOpen);
    void CloseNow_Click(object? sender, RoutedEventArgs e) => ShowNow(false);
    void Full_Click(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    void NowLayer_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!QueueList.IsPointerOver) ToggleFullscreen();
    }

    void Queue_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Ui.Item<PlayItem>(e) is { } item) P.PlayAt(P.Queue.IndexOf(item));
        e.Handled = true;
    }

    public void ToggleFullscreen()
    {
        if (!_full)
        {
            _full = true;
            _stateBeforeFull = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            _full = false;
            WindowState = _stateBeforeFull;
        }
        ShowFullscreenControls();
        UpdateSurface();
    }

    void ShowFullscreenControls()
    {
        Cursor = Cursor.Default;
        Transport.IsVisible = true;
        _idleTimer.Stop();
        if (_full) _idleTimer.Start();
    }

    async void Viz_Click(object? sender, RoutedEventArgs e)
    {
        var items = new List<object?>();
        for (var i = 0; i < Viz.Styles.Length; i++)
        {
            var style = i;
            items.Add(Ui.MenuItem(Viz.Styles[i], () => SetVisualizer(style, App.Settings.VisualizerColor), check: App.Settings.Visualizer == i));
            if (i == 0) items.Add(new Separator());
        }
        items.Add(new Separator());
        items.Add(Ui.Sub("Colours", Viz.Palettes.Select((name, i) => (object)Ui.MenuItem(name, () => SetVisualizer(App.Settings.Visualizer, i), check: App.Settings.VisualizerColor == i))));
        items.Add(Ui.Sub("Sensitivity", Viz.Sensitivities.Select((name, i) => (object)Ui.MenuItem(name, () => { App.Settings.VisualizerSensitivity = i; UpdateSurface(); }, check: App.Settings.VisualizerSensitivity == i))));
        Ui.Menu(items.ToArray()).Show(VizBtn);
        await Task.CompletedTask;
    }

    public void SetVisualizer(int style, int palette)
    {
        App.Settings.Visualizer = style;
        App.Settings.VisualizerColor = palette;
        UpdateSurface();
    }

    // ---------- transport ----------

    async Task UpdateCoverColorAsync(string? art) => VizView.CoverColor = await Viz.DominantAsync(art);

    void Bookmark_Click(object? sender, RoutedEventArgs e)
    {
        if (P.Current is not { Book: { } book } cur) return;
        var m = App.Lib.AddBookmark(book, cur.BookPart, P.PositionMs);
        SetStatus($"bookmarked {m.Label}");
    }

    void OnCurrentChanged()
    {
        var cur = P.Current;
        BookmarkBtn.IsVisible = cur?.Book != null;
        _ = UpdateCoverColorAsync(cur?.ArtPath);
        BarTitle.Text = cur?.Title ?? "nothing playing";
        BarArtist.Text = cur?.Subtitle ?? "";
        Art.SetPath(BarArt, cur?.ArtPath);
        NowTitle.Text = cur?.Title ?? "";
        NowArtist.Text = cur?.Subtitle ?? "";
        NowAlbum.Text = cur?.Album ?? "";
        Art.SetPath(NowArt, cur?.ArtPath);
        if (cur?.IsVideo == true && !IsNowOpen) ShowNow(true); else UpdateSurface();
        if (IsNowOpen && cur != null) QueueList.ScrollIntoView(cur);
        RefreshTransport();
    }

    public void RefreshTransport()
    {
        PlayPath.Data = Icon(P.IsPlaying ? "IconPause" : "IconPlay");
        ShuffleBtn.Classes.Set("on", P.Shuffle);
        RepeatPath.Data = Icon(P.Repeat == 2 ? "IconRepeatOne" : "IconRepeat");
        RepeatBtn.Classes.Set("on", P.Repeat > 0);
        MutePath.Data = Icon(HalcyonApp.Engine.Muted ? "IconMute" : "IconVolume");
        var t = P.Current?.Track;
        HeartBtn.IsEnabled = t != null;
        // liked: a filled pink heart; disliked: a crossed-out one; otherwise an outline
        HeartPath.Fill = t is { Rating: > 0 } && this.TryFindResource("Accent", out var accent) && accent is IBrush b ? b : null;
        HeartBtn.Classes.Set("on", t is { Rating: not 0 });
        HeartBtn.Opacity = t == null ? 0.3 : t.Rating < 0 ? 0.45 : 1;
    }

    void Tick()
    {
        // Every ten seconds, note where a playing podcast is, so a crash loses little.
        if (++_ticks % 40 == 0 && P.IsPlaying) P.SaveEpisodePosition();
        if (P.Current == null)
        {
            PosText.Text = DurText.Text = "";
            if (!_dragging) { _settingSeek = true; Seek.Value = 0; _settingSeek = false; }
            return;
        }
        double dur = P.DurationMs / 1000.0, pos = P.PositionMs / 1000.0;
        DurText.Text = Fmt.Time(P.DurationMs);
        if (_dragging) return;
        PosText.Text = Fmt.Clock(P.PositionMs);
        _settingSeek = true;
        Seek.Maximum = dur > 0 ? dur : 1;
        Seek.Value = Math.Min(pos, Seek.Maximum);
        _settingSeek = false;
    }

    void Play_Click(object? sender, RoutedEventArgs e) => P.Toggle();
    void Prev_Click(object? sender, RoutedEventArgs e) => P.Previous();
    void Next_Click(object? sender, RoutedEventArgs e) => P.Next();
    void Shuffle_Click(object? sender, RoutedEventArgs e) { P.Shuffle = !P.Shuffle; RefreshTransport(); }
    void Repeat_Click(object? sender, RoutedEventArgs e) { P.Repeat = (P.Repeat + 1) % 3; RefreshTransport(); }
    void Heart_Click(object? sender, RoutedEventArgs e) => Cmd.Rate.Execute(P.Current?.Track);

    async void Mute_Click(object? sender, RoutedEventArgs e)
    {
        var muted = !HalcyonApp.Engine.Muted;
        HalcyonApp.Engine.Muted = muted;
        MutePath.Data = Icon(muted ? "IconMute" : "IconVolume");
        await Task.CompletedTask;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        var typing = FocusManager?.GetFocusedElement() is TextBox;
        var music = P.Current?.IsVideo != true;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Space when !typing: P.Toggle(); e.Handled = true; break;
            case Key.MediaPlayPause: P.Toggle(); e.Handled = true; break;
            case Key.MediaNextTrack: P.Next(); e.Handled = true; break;
            case Key.MediaPreviousTrack: P.Previous(); e.Handled = true; break;
            case Key.Right when !typing && command: P.Next(); e.Handled = true; break;
            case Key.Left when !typing && command: P.Previous(); e.Handled = true; break;
            case Key.F11: if (!IsNowOpen) ShowNow(true); ToggleFullscreen(); e.Handled = true; break;
            case Key.V when !typing && IsNowOpen && music: SetVisualizer((App.Settings.Visualizer + 1) % Viz.Styles.Length, App.Settings.VisualizerColor); e.Handled = true; break;
            case Key.C when !typing && IsNowOpen && music: SetVisualizer(App.Settings.Visualizer, (App.Settings.VisualizerColor + 1) % Viz.Palettes.Length); e.Handled = true; break;
            case Key.Escape when _full: ToggleFullscreen(); e.Handled = true; break;
            case Key.Escape when IsNowOpen: ShowNow(false); e.Handled = true; break;
            case Key.F when command: SearchBox.Focus(); e.Handled = true; break;
        }
    }
}
