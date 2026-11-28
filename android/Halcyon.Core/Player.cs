using Halcyon.Services;

namespace Halcyon.Core;

/// <summary>What the play queue drives: ExoPlayer on Android, a fake in tests. Events arrive on the UI thread.</summary>
public interface IMediaEngine
{
    /// <summary>Opens a URL or file path and starts playing it.</summary>
    void Load(string url, bool isVideo);
    void Play();
    void Pause();
    void Stop();
    long PositionMs { get; }
    /// <summary>Length of the loaded stream, or 0 when it is unknown.</summary>
    long DurationMs { get; }
    void SeekTo(long ms);
    float Volume { get; set; }
    event Action? Opened;
    event Action? Ended;
    event Action<string>? Failed;
}

/// <summary>An engine that can play faster or slower (podcasts and audiobooks).</summary>
public interface ISpeedEngine
{
    float Speed { get; set; }
}

/// <summary>Play queue plus transport over a single media engine, for audio and video.</summary>
public class Player
{
    readonly IMediaEngine _me;
    int _index = -1;
    List<int> _order = new();
    int _failStreak;
    long _resumeMs;
    bool _shuffle;
    long _offset;        // where the current (server-transcoded) stream started within the item
    int _gen;            // bumps whenever a different item starts, so late URL lookups are dropped
    bool _triedFallback;
    double _vol = 1;        // the volume you set
    double _fade = 1;       // the sleep timer turning it down at the end
    double _speed = 1;
    Timer? _sleepTimer;

    public List<PlayItem> Queue { get; } = new();
    public PlayItem? Current => _index >= 0 && _index < Queue.Count ? Queue[_index] : null;
    public int CurrentIndex => _index;
    public bool IsPlaying { get; private set; }
    public int Repeat { get; set; }   // 0 off, 1 all, 2 one

    public bool Shuffle
    {
        get => _shuffle;
        set { _shuffle = value; BuildOrder(Math.Max(0, _index)); }
    }

    public event Action? CurrentChanged;
    public event Action? StateChanged;
    public event Action? QueueChanged;
    public event Action<string>? Error;

    public Player(IMediaEngine engine)
    {
        _me = engine;
        _shuffle = App.Settings.Shuffle;
        Repeat = App.Settings.Repeat;
        _vol = App.Settings.Volume;
        _me.Volume = (float)_vol;
        _me.Opened += () =>
        {
            _failStreak = 0;
            if (_me is ISpeedEngine fast) fast.Speed = (float)_speed;
            var cur = Current;
            if (cur == null) return;
            if (_me.DurationMs > 0 && cur.DurationMs <= 0) cur.DurationMs = _me.DurationMs;
            if (_resumeMs > 0) { var to = _resumeMs; _resumeMs = 0; Seek(to); }
            StateChanged?.Invoke();
        };
        _me.Ended += () =>
        {
            var cur = Current;
            if (cur == null) return;
            Listening.Left(cur, 0, true);
            if (cur.Track is { } t) { t.PlayCount++; t.LastPlayed = Fmt.Now; App.Lib.Db.SaveStats(t); }
            if (cur.Episode is { } e) { e.Played = true; e.PositionMs = 0; App.Lib.Db.SaveEpisodeState(e); e.Raise(); }
            if (cur.Book is { } b && cur.BookPart == b.Parts.Count - 1)
            {
                // last file of the book: it is finished, and the next listen starts over
                b.Finished = true; b.Part = 0; b.PositionMs = 0; b.Progress = 1;
                App.Lib.Db.SaveBookState(b);
                b.Raise();
            }
            Next(auto: true);
        };
        _me.Failed += OnFailed;
    }

    void OnFailed(string message)
    {
        var cur = Current;
        if (cur == null) return;
        if (cur.Fallback != null && !_triedFallback)
        {
            // e.g. the phone can't decode the original file: ask the server to convert it instead.
            _triedFallback = true;
            _resumeMs = Math.Max(_resumeMs, PositionMs);
            _offset = 0;
            try { _me.Load(cur.Fallback(), cur.IsVideo); if (!IsPlaying) _me.Pause(); return; }
            catch (Exception ex) { message = ex.Message; }
        }
        if (cur.FallbackAsync != null && !_triedFallback)
        {
            _triedFallback = true;
            _resumeMs = Math.Max(_resumeMs, PositionMs);
            _offset = 0;
            Error?.Invoke("downloading…");
            _ = DownloadFallbackAsync(cur, _gen, message);
            return;
        }
        Error?.Invoke($"can't play \"{cur.Title}\": {message}");
        if (++_failStreak < 3 && Queue.Count > 1) Next(auto: true); else Stop();
    }

