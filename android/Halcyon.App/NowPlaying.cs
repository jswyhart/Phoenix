using Android.Content;
using Android.Graphics;
using Android.Views;
using Halcyon.Core;
using AFx = Android.Media.Audiofx;

namespace Halcyon.Droid;

/// <summary>Full-screen player: cover art, visualizer or video; like/dislike; seek bar; transport; shuffle and repeat.</summary>
public class NowPlayingView : LinearLayout
{
    readonly ImageView _bookmark;
    readonly MainActivity _a;
    readonly FrameLayout _stage;
    readonly ImageView _art;
    readonly VisualizerView _viz;
    readonly SurfaceView _surface;
    readonly TextView _title, _sub, _pos, _dur, _mode;
    readonly SeekBar _seek;
    readonly ImageView _play, _like, _dislike, _shuffle, _repeat;
    readonly View _info, _transport, _topBar;
    readonly LyricsPane _lyrics;
    readonly TextView _lyricsChip;
    bool _dragging, _landscape, _chromeHidden;
    string _artFor = "\0";

    static Player P => App.Player;

    public NowPlayingView(MainActivity a) : base(a)
    {
        _a = a;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(Th.Bg);
        Clickable = true;
        Visibility = ViewStates.Gone;

        var caption = Ui.Caption(a, "NOW PLAYING");
        caption.Gravity = GravityFlags.Center;
        caption.SetPadding(0, 0, 0, 0);
        _topBar = Ui.RowOf(a, Ui.Icon(a, IconKind.Down, Th.Fg, 22, a.HideNowPlaying, 12));
        ((LinearLayout)_topBar).AddView(caption, Ui.Weight());
        _bookmark = Ui.Icon(a, IconKind.Bookmark, Th.Fg, 22, () =>
        {
            if (P.Current is { Book: { } book } item) BookScreens.BookmarkMenu(a, book, () => (item.BookPart, P.PositionMs));
        }, 12);
        _bookmark.Visibility = ViewStates.Gone;
        ((LinearLayout)_topBar).AddView(_bookmark);
        ((LinearLayout)_topBar).AddView(Ui.Icon(a, IconKind.More, Th.Fg, 22, () => MoreScreens.PlayerMenu(a), 12));   // sleep timer, speed, stats
        ((LinearLayout)_topBar).AddView(Ui.Icon(a, IconKind.Queue, Th.Fg, 22, () => { a.HideNowPlaying(); a.Push(Screens.Queue(a)); }, 12));
        AddView(_topBar, Ui.Fill());

        _stage = new FrameLayout(a);
        _stage.SetBackgroundColor(Color.Black);
        _art = new ImageView(a);
        _art.SetScaleType(ImageView.ScaleType.FitCenter);
        _viz = new VisualizerView(a);
        _surface = new SurfaceView(a) { Visibility = ViewStates.Gone };
        _stage.AddView(_art, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _stage.AddView(_surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent, GravityFlags.Center));
        _stage.AddView(_viz, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _mode = Ui.Text(a, "", 12, Color.White);
        _mode.SetPadding(Th.Dp(10), Th.Dp(6), Th.Dp(10), Th.Dp(6));
        _mode.SetBackgroundColor(Color.Argb(0x66, 0, 0, 0));
        _mode.Click += (_, _) => VisualizerPicker.Show(a, Update);
        _stage.AddView(_mode, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.End));
        // the song's lyrics, over the picture
        _lyrics = new LyricsPane(a) { Visibility = ViewStates.Gone };
        _stage.AddView(_lyrics, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _lyricsChip = Ui.Text(a, "lyrics", 12, Color.White);
        _lyricsChip.SetPadding(Th.Dp(10), Th.Dp(6), Th.Dp(10), Th.Dp(6));
        _lyricsChip.SetBackgroundColor(Color.Argb(0x66, 0, 0, 0));
        _lyricsChip.Click += (_, _) => { App.Settings.ShowLyrics = !App.Settings.ShowLyrics; ShowLyricsPane(); };
        _stage.AddView(_lyricsChip, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Start));
        _stage.Click += (_, _) => { if (_landscape && P.Current?.IsVideo == true) { _chromeHidden = !_chromeHidden; ApplyChrome(); } };
        _stage.LayoutChange += (_, _) => FitVideo();
        AddView(_stage, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        _title = Ui.Text(a, "", 22, Th.Fg, Th.Light, 2);
        _sub = Ui.Text(a, "", 14, Th.FgDim);
        _like = Ui.Icon(a, IconKind.Heart, Th.FgDim, 24, () => Rate(1));
        _dislike = Ui.Icon(a, IconKind.HeartBroken, Th.FgDim, 24, () => Rate(-1));
        var names = Ui.Column(a, _title, _sub);
        var infoRow = Ui.RowOf(a);
        infoRow.AddView(names, Ui.Weight());
        infoRow.AddView(_like);
        infoRow.AddView(_dislike);
        infoRow.SetPadding(Th.Dp(18), Th.Dp(12), Th.Dp(6), 0);
        _info = infoRow;
        AddView(_info, Ui.Fill());

        _pos = Ui.Text(a, "0:00", 12, Th.FgDim);
        _dur = Ui.Text(a, "", 12, Th.FgDim);
        _seek = new SeekBar(a) { Max = 1000 };
        _seek.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Th.Accent);
        _seek.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(Th.Accent);
        _seek.StartTrackingTouch += (_, _) => _dragging = true;
        _seek.ProgressChanged += (_, e) => { if (e.FromUser) _pos.Text = Fmt.Clock(P.DurationMs * e.Progress / 1000); };
        _seek.StopTrackingTouch += (_, _) =>
        {
            _dragging = false;
            if (P.DurationMs > 0) P.Seek(P.DurationMs * _seek.Progress / 1000);
        };
        var seekRow = Ui.RowOf(a, _pos);
        seekRow.AddView(_seek, Ui.Weight());
        seekRow.AddView(_dur);
        seekRow.SetPadding(Th.Dp(18), Th.Dp(6), Th.Dp(18), 0);

        _shuffle = Ui.Icon(a, IconKind.Shuffle, Th.FgDim, 22, () => { P.Shuffle = !P.Shuffle; App.Settings.Shuffle = P.Shuffle; Update(); }, 12);
        _repeat = Ui.Icon(a, IconKind.Repeat, Th.FgDim, 22, () => { P.Repeat = (P.Repeat + 1) % 3; App.Settings.Repeat = P.Repeat; Update(); }, 12);
        _play = Ui.Icon(a, IconKind.Play, Th.Fg, 38, P.Toggle, 14);
        var transport = Ui.RowOf(a);
        transport.SetGravity(GravityFlags.Center);
        foreach (var v in new View[] { _shuffle, Ui.Icon(a, IconKind.Prev, Th.Fg, 28, P.Previous, 14), _play, Ui.Icon(a, IconKind.Next, Th.Fg, 28, () => P.Next(), 14), _repeat })
            transport.AddView(v);
        transport.SetPadding(0, 0, 0, Th.Dp(10));
        _transport = Ui.Column(a, seekRow, transport);
        AddView(_transport, Ui.Fill());

        HalcyonApp.Engine.VideoSize += (_, _) => FitVideo();
    }

