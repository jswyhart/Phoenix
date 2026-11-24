using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Halcyon.Core;

/// <summary>
/// Plays music through one continuous audio stream, so one song can run straight into the next with no gap,
/// two songs can overlap (crossfade), and an equalizer can shape the sound. Songs are decoded by Windows
/// (Media Foundation), converted to one common format and mixed here.
/// </summary>
public sealed class AudioEngine : ISampleProvider, IDisposable
{
    public static readonly float[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public static readonly (string Name, float[] Gains)[] Presets =
    {
        ("flat", new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("acoustic", new float[] { 4, 4, 3, 1, 1.5f, 1.5f, 3, 3.5f, 3, 2 }),
        ("bass boost", new float[] { 6, 5, 4, 2.5f, 1, 0, 0, 0, 0, 0 }),
        ("classical", new float[] { 4, 3, 2.5f, 2, -1, -1, 0, 2, 3, 3.5f }),
        ("dance", new float[] { 4, 6, 4.5f, 0, 1.5f, 3, 4.5f, 4, 3, 0 }),
        ("electronic", new float[] { 4, 3.5f, 1, 0, -2, 2, 1, 1.5f, 4, 4.5f }),
        ("hip hop", new float[] { 5, 4, 1.5f, 3, -1, -1, 1.5f, -0.5f, 2, 3 }),
        ("jazz", new float[] { 4, 3, 1.5f, 2, -1.5f, -1.5f, 0, 1.5f, 3, 3.5f }),
        ("pop", new float[] { -1.5f, -1, 0, 2, 4, 4, 2, 0, -1, -1.5f }),
        ("rock", new float[] { 5, 4, 3, 1.5f, -0.5f, -1, 0.5f, 2.5f, 3.5f, 4.5f }),
        ("treble boost", new float[] { 0, 0, 0, 0, 0, 1, 2.5f, 4, 5, 6 }),
        ("vocal", new float[] { -2, -3, -3, 1.5f, 4, 4, 3, 1.5f, 0, -1.5f }),
    };

    sealed class Deck : IDisposable
    {
        public PlayItem Item = null!;
        public WaveStream Reader = null!;
        public ISampleProvider Source = null!;
        public double Seconds => Reader.TotalTime.TotalSeconds;
        public double Remaining => Reader.WaveFormat.AverageBytesPerSecond > 0
            ? Math.Max(0, (Reader.Length - Reader.Position) / (double)Reader.WaveFormat.AverageBytesPerSecond) : 0;
        public void Dispose() { try { Reader.Dispose(); } catch { } }
    }

    /// <summary>Keeps the first two channels of a surround file.</summary>
    sealed class FirstTwo(ISampleProvider source) : ISampleProvider
    {
        readonly int _channels = source.WaveFormat.Channels;
        float[] _buf = Array.Empty<float>();
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        public int Read(Span<float> buffer)
        {
            var need = buffer.Length / 2 * _channels;
            if (_buf.Length < need) _buf = new float[need];
            var got = source.Read(_buf.AsSpan(0, need)) / _channels;
            for (var i = 0; i < got; i++) { buffer[i * 2] = _buf[i * _channels]; buffer[i * 2 + 1] = _buf[i * _channels + 1]; }
            return got * 2;
        }
    }

    readonly object _gate = new();
    readonly int _rate;
    Deck? _cur, _tail, _next;
    double _tailTotal, _tailLeft;      // seconds of fade-out for the song that is leaving
    double _fadeInTotal, _fadeInLeft;  // seconds of fade-in for the song that is arriving
    float[] _mix = Array.Empty<float>();
    IWavePlayer? _out;
    int _gen;
    bool _paused;
    float _volume = 1;
    BiQuadFilter[,]? _eq;
    float _preamp = 1;

    public WaveFormat WaveFormat { get; }
    /// <summary>Seconds two songs overlap when one follows another by itself; 0 plays them back to back.</summary>
    public double Crossfade { get; set; }
    public bool Muted { get; set; }
    public float Volume { get => _volume; set => _volume = Math.Clamp(value, 0, 1); }

    /// <summary>A song ended by itself and the next one took over without a break: (the one that ended, the one now playing).</summary>
    public event Action<PlayItem, PlayItem>? Advanced;
    /// <summary>A song ended by itself and nothing was ready to follow.</summary>
    public event Action<PlayItem>? Ended;
    public event Action<string>? Failed;

    public AudioEngine()
    {
        var rate = 48000;
        try
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            rate = device.AudioClient.MixFormat.SampleRate;
        }
        catch { }
        _rate = rate is >= 32000 and <= 192000 ? rate : 48000;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_rate, 2);
    }

