using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Halcyon.UI;

/// <summary>
/// Makes the mouse wheel glide instead of jump. Windows hands a list a wheel click and the list moves three rows at
/// once, in a single frame, however fast the screen is. Here each click sets where the list should end up, and the
/// list is moved toward it a little on every screen frame, so scrolling runs at the display's own refresh rate.
/// </summary>
public static class SmoothScroll
{
    sealed class State
    {
        public double Target, Current;
        public bool Moving, Ours;
    }

    static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    static readonly List<WeakReference<ScrollViewer>> Active = new();
    static bool _hooked;
    static TimeSpan _lastFrame;
    static long _lastTicks;

    /// <summary>How far one wheel click travels, in pixels.</summary>
    public const double Step = 110;
    /// <summary>How quickly the list closes on where it is going: the time to cover about two thirds of the way, in seconds.</summary>
    const double Ease = 0.075;

    /// <summary>Timing harness only: lay the list out inside the step and note how long that took (milliseconds).</summary>
    public static List<double>? LayoutTimes;

    /// <summary>Whether any list is gliding right now.</summary>
    public static bool Moving => _hooked;

    /// <summary>Frames in which a list was moved (for the self-test).</summary>
    public static int Frames { get; private set; }

    public static void Enable()
    {
        // a class handler runs before the list's own wheel handling, so the jump never happens
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrolled));
    }

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sv.ScrollableHeight <= 0) return;                         // nothing to scroll here: let an outer list have it
        // lists that scroll row by row (not by pixel) cannot be moved part of a row: leave those to Windows
        if (sv.CanContentScroll && FindPanel(sv) is { } panel && VirtualizingPanel.GetScrollUnit(ItemsControl.GetItemsOwner(panel) ?? (DependencyObject)panel) == ScrollUnit.Item) return;
        var s = States.GetOrCreateValue(sv);
        if (!s.Moving) s.Target = s.Current = sv.VerticalOffset;
        var wanted = Math.Clamp(s.Target - e.Delta / 120.0 * Step, 0, sv.ScrollableHeight);
        if (Math.Abs(wanted - s.Target) < 0.5 && !s.Moving)
        {
            // already at the end in that direction: an outer list may still be able to move
            return;
        }
        s.Target = wanted;
        e.Handled = true;
        if (!s.Moving)
        {
            s.Moving = true;
            lock (Active) Active.Add(new WeakReference<ScrollViewer>(sv));
            if (!_hooked) { _hooked = true; _lastTicks = 0; CompositionTarget.Rendering += OnFrame; }
        }
    }

    /// <summary>Something else moved the list (the scroll bar, the keyboard, code): carry on from where it now is.</summary>
    static void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || !States.TryGetValue(sv, out var s) || s.Ours) return;
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if (s.Moving && Math.Abs(sv.VerticalOffset - s.Current) > 1.5) { s.Target = s.Current = sv.VerticalOffset; }
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        // one step per screen frame (the event can fire more than once for the same frame)
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _lastFrame) return;
            _lastFrame = frame.RenderingTime;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = _lastTicks == 0 ? 1.0 / 60 : Math.Clamp((now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency, 0.001, 0.05);
        _lastTicks = now;
        var keep = Math.Exp(-dt / Ease);

        List<WeakReference<ScrollViewer>> list;
        lock (Active) list = Active.ToList();
        foreach (var weak in list)
        {
            if (!weak.TryGetTarget(out var sv) || !States.TryGetValue(sv, out var s) || !s.Moving) { lock (Active) Active.Remove(weak); continue; }
            s.Target = Math.Clamp(s.Target, 0, sv.ScrollableHeight);
            s.Current = s.Target + (s.Current - s.Target) * keep;
            var done = Math.Abs(s.Target - s.Current) < 0.4;
            if (done) s.Current = s.Target;
            s.Ours = true;
            // Lists are moved by whole screen pixels. At a part-pixel position every line of text has to be drawn again
            // (and comes out soft); on whole pixels the graphics card just slides what it already drew.
            var scale = VisualTreeHelper.GetDpi(sv).DpiScaleY;
            var snapped = Math.Round(s.Current * scale) / scale;
            if (Math.Abs(snapped - sv.VerticalOffset) < 0.01 && !done) { s.Ours = false; continue; }   // not a whole pixel further yet
            sv.ScrollToVerticalOffset(done ? Math.Round(s.Target * scale) / scale : snapped);
            if (LayoutTimes != null)
            {
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                sv.UpdateLayout();
                LayoutTimes.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            s.Ours = false;
            Frames++;
            if (done) { s.Moving = false; lock (Active) Active.Remove(weak); }
        }
        lock (Active)
        {
            if (Active.Count == 0) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
        }
    }

    static Panel? FindPanel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingPanel v) return v;
            if (child is ItemsPresenter or ScrollContentPresenter or Border or Grid or Decorator)
            {
                if (FindPanel(child) is { } found) return found;
            }
        }
        return null;
    }

    /// <summary>For the self-test: a wheel click on a list, as the mouse would send it.</summary>
    public static void Wheel(ScrollViewer sv, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sv };
        sv.RaiseEvent(args);
    }
}
