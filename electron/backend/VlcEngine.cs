using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Halcyon.Core;
using LibVLCSharp.Shared;

namespace Phoenix.Backend;

/// <summary>
/// Playback through VLC's engine (libVLC): plays local files and network streams in practically any format,
/// with seeking handled by the engine. A second, silent decoder supplies samples to the visualizer. Video is not
/// given a window: VLC hands each frame to <see cref="VideoOut"/>, which passes it on to the Electron window.
/// For crossfading, music alternates between two players: the next song starts on the other one while the last
/// fades out. The equalizer and volume levelling are VLC's own, set as the Windows app's settings say.
/// </summary>
public class VlcEngine : IMediaEngine, ISpeedEngine, IDisposable
{
    readonly LibVLC _vlc;
    readonly MediaPlayer _mp;
    /// <summary>The second player music crossfades onto (made the first time it is needed).</summary>
    MediaPlayer? _mp2;
    /// <summary>The player that is heard now and that the controls work: <see cref="_mp"/> or <see cref="_mp2"/>.</summary>
    volatile MediaPlayer _cur;
    /// <summary>A player whose song was handed over early for a crossfade: its own end must not count as the end again.</summary>
    volatile MediaPlayer? _handedOver;
    bool _isVideo;
    // libVLC must not be re-entered from its own callback threads, and Stop can block: all commands go through one worker.
    readonly BlockingCollection<Action> _ops = new();
    bool _awaitingOpen;
    string _url = "";
    int _volume = 100;