    async Task DownloadFallbackAsync(PlayItem item, int gen, string original)
    {
        try
        {
            var file = await item.FallbackAsync!();
            if (gen != _gen) return;
            Error?.Invoke("");
            _me.Load(file, item.IsVideo);
            if (!IsPlaying) _me.Pause();
        }
        catch (Exception ex)
        {
            if (gen != _gen) return;
            Error?.Invoke($"can't play \"{item.Title}\": {original} ({ex.Message})");
            if (++_failStreak < 3 && Queue.Count > 1) Next(auto: true); else Stop();
        }
    }

    public double Volume { get => _vol; set { _vol = value; _me.Volume = (float)(_vol * _fade); App.Settings.Volume = value; if (_onCast) _ = Quiet(Cast!.SetVolumeAsync(value)); } }

    // ---------- sleep timer ----------

    /// <summary>When playback will be paused by the sleep timer, if it is set.</summary>
    public DateTime? SleepAt { get; private set; }
    /// <summary>The sleep timer is set to "when this one ends".</summary>
    public bool SleepAtEnd { get; private set; }
    public bool SleepOn => SleepAt != null || SleepAtEnd;

    /// <summary>Sets the sleep timer: after a time, at the end of what is playing, or (both empty) off.</summary>
    public void SetSleep(TimeSpan? after, bool atEnd = false)
    {
        SleepAt = after is { } a ? DateTime.Now + a : null;
        SleepAtEnd = atEnd && after == null;
        _fade = 1;
        _me.Volume = (float)_vol;
        _sleepTimer?.Dispose();
        // its own clock, so it still fires when the screen is off and nothing is being drawn
        _sleepTimer = SleepAt == null ? null : new Timer(_ => App.Ui(SleepTick), null, 1000, 1000);
        StateChanged?.Invoke();
    }

    public string SleepText => SleepAtEnd ? "sleep: when this ends"
        : SleepAt is { } at ? $"sleep in {Math.Max(1, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes))} min" : "";

    /// <summary>Turns the sound down over the last seconds, then pauses.</summary>
    public void SleepTick()
    {
        if (SleepAt is not { } at) return;
        var left = (at - DateTime.Now).TotalSeconds;
        if (left > 12) return;
        if (left > 0) { _fade = Math.Clamp(left / 12, 0, 1); _me.Volume = (float)(_vol * _fade); return; }
        SleepAt = null;
        _sleepTimer?.Dispose();
        _sleepTimer = null;
        if (IsPlaying) Toggle();
        _fade = 1;
        _me.Volume = (float)_vol;
        StateChanged?.Invoke();
    }

    // ---------- playback speed ----------

    /// <summary>Speed can be changed for anything but radio, when the engine can do it.</summary>
    public bool CanSpeed => Current is { Station: null } && _me is ISpeedEngine && !_onCast;

    static string? SpeedKey(PlayItem? i) => i?.Episode is { } e ? "podcast:" + e.PodcastId : i?.Book is { } b ? "book:" + b.Id : null;

    /// <summary>The speed of what is playing. A podcast series or an audiobook remembers its own; everything else starts at normal speed.</summary>
    public double Speed
    {
        get => CanSpeed ? _speed : 1;
        set
        {
            if (!CanSpeed) return;
            _speed = Math.Clamp(value, 0.5, 3);
            ((ISpeedEngine)_me).Speed = (float)_speed;
            if (SpeedKey(Current) is { } key)
            {
                if (Math.Abs(_speed - 1) < 0.01) App.Settings.Speeds.Remove(key); else App.Settings.Speeds[key] = _speed;
            }
            StateChanged?.Invoke();
        }
    }

    void LeaveCurrent()
    {
        if (_cued) return;   // put back at start and not played: nothing was heard
        if (Current is { Track: not null } c) Listening.Left(c, PositionMs, false);
    }

    // ---------- picking up where you left off ----------

    bool _cued;
    long _cuedMs;

    /// <summary>The queue was put back at start and nothing has played yet (play starts it where it was).</summary>
    public bool IsCued => _cued;

