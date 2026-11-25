using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Halcyon.UI;

/// <summary>
/// Keeps frames coming on time on fast displays. When the drawing system (WPF) finishes a frame before the screen has
/// shown the last one, it waits for the next screen refresh with an ordinary Windows timer, and those cannot fire
/// sooner than 10 to 16 ms: on a 500 Hz display that is five to eight missed frames each time. While something is
/// moving, a precise clock here notices that wait and ends it as soon as the refresh has actually passed.
/// It reaches into WPF's private parts to do so; if those are not found (a later .NET changed them) it does nothing.
/// </summary>
public static class FramePacer
{
    static DispatcherTimer? _wait;          // WPF's own "wait for the next refresh" timer
    static MethodInfo? _expired;            // what that timer runs when it fires
    static PropertyInfo? _ticks;            // WPF's clock, in the units the timer's due time is kept in
    static object? _context;
    static Dispatcher? _dispatcher;
    static int _holds, _pending;
    static Thread? _thread;
    static bool _looked;

    /// <summary>How long before WPF's own due time the wait is ended, in 100 ns units. WPF adds a millisecond of
    /// caution after the refresh; on a display whose whole frame is 2 ms that caution costs a frame.</summary>
    public static long Lead = long.TryParse(Environment.GetEnvironmentVariable("PHOENIX_PACER_LEAD"), out var lead) ? lead : 9000;
    /// <summary>Off for comparison in the timing harness.</summary>
    public static bool Enabled = true;
    /// <summary>Whether WPF's parts were found, so this can do anything.</summary>
    public static bool Available { get; private set; }
    /// <summary>How many waits were ended early.</summary>
    public static int Nudges { get; private set; }

    static void Look()
    {
        if (_looked) return;
        _looked = true;
        try
        {
            _dispatcher = Application.Current.Dispatcher;
            var type = typeof(System.Windows.Media.Visual).Assembly.GetType("System.Windows.Media.MediaContext");
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            _context = type?.GetMethod("From", any, null, new[] { typeof(Dispatcher) }, null)?.Invoke(null, new object[] { _dispatcher });
            _wait = type?.GetField("_estimatedNextVSyncTimer", any)?.GetValue(_context) as DispatcherTimer;
            _expired = type?.GetMethod("EstimatedNextVSyncTimeExpired", any);
            _ticks = type?.GetProperty("CurrentTicks", any);
            Available = _context != null && _wait != null && _expired != null && _ticks?.GetValue(null) is long;
        }
        catch { Available = false; }
    }

    /// <summary>Something started moving (call on the interface thread; pair with <see cref="Release"/>).</summary>
    public static void Hold()
    {
        Look();
        if (!Available || !Enabled) return;
        if (Interlocked.Increment(ref _holds) == 1 && _thread == null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "frame pacer", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    public static void Release()
    {
        if (!Available || !Enabled) return;
        if (Interlocked.Decrement(ref _holds) < 0) Interlocked.Exchange(ref _holds, 0);
    }

    static void Run()
    {
        // a timer that really can wait half a millisecond (ordinary sleeps round up to 1 to 16 ms)
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* high resolution */, 0x1F0003);
        try
        {
            var idle = 0;
            while (true)
            {
                if (Volatile.Read(ref _holds) <= 0)
                {
                    if (++idle > 4000) break;        // nothing has moved for a couple of seconds: stop the thread
                    Thread.Sleep(1);
                    continue;
                }
                idle = 0;
                if (timer != IntPtr.Zero) { long due = -3000; SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false); WaitForSingleObject(timer, 50); }
                else Thread.Sleep(1);
                try
                {
                    if (_wait!.IsEnabled && Volatile.Read(ref _pending) == 0 && _wait.Tag is long due2 && (long)_ticks!.GetValue(null)! >= due2 - Lead)
                    {
                        Interlocked.Exchange(ref _pending, 1);
                        _dispatcher!.BeginInvoke(DispatcherPriority.Render, Nudge);
                    }
                }
                catch { }
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
            _thread = null;
            if (Volatile.Read(ref _holds) > 0) { _thread = new Thread(Run) { IsBackground = true, Name = "frame pacer" }; _thread.Start(); }
        }
    }

    static void Nudge()
    {
        try
        {
            // the same thing WPF's timer would do when it fired, only on time
            if (_wait!.IsEnabled && _wait.Tag is long due && (long)_ticks!.GetValue(null)! is var now && now >= due - Lead)
            {
                if (now < due) _wait.Tag = now;
                _expired!.Invoke(_context, new object[] { _wait, EventArgs.Empty });
                Nudges++;
            }
        }
        catch { Available = false; }
        finally { Interlocked.Exchange(ref _pending, 0); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