    public Spectrum Spectrum { get; } = new();
    public VideoOut Video { get; }

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public VlcEngine()
    {
        InitializeLibVlc();
        // no --quiet: it would also silence the log callback below, which keeps only warnings and errors anyway.
        // The equalizer uses the standard ten bands (31 Hz to 16 kHz), as the Windows app's does.
        _vlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-equalizer-vlcfreqs");
        // VLC's own errors go to vlc.log next to the library, for troubleshooting (it is kept small)
        var log = Path.Combine(AppPaths.DataDir, "vlc.log");
        try { if (File.Exists(log) && new FileInfo(log).Length > 256_000) File.Delete(log); } catch { }
        _vlc.Log += (_, e) =>
        {
            if (e.Level < LogLevel.Warning) return;
            try { lock (LogLock) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {e.Level} {e.Module}: {e.Message}\n"); } catch { }
        };
        _mp = new MediaPlayer(_vlc) { EnableKeyInput = false, EnableMouseInput = false };
        _cur = _mp;
        Video = new VideoOut(_mp);
        Hook(_mp);
        _fadeTimer = MainLoop.Every(100, FadeTick);
        new Thread(() => { foreach (var op in _ops.GetConsumingEnumerable()) try { op(); } catch { } }) { IsBackground = true, Name = "vlc" }.Start();
    }

    /// <summary>
    /// Finds libVLC: the copy bundled in the app, else the VLC app or Homebrew install (Mac), else the system's own
    /// (Linux, Windows). Throws a plain-language message if there is none.
    /// </summary>
    static void InitializeLibVlc()
    {
        if (Program.IsMac)
        {
            // the copy inside the app: osx-arm64 in the Apple Silicon build, osx-x64 in the Intel one
            var bundled = Path.Combine(AppContext.BaseDirectory, "libvlc", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "lib");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new (string lib, string plugins)[]
            {
                (bundled, Path.Combine(bundled, "vlc", "plugins")),
                ("/Applications/VLC.app/Contents/MacOS/lib", "/Applications/VLC.app/Contents/MacOS/plugins"),
                (Path.Combine(home, "Applications/VLC.app/Contents/MacOS/lib"), Path.Combine(home, "Applications/VLC.app/Contents/MacOS/plugins")),
                ("/opt/homebrew/lib", "/opt/homebrew/lib/vlc/plugins"),
                ("/usr/local/lib", "/usr/local/lib/vlc/plugins")
            };
            foreach (var (lib, plugins) in candidates)
            {
                if (!File.Exists(Path.Combine(lib, "libvlc.dylib"))) continue;
                if (Directory.Exists(plugins)) SetNativeEnv("VLC_PLUGIN_PATH", plugins);
                // libvlc.dylib finds libvlccore.dylib next to itself, so load the core first, then the library
                var core = Path.Combine(lib, "libvlccore.dylib");
                var libvlc = Path.Combine(lib, "libvlc.dylib");
                try
                {
                    var coreHandle = File.Exists(core) ? NativeLibrary.Load(core) : IntPtr.Zero;
                    var handle = NativeLibrary.Load(libvlc);
                    // LibVLCSharp imports from both "libvlc" and "libvlccore"; answer both with what was loaded
                    ResolveTo(n => n switch
                    {
                        "libvlc" or "libvlc.dylib" => handle,
                        "libvlccore" or "libvlccore.dylib" => coreHandle,
                        _ => IntPtr.Zero
                    });
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Phoenix found VLC at {lib} but couldn't load it.\n\n" +
                        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                            ? "This is the Apple Silicon build of Phoenix, so it needs the Apple Silicon (or Universal) VLC. Download the latest VLC for Mac from videolan.org, replace the one in Applications, then open Phoenix again.\n\n"
                            : "Reinstalling VLC from videolan.org usually fixes this.\n\n") + ex.Message);
                }
                try { LibVLCSharp.Shared.Core.Initialize(lib); } catch { /* the resolver above already answers for libvlc */ }
                return;
            }
            throw new InvalidOperationException("Phoenix plays media with VLC's engine, and VLC isn't installed.\n\nInstall VLC from videolan.org (or with Homebrew: brew install --cask vlc), then open Phoenix again.");
        }
        if (Program.IsLinux)
        {
            // distributions ship libvlc.so.5 (and libvlccore.so.9); the plain names exist only with the -dev package
            var handle = IntPtr.Zero;
            foreach (var name in new[] { "libvlc.so.5", "libvlc.so" })
                if (NativeLibrary.TryLoad(name, out handle)) break;
            var coreHandle = IntPtr.Zero;
            foreach (var name in new[] { "libvlccore.so.9", "libvlccore.so" })
                if (NativeLibrary.TryLoad(name, out coreHandle)) break;
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Phoenix plays media with VLC's engine (libVLC), and it couldn't be loaded.\n\nInstall VLC with your package manager, for example:\n  sudo apt install vlc\n  sudo dnf install vlc\n  sudo pacman -S vlc");
            ResolveTo(n => n switch
            {
                "libvlc" or "libvlc.so" => handle,
                "libvlccore" or "libvlccore.so" => coreHandle,
                _ => IntPtr.Zero
            });
            try { LibVLCSharp.Shared.Core.Initialize(); } catch { /* the resolver above already answers for libvlc */ }
            return;
        }
        LibVLCSharp.Shared.Core.Initialize();
    }

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    static extern int setenv(string name, string value, int overwrite);

    /// <summary>
    /// Sets an environment variable where native code can see it. On macOS and Linux, .NET's
    /// Environment.SetEnvironmentVariable only changes its own copy, so libVLC's getenv would never see the plug-in path.
    /// </summary>
    static void SetNativeEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows())
            try { setenv(name, value, 1); } catch { /* no libc: nothing more we can do */ }
    }

    /// <summary>
    /// Answers LibVLCSharp's own lookups for "libvlc" with the library already loaded from its real location. Without
    /// this, .NET only searches next to the helper and in the system folders, and never finds the bundled copy.
    /// </summary>
    static void ResolveTo(Func<string, IntPtr> lookup) =>
        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) => lookup(name));

    Media Open(string url) => url.Contains("://") ? new Media(_vlc, new Uri(url)) : new Media(_vlc, url, FromType.FromPath);

    void Hook(MediaPlayer mp)
    {
        mp.Playing += (_, _) => MainLoop.Post(() =>
        {
            if (!ReferenceEquals(mp, _cur) || !_awaitingOpen) return;
            _awaitingOpen = false;
            Opened?.Invoke();
        });
        mp.EndReached += (_, _) => MainLoop.Post(() =>
        {
            // a song handed over early (crossfade) already counted as ended; one that faded out on its own just stops
            if (ReferenceEquals(mp, _handedOver)) { _handedOver = null; return; }
            if (!ReferenceEquals(mp, _cur)) return;
            StopAnalysis();
            Ended?.Invoke();
        });
        mp.EncounteredError += (_, _) => MainLoop.Post(() => { if (ReferenceEquals(mp, _cur)) Failed?.Invoke("the file or stream could not be opened"); });
    }

    MediaPlayer Other(MediaPlayer mp)
    {
        if (!ReferenceEquals(mp, _mp)) return _mp;
        if (_mp2 == null)
        {
            _mp2 = new MediaPlayer(_vlc) { EnableKeyInput = false, EnableMouseInput = false };
            Hook(_mp2);
            if (_eq != null) _mp2.SetEqualizer(_eq);
        }
        return _mp2;
    }

    /// <summary>Options VLC is given for each song (volume levelling); set by the host from the settings.</summary>
    public Func<string, bool, IEnumerable<string>> MediaOptions = (_, _) => Array.Empty<string>();

    public void Load(string url, bool isVideo)
    {
        _url = url;
        _awaitingOpen = true;
        _analysisWanted = !isVideo;
        var options = MediaOptions(url, isVideo).ToList();
        // the next song of a crossfade starts on the other player while the last one fades out
        var crossfade = !isVideo && !_isVideo && _handedOver != null && ReferenceEquals(_handedOver, _cur) && _fadeSeconds > 0;
        var from = _cur;
        var to = isVideo ? _mp : crossfade ? Other(from) : from;
        _isVideo = isVideo;
        if (!ReferenceEquals(to, from) && !crossfade) { var old = from; _ops.Add(() => old.Stop()); }
        _cur = to;
        if (crossfade) { _fading = from; _fadeStart = DateTime.UtcNow; _fadeIn = to; }
        else { _fading = null; _fadeIn = null; }
        var startVolume = crossfade ? 0 : _volume;
        var speed = _speed;
        _ops.Add(() =>
        {
            using var media = Open(url);
            foreach (var o in options) media.AddOption(o);
            if (!isVideo && !ReferenceEquals(to, _mp)) media.AddOption(":no-video");
            to.Play(media);
            to.Volume = startVolume;
            to.Mute = _muted;
            if (Math.Abs(speed - 1) > 0.001) to.SetRate(speed);
        });
        RestartAnalysis();
    }

    // ---------- crossfade ----------

    double _fadeSeconds;
    MediaPlayer? _fading, _fadeIn;
    DateTime _fadeStart;
    readonly Timer _fadeTimer;

    /// <summary>Seconds songs overlap (0: back to back). Videos, podcasts and audiobooks are never crossfaded.</summary>
    public double Crossfade { get => _fadeSeconds; set => _fadeSeconds = Math.Clamp(value, 0, 12); }
    /// <summary>How many songs have handed over to the next early, for a crossfade (self-test).</summary>
    public int Handovers { get; private set; }
    /// <summary>Whether what is playing now may hand over early (music; set by the host for each item).</summary>
    public bool FadeThis { get; set; }

    void FadeTick()
    {
        // near the end of a song: say it has ended, so the next one starts while this one fades out
        var cur = _cur;
        if (_fadeSeconds > 0 && FadeThis && !_isVideo && _handedOver == null && _fading == null && cur.IsPlaying)
        {
            long length = cur.Length, at = cur.Time;
            if (length > _fadeSeconds * 3000 && at > 0 && length - at <= _fadeSeconds * 1000)
            {
                Handovers++;
                _handedOver = cur;
                StopAnalysis();
                Ended?.Invoke();
                // nothing came next (the end of the list): the song plays out, fading
                if (ReferenceEquals(_cur, cur) && _fading == null) { _fading = cur; _fadeIn = null; _fadeStart = DateTime.UtcNow; }
            }
        }
        if (_fading == null) return;
        var t = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalSeconds / Math.Max(0.5, _fadeSeconds), 0, 1);
        int down = (int)Math.Round(_volume * (1 - t)), up = (int)Math.Round(_volume * t);
        var fading = _fading;
        var fadeIn = _fadeIn;
        _ops.Add(() =>
        {
            fading.Volume = down;
            if (fadeIn != null) fadeIn.Volume = up;
        });
        if (t >= 1)
        {
            if (!ReferenceEquals(fading, _cur)) _ops.Add(() => fading.Stop());
            // stopped before its own end: that end will not come, so the next song may hand over in its turn
            if (ReferenceEquals(_handedOver, fading) && !ReferenceEquals(fading, _cur)) _handedOver = null;
            _fading = null;
            _fadeIn = null;
        }
    }

    // ---------- equalizer ----------

    Equalizer? _eq;

    /// <summary>Ten bands' gains in decibels (31 Hz to 16 kHz), or null for no equalizer.</summary>
    public void SetEqualizer(double[]? gains)
    {
        _ops.Add(() =>
        {
            if (gains == null || gains.All(g => Math.Abs(g) < 0.05))
            {
                _eq = null;
                _mp.UnsetEqualizer();
                _mp2?.UnsetEqualizer();
                return;
            }
            var eq = new Equalizer();
            for (uint b = 0; b < Math.Min(eq.BandCount, (uint)gains.Length); b++) eq.SetAmp((float)Math.Clamp(gains[b], -12, 12), b);
            // as on Windows: whatever the bands lift is taken off the whole, so loud songs don't clip
            eq.SetPreamp((float)-Math.Max(0, gains.Max()));
            _eq = eq;
            _mp.SetEqualizer(eq);
            _mp2?.SetEqualizer(eq);
        });
    }

    /// <summary>The equalizer's band frequencies in hertz (for the self-test).</summary>
    public float[] BandFrequencies
    {
        get
        {
            using var eq = new Equalizer();
            return Enumerable.Range(0, (int)eq.BandCount).Select(i => eq.BandFrequency((uint)i)).ToArray();
        }
    }

    // ---------- video thumbnails ----------

    static readonly object ThumbLock = new(), LogLock = new(), SpeechLock = new();

    /// <summary>
    /// A song as 16 kHz mono samples for the speech model: VLC converts it to a WAV file as fast as it can (no sound
    /// plays), and the samples are read back from it.
    /// </summary>
    public float[] DecodeForSpeech(string source, CancellationToken cancel) => DecodeMono(source, 16000, cancel);

    /// <summary>A song as mono samples at the given rate (the speech model, the auto equalizer).</summary>
    public float[] DecodeMono(string source, int rate, CancellationToken cancel)
    {
        lock (SpeechLock)
        {
            var wav = Path.Combine(Path.GetTempPath(), $"phoenix-speech-{Guid.NewGuid():N}.wav");
            try
            {
                using var media = Open(source);
                media.AddOption($":sout=#transcode{{vcodec=none,acodec=s16l,channels=1,samplerate={rate}}}:std{{access=file,mux=wav,dst='{wav.Replace("'", "\\'")}'}}");
                media.AddOption(":no-sout-video");
                media.AddOption(":sout-keep");
                using var mp = new MediaPlayer(media);
                using var done = new ManualResetEventSlim();
                mp.EndReached += (_, _) => done.Set();
                mp.EncounteredError += (_, _) => done.Set();
                mp.Stopped += (_, _) => done.Set();
                mp.Play();
                while (!done.Wait(200))
                    if (cancel.IsCancellationRequested) break;
                ThreadPool.QueueUserWorkItem(_ => { try { mp.Stop(); } catch { } });
                Thread.Sleep(300);
                cancel.ThrowIfCancellationRequested();
                return ReadWav(wav);
            }
            finally { try { File.Delete(wav); } catch { } }
        }
    }

    /// <summary>16-bit PCM from a WAV file, as -1..1.</summary>
    static float[] ReadWav(string file)
    {
        var bytes = File.ReadAllBytes(file);
        // find the "data" chunk
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "data")
            {
                var n = Math.Min(size < 0 ? int.MaxValue : size, bytes.Length - at - 8) / 2;
                var samples = new float[n];
                for (var i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2) / 32768f;
                return samples;
            }
            at += 8 + Math.Max(0, size) + (size & 1);
        }
        return Array.Empty<float>();
    }

    /// <summary>
    /// Saves a picture from about a tenth of the way into a video as a PNG <paramref name="width"/> pixels wide.
    /// Runs a silent, invisible player that hands its frames to memory. Returns false if no frame arrived.
    /// </summary>
    public bool Thumbnail(string file, string png, int width = 320)
    {
        lock (ThumbLock)
        {
            using var media = new Media(_vlc, file, FromType.FromPath);
            try { media.Parse(MediaParseOptions.ParseLocal, 4000).GetAwaiter().GetResult(); } catch { }
            var startSec = media.Duration > 0 ? Math.Clamp(media.Duration / 10_000.0, 0, 120) : 5;
            media.AddOption(":no-audio");
            media.AddOption(":no-sub-autodetect-file");
            media.AddOption($":start-time={startSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            uint w = 0, h = 0;
            var buffer = IntPtr.Zero;
            var frames = 0;
            using var ready = new ManualResetEventSlim();
            MediaPlayer.LibVLCVideoFormatCb format = (ref IntPtr opaque, IntPtr chroma, ref uint width0, ref uint height0, ref uint pitches, ref uint lines) =>
            {
                // keep the picture's shape, scaled to the requested width
                var tw = (uint)Math.Max(16, Math.Min(width, (int)width0));
                var th = (uint)Math.Max(16, Math.Round(tw * (double)height0 / Math.Max(1, width0)));
                Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
                width0 = w = tw; height0 = h = th;
                pitches = tw * 4; lines = th;
                buffer = Marshal.AllocHGlobal((int)(tw * 4 * th));
                return 1;
            };
            MediaPlayer.LibVLCVideoCleanupCb cleanup = (ref IntPtr opaque) => { };
            MediaPlayer.LibVLCVideoLockCb lockCb = (opaque, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; };
            MediaPlayer.LibVLCVideoUnlockCb unlock = (opaque, picture, planes) => { };
            MediaPlayer.LibVLCVideoDisplayCb display = (opaque, picture) => { if (++frames >= 2) ready.Set(); };   // the first frame after a seek can be half drawn

            using var mp = new MediaPlayer(_vlc);
            mp.SetVideoFormatCallbacks(format, cleanup);
            mp.SetVideoCallbacks(lockCb, unlock, display);
            mp.Play(media);
            var ok = ready.Wait(8000);
            byte[]? pixels = null;
            if (ok && buffer != IntPtr.Zero)
            {
                pixels = new byte[w * 4 * h];
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
            }
            mp.Stop();
            GC.KeepAlive(format); GC.KeepAlive(cleanup); GC.KeepAlive(lockCb); GC.KeepAlive(unlock); GC.KeepAlive(display);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (pixels == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            File.WriteAllBytes(png, Png.FromBgra(pixels, (int)w, (int)h));
            return true;
        }
    }

    public void Play() { var c = _cur; var f = _fading; _ops.Add(() => { c.SetPause(false); if (f != null && !ReferenceEquals(f, c)) f.SetPause(false); }); _ops.Add(() => _an?.SetPause(false)); }
    public void Pause() { var c = _cur; var f = _fading; _ops.Add(() => { c.SetPause(true); if (f != null && !ReferenceEquals(f, c)) f.SetPause(true); }); _ops.Add(() => _an?.SetPause(true)); }

    public void Stop()
    {
        _awaitingOpen = false;
        var c = _cur;
        // the end of the list, reached by a crossfade's early hand-over: the last song plays out, fading
        if (ReferenceEquals(_handedOver, c) && ReferenceEquals(_fading, c)) { StopAnalysis(); return; }
        _handedOver = null;
        var others = new[] { _mp, _mp2 }.Where(m => m != null && !ReferenceEquals(m, c)).ToList();
        _fading = _fadeIn = null;
        _ops.Add(() => { c.Stop(); foreach (var o in others) o!.Stop(); });
        StopAnalysis();
    }

    public long PositionMs => Math.Max(0, _cur.Time);
    public long DurationMs => Math.Max(0, _cur.Length);

    public void SeekTo(long ms)
    {
        var c = _cur;
        _ops.Add(() => c.Time = ms);
        _ops.Add(() => { if (_an is { IsPlaying: true }) _an.Time = ms; });
    }

    public float Volume
    {
        get => _volume / 100f;
        set
        {
            _volume = (int)Math.Clamp(value * 100, 0, 100);
            var c = _cur;
            if (_fading == null) _ops.Add(() => c.Volume = _volume);
        }
    }

    float _speed = 1;
    /// <summary>Playback speed (podcasts and audiobooks); VLC keeps the pitch.</summary>
    public float Speed
    {
        get => _speed;
        set { _speed = Math.Clamp(value, 0.25f, 4f); var r = _speed; var c = _cur; _ops.Add(() => c.SetRate(r)); _ops.Add(() => _an?.SetRate(r)); }
    }

    bool _muted;
    public bool Muted
    {
        get => _muted;
        set { _muted = value; _ops.Add(() => { _mp.Mute = value; if (_mp2 != null) _mp2.Mute = value; }); }
    }

    // ---------- visualizer feed ----------
    // The audible player can't hand out its samples without also taking over audio output, so a second player
    // decodes the same audio silently (its output goes to these callbacks) and is kept in step with the first.

    MediaPlayer? _an;
    bool _analysisOn, _analysisWanted;
    MediaPlayer.LibVLCAudioPlayCb? _playCb;
    Timer? _syncTimer;
    const int AnalysisRate = 44100;

    /// <summary>Turned on while a visualizer is on screen.</summary>
    public bool AnalysisEnabled
    {
        get => _analysisOn;
        set
        {
            if (_analysisOn == value) return;
            _analysisOn = value;
            if (value) RestartAnalysis(); else StopAnalysis();
        }
    }

    void RestartAnalysis()
    {
        if (!_analysisOn || !_analysisWanted || _url == "") { StopAnalysis(); return; }
        var url = _url;
        _ops.Add(() =>
        {
            if (_an == null)
            {
                _an = new MediaPlayer(_vlc);
                _playCb = OnSamples;
                _an.SetAudioFormat("S16N", AnalysisRate, 1);
                _an.SetAudioCallbacks(_playCb, null, null, null, null);
            }
            using var media = Open(url);
            media.AddOption(":no-video");
            _an.Play(media);
        });
        _syncTimer ??= MainLoop.Every(1000, SyncAnalysis);
    }

    void StopAnalysis()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
        _ops.Add(() => _an?.Stop());
        Spectrum.Clear();
    }

    void SyncAnalysis()
    {
        var an = _an;
        if (an == null || !_analysisOn || _syncTimer == null) return;
        var target = _cur.Time;
        var playing = _cur.IsPlaying;
        _ops.Add(() =>
        {
            if (!an.IsPlaying && playing && an.State == VLCState.Paused) an.SetPause(false);
            if (an.IsPlaying && !playing) an.SetPause(true);
            if (playing && an.IsPlaying && Math.Abs(an.Time - target) > 350) an.Time = target;
        });
    }

    void OnSamples(IntPtr data, IntPtr samples, uint count, long pts)
    {
        var n = (int)count;
        var raw = new short[n];
        Marshal.Copy(samples, raw, 0, n);
        var mono = new float[n];
        for (var i = 0; i < n; i++) mono[i] = raw[i] / 32768f;
        Spectrum.Feed(mono, AnalysisRate);
    }

    public void Dispose()
    {
        try
        {
            _syncTimer?.Dispose();
            _ops.CompleteAdding();
            // Stop on a side thread with a deadline: a hung network stream must not keep the app from closing.
            _fadeTimer.Dispose();
            Task.Run(() => { try { _an?.Stop(); _mp.Stop(); _mp2?.Stop(); } catch { } }).Wait(2000);
        }
        catch { }
    }
}