    /// <summary>Puts a queue back without playing it: the song is shown, paused at where it was, and play carries on from there.</summary>
    public void Cue(IEnumerable<PlayItem> items, int index, long positionMs)
    {
        Queue.Clear();
        Queue.AddRange(items);
        if (Queue.Count == 0) return;
        _index = Math.Clamp(index, 0, Queue.Count - 1);
        Queue[_index].IsCurrent = true;
        BuildOrder(_index);
        _cued = true;
        // a song that was all but finished starts again from the top
        var length = Queue[_index].DurationMs;
        _cuedMs = length > 0 && positionMs > length - 1500 ? 0 : Math.Max(0, positionMs);
        IsPlaying = false;
        QueueChanged?.Invoke();
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public long PositionMs => Current == null ? 0 : _cued ? _cuedMs : _onCast && Cast != null ? (long)(Cast.Position * 1000) : _offset + _me.PositionMs;

    // A transcoded stream doesn't know its real length, so trust the library's duration for those.
    public long DurationMs => _onCast && Cast is { Duration: > 0 } cast ? (long)(cast.Duration * 1000)
        : Current is { SeekUrl: not null, DurationMs: > 0 } c ? c.DurationMs
        : _me.DurationMs > 0 ? _me.DurationMs : Current?.DurationMs ?? 0;

    public void Seek(long ms)
    {
        var cur = Current;
        if (cur == null) return;
        if (ms < 0) ms = 0;
        if (_cued) { _cuedMs = ms; StateChanged?.Invoke(); return; }
        if (_onCast) { _ = Quiet(Cast!.SeekAsync(ms / 1000.0)); return; }
        if (cur.SeekUrl == null) { _me.SeekTo(ms); StateChanged?.Invoke(); return; }
        // Not seekable in place: ask the server for a new stream that begins at the target time.
        if (cur.DurationMs > 0 && ms > cur.DurationMs - 2000) ms = Math.Max(0, cur.DurationMs - 2000);
        _offset = ms;
        _me.Load(cur.SeekUrl(ms), cur.IsVideo);
        if (!IsPlaying) _me.Pause();
        StateChanged?.Invoke();
    }

    void BuildOrder(int first)
    {
        _order = Enumerable.Range(0, Queue.Count).ToList();
        if (!_shuffle || Queue.Count == 0 || Queue[0].Book != null) return;   // a book always plays in order
        var rnd = new Random();
        _order = _order.OrderBy(_ => rnd.Next()).ToList();
        _order.Remove(first);
        _order.Insert(0, first);
    }

    // ---------- casting to another device ----------

    bool _onCast;   // the current item is playing on another device

    /// <summary>The speaker or TV everything is being played on, if any.</summary>
    public ICastDevice? Cast { get; private set; }
    public bool OnCast => _onCast;

    /// <summary>Sends playback to a device on the network; what is playing carries on there from the same place.</summary>
    public void StartCast(ICastDevice device)
    {
        StopCast(resumeHere: false);
        Cast = device;
        device.Ended += () => App.Ui(() =>
        {
            if (!_onCast || !ReferenceEquals(Cast, device)) return;
            if (Current is { Track: not null } c) Listening.Left(c, PositionMs, true);
            Next(auto: true);
        });
        device.Failed += msg => App.Ui(() => Error?.Invoke($"{device.Name}: {msg}"));
        device.Changed += () => App.Ui(() => { if (_onCast && ReferenceEquals(Cast, device) && IsPlaying != device.Playing && device.Loaded) { IsPlaying = device.Playing; StateChanged?.Invoke(); } });
        if (Current != null) PlayAt(_index, PositionMs);
        StateChanged?.Invoke();
    }

    /// <summary>Brings playback back to this computer (paused, at the same place).</summary>
    public void StopCast(bool resumeHere = true)
    {
        var device = Cast;
        if (device == null) return;
        var at = PositionMs;
        var was = _onCast;
        Cast = null;
        _onCast = false;
        _ = device.StopAsync().ContinueWith(_ => device.Dispose());
        if (was && resumeHere && Current != null)
        {
            PlayAt(_index, at);
            if (IsPlaying) Toggle();
        }
        StateChanged?.Invoke();
    }

    async Task StartOnCast(PlayItem item, long startMs, int gen)
    {
        var device = Cast;
        if (device == null) return;
        try
        {
            if (item.Resolve != null) item.Url = await item.Resolve();
            if (gen != _gen) return;
            await device.LoadAsync(item, startMs / 1000.0);
            if (!ReferenceEquals(Current, item) || !ReferenceEquals(Cast, device)) return;
            _failStreak = 0;
            await device.SetVolumeAsync(_me.Volume);
            if (!IsPlaying) await device.PauseAsync();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(Current, item)) return;
            Error?.Invoke($"couldn't play on {device.Name}: {ex.Message}");
        }
    }

