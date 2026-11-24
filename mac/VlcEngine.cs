using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Halcyon.Core;
using LibVLCSharp.Shared;

namespace Halcyon.Desktop;

/// <summary>
/// Playback through VLC's engine (libVLC): plays local files and network streams in practically any format,
/// with seeking handled by the engine. A second, silent decoder supplies samples to the visualizer.
/// </summary>
public class VlcEngine : IMediaEngine, IDisposable
{
    readonly LibVLC _vlc;
    readonly MediaPlayer _mp;
    // libVLC must not be re-entered from its own callback threads, and Stop can block: all commands go through one worker.
    readonly BlockingCollection<Action> _ops = new();
    bool _awaitingOpen;
    string _url = "";
    int _volume = 100;

    public MediaPlayer MediaPlayer => _mp;
    public Spectrum Spectrum { get; } = new();

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public VlcEngine()
    {
        InitializeLibVlc();
        // no --quiet: it would also silence the log callback below, which keeps only warnings and errors anyway
        _vlc = new LibVLC("--no-video-title-show", "--no-osd");
        // VLC's own errors go to vlc.log next to the library, for troubleshooting (it is kept small)
        var log = Path.Combine(AppPaths.DataDir, "vlc.log");
        try { if (File.Exists(log) && new FileInfo(log).Length > 256_000) File.Delete(log); } catch { }
        _vlc.Log += (_, e) =>
        {
            if (e.Level < LogLevel.Warning) return;
            try { lock (LogLock) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {e.Level} {e.Module}: {e.Message}\n"); } catch { }
        };
        _mp = new MediaPlayer(_vlc) { EnableKeyInput = false, EnableMouseInput = false };
        _mp.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_awaitingOpen) return;
            _awaitingOpen = false;
            Opened?.Invoke();
        });
        _mp.EndReached += (_, _) => Dispatcher.UIThread.Post(() => { StopAnalysis(); Ended?.Invoke(); });
        _mp.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => Failed?.Invoke("the file or stream could not be opened"));
        new Thread(() => { foreach (var op in _ops.GetConsumingEnumerable()) try { op(); } catch { } }) { IsBackground = true, Name = "vlc" }.Start();
    }

    /// <summary>
    /// Finds libVLC: the copy bundled in the app (Intel Mac build), else the VLC app or Homebrew install (Apple Silicon),
    /// else the system's own (Linux, Windows). Throws a plain-language message if there is none.
    /// </summary>
    static void InitializeLibVlc()
    {
        if (Platform.IsMac)
        {
            // the copy inside the app: osx-arm64 in the Apple Silicon build, osx-x64 in the Intel one
            var bundled = Path.Combine(AppContext.BaseDirectory, "libvlc", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "lib");
            var candidates = new (string lib, string plugins)[]
            {
                (bundled, Path.Combine(bundled, "vlc", "plugins")),
                ("/Applications/VLC.app/Contents/MacOS/lib", "/Applications/VLC.app/Contents/MacOS/plugins"),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/VLC.app/Contents/MacOS/lib"),
                 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/VLC.app/Contents/MacOS/plugins")),
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
                        $"Halcyon found VLC at {lib} but couldn't load it.\n\n" +
                        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                            ? "This is the Apple Silicon build of Halcyon, so it needs the Apple Silicon (or Universal) VLC. Download the latest VLC for Mac from videolan.org, replace the one in Applications, then open Halcyon again.\n\n"
                            : "Reinstalling VLC from videolan.org usually fixes this.\n\n") + ex.Message);
                }
                try { LibVLCSharp.Shared.Core.Initialize(lib); } catch { /* the resolver above already answers for libvlc */ }
                return;
            }
            throw new InvalidOperationException("Halcyon plays media with VLC's engine, and VLC isn't installed.\n\nInstall VLC from videolan.org (or with Homebrew: brew install --cask vlc), then open Halcyon again.");
        }
        if (Platform.IsLinux)
        {
            // distributions ship libvlc.so.5 (and libvlccore.so.9); the plain names exist only with the -dev package
            var handle = IntPtr.Zero;
            foreach (var name in new[] { "libvlc.so.5", "libvlc.so" })
                if (NativeLibrary.TryLoad(name, out handle)) break;
            var coreHandle = IntPtr.Zero;
            foreach (var name in new[] { "libvlccore.so.9", "libvlccore.so" })
                if (NativeLibrary.TryLoad(name, out coreHandle)) break;
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Halcyon plays media with VLC's engine (libVLC), and it couldn't be loaded.\n\nInstall VLC with your package manager, for example:\n  sudo apt install vlc\n  sudo dnf install vlc\n  sudo pacman -S vlc");
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

    /// <summary>
    /// Answers LibVLCSharp's own lookups for "libvlc" with the library already loaded from its real location. Without
    /// this, .NET only searches next to Halcyon and in the system folders, and never finds the VLC app's copy.
    /// </summary>
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

    static void ResolveTo(Func<string, IntPtr> lookup) =>
        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) => lookup(name));

    Media Open(string url) => url.Contains("://") ? new Media(_vlc, new Uri(url)) : new Media(_vlc, url, FromType.FromPath);

    public void Load(string url, bool isVideo)
    {
        _url = url;
        _awaitingOpen = true;
        _analysisWanted = !isVideo;
        _ops.Add(() =>
        {
            using var media = Open(url);
            _mp.Play(media);
            _mp.Volume = _volume;
        });
        RestartAnalysis();
    }

    // ---------- video thumbnails ----------

    static readonly object ThumbLock = new(), LogLock = new();

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
                Marshal.Copy(System.Text.Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
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

            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;   // RV32 leaves the alpha byte empty
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var bmp = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque,
                    handle.AddrOfPinnedObject(), new Avalonia.PixelSize((int)w, (int)h), new Avalonia.Vector(96, 96), (int)w * 4);
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);
                bmp.Save(png);
            }
            finally { handle.Free(); }
            return true;
        }
    }

    public void Play() { _ops.Add(() => _mp.SetPause(false)); _ops.Add(() => _an?.SetPause(false)); }
    public void Pause() { _ops.Add(() => _mp.SetPause(true)); _ops.Add(() => _an?.SetPause(true)); }
    public void Stop() { _awaitingOpen = false; _ops.Add(() => _mp.Stop()); StopAnalysis(); }
    public long PositionMs => Math.Max(0, _mp.Time);
    public long DurationMs => Math.Max(0, _mp.Length);

    public void SeekTo(long ms)
    {
        _ops.Add(() => _mp.Time = ms);
        _ops.Add(() => { if (_an is { IsPlaying: true }) _an.Time = ms; });
    }

    public float Volume
    {
        get => _volume / 100f;
        set { _volume = (int)Math.Clamp(value * 100, 0, 100); _ops.Add(() => _mp.Volume = _volume); }
    }

    public bool Muted
    {
        get => _mp.Mute;
        set => _ops.Add(() => _mp.Mute = value);
    }

    // ---------- visualizer feed ----------
    // The audible player can't hand out its samples without also taking over audio output, so a second player
    // decodes the same audio silently (its output goes to these callbacks) and is kept in step with the first.

    MediaPlayer? _an;
    bool _analysisOn, _analysisWanted;
    MediaPlayer.LibVLCAudioPlayCb? _playCb;
    DispatcherTimer? _syncTimer;
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
        _syncTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SyncAnalysis());
        _syncTimer.Start();
    }

    void StopAnalysis()
    {
        _syncTimer?.Stop();
        _ops.Add(() => _an?.Stop());
        Spectrum.Clear();
    }

    void SyncAnalysis()
    {
        var an = _an;
        if (an == null || !_analysisOn) return;
        var target = _mp.Time;
        var playing = _mp.IsPlaying;
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
            _syncTimer?.Stop();
            _ops.CompleteAdding();
            // Stop on a side thread with a deadline: a hung network stream must not keep the app from closing.
            Task.Run(() => { try { _an?.Stop(); _mp.Stop(); } catch { } }).Wait(2000);
        }
        catch { }
    }
}
