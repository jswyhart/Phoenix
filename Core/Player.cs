using System.Collections.ObjectModel;
using System.Windows.Controls;
using Halcyon.Services;

namespace Halcyon.Core;

/// <summary>Another player the transport controls can work instead of the queue (the Spotify web player).</summary>
public interface IRemotePlayer
{
    /// <summary>It is what is being listened to right now, so play, next and previous belong to it.</summary>
    bool Active { get; }
    bool Playing { get; }
    void Toggle();
    void Next();
    void Previous();
}

/// <summary>
/// Play queue plus transport. Music goes through the audio engine (gapless, crossfade, equalizer); video, podcasts,
/// audiobooks and server-transcoded streams go through a MediaElement (Media Foundation).
/// </summary>
public class Player
{
    MediaElement _me = null!;
    int _index = -1;
    List<int> _order = new();
    int _failStreak;
    long _resumeMs;
    bool _shuffle;
    TimeSpan _offset;   // where the current (server-transcoded) stream started within the item
    AudioEngine? _engine;
    bool _onEngine;     // the current item is playing through the audio engine, not the MediaElement
    readonly System.Windows.Threading.DispatcherTimer _ahead = new() { Interval = TimeSpan.FromMilliseconds(500) };
    double _vol = 0.8;      // the volume you set
    double _fade = 1;       // the sleep timer turning it down at the end
    double _speed = 1;
    bool _onCast;           // the current item is playing on another device

    /// <summary>When set and active, play/pause, next and previous go to it instead of the queue.</summary>
    public IRemotePlayer? Remote { get; set; }
    bool RemoteActive => Remote is { Active: true };
    public bool RemoteIsActive => RemoteActive;

    public AudioEngine? Engine => _engine;
    public bool OnEngine => _onEngine;
    /// <summary>The song a Smart DJ mix was started from; while set, the queue tops itself up with similar music.</summary>
    public Track? DjSeed { get; private set; }

    public ObservableCollection<PlayItem> Queue { get; } = new();
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
    public event Action<string>? Error;

    public void Attach(MediaElement me)
    {
        _me = me;
        _shuffle = App.Settings.Shuffle;
        Repeat = App.Settings.Repeat;
        _vol = App.Settings.Volume;
        me.Volume = _vol;
        me.MediaOpened += (_, _) =>
        {
            _failStreak = 0;
            try { if (_me.SpeedRatio != _speed) _me.SpeedRatio = _speed; } catch { }
            var cur = Current;
            if (cur == null) return;
            if (_me.NaturalDuration.HasTimeSpan && cur.DurationMs <= 0) cur.DurationMs = (long)_me.NaturalDuration.TimeSpan.TotalMilliseconds;
            if (_resumeMs > 0) { Seek(TimeSpan.FromMilliseconds(_resumeMs)); _resumeMs = 0; }
            StateChanged?.Invoke();
        };
        me.MediaEnded += (_, _) =>
        {
            if (_onEngine) return;
            Finished(Current);
            Next(auto: true);
        };
        _ahead.Tick += (_, _) => LookAhead();
        _ahead.Start();
        me.MediaFailed += (_, a) =>
        {
            if (_onEngine) return;
            // a podcast the media engine could not stream: fetch the file and play that instead
            if (Current is { Episode: not null, Downloaded: false } ep && (ep.Resolved ?? ep.Url).StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ep.Downloaded = true;
                _ = DownloadEpisodeAsync(ep);
                return;
            }
            Error?.Invoke($"can't play \"{Current?.Title}\": {a.ErrorException?.Message}");
            if (++_failStreak < 3 && Queue.Count > 1) Next(auto: true); else Stop();
        };
    }