    static async Task Quiet(Task t) { try { await t; } catch { } }

    // ---------- smart dj ----------

    /// <summary>The song a Smart DJ mix is built around, while one is playing.</summary>
    public Track? DjSeed { get; private set; }

    /// <summary>Starts an endless mix of music like the given song.</summary>
    public void PlaySmartDj(Track seed)
    {
        var mix = App.Lib.SmartDj(seed, new[] { seed }, 30);
        Play(new[] { seed }.Concat(mix).Select(From));
        DjSeed = seed;
    }

    /// <summary>Near the end of a mix, more like the seed is added.</summary>
    void RefillDj()
    {
        if (DjSeed == null) return;
        var pos = _order.IndexOf(_index);
        if (_order.Count - pos > 3) return;
        var have = Queue.Where(q => q.Track != null).Select(q => q.Track!).ToList();
        // recent songs must not come straight back, but an old mix may repeat itself eventually
        var more = App.Lib.SmartDj(DjSeed, have.Skip(Math.Max(0, have.Count - 150)), 20);
        if (more.Count > 0) Enqueue(more.Select(From));
    }

    public void Play(IEnumerable<PlayItem> items, int start = 0)
    {
        DjSeed = null;
        SaveEpisodePosition();
        if (Current != null) Current.IsCurrent = false;
        _index = -1;
        Queue.Clear();
        Queue.AddRange(items);
        QueueChanged?.Invoke();
        if (Queue.Count == 0) { Stop(); return; }
        start = Math.Clamp(start, 0, Queue.Count - 1);
        BuildOrder(start);
        _failStreak = 0;
        PlayAt(start);
    }

    public void Enqueue(IEnumerable<PlayItem> items)
    {
        var wasEmpty = Queue.Count == 0;
        foreach (var i in items) { Queue.Add(i); _order.Add(Queue.Count - 1); }
        QueueChanged?.Invoke();
        if (wasEmpty && Queue.Count > 0) { BuildOrder(0); PlayAt(0); }
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        if (index == _index) { if (Queue.Count == 1) { Queue.Clear(); QueueChanged?.Invoke(); Stop(); return; } Next(); }
        // Next() may have wrapped around to this very item when it was the only other choice.
        if (index == _index) return;
        Queue.RemoveAt(index);
        if (index < _index) _index--;
        _order.Remove(index);
        for (var i = 0; i < _order.Count; i++) if (_order[i] > index) _order[i]--;
        QueueChanged?.Invoke();
    }

    public void PlayAt(int index) => PlayAt(index, -1);

    /// <summary>Plays an item of the queue, from <paramref name="startMs"/> if it is given (moving to or from a cast device).</summary>
    public void PlayAt(int index, long startMs)
    {
        if (index < 0 || index >= Queue.Count) return;
        LeaveCurrent();
        _cued = false;
        SaveEpisodePosition();
        if (Current != null) Current.IsCurrent = false;
        _index = index;
        var item = Queue[index];
        item.IsCurrent = true;
        _resumeMs = item.Episode is { Played: false } e ? e.PositionMs : 0;
        if (item.Book is { } book)
        {
            if (book.Part == item.BookPart && !book.Finished) _resumeMs = book.PositionMs;
            else { book.Part = item.BookPart; book.PositionMs = 0; }
        }
        if (startMs >= 0) _resumeMs = startMs;
        _offset = 0;
        _triedFallback = false;
        IsPlaying = true;
        var gen = ++_gen;
        item.Logged = false;
        _speed = SpeedKey(item) is { } speedKey && App.Settings.Speeds.TryGetValue(speedKey, out var kept) ? Math.Clamp(kept, 0.5, 3) : 1;
        if (_me is ISpeedEngine fast) fast.Speed = (float)_speed;
        Listening.Started(item);
        if (item.Track is { } t) { t.LastPlayed = Fmt.Now; App.Lib.Db.SaveStats(t); }
        RefillDj();
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
        if (Cast != null && CastMedia.Can(item))
        {
            // playing on another device: this computer stays quiet
            _me.Stop();
            _onCast = true;
            _ = StartOnCast(item, _resumeMs, gen);
            _resumeMs = 0;
            return;
        }
        if (_onCast) { _ = Quiet(Cast?.StopAsync() ?? Task.CompletedTask); _onCast = false; }
        _ = StartAsync(item, gen);
    }