    void Rate(int value)
    {
        if (P.Current?.Track is { } t) App.Lib.Rate(new[] { t }, t.Rating == value ? 0 : value);
    }

    public bool IsOpen => Visibility == ViewStates.Visible;

    public void Open()
    {
        Visibility = ViewStates.Visible;
        _chromeHidden = false;
        Update();
    }

    public void Close()
    {
        Visibility = ViewStates.Gone;
        _viz.Stop();
        HalcyonApp.Engine.SetSurface(null);
        _surface.Visibility = ViewStates.Gone;
        _a.Window!.ClearFlags(WindowManagerFlags.KeepScreenOn);
    }

    public void SetLandscape(bool landscape)
    {
        _landscape = landscape;
        _chromeHidden = landscape;
        ApplyChrome();
    }

    void ApplyChrome()
    {
        var hide = _landscape && _chromeHidden && P.Current?.IsVideo == true;
        var state = hide ? ViewStates.Gone : ViewStates.Visible;
        _topBar.Visibility = _info.Visibility = _transport.Visibility = state;
    }

    /// <summary>Sizes the video surface to the picture's shape, centred in the stage.</summary>
    void FitVideo()
    {
        int vw = HalcyonApp.Engine.VideoWidth, vh = HalcyonApp.Engine.VideoHeight, sw = _stage.Width, sh = _stage.Height;
        if (vw <= 0 || vh <= 0 || sw <= 0 || sh <= 0) return;
        var scale = Math.Min((float)sw / vw, (float)sh / vh);
        int w = (int)(vw * scale), h = (int)(vh * scale);
        var lp = (FrameLayout.LayoutParams)_surface.LayoutParameters!;
        if (lp.Width == w && lp.Height == h) return;
        lp.Width = w; lp.Height = h; lp.Gravity = GravityFlags.Center;
        _surface.LayoutParameters = lp;
    }