/// <summary>
/// Receives the playing video's frames from VLC in memory (planar YUV, "I420": about a third the size of RGB) and keeps
/// the newest one for whoever is showing it. VLC is never handed a native window, which does not work reliably on
/// macOS, and the window can draw its own controls over the picture.
/// </summary>
public sealed unsafe class VideoOut
{
    const int MaxWidth = 1920;
    readonly object _lock = new();
    IntPtr _buffer;
    int _size;

    // the callbacks must stay referenced while VLC holds them
    readonly MediaPlayer.LibVLCVideoFormatCb _format;
    readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Bytes per row of the brightness plane and of each colour plane (rows are padded for the decoder).</summary>
    public int LumaStride { get; private set; }
    public int ChromaStride { get; private set; }
    public int LumaLines { get; private set; }
    public int ChromaLines { get; private set; }
    /// <summary>Frames VLC has presented so far.</summary>
    public int Frames { get; private set; }

    /// <summary>Raised on VLC's thread each time a new frame is complete.</summary>
    public event Action? FrameReady;
    /// <summary>Raised on VLC's thread when the video ends or its size changes.</summary>
    public event Action? Cleared;

    public VideoOut(MediaPlayer player)
    {
        _format = Format;
        _cleanup = Cleanup;
        _lockCb = Lock;
        _unlock = Unlock;
        _display = Display;
        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lockCb, _unlock, _display);
    }

    static int Align(int n, int to) => (n + to - 1) / to * to;

    uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // keep the picture's shape; very large videos are scaled down to a sensible size for the screen
        var w = (int)Math.Min(width, MaxWidth) & ~1;
        var h = Math.Max(2, (int)Math.Round(w * (double)height / Math.Max(1, width)) & ~1);
        Marshal.Copy("I420"u8.ToArray(), 0, chroma, 4);
        width = (uint)w; height = (uint)h;
        int ys = Align(w, 32), yl = Align(h, 32), cs = ys / 2, cl = yl / 2;
        fixed (uint* p = &pitches, l = &lines)
        {
            p[0] = (uint)ys; p[1] = (uint)cs; p[2] = (uint)cs;
            l[0] = (uint)yl; l[1] = (uint)cl; l[2] = (uint)cl;
        }
        lock (_lock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _size = ys * yl + 2 * cs * cl;
            _buffer = Marshal.AllocHGlobal(_size);
            new Span<byte>((void*)_buffer, ys * yl).Clear();
            new Span<byte>((void*)(_buffer + ys * yl), 2 * cs * cl).Fill(128);
            Width = w; Height = h; LumaStride = ys; ChromaStride = cs; LumaLines = yl; ChromaLines = cl;
        }
        return 1;
    }

    void Cleanup(ref IntPtr opaque)
    {
        lock (_lock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            Width = Height = 0;
        }
        Cleared?.Invoke();
    }

    IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_lock);   // released in Unlock: VLC writes the frame while we hold the buffer
        Marshal.WriteIntPtr(planes, 0, _buffer);
        Marshal.WriteIntPtr(planes, IntPtr.Size, _buffer + LumaStride * LumaLines);
        Marshal.WriteIntPtr(planes, 2 * IntPtr.Size, _buffer + LumaStride * LumaLines + ChromaStride * ChromaLines);
        return IntPtr.Zero;
    }

    void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(_lock);

    void Display(IntPtr opaque, IntPtr picture)
    {
        Frames++;
        FrameReady?.Invoke();
    }

    /// <summary>Hands the newest frame to <paramref name="read"/> while VLC is kept from writing over it. False when there is no video.</summary>
    public bool Read(Action<IntPtr, int> read)
    {
        lock (_lock)
        {
            if (_buffer == IntPtr.Zero) return false;
            read(_buffer, _size);
            return true;
        }
    }
}