    /// <summary>What happens when an item has played to its end: counts, played marks, finished books.</summary>
    static void Finished(PlayItem? cur)
    {
        if (cur != null) Listening.Left(cur, 0, true);
        if (cur?.Track is { } t) { t.PlayCount++; t.LastPlayed = Fmt.Now; App.Lib.Db.SaveStats(t); }
        if (cur?.Episode is { } e) { e.Played = true; e.PositionMs = 0; App.Lib.Db.SaveEpisodeState(e); e.Raise(); }
        if (cur?.Book is { } b && cur.BookPart == b.Parts.Count - 1)
        {
            // last file of the book: it is finished, and the next listen starts over
            b.Finished = true; b.Part = 0; b.PositionMs = 0; b.Progress = 1;
            App.Lib.Db.SaveBookState(b);
            b.Raise();
        }
    }

    // ---------- the audio engine (music) ----------

    // Radio always goes through the audio engine: Windows' own player (the MediaElement) refuses https addresses
    // outright ("only site-of-origin pack URIs are supported"), and most stations are https now.
    static bool UseEngine(PlayItem item) => item.Station != null || (App.Settings.AudioEngine && item.Track != null && !item.IsVideo && item.SeekUrl == null);

    AudioEngine Eng()
    {
        if (_engine != null) return _engine;
        var e = new AudioEngine { Volume = (float)(_vol * _fade), Muted = _me.IsMuted, GainFor = Levelling.For };
        var ui = _me.Dispatcher;
        e.Advanced += (was, now) => ui.BeginInvoke(() => OnAdvanced(was, now));
        e.Ended += was => ui.BeginInvoke(() =>
        {
            if (!_onEngine || !ReferenceEquals(Current, was)) return;   // the listener moved on meanwhile
            Finished(was);
            Next(auto: true);
        });
        e.Failed += msg => ui.BeginInvoke(() => Error?.Invoke("audio output: " + msg));
        _engine = e;
        ApplyAudioSettings();
        return e;
    }

    /// <summary>Hands the crossfade and equalizer settings to the engine.</summary>
    public void ApplyAudioSettings()
    {
        if (_engine == null) return;
        _engine.Crossfade = Math.Clamp(App.Settings.Crossfade, 0, 12);
        _engine.RefreshGains();
        // your own equalizer plus the auto equalizer's adjustment for the song playing
        _engine.SetEqualizer(Halcyon.Services.AutoEq.Combine(App.Settings, Current?.Track));
    }

    /// <summary>Sets the equalizer for the song playing (your own, plus the auto equalizer's adjustment for it).</summary>
    public void ApplyEqualizer() => _engine?.SetEqualizer(Halcyon.Services.AutoEq.Combine(App.Settings, Current?.Track));
    public bool EqualizerOnForTest => _engine?.EqualizerOn == true;