    /// <summary>Refreshes everything except the moving position (see <see cref="Tick"/>).</summary>
    public void Update()
    {
        if (!IsOpen) return;
        var cur = P.Current;
        if (cur == null) { _a.HideNowPlaying(); return; }
        _title.Text = cur.Title;
        _sub.Text = Fmt.Join(cur.Subtitle, cur.Album, MoreScreens.ModeText);
        _bookmark.Visibility = cur.Book != null ? ViewStates.Visible : ViewStates.Gone;
        _play.SetImageDrawable(new IconDrawable(P.IsPlaying ? IconKind.Pause : IconKind.Play, Th.Fg));
        _shuffle.SetImageDrawable(new IconDrawable(IconKind.Shuffle, P.Shuffle ? Th.Accent : Th.FgFaint));
        _repeat.SetImageDrawable(new IconDrawable(P.Repeat == 2 ? IconKind.RepeatOne : IconKind.Repeat, P.Repeat > 0 ? Th.Accent : Th.FgFaint));

        var track = cur.Track;
        _like.Visibility = _dislike.Visibility = track != null ? ViewStates.Visible : ViewStates.Gone;
        if (track != null)
        {
            _like.SetImageDrawable(new IconDrawable(track.Rating > 0 ? IconKind.HeartFull : IconKind.Heart, track.Rating > 0 ? Th.Accent : Th.FgDim));
            _dislike.SetImageDrawable(new IconDrawable(IconKind.HeartBroken, track.Rating < 0 ? Th.Accent : Th.FgFaint));
        }

        if (cur.IsVideo)
        {
            _viz.Stop();
            _viz.Visibility = _mode.Visibility = ViewStates.Gone;
            _art.Visibility = ViewStates.Gone;
            if (_surface.Visibility != ViewStates.Visible)
            {
                _surface.Visibility = ViewStates.Visible;
                HalcyonApp.Engine.SetSurface(_surface);
            }
            FitVideo();
            if (P.IsPlaying) _a.Window!.AddFlags(WindowManagerFlags.KeepScreenOn); else _a.Window!.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            if (_surface.Visibility == ViewStates.Visible)
            {
                HalcyonApp.Engine.SetSurface(null);
                _surface.Visibility = ViewStates.Gone;
            }
            _a.Window!.ClearFlags(WindowManagerFlags.KeepScreenOn);
            var mode = App.Settings.Visualizer;
            if (mode != 0 && !_a.HasAudioPermission) mode = 0;
            _mode.Visibility = ViewStates.Visible;
            _mode.Text = VisualizerOptions.Styles[Math.Clamp(mode, 0, VisualizerOptions.Styles.Length - 1)];
            _viz.Mode = mode;
            _viz.SetCover(cur.ArtPath);
            _art.Visibility = ViewStates.Visible;
            _art.Alpha = mode == 0 ? 1f : 0.28f;   // the cover stays as a dim backdrop behind the visualizer
            if (_artFor != cur.ArtPath) { _artFor = cur.ArtPath; Art.Into(_art, cur.ArtPath, 1024); }
            if (mode != 0 && P.IsPlaying)
            {
                _viz.Visibility = ViewStates.Visible;
                if (!_viz.Start(HalcyonApp.Engine.AudioSessionId)) { _viz.Visibility = ViewStates.Gone; _art.Alpha = 1f; _mode.Text = "album art"; }
            }
            else if (mode == 0) { _viz.Stop(); _viz.Visibility = ViewStates.Gone; }
        }
        ShowLyricsPane();
        ApplyChrome();
        Tick();
    }