    async Task StartAsync(PlayItem item, int gen)
    {
        try
        {
            if (item.Resolve != null)
            {
                _me.Stop();   // silence the previous item while the server is asked how to play this one
                item.Url = await item.Resolve();
                if (gen != _gen) return;
            }
            _me.Load(item.Url, item.IsVideo);
            if (!IsPlaying) _me.Pause();
        }
        catch (Exception ex)
        {
            if (gen == _gen) OnFailed(ex.Message);
        }
    }

    public void Toggle()
    {
        if (Current == null) return;
        if (_cued) { var at = _cuedMs; PlayAt(_index, at); return; }   // the queue put back at start: carry on where it was
        if (_onCast) _ = Quiet(IsPlaying ? Cast!.PauseAsync() : Cast!.PlayAsync());
        else if (IsPlaying) _me.Pause(); else _me.Play();
        IsPlaying = !IsPlaying;
        if (!IsPlaying) SaveEpisodePosition();
        StateChanged?.Invoke();
    }

    public void Next(bool auto = false)
    {
        // "stop after this one": the next item is made ready, but left paused
        var sleep = auto && SleepAtEnd;
        if (sleep) SleepAtEnd = false;
        Advance(auto);
        if (sleep && IsPlaying) Toggle();
    }

    void Advance(bool auto)
    {
        if (Queue.Count == 0) return;
        if (auto && Repeat == 2) { if (_onCast) { PlayAt(_index); return; } if (Current != null) Current.Logged = false; Seek(0); _me.Play(); return; }
        var pos = _order.IndexOf(_index);
        if (pos + 1 < _order.Count) PlayAt(_order[pos + 1]);
        else if (Repeat == 1 || !auto) { BuildOrder(_shuffle ? new Random().Next(Queue.Count) : 0); PlayAt(_order[0]); }
        else Stop();
    }

    public void Previous()
    {
        if (Queue.Count == 0) return;
        if (PositionMs > 3000) { Seek(0); return; }
        var pos = _order.IndexOf(_index);
        if (pos > 0) PlayAt(_order[pos - 1]); else Seek(0);
    }

