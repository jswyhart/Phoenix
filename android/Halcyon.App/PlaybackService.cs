using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Halcyon.Core;
using Android.Service.Media;
using AM = Android.Media;
using AMS = Android.Media.Session;

namespace Halcyon.Droid;

/// <summary>
/// Keeps playback alive when the app is in the background: a foreground service that owns the media session
/// (lock screen, headset buttons, Bluetooth) and the media notification with previous / play-pause / next.
/// It is also what Android Auto (and other media browsers) talk to: they browse the collection through it
/// (playlists, artists, albums, liked songs), play what is chosen on the car's screen, and ask it to play by voice.
/// </summary>
[Service(Exported = true, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
public class PlaybackService : MediaBrowserService
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
        if (_instance != null && _running) { _instance.Refresh(); return; }
        if (_running) return;
        _running = true;
        try { _app.StartForegroundService(new Intent(_app, typeof(PlaybackService))); }
        catch { _running = false; }   // not allowed right now (app in background): playback itself still works
    }

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
        SessionToken = _session.SessionToken;   // what Android Auto connects to
        // before anything plays, the car can still start something: from its lists, or by voice
        _session.SetPlaybackState(new AMS.PlaybackState.Builder().SetActions(Actions)!
            .SetState(AMS.PlaybackStateCode.None, 0, 0f)!.Build());
        Refresh();
    }

    const long Actions = AMS.PlaybackState.ActionPlay | AMS.PlaybackState.ActionPause | AMS.PlaybackState.ActionPlayPause | AMS.PlaybackState.ActionSkipToNext
                         | AMS.PlaybackState.ActionSkipToPrevious | AMS.PlaybackState.ActionSeekTo | AMS.PlaybackState.ActionStop
                         | AMS.PlaybackState.ActionPlayFromMediaId | AMS.PlaybackState.ActionPlayFromSearch;

    // ---------- Android Auto: browsing the collection ----------

    const char Sep = '\u001e';
    const string Root = "root";

    public override BrowserRoot? OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints) => new(Root, null);

    public override void OnLoadChildren(string parentId, Result result)
    {
        var items = new List<AM.Browse.MediaBrowser.MediaItem>();
        try { items = Children(parentId); } catch { /* the collection is changing: an empty list for now */ }
        result.SendResult(new Android.Runtime.JavaList<AM.Browse.MediaBrowser.MediaItem>(items));
    }

    static AM.Browse.MediaBrowser.MediaItem Folder(string id, string title, string sub = "") =>
        new(new AM.MediaDescription.Builder().SetMediaId(id)!.SetTitle(title)!.SetSubtitle(sub)!.Build()!, AM.Browse.MediaItemFlags.Browsable);

    static AM.Browse.MediaBrowser.MediaItem Song(string context, Track t) =>
        new(new AM.MediaDescription.Builder().SetMediaId($"play{Sep}{context}{Sep}{t.Id}")!.SetTitle(t.Title)!
            .SetSubtitle(string.Join("  ·  ", new[] { t.Artist, t.Album }.Where(x => x != "")))!.Build()!, AM.Browse.MediaItemFlags.Playable);

    static AM.Browse.MediaBrowser.MediaItem ShuffleAll(string context, int count) =>
        new(new AM.MediaDescription.Builder().SetMediaId($"shuffle{Sep}{context}")!.SetTitle("Shuffle all")!.SetSubtitle(Fmt.Plural(count, "song"))!.Build()!, AM.Browse.MediaItemFlags.Playable);

    /// <summary>What a list in the car shows: the top level, then playlists, artists, albums and songs.</summary>
    static List<AM.Browse.MediaBrowser.MediaItem> Children(string parentId)
    {
        var lib = App.Lib;
        var list = new List<AM.Browse.MediaBrowser.MediaItem>();
        var parts = parentId.Split(Sep);
        switch (parts[0])
        {
            case Root:
                list.Add(Folder("liked", "Liked songs"));
                list.Add(Folder("playlists", "Playlists"));
                list.Add(Folder("artists", "Artists"));
                list.Add(Folder("albums", "Albums"));
                list.Add(Folder("recent", "Recently added"));
                break;
            case "liked":
                var liked = SongsOf("liked");
                if (liked.Count > 1) list.Add(ShuffleAll("liked", liked.Count));
                list.AddRange(liked.Take(400).Select(t => Song("liked", t)));
                break;
            case "playlists":
                list.AddRange(lib.Playlists.Where(p => p.TrackIds.Count > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => Folder($"playlist{Sep}{p.Id}", p.Name, Fmt.Plural(p.TrackIds.Count, "song"))));
                break;
            case "artists":
                list.AddRange(lib.Albums.GroupBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => Folder($"artist{Sep}{g.Key}", g.Key, Fmt.Plural(g.Count(), "album"))));
                break;
            case "albums":
                list.AddRange(lib.Albums.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).Select(a => Folder($"album{Sep}{a.Key}", a.Title, a.Artist)));
                break;
            case "recent":
                list.AddRange(lib.Albums.OrderByDescending(a => a.DateAdded).Take(60).Select(a => Folder($"album{Sep}{a.Key}", a.Title, a.Artist)));
                break;
            case "artist" when parts.Length > 1:
                var albums = lib.Albums.Where(a => string.Equals(a.Artist, parts[1], StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Year).ToList();
                var all = albums.SelectMany(a => a.Tracks).ToList();
                if (all.Count > 1) list.Add(ShuffleAll(parentId, all.Count));
                list.AddRange(albums.Select(a => Folder($"album{Sep}{a.Key}", a.Title, a.Year > 0 ? a.Year.ToString() : "")));
                break;
            case "album" or "playlist" when parts.Length > 1:
                var songs = SongsOf(parentId);
                if (songs.Count > 1) list.Add(ShuffleAll(parentId, songs.Count));
                list.AddRange(songs.Select(t => Song(parentId, t)));
                break;
        }
        return list;
    }

    /// <summary>The songs a list in the car stands for, in its order.</summary>
    static List<Track> SongsOf(string context)
    {
        var lib = App.Lib;
        var parts = context.Split(Sep);
        return parts[0] switch
        {
            "liked" => lib.Tracks.Where(t => t.Rating > 0).OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album).ThenBy(t => t.TrackNo).ToList(),
            "album" when parts.Length > 1 => lib.Albums.FirstOrDefault(a => a.Key == parts[1])?.Tracks.ToList() ?? new(),
            "playlist" when parts.Length > 1 && long.TryParse(parts[1], out var id) => lib.Playlists.FirstOrDefault(p => p.Id == id) is { } p ? lib.TracksOf(p) : new(),
            "artist" when parts.Length > 1 => lib.Albums.Where(a => string.Equals(a.Artist, parts[1], StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Year).SelectMany(a => a.Tracks).ToList(),
            _ => new()
        };
    }

    /// <summary>A song or "shuffle all" chosen on the car's screen.</summary>
    static void PlayFromId(string mediaId)
    {
        var parts = mediaId.Split(Sep);
        if (parts[0] == "shuffle" && parts.Length > 1)
        {
            var context = string.Join(Sep, parts.Skip(1));
            var songs = SongsOf(context).OrderBy(_ => Random.Shared.Next()).ToList();
            if (songs.Count > 0) App.Player.Play(songs.Select(Player.From));
        }
        else if (parts[0] == "play" && parts.Length >= 3 && long.TryParse(parts[^1], out var id))
        {
            var context = string.Join(Sep, parts.Skip(1).Take(parts.Length - 2));
            var songs = SongsOf(context);
            var start = Math.Max(0, songs.FindIndex(t => t.Id == id));
            if (songs.Count == 0 && App.Lib.ById.TryGetValue(id, out var one)) songs = new() { one };
            if (songs.Count > 0) App.Player.Play(songs.Select(Player.From), start);
        }
    }

    /// <summary>"Play … on Phoenix": an artist first, then an album, then songs whose names match; nothing said plays liked songs.</summary>
    static void PlayFromSearch(string? query)
    {
        var lib = App.Lib;
        var q = (query ?? "").Trim();
        List<Track> songs;
        if (q == "")
            songs = (lib.Tracks.Any(t => t.Rating > 0) ? lib.Tracks.Where(t => t.Rating > 0) : lib.Tracks).OrderBy(_ => Random.Shared.Next()).ToList();
        else
        {
            bool Has(string text) => text.Contains(q, StringComparison.OrdinalIgnoreCase);
            var artist = lib.Albums.Where(a => Has(a.Artist)).SelectMany(a => a.Tracks).ToList();
            var album = lib.Albums.FirstOrDefault(a => Has(a.Title))?.Tracks.ToList() ?? new();
            songs = artist.Count > 0 ? artist.OrderBy(_ => Random.Shared.Next()).ToList()
                  : album.Count > 0 ? album
                  : lib.Tracks.Where(t => Has(t.Title)).ToList();
        }
        if (songs.Count > 0) App.Player.Play(songs.Select(Player.From));
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

        _session.SetPlaybackState(new AMS.PlaybackState.Builder()
            .SetActions(Actions)!
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
        public override void OnPlayFromMediaId(string? mediaId, Bundle? extras) { if (mediaId != null) PlayFromId(mediaId); }
        public override void OnPlayFromSearch(string? query, Bundle? extras) => PlayFromSearch(query);
    }
}