    /// <summary>Lyrics over the picture, when asked for (a song, not a video).</summary>
    void ShowLyricsPane()
    {
        var cur = P.Current;
        var song = cur != null && !cur.IsVideo;
        _lyricsChip.Visibility = song ? ViewStates.Visible : ViewStates.Gone;
        var show = song && App.Settings.ShowLyrics;
        _lyricsChip.SetTextColor(show ? Th.Accent : Color.White);
        _lyricsChip.Text = show ? "hide lyrics" : "lyrics";
        _lyrics.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        _mode.Visibility = show || !song ? ViewStates.Gone : ViewStates.Visible;
        if (show) _lyrics.Load(cur!);
    }

    public bool LyricsShown => _lyrics.Visibility == ViewStates.Visible;
    public LyricsPane LyricsView => _lyrics;

    /// <summary>Moves the seek bar and clock; called twice a second while the screen is on.</summary>
    public void Tick()
    {
        if (!IsOpen || P.Current == null) return;
        long pos = P.PositionMs, dur = P.DurationMs;
        _dur.Text = Fmt.Clock(dur);
        if (_dragging) return;
        _pos.Text = Fmt.Clock(pos);
        _seek.Progress = dur > 0 ? (int)Math.Clamp(pos * 1000 / dur, 0, 1000) : 0;
        if (LyricsShown) _lyrics.Follow(pos);
    }
}

/// <summary>Full-screen picture viewer with previous / next and a 4-second slideshow.</summary>
public class PictureViewer : FrameLayout
{
    readonly ImageView _image;
    readonly TextView _caption;
    List<PictureItem> _pics = new();
    int _index;
    bool _slideshow;
    readonly Action _advance;

    public PictureViewer(MainActivity a) : base(a)
    {
        SetBackgroundColor(Color.Black);
        Clickable = true;
        Visibility = ViewStates.Gone;
        _image = new ImageView(a);
        _image.SetScaleType(ImageView.ScaleType.FitCenter);
        AddView(_image, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Tap the left or right third to step; the middle starts or pauses the slideshow.
        var zones = new LinearLayout(a) { Orientation = Orientation.Horizontal };
        View Zone(Action act) { var v = new View(a) { Clickable = true }; v.Click += (_, _) => act(); return v; }
        zones.AddView(Zone(() => { _slideshow = false; Step(-1); }), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
        zones.AddView(Zone(() => { _slideshow = !_slideshow; Schedule(); ShowCaption(); }), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
        zones.AddView(Zone(() => { _slideshow = false; Step(1); }), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
        AddView(zones, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _caption = Ui.Text(a, "", 13, Color.White);
        _caption.SetPadding(Th.Dp(14), Th.Dp(10), Th.Dp(14), Th.Dp(14));
        _caption.SetBackgroundColor(Color.Argb(0x66, 0, 0, 0));
        AddView(_caption, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
        var close = Ui.Icon(a, IconKind.Close, Color.White, 22, Close, 12);
        AddView(close, new LayoutParams(Th.Dp(46), Th.Dp(46), GravityFlags.Top | GravityFlags.End));
        _advance = () => { if (_slideshow && IsOpen) Step(1); };
    }

    public bool IsOpen => Visibility == ViewStates.Visible;

    public void Open(List<PictureItem> pics, int index, bool slideshow)
    {
        if (pics.Count == 0) return;
        _pics = pics;
        _index = Math.Clamp(index, 0, pics.Count - 1);
        _slideshow = slideshow;
        Visibility = ViewStates.Visible;
        ShowCurrent();
    }

    public void Close()
    {
        _slideshow = false;
        HalcyonApp.Main.RemoveCallbacks(_advance);
        Visibility = ViewStates.Gone;
        _image.SetImageDrawable(null);
    }

    void Step(int delta)
    {
        if (_pics.Count == 0) return;
        _index = (_index + delta + _pics.Count) % _pics.Count;
        ShowCurrent();
    }

    void ShowCaption() =>
        _caption.Text = $"{_pics[_index].Name}   ·   {_index + 1} of {_pics.Count}{(_slideshow ? "   ·   slideshow" : "")}";

    void Schedule()
    {
        HalcyonApp.Main.RemoveCallbacks(_advance);
        if (_slideshow) HalcyonApp.Main.PostDelayed(_advance, 4000);
    }

    void ShowCurrent()
    {
        ShowCaption();
        Art.Into(_image, _pics[_index].File, 2048);
        Schedule();
    }
}