    public void Stop()
    {
        DjSeed = null;
        LeaveCurrent();
        _cued = false;
        SaveEpisodePosition();
        _gen++;
        _me.Stop();
        if (_onCast) { _ = Quiet(Cast?.StopAsync() ?? Task.CompletedTask); _onCast = false; }
        if (Current != null) Current.IsCurrent = false;
        _index = -1;
        _offset = 0;
        IsPlaying = false;
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>Records where the current podcast episode or audiobook is, so it can be picked up later.</summary>
    public void SaveEpisodePosition()
    {
        if (Current is { Book: { Finished: false } b } item)
        {
            // While a part is still opening the engine reports 0; keep the place it is about to resume from.
            b.Part = item.BookPart;
            b.PositionMs = _resumeMs > 0 ? _resumeMs : PositionMs;
            var before = b.Parts.Take(b.Part).Sum(p => p.DurationMs);
            b.Progress = b.DurationMs > 0 ? Math.Clamp((before + b.PositionMs) / (double)b.DurationMs, 0, 1) : 0;
            b.LastOpened = Fmt.Now;
            App.Lib.Db.SaveBookState(b);
            b.Raise();
        }
        if (Current?.Episode is not { } e || e.Played) return;
        e.PositionMs = PositionMs;
        App.Lib.Db.SaveEpisodeState(e);
    }

    // ---------- item factories ----------

    public static PlayItem From(Track t) => new()
    {
        Title = t.Title, Subtitle = t.Artist, Album = t.Album, ArtPath = t.ArtPath, DurationMs = t.DurationMs, Track = t,
        Url = t.Source switch { "jellyfin" => JellyfinClient.IsDirect(t.Location) ? App.Jellyfin.AudioUrl(t.Location) : "", "plex" => App.Plex.PartUrl(t.Location), _ => t.Location },
        // A converted stream gets a fresh server session each time it starts.
        Resolve = t.Source == "jellyfin" && !JellyfinClient.IsDirect(t.Location) ? () => Task.FromResult(App.Jellyfin.AudioUrl(t.Location)) : null,
        SeekUrl = t.Source == "jellyfin" && !JellyfinClient.IsDirect(t.Location) ? ms => App.Jellyfin.AudioUrl(t.Location, ms) : null
    };

    public static PlayItem From(VideoItem v)
    {
        var item = new PlayItem
        {
            Title = v.Title, ArtPath = v.ArtPath, DurationMs = v.DurationMs, IsVideo = true,
            Subtitle = v.Kind == "episode" ? Fmt.Join(v.Series, v.EpisodeCode) : v.Year > 0 ? v.Year.ToString() : "",
            Url = v.Source switch { "plex" => App.Plex.PartUrl(v.Location), "jellyfin" => "", _ => v.Location }
        };
        if (v.Source == "jellyfin")
        {
            // Original file when the phone can decode it, otherwise the server's HLS conversion (seekable in place).
            item.Resolve = () => App.Jellyfin.ResolveVideoAsync(v.Location);
            item.Fallback = () => App.Jellyfin.HlsUrl(v.Location, "h264");
        }
        return item;
    }

    public static PlayItem From(Station s) => new()
    {
        Title = s.Name, Subtitle = s.Subtitle == "" ? "radio" : "radio  ·  " + s.Subtitle, ArtPath = s.Art, Url = s.Url, Station = s
    };

    /// <summary>Tunes in to a radio station.</summary>
    public void PlayStation(Station s) => Play(new[] { From(s) });

    /// <summary>Plays an audiobook from where it was left (or from the start).</summary>
    public void PlayBook(Book b, bool restart = false)
    {
        if (b.Parts.Count == 0) return;
        SaveEpisodePosition();   // if this very book is playing, pick up from where it is right now
        if (restart || b.Finished) { b.Finished = false; b.Part = 0; b.PositionMs = 0; b.Progress = 0; }
        b.LastOpened = Fmt.Now;
        App.Lib.Db.SaveBookState(b);
        var remote = b.Source == "jellyfin";
        Play(b.Parts.Select((p, i) =>
        {
            var converted = remote && !JellyfinClient.IsDirect(p.Location);
            return new PlayItem
            {
                Title = b.Parts.Count > 1 ? p.Title : b.Title, Subtitle = b.Author, Album = b.Title, ArtPath = b.ArtPath, DurationMs = p.DurationMs,
                Book = b, BookPart = i,
                Url = !remote ? p.Location : converted ? "" : App.Jellyfin.AudioUrl(p.Location),
                Resolve = converted ? () => Task.FromResult(App.Jellyfin.AudioUrl(p.Location)) : null,
                SeekUrl = converted ? ms => App.Jellyfin.AudioUrl(p.Location, ms) : null
            };
        }), Math.Clamp(b.Part, 0, b.Parts.Count - 1));
    }

    /// <summary>Plays an audiobook from a given file and position (a bookmark), instead of from its saved place.</summary>
    public void PlayBookAt(Book b, int part, long positionMs)
    {
        if (Current?.Book == b) Stop();   // let go of the old place first so it is not saved over the new one
        b.Part = Math.Clamp(part, 0, Math.Max(0, b.Parts.Count - 1));
        b.PositionMs = positionMs;
        b.Finished = false;
        PlayBook(b);
    }

    public static PlayItem From(Episode e, Podcast p) => e.File != "" && System.IO.File.Exists(e.File)
        // downloaded: played from the file
        ? new PlayItem { Title = e.Title, Subtitle = p.Title, ArtPath = p.ArtUrl, DurationMs = e.DurationMs, IsVideo = e.IsVideo, Episode = e, Url = e.File }
        : new PlayItem
    {
        Title = e.Title, Subtitle = p.Title, ArtPath = p.ArtUrl, DurationMs = e.DurationMs, IsVideo = e.IsVideo, Episode = e, Url = e.Url,
        // Hosts hide the audio behind redirects: find the real address when the episode starts (the original one if that fails),
        // and if the engine still can't stream it, download the file and play that.
        Resolve = async () =>
        {
            try { return await PodcastService.ResolveAsync(e.Url); }
            catch { return e.Url; }
        },
        FallbackAsync = () => PodcastService.DownloadAsync(e.Url)   // replaced below with the resolved address
    }.WithResolvedDownload();

    // ---------- shared play actions ----------

    public void PlayTracks(IEnumerable<Track> tracks, Track? start = null)
    {
        var list = tracks.ToList();
        Play(list.Select(From), start == null ? 0 : Math.Max(0, list.IndexOf(start)));
    }

    public void PlayShuffled(IEnumerable<Track> tracks)
    {
        var rnd = new Random();
        PlayTracks(tracks.Where(t => t.Rating >= 0).OrderBy(_ => rnd.Next()));
    }

    public void EnqueueTracks(IEnumerable<Track> tracks) => Enqueue(tracks.Select(From));
}