    public PlayItem? Playing { get { lock (_gate) return _cur?.Item; } }
    public PlayItem? Preloaded { get { lock (_gate) return _next?.Item; } }
    public bool IsLoadingNext { get; private set; }
    public TimeSpan Position { get { lock (_gate) return _cur?.Reader.CurrentTime ?? TimeSpan.Zero; } }
    public TimeSpan Duration { get { lock (_gate) return _cur?.Reader.TotalTime ?? TimeSpan.Zero; } }
    public double Remaining { get { lock (_gate) return _cur?.Remaining ?? 0; } }
    /// <summary>Songs that followed another without a break since the engine started (for the self-test).</summary>
    public int Handovers { get; private set; }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            try { if (value) _out?.Pause(); else if (Playing != null) _out?.Play(); } catch (Exception ex) { Failed?.Invoke(ex.Message); }
        }
    }

    Deck Open(PlayItem item)
    {
        var reader = new MediaFoundationReader(item.Url, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true });
        try
        {
            ISampleProvider s = reader.ToSampleProvider();
            if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
            else if (s.WaveFormat.Channels > 2) s = new FirstTwo(s);
            if (s.WaveFormat.SampleRate != _rate) s = new WdlResamplingSampleProvider(s, _rate);
            return new Deck { Item = item, Reader = reader, Source = s };
        }
        catch { reader.Dispose(); throw; }
    }

    /// <summary>Starts a song now, replacing whatever is playing. False if it could not be opened.</summary>
    public async Task<bool> PlayAsync(PlayItem item, long startMs = 0)
    {
        int gen;
        lock (_gate) gen = ++_gen;
        Deck deck;
        try
        {
            deck = await Task.Run(() =>
            {
                var d = Open(item);
                if (startMs > 0) d.Reader.CurrentTime = TimeSpan.FromMilliseconds(startMs);
                return d;
            });
        }
        catch { return false; }
        Deck? old, oldTail, oldNext;
        lock (_gate)
        {
            if (gen != _gen) { Toss(deck); return true; }   // something else was started meanwhile
            (old, oldTail, oldNext) = (_cur, _tail, _next);
            _cur = deck; _tail = _next = null;
            _fadeInLeft = _tailLeft = 0;
        }
        Toss(old); Toss(oldTail); Toss(oldNext);
        try
        {
            EnsureOutput();
            if (!_paused) _out!.Play();
        }
        catch (Exception ex) { Stop(); Failed?.Invoke(ex.Message); return false; }
        return true;
    }

    /// <summary>Gets the song that will follow ready, so it can start the instant the current one ends.</summary>
    public async Task PreloadAsync(PlayItem item)
    {
        int gen;
        lock (_gate) { if (IsLoadingNext || _cur == null) return; gen = _gen; IsLoadingNext = true; }
        try
        {
            var deck = await Task.Run(() => Open(item));
            lock (_gate)
            {
                if (gen != _gen || _cur == null || _next != null) { Toss(deck); return; }
                _next = deck;
            }
        }
        catch { }
        finally { IsLoadingNext = false; }
    }

    public void ClearNext()
    {
        Deck? n;
        lock (_gate) { n = _next; _next = null; }
        Toss(n);
    }

    public void Seek(TimeSpan t)
    {
        Deck? tail;
        lock (_gate)
        {
            if (_cur == null) return;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            var total = _cur.Reader.TotalTime;
            if (total > TimeSpan.Zero && t > total) t = total;
            try { _cur.Reader.CurrentTime = t; } catch { }
            tail = _tail; _tail = null; _fadeInLeft = 0;
        }
        Toss(tail);
    }

    public void Stop()
    {
        Deck? a, b, c;
        lock (_gate) { _gen++; (a, b, c) = (_cur, _tail, _next); _cur = _tail = _next = null; }
        try { _out?.Pause(); } catch { }
        Toss(a); Toss(b); Toss(c);
    }

    static void Toss(Deck? d) { if (d != null) Task.Run(d.Dispose); }

    void EnsureOutput()
    {
        if (_out != null) return;
        // the standard Windows output: it follows the default device when headphones or speakers are switched
        var o = new WaveOutEvent { BufferMilliseconds = 60, NumberOfBuffers = 3 };
        o.PlaybackStopped += (_, e) =>
        {
            if (e.Exception == null) return;
            try { o.Dispose(); } catch { }
            if (ReferenceEquals(_out, o)) _out = null;
            Failed?.Invoke(e.Exception.Message);
        };
        o.Init(this);
        _out = o;
    }

    // ---------- equalizer ----------

    /// <summary>Sets the ten band gains in decibels (-12 to 12); null or all zero turns the equalizer off.</summary>
    public void SetEqualizer(IReadOnlyList<double>? gains)
    {
        if (gains == null || gains.Count < BandFrequencies.Length || gains.All(g => Math.Abs(g) < 0.05)) { lock (_gate) { _eq = null; _preamp = 1; } return; }
        var eq = new BiQuadFilter[2, BandFrequencies.Length];
        for (var ch = 0; ch < 2; ch++)
            for (var b = 0; b < BandFrequencies.Length; b++)
                eq[ch, b] = BiQuadFilter.PeakingEQ(_rate, Math.Min(BandFrequencies[b], _rate * 0.45f), 1.1f, (float)Math.Clamp(gains[b], -12, 12));
        // make room for the boosted bands so loud songs do not clip
        var boost = Math.Max(0, gains.Take(BandFrequencies.Length).Max());
        lock (_gate) { _eq = eq; _preamp = (float)Math.Pow(10, -boost * 0.6 / 20); }
    }

    public bool EqualizerOn { get { lock (_gate) return _eq != null; } }

    // ---------- the audio thread ----------

    public int Read(Span<float> buffer)
    {
        const int offset = 0;
        var count = buffer.Length;
        (PlayItem, PlayItem)? advanced = null;
        PlayItem? ended = null;
        lock (_gate)
        {
            buffer.Clear();
            var done = 0;
            while (done < count && _cur != null)
            {
                // time to start blending into the next song?
                if (_next != null && _tail == null && Crossfade > 0)
                {
                    var fade = Math.Min(Crossfade, Math.Min(_cur.Seconds, _next.Seconds) / 3);
                    if (fade > 0.2 && _cur.Remaining <= fade)
                    {
                        advanced = (_cur.Item, _next.Item);
                        _tail = _cur; _tailTotal = _tailLeft = Math.Max(0.05, _cur.Remaining);
                        _cur = _next; _next = null;
                        _fadeInTotal = _fadeInLeft = _tailTotal;
                    }
                }
                var want = count - done;
                var n = _cur.Source.Read(buffer.Slice(done, want));
                if (_fadeInLeft > 0 && n > 0)
                {
                    for (var i = 0; i < n; i += 2)
                    {
                        var g = (float)(1 - _fadeInLeft / _fadeInTotal);
                        buffer[offset + done + i] *= g;
                        if (i + 1 < n) buffer[offset + done + i + 1] *= g;
                        _fadeInLeft = Math.Max(0, _fadeInLeft - 1.0 / _rate);
                    }
                }
                done += n;
                if (n >= want) break;
                // the song ran out: carry straight on with the next one if it is ready
                var over = _cur;
                if (_next != null) { advanced = (over.Item, _next.Item); _cur = _next; _next = null; }
                else { ended = over.Item; _cur = null; }
                Toss(over);
            }
            if (_tail != null)
            {
                if (_mix.Length < count) _mix = new float[count];
                var n = _tail.Source.Read(_mix.AsSpan(0, count));
                for (var i = 0; i < n; i += 2)
                {
                    var g = (float)(_tailLeft / _tailTotal);
                    buffer[offset + i] += _mix[i] * g;
                    if (i + 1 < n) buffer[offset + i + 1] += _mix[i + 1] * g;
                    _tailLeft = Math.Max(0, _tailLeft - 1.0 / _rate);
                }
                if (n < count || _tailLeft <= 0) { Toss(_tail); _tail = null; }
            }
            var eq = _eq;
            var gain = Muted ? 0 : _volume * (eq != null ? _preamp : 1);
            for (var i = 0; i < count; i++)
            {
                var s = buffer[offset + i];
                if (eq != null)
                {
                    var ch = i & 1;
                    for (var b = 0; b < BandFrequencies.Length; b++) s = eq[ch, b].Transform(s);
                }
                s *= gain;
                buffer[offset + i] = s > 1 ? 1 : s < -1 ? -1 : s;
            }
            if (advanced != null) Handovers++;
        }
        if (advanced is { } a) Advanced?.Invoke(a.Item1, a.Item2);
        if (ended != null) Ended?.Invoke(ended);
        return count;
    }

    public void Dispose()
    {
        Stop();
        try { _out?.Dispose(); } catch { }
        _out = null;
    }
}
