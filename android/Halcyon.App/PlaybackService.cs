using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Halcyon.Core;
using AM = Android.Media;
using AMS = Android.Media.Session;

namespace Halcyon.Droid;

/// <summary>
/// Keeps playback alive when the app is in the background: a foreground service that owns the media session
/// (lock screen, headset buttons, Bluetooth) and the media notification with previous / play-pause / next.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public class PlaybackService : Service
{
    const string Channel = "playback";
    const int NoteId = 1;
    const string ActToggle = "halcyon.toggle", ActNext = "halcyon.next", ActPrev = "halcyon.prev", ActStop = "halcyon.stop";

    static Context _app = null!;
    static bool _running;
    static PlaybackService? _instance;

    AMS.MediaSession? _session;
    Bitmap? _art;
    string _artFor = "";

    /// <summary>Starts and stops the service as the queue starts and stops.</summary>
    public static void Watch(Context app)
    {
        _app = app;
        App.Player.CurrentChanged += Sync;
        App.Player.StateChanged += Sync;
        App.Lib.RatingsChanged += Sync;
    }

    static void Sync()
    {
        if (App.Player.Current == null) { _instance?.Shutdown(); return; }
        if (_instance != null) { _instance.Refresh(); return; }
        if (_running) return;
        _running = true;
        try { _app.StartForegroundService(new Intent(_app, typeof(PlaybackService))); }
        catch { _running = false; }   // not allowed right now (app in background): playback itself still works
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        var ch = new NotificationChannel(Channel, "Playback", NotificationImportance.Low) { Description = "Now playing controls" };
        ch.SetShowBadge(false);
        nm.CreateNotificationChannel(ch);

        _session = new AMS.MediaSession(this, "Halcyon");
        _session.SetCallback(new SessionCallback());
        _session.SetSessionActivity(PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).PutExtra("nowplaying", true), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        _session.Active = true;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActToggle: App.Player.Toggle(); break;
            case ActNext: App.Player.Next(); break;
            case ActPrev: App.Player.Previous(); break;
            case ActStop: App.Player.Stop(); break;
        }
        if (App.Player.Current == null) { Shutdown(); return StartCommandResult.NotSticky; }
        Refresh();
        return StartCommandResult.NotSticky;
    }

    PendingIntent Act(string action, int code) =>
        PendingIntent.GetService(this, code, new Intent(this, typeof(PlaybackService)).SetAction(action), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    void Refresh()
    {
        var p = App.Player;
        var cur = p.Current;
        if (cur == null || _session == null) return;

        if (_artFor != cur.ArtPath)
        {
            _artFor = cur.ArtPath;
            _art = null;
            var want = cur.ArtPath;
            _ = Task.Run(async () =>
            {
                var bmp = await Art.LoadAsync(want, 512);
                HalcyonApp.Main.Post(() => { if (_artFor == want && bmp != null && _instance == this) { _art = bmp; Refresh(); } });
            });
        }

        var meta = new AM.MediaMetadata.Builder()
            .PutString(AM.MediaMetadata.MetadataKeyTitle, cur.Title)!
            .PutString(AM.MediaMetadata.MetadataKeyArtist, cur.Subtitle)!
            .PutString(AM.MediaMetadata.MetadataKeyAlbum, cur.Album)!
            .PutLong(AM.MediaMetadata.MetadataKeyDuration, p.DurationMs > 0 ? p.DurationMs : -1)!;
        if (_art != null) meta.PutBitmap(AM.MediaMetadata.MetadataKeyAlbumArt, _art);
        _session.SetMetadata(meta.Build());

        var actions = AMS.PlaybackState.ActionPlay | AMS.PlaybackState.ActionPause | AMS.PlaybackState.ActionPlayPause | AMS.PlaybackState.ActionSkipToNext
                      | AMS.PlaybackState.ActionSkipToPrevious | AMS.PlaybackState.ActionSeekTo | AMS.PlaybackState.ActionStop;
        _session.SetPlaybackState(new AMS.PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(p.IsPlaying ? AMS.PlaybackStateCode.Playing : AMS.PlaybackStateCode.Paused, p.PositionMs, p.IsPlaying ? 1f : 0f)!
            .Build());

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).PutExtra("nowplaying", true), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var b = new Notification.Builder(this, Channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_play)!
            .SetContentTitle(cur.Title)!
            .SetContentText(cur.Subtitle)!
            .SetSubText(cur.Album)!
            .SetContentIntent(open)!
            .SetVisibility(NotificationVisibility.Public)!
            .SetOngoing(p.IsPlaying)!
            .SetShowWhen(false)!
            .SetDeleteIntent(Act(ActStop, 4))!
            .SetStyle(new Notification.MediaStyle().SetMediaSession(_session.SessionToken)!.SetShowActionsInCompactView(0, 1, 2))!;
        if (_art != null) b.SetLargeIcon(_art);
        b.AddAction(NoteAction(Android.Resource.Drawable.IcMediaPrevious, "Previous", Act(ActPrev, 1)));
        b.AddAction(p.IsPlaying
            ? NoteAction(Android.Resource.Drawable.IcMediaPause, "Pause", Act(ActToggle, 2))
            : NoteAction(Android.Resource.Drawable.IcMediaPlay, "Play", Act(ActToggle, 2)));
        b.AddAction(NoteAction(Android.Resource.Drawable.IcMediaNext, "Next", Act(ActNext, 3)));
        var note = b.Build();

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) StartForeground(NoteId, note, ForegroundService.TypeMediaPlayback);
            else StartForeground(NoteId, note);
        }
        catch { /* foreground start refused; keep the session, skip the notification */ }
    }

    Notification.Action NoteAction(int icon, string title, PendingIntent intent) =>
        new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, icon), title, intent).Build()!;

    void Shutdown()
    {
        _running = false;
        _instance = null;
        try { StopForeground(StopForegroundFlags.Remove); } catch { }
        StopSelf();
    }

    public override void OnDestroy()
    {
        _running = false;
        if (_instance == this) _instance = null;
        if (_session != null) { _session.Active = false; _session.Release(); _session = null; }
        base.OnDestroy();
    }

    /// <summary>Swiping the app away stops playback, as closing the window does on Windows.</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        App.Player.Stop();
        try { App.Settings.Save(); } catch { }
        base.OnTaskRemoved(rootIntent);
    }

    class SessionCallback : AMS.MediaSession.Callback
    {
        public override void OnPlay() { if (!App.Player.IsPlaying) App.Player.Toggle(); }
        public override void OnPause() { if (App.Player.IsPlaying) App.Player.Toggle(); }
        public override void OnSkipToNext() => App.Player.Next();
        public override void OnSkipToPrevious() => App.Player.Previous();
        public override void OnSeekTo(long pos) => App.Player.Seek(pos);
        public override void OnStop() => App.Player.Stop();
    }
}
