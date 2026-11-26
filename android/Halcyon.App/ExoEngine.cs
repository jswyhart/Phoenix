using Android.Content;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Halcyon.Core;
using AFx = Android.Media.Audiofx;

namespace Halcyon.Droid;

/// <summary>ExoPlayer (Media3) behind the shared play queue: local files, direct server files, HLS and progressive streams.</summary>
public class ExoEngine : Java.Lang.Object, IMediaEngine, ISpeedEngine, IPlayerListener
{
    const int StateReady = 3, StateEnded = 4;
    readonly IExoPlayer _player;
    bool _opened;

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    /// <summary>Raised when the picture size of the current video becomes known (width, height).</summary>
    public event Action<int, int>? VideoSize;
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    public ExoEngine(Context context)
    {
        _player = new ExoPlayerBuilder(context).Build()!;
        var attrs = new AudioAttributes.Builder()!.SetUsage(C.UsageMedia)!.SetContentType(C.AudioContentTypeMusic)!.Build()!;
        _player.SetAudioAttributes(attrs, true);          // pause for calls / other players, resume after
        _player.SetHandleAudioBecomingNoisy(true);         // pause when headphones are unplugged
        _player.SetWakeMode(C.WakeModeNetwork);
        _player.AddListener(this);
    }

    float _speed = 1;
    /// <summary>Faster or slower playback (the pitch is kept), for podcasts and audiobooks.</summary>
    public float Speed
    {
        get => _speed;
        set { _speed = Math.Clamp(value, 0.5f, 3f); _player.SetPlaybackSpeed(_speed); }
    }

    public int AudioSessionId => _player.AudioSessionId;
    public bool IsBuffering => _player.PlaybackState == 2;

    // ---- the equalizer and volume levelling (settings → playback) ----

    AFx.Equalizer? _eq;
    AFx.LoudnessEnhancer? _boost;
    float _volume = 1, _level = 1;
    public string EqTrouble { get; private set; } = "";
    /// <summary>The levelling applied to the song playing, in dB (0 when off or the song carries no ReplayGain).</summary>
    public double LevelDb { get; private set; }

    /// <summary>
    /// Sets the phone's equalizer: your own ten bands (when on) plus the auto equalizer's adjustment for the song playing,
    /// read off at the phone's own band centres.
    /// </summary>
    public void ApplyEqualizer()
    {
        var s = App.Settings;
        try
        {
            var own = s.EqEnabled ? EqPresets.Current(s) : null;
            var auto = s.AutoEq && App.Player.Current?.Track is { } t && Halcyon.Services.AutoEq.LevelsFor(t) is { } l ? Halcyon.Services.AutoEq.Gains(l, s.AutoEqProfile) : null;
            if (own == null && auto == null) { if (_eq != null) _eq.SetEnabled(false); return; }
            _eq ??= new AFx.Equalizer(0, _player.AudioSessionId);
            var gains = own == null ? auto! : auto == null ? own : own.Zip(auto, (a, b) => Math.Clamp(a + b, -12, 12)).ToArray();
            var range = _eq.GetBandLevelRange()!;   // millibels
            for (short b = 0; b < _eq.NumberOfBands; b++)
            {
                var hz = _eq.GetCenterFreq(b) / 1000.0;
                var mb = (short)Math.Clamp(EqPresets.At(gains, hz) * 100, range[0], range[1]);
                _eq.SetBandLevel(b, mb);
            }
            _eq.SetEnabled(true);
            EqTrouble = "";
        }
        catch (Exception ex) { EqTrouble = "the phone's equalizer isn't available: " + ex.Message; }
    }

    /// <summary>How loud this song should be played: its ReplayGain, quieter by turning the volume down, louder through the loudness enhancer.</summary>
    void Level(string url)
    {
        var s = App.Settings;
        LevelDb = 0;
        if (s.Levelling && !url.Contains("://") && EqPresets.ReplayGain(url, s.LevelByAlbum) is { } db) LevelDb = db;
        _level = LevelDb < 0 ? (float)Math.Pow(10, LevelDb / 20) : 1f;
        try
        {
            if (LevelDb > 0)
            {
                _boost ??= new AFx.LoudnessEnhancer(_player.AudioSessionId);
                _boost.SetTargetGain((int)(LevelDb * 100));
                _boost.SetEnabled(true);
            }
            else if (_boost != null) _boost.SetEnabled(false);
        }
        catch { }
        _player.Volume = Math.Clamp(_volume * _level, 0f, 1f);
    }

    public void Load(string url, bool isVideo)
    {
        _opened = false;
        Level(url);
        ApplyEqualizer();
        VideoWidth = VideoHeight = 0;
        var uri = url.Contains("://") ? Android.Net.Uri.Parse(url)! : Android.Net.Uri.FromFile(new Java.IO.File(url))!;
        var b = new MediaItem.Builder()!.SetUri(uri)!;
        if (url.Contains(".m3u8")) b.SetMimeType("application/x-mpegURL");
        _player.SetMediaItem(b.Build()!);
        _player.Prepare();
        _player.PlayWhenReady = true;
    }

    public void Play() => _player.PlayWhenReady = true;
    public void Pause() => _player.PlayWhenReady = false;

    public void Stop()
    {
        _player.Stop();
        _player.ClearMediaItems();
        _opened = false;
    }

    public long PositionMs => Math.Max(0, _player.CurrentPosition);
    public long DurationMs => _player.Duration > 0 ? _player.Duration : 0;   // C.TIME_UNSET is negative
    public void SeekTo(long ms) => _player.SeekTo(ms);
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); _player.Volume = Math.Clamp(_volume * _level, 0f, 1f); }
    }

    public void SetSurface(SurfaceView? view)
    {
        if (view != null) _player.SetVideoSurfaceView(view); else _player.ClearVideoSurface();
    }

    // ---- IPlayerListener (called on the main thread) ----

    public void OnPlaybackStateChanged(int playbackState)
    {
        if (playbackState == StateReady && !_opened) { _opened = true; Opened?.Invoke(); }
        else if (playbackState == StateEnded) Ended?.Invoke();
    }

    public void OnPlayerError(PlaybackException? error) => Failed?.Invoke(error?.ErrorCodeName ?? error?.Message ?? "playback error");

    public void OnVideoSizeChanged(VideoSize? videoSize)
    {
        if (videoSize == null || videoSize.Width == 0) return;
        VideoWidth = videoSize.Width;
        VideoHeight = videoSize.Height;
        VideoSize?.Invoke(VideoWidth, VideoHeight);
    }
}