    async Task StartOnEngine(PlayItem item, long startMs = 0)
    {
        var e = Eng();
        e.Paused = false;
        var ok = await e.PlayAsync(item, startMs);
        if (!ReferenceEquals(Current, item)) return;
        if (!ok && item.Station != null)
        {
            // a station that could not be opened: an http one may still play the old way; otherwise say so plainly
            _onEngine = false;
            if (item.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                try { _me.Source = new Uri(item.Url); _me.Play(); return; } catch { }
            Error?.Invoke($"can't tune in to {item.Title}: the stream did not answer, or is in a form Windows can't play");
            Stop();
            return;
        }
        if (!ok)
        {
            // the engine could not open it: let the media element try, as before
            _onEngine = false;
            try { _me.Source = new Uri(item.Url); _me.Play(); if (!IsPlaying) _me.Pause(); }
            catch (Exception ex) { Error?.Invoke(ex.Message); }
            return;
        }
        _failStreak = 0;
        if (item.DurationMs <= 0) item.DurationMs = (long)e.Duration.TotalMilliseconds;
        if (!IsPlaying) e.Paused = true;
        StateChanged?.Invoke();
    }

    /// <summary>The engine ran one song into the next by itself; catch the queue up with it.</summary>
    void OnAdvanced(PlayItem was, PlayItem now)
    {
        if (!_onEngine || !ReferenceEquals(Current, was)) return;
        Finished(was);
        var idx = Queue.IndexOf(now);
        if (idx < 0) { Next(auto: true); return; }
        was.IsCurrent = false;
        _index = idx;
        now.IsCurrent = true;
        if (now.DurationMs <= 0 && _engine != null) now.DurationMs = (long)_engine.Duration.TotalMilliseconds;
        if (now.Track is { } t) { t.LastPlayed = Fmt.Now; App.Lib.Db.SaveStats(t); }
        now.Logged = false;
        Listening.Started(now);
        RefillDj();
        if (SleepAtEnd) { SleepAtEnd = false; if (IsPlaying) Toggle(); }   // "stop after this song"
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>The item that will play when the current one ends by itself, if that is already certain.</summary>
    public PlayItem? PeekNext()
    {
        if (Current == null) return null;
        if (Repeat == 2) return Current;
        var pos = _order.IndexOf(_index);
        if (pos >= 0 && pos + 1 < _order.Count) return Queue[_order[pos + 1]];
        return Repeat == 1 && !_shuffle && Queue.Count > 0 ? Queue[0] : null;
    }

    /// <summary>Near the end of a song, gets the next one ready so it follows without a gap (or blends in).</summary>
    void LookAhead()
    {
        if (!_onEngine || _engine == null || Current == null) return;
        var next = PeekNext();
        var ready = _engine.Preloaded;
        if (ready != null && !ReferenceEquals(ready, next)) { _engine.ClearNext(); ready = null; }   // the queue changed
        if (ready == null && next != null && IsPlaying && UseEngine(next) && !_engine.IsLoadingNext
            && _engine.Duration > TimeSpan.Zero && _engine.Remaining < 25)
            _ = _engine.PreloadAsync(next);
    }

    // ---------- smart dj ----------

    /// <summary>Starts an endless mix of music like the given song.</summary>
    public void PlaySmartDj(Track seed)
    {
        var mix = App.Lib.SmartDj(seed, new[] { seed }, 30);
        Play(new[] { seed }.Concat(mix).Select(From));
        DjSeed = seed;
    }

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

    public double Volume
    {
        get => _vol;
        set { _vol = value; ApplyVolume(); App.Settings.Volume = value; if (_onCast) _ = Quiet(Cast!.SetVolumeAsync(value)); }
    }

    void ApplyVolume()
    {
        _me.Volume = _vol * _fade;
        if (_engine != null) _engine.Volume = (float)(_vol * _fade);
    }

    static async Task Quiet(Task t) { try { await t; } catch { } }

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
        ApplyVolume();
        StateChanged?.Invoke();
    }

    public string SleepText => SleepAtEnd ? "sleep: when this ends"
        : SleepAt is { } at ? $"sleep in {Math.Max(1, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes))} min" : "";

    /// <summary>Called a few times a second: turns the sound down over the last seconds, then pauses.</summary>
    public void SleepTick()
    {
        if (SleepAt is not { } at) return;
        var left = (at - DateTime.Now).TotalSeconds;
        if (left > 12) return;
        if (left > 0) { _fade = Math.Clamp(left / 12, 0, 1); ApplyVolume(); return; }
        SleepAt = null;
        if (RemoteActive) { if (Remote!.Playing) Remote.Toggle(); }
        else if (IsPlaying) Toggle();
        _fade = 1;
        ApplyVolume();
        StateChanged?.Invoke();
    }

    // ---------- playback speed (podcasts, audiobooks, video) ----------

    /// <summary>Speed can be changed for what plays through Windows' own player: not music on the audio engine, not while casting.</summary>
    public bool CanSpeed => Current != null && !_onEngine && !_onCast && !RemoteActive && Current.Station == null;

    static string? SpeedKey(PlayItem? i) => i?.Episode is { } e ? "podcast:" + e.PodcastId : i?.Book is { } b ? "book:" + b.Id : null;

    /// <summary>The speed of what is playing. A podcast series or an audiobook remembers its own.</summary>
    public double Speed
    {
        get => CanSpeed ? _speed : 1;
        set
        {
            if (!CanSpeed) return;
            _speed = Math.Clamp(value, 0.5, 3);
            try { _me.SpeedRatio = _speed; } catch { }
            if (SpeedKey(Current) is { } key)
            {
                if (Math.Abs(_speed - 1) < 0.01) App.Settings.Speeds.Remove(key); else App.Settings.Speeds[key] = _speed;
            }
            StateChanged?.Invoke();
        }
    }

    void ApplySpeed(PlayItem item)
    {
        _speed = SpeedKey(item) is { } key && App.Settings.Speeds.TryGetValue(key, out var s) ? Math.Clamp(s, 0.5, 3) : 1;
        try { _me.SpeedRatio = _speed; } catch { }
    }

    // ---------- casting to another device ----------

    /// <summary>The speaker or TV everything is being played on, if any.</summary>
    public ICastDevice? Cast { get; private set; }
    public bool OnCast => _onCast;

    /// <summary>Sends playback to a device on the network; what is playing carries on there from the same place.</summary>
    public void StartCast(ICastDevice device)
    {
        StopCast(resumeHere: false);
        Cast = device;
        var ui = _me.Dispatcher;
        device.Ended += () => ui.BeginInvoke(() =>
        {
            if (!_onCast || !ReferenceEquals(Cast, device)) return;
            Finished(Current);
            Next(auto: true);
        });
        device.Failed += msg => ui.BeginInvoke(() => Error?.Invoke($"{device.Name}: {msg}"));
        device.Changed += () => ui.BeginInvoke(() => { if (_onCast && ReferenceEquals(Cast, device) && IsPlaying != device.Playing && device.Loaded) { IsPlaying = device.Playing; StateChanged?.Invoke(); } });
        if (Current != null) PlayAt(_index, (long)Position.TotalMilliseconds);
        StateChanged?.Invoke();
    }

    /// <summary>Brings playback back to this PC (paused, at the same place).</summary>
    public void StopCast(bool resumeHere = true)
    {
        var device = Cast;
        if (device == null) return;
        var at = (long)Position.TotalMilliseconds;
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

    async Task StartOnCast(PlayItem item, long startMs)
    {
        var device = Cast;
        if (device == null) return;
        try
        {
            await device.LoadAsync(item, startMs / 1000.0);
            if (!ReferenceEquals(Current, item) || !ReferenceEquals(Cast, device)) return;
            _failStreak = 0;
            await device.SetVolumeAsync(_vol);
            if (!IsPlaying) await device.PauseAsync();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(Current, item)) return;
            Error?.Invoke($"couldn't play on {device.Name}: {ex.Message}");
        }
    }

    void LeaveCurrent()
    {
        if (_cued) return;   // put back at start and not played: nothing was heard
        if (Current is { Track: not null } c) Listening.Left(c, (long)Position.TotalMilliseconds, false);
    }

    // ---------- picking up where you left off ----------

    bool _cued;
    long _cuedMs;

    /// <summary>The queue was put back at start and nothing has played yet (play starts it where it was).</summary>
    public bool IsCued => _cued;

    /// <summary>
    /// Puts a queue back without playing it: the song is shown, paused at where it was, and play carries on from there.
    /// </summary>
    public void Cue(IEnumerable<PlayItem> items, int index, long positionMs)
    {
        Queue.Clear();
        foreach (var i in items) Queue.Add(i);
        if (Queue.Count == 0) return;
        _index = Math.Clamp(index, 0, Queue.Count - 1);
        Queue[_index].IsCurrent = true;
        BuildOrder(_index);
        _cued = true;
        // a song that was all but finished starts again from the top
        var length = Queue[_index].DurationMs;
        _cuedMs = length > 0 && positionMs > length - 1500 ? 0 : Math.Max(0, positionMs);
        IsPlaying = false;
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public bool Muted
    {
        get => _me.IsMuted;
        set { _me.IsMuted = value; if (_engine != null) _engine.Muted = value; StateChanged?.Invoke(); }
    }

    public TimeSpan Position => Current == null ? TimeSpan.Zero : _cued ? TimeSpan.FromMilliseconds(_cuedMs) : _onCast && Cast != null ? TimeSpan.FromSeconds(Cast.Position)
        : _onEngine ? _engine!.Position : _offset + _me.Position;

    // A transcoded stream doesn't know its real length, so trust the library's duration for those.
    public TimeSpan Duration => _onCast && Cast is { Duration: > 0 } cast ? TimeSpan.FromSeconds(cast.Duration)
        : _onEngine && _engine!.Duration > TimeSpan.Zero ? _engine.Duration
        : Current is { SeekUrl: not null, DurationMs: > 0 } c ? TimeSpan.FromMilliseconds(c.DurationMs)
        : _me.NaturalDuration.HasTimeSpan ? _me.NaturalDuration.TimeSpan : TimeSpan.FromMilliseconds(Current?.DurationMs ?? 0);

    public void Seek(TimeSpan t)
    {
        var cur = Current;
        if (cur == null) return;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (_cued) { _cuedMs = (long)t.TotalMilliseconds; StateChanged?.Invoke(); return; }
        if (_onCast) { _ = Quiet(Cast!.SeekAsync(t.TotalSeconds)); return; }
        if (_onEngine) { _engine!.Seek(t); return; }
        if (cur.SeekUrl == null) { _me.Position = t; return; }
        // Not seekable in place: ask the server for a new stream that begins at the target time.
        if (cur.DurationMs > 0 && t.TotalMilliseconds > cur.DurationMs - 2000) t = TimeSpan.FromMilliseconds(Math.Max(0, cur.DurationMs - 2000));
        _offset = t;
        _me.Source = new Uri(cur.SeekUrl((long)t.TotalMilliseconds));
        _me.Play();
        if (!IsPlaying) _me.Pause();
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

    public void Play(IEnumerable<PlayItem> items, int start = 0)
    {
        SaveEpisodePosition();
        DjSeed = null;
        Queue.Clear();
        foreach (var i in items) Queue.Add(i);
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
        if (wasEmpty && Queue.Count > 0) { BuildOrder(0); PlayAt(0); }
    }

    public void PlayAt(int index, long startMs = -1)
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
        item.Logged = false;
        _offset = TimeSpan.Zero;
        try
        {
            IsPlaying = true;
            if (Cast != null && CastMedia.Can(item))
            {
                _me.Stop();
                _me.Source = null;
                if (_onEngine) { _engine?.Stop(); _onEngine = false; }
                _onCast = true;
                _ = StartOnCast(item, _resumeMs);
                _resumeMs = 0;
            }
            else if (UseEngine(item))
            {
                if (_onCast) { _ = Quiet(Cast?.StopAsync() ?? Task.CompletedTask); _onCast = false; }
                _me.Stop();
                _me.Source = null;
                _onEngine = true;
                _ = StartOnEngine(item, Math.Max(0, startMs));
                _resumeMs = 0;
            }
            else
            {
                if (_onCast) { _ = Quiet(Cast?.StopAsync() ?? Task.CompletedTask); _onCast = false; }
                ApplySpeed(item);
                if (_onEngine) { _engine?.Stop(); _onEngine = false; }
                if (item.Episode != null && item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _ = OpenEpisodeAsync(item);
                else { _me.Source = new Uri(item.Url); _me.Play(); }
            }
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); IsPlaying = false; }
        if (item.Track is { } t) { t.LastPlayed = Fmt.Now; App.Lib.Db.SaveStats(t); }
        Listening.Started(item);
        RefillDj();
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>Podcast hosts hide the audio behind redirects the media engine will not follow, so find the real address first.</summary>
    async Task OpenEpisodeAsync(PlayItem item)
    {
        if (item.Resolved == null)
        {
            Error?.Invoke("opening episode…");
            try { item.Resolved = await PodcastService.ResolveAsync(item.Url); }
            catch (Exception ex) { Error?.Invoke($"can't reach \"{item.Title}\": {ex.Message}"); item.Resolved = item.Url; }
        }
        if (!ReferenceEquals(Current, item)) return;
        Error?.Invoke("");
        // Windows' own player refuses https; the same stream is handed to it from this PC instead (see LocalProxy)
        _me.Source = new Uri(item.Resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? LocalProxy.Url(item.Resolved) : item.Resolved);
        _me.Play();
    }

    async Task DownloadEpisodeAsync(PlayItem item)
    {
        Error?.Invoke("downloading episode…");
        try
        {
            var file = await PodcastService.DownloadAsync(item.Resolved ?? item.Url);
            if (!ReferenceEquals(Current, item)) return;
            Error?.Invoke("");
            _me.Source = new Uri(file);
            _me.Play();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(Current, item)) return;
            Error?.Invoke($"can't play \"{item.Title}\": {ex.Message}");
            Stop();
        }
    }

    public void Toggle()
    {
        if (RemoteActive) { Remote!.Toggle(); return; }
        if (Current == null) return;
        if (_cued) { var at = _cuedMs; PlayAt(_index, at); return; }   // the queue put back at start: carry on where it was
        if (_onCast) _ = Quiet(IsPlaying ? Cast!.PauseAsync() : Cast!.PlayAsync());
        else if (_onEngine) _engine!.Paused = IsPlaying;
        else if (IsPlaying) _me.Pause(); else _me.Play();
        IsPlaying = !IsPlaying;
        StateChanged?.Invoke();
    }

    public void Next(bool auto = false)
    {
        // "stop after this one": the next item is made ready, but left paused
        var sleep = auto && SleepAtEnd;
        if (sleep) SleepAtEnd = false;
        Advance(auto);
        if (sleep && IsPlaying && !RemoteActive) Toggle();
    }

    void Advance(bool auto)
    {
        if (!auto && RemoteActive) { Remote!.Next(); return; }
        if (Queue.Count == 0) return;
        if (auto && Repeat == 2) { if (_onEngine || _onCast) PlayAt(_index); else { if (Current != null) Current.Logged = false; Seek(TimeSpan.Zero); _me.Play(); } return; }
        var pos = _order.IndexOf(_index);
        if (pos + 1 < _order.Count) PlayAt(_order[pos + 1]);
        else if (Repeat == 1 || !auto) { BuildOrder(_shuffle ? new Random().Next(Queue.Count) : 0); PlayAt(_order[0]); }
        else Stop();
    }

    public void Previous()
    {
        if (RemoteActive) { Remote!.Previous(); return; }
        if (Queue.Count == 0) return;
        if (Position.TotalSeconds > 3) { Seek(TimeSpan.Zero); return; }
        var pos = _order.IndexOf(_index);
        if (pos > 0) PlayAt(_order[pos - 1]); else Seek(TimeSpan.Zero);
    }

    public void Stop()
    {
        LeaveCurrent();
        _cued = false;
        SaveEpisodePosition();
        if (_onCast) { _ = Quiet(Cast?.StopAsync() ?? Task.CompletedTask); _onCast = false; }
        _me.Stop();
        _me.Source = null;
        _engine?.Stop();
        _onEngine = false;
        DjSeed = null;
        if (Current != null) Current.IsCurrent = false;
        _index = -1;
        _offset = TimeSpan.Zero;
        IsPlaying = false;
        CurrentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>Records where the current podcast episode or audiobook is, so it can be picked up later.</summary>
    public void SaveEpisodePosition()
    {
        if (Current is { Book: { Finished: false } b } item)
        {
            b.Part = item.BookPart;
            b.PositionMs = (long)Position.TotalMilliseconds;
            var before = b.Parts.Take(b.Part).Sum(p => p.DurationMs);
            b.Progress = b.DurationMs > 0 ? Math.Clamp((before + b.PositionMs) / (double)b.DurationMs, 0, 1) : 0;
            b.LastOpened = Fmt.Now;
            App.Lib.Db.SaveBookState(b);
            b.Raise();
        }
        if (Current?.Episode is not { } e || e.Played) return;
        e.PositionMs = (long)Position.TotalMilliseconds;
        App.Lib.Db.SaveEpisodeState(e);
    }

    // ---------- item factories ----------

    public static PlayItem From(Track t) => new()
    {
        Title = t.Title, Subtitle = t.Artist, Album = t.Album, ArtPath = t.ArtPath, DurationMs = t.DurationMs, Track = t,
        Url = t.Source switch { "jellyfin" => App.Jellyfin.AudioUrl(t.Location), "plex" => App.Plex.PartUrl(t.Location), _ => t.Location },
        SeekUrl = t.Source == "jellyfin" && !JellyfinClient.IsDirect(t.Location) ? ms => App.Jellyfin.AudioUrl(t.Location, ms) : null
    };

    public static PlayItem From(VideoItem v) => new()
    {
        Title = v.Title, ArtPath = v.ArtPath, DurationMs = v.DurationMs, IsVideo = true,
        Subtitle = v.Kind == "episode" ? string.Join("  ·  ", new[] { v.Series, v.EpisodeCode }.Where(s => s != "")) : v.Year > 0 ? v.Year.ToString() : "",
        Url = v.Source switch { "jellyfin" => App.Jellyfin.VideoUrl(v.Location), "plex" => App.Plex.PartUrl(v.Location), _ => v.Location },
        SeekUrl = v.Source == "jellyfin" && !JellyfinClient.IsDirect(v.Location) ? ms => App.Jellyfin.VideoUrl(v.Location, ms) : null
    };

    public static PlayItem From(Station s) => new()
    {
        Title = s.Name, Subtitle = s.Subtitle == "" ? "radio" : "radio  ·  " + s.Subtitle, ArtPath = s.Art, Url = s.Url, Station = s
    };

    /// <summary>Plays an audiobook from where it was left (or from the start).</summary>
    public void PlayBook(Book b, bool restart = false)
    {
        if (b.Parts.Count == 0) return;
        if (restart || b.Finished) { b.Finished = false; b.Part = 0; b.PositionMs = 0; b.Progress = 0; }
        b.LastOpened = Fmt.Now;
        App.Lib.Db.SaveBookState(b);
        var remote = b.Source == "jellyfin";
        Play(b.Parts.Select((p, i) => new PlayItem
        {
            Title = b.Parts.Count > 1 ? p.Title : b.Title, Subtitle = b.Author, Album = b.Title, ArtPath = b.ArtPath, DurationMs = p.DurationMs,
            Book = b, BookPart = i,
            Url = remote ? App.Jellyfin.AudioUrl(p.Location) : p.Location,
            SeekUrl = remote && !JellyfinClient.IsDirect(p.Location) ? ms => App.Jellyfin.AudioUrl(p.Location, ms) : null
        }), Math.Clamp(b.Part, 0, b.Parts.Count - 1));
    }

    /// <summary>Plays an audiobook from a given file and position (a bookmark), even if that book is playing right now.</summary>
    public void PlayBookAt(Book b, int part, long positionMs)
    {
        if (Current?.Book == b) Stop();   // let go of the old place first so it is not saved over the new one
        b.Part = Math.Clamp(part, 0, Math.Max(0, b.Parts.Count - 1));
        b.PositionMs = positionMs;
        b.Finished = false;
        PlayBook(b);
    }

    public static PlayItem From(Episode e, Podcast p) => new()
    {
        Title = e.Title, Subtitle = p.Title, ArtPath = p.ArtUrl, DurationMs = e.DurationMs, IsVideo = e.IsVideo, Episode = e,
        Url = e.File != "" && System.IO.File.Exists(e.File) ? e.File : e.Url
    };
}
