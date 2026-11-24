using Android.Content;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Halcyon.Core;

namespace Halcyon.Droid;

/// <summary>ExoPlayer (Media3) behind the shared play queue: local files, direct server files, HLS and progressive streams.</summary>
public class ExoEngine : Java.Lang.Object, IMediaEngine, IPlayerListener
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

    public int AudioSessionId => _player.AudioSessionId;
    public bool IsBuffering => _player.PlaybackState == 2;

    public void Load(string url, bool isVideo)
    {
        _opened = false;
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
    public float Volume { get => _player.Volume; set => _player.Volume = Math.Clamp(value, 0f, 1f); }

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
