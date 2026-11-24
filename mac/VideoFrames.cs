using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Halcyon.Desktop;

/// <summary>
/// Draws a VLC player's video inside the app: VLC decodes each frame into memory (BGRA), and the newest frame is
/// copied into a bitmap that an ordinary Image shows. This avoids handing VLC a native window, which does not work
/// reliably on macOS, and lets the app draw its own controls over the picture.
/// </summary>
public sealed class VideoFrames
{
    const int MaxWidth = 1920;
    readonly object _lock = new();
    readonly Image _target;
    IntPtr _buffer;
    int _width, _height, _pitch;
    bool _frameReady, _postPending;
    WriteableBitmap? _bitmap;

    // the callbacks must stay referenced while VLC holds them
    readonly MediaPlayer.LibVLCVideoFormatCb _format;
    readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    /// <summary>Frames shown so far (for the self-test).</summary>
    public int FramesShown { get; private set; }
    public PixelSize Size => new(_width, _height);

    public VideoFrames(MediaPlayer player, Image target)
    {
        _target = target;
        _format = Format;
        _cleanup = Cleanup;
        _lockCb = Lock;
        _unlock = (_, _, _) => { };
        _display = Display;
        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lockCb, _unlock, _display);
    }

    uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // keep the picture's shape; very large videos are scaled down to a sensible size for the screen
        var w = (int)Math.Min(width, MaxWidth);
        var h = (int)Math.Max(2, Math.Round(w * (double)height / Math.Max(1, width)));
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        width = (uint)w; height = (uint)h; pitches = (uint)(w * 4); lines = (uint)h;
        lock (_lock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(w * 4 * h);
            _width = w; _height = h; _pitch = w * 4;
            _frameReady = false;
        }
        return 1;
    }

    void Cleanup(ref IntPtr opaque)
    {
        lock (_lock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _width = _height = 0;
        }
        Dispatcher.UIThread.Post(() => { _target.Source = null; _bitmap = null; });
    }

    IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_lock);   // released in Display: VLC writes the frame while we hold the buffer
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    void Display(IntPtr opaque, IntPtr picture)
    {
        _frameReady = true;
        var post = !_postPending;
        _postPending = true;
        Monitor.Exit(_lock);
        // only one copy waits for the UI at a time; if the UI is slow, frames are dropped rather than queued
        if (post) Dispatcher.UIThread.Post(Show, DispatcherPriority.Render);
    }

    void Show()
    {
        lock (_lock)
        {
            _postPending = false;
            if (!_frameReady || _buffer == IntPtr.Zero) return;
            if (_bitmap == null || _bitmap.PixelSize.Width != _width || _bitmap.PixelSize.Height != _height)
                _bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = _bitmap.Lock())
            {
                if (fb.RowBytes == _pitch) CopyBytes(_buffer, fb.Address, _pitch * _height);
                else for (var y = 0; y < _height; y++) CopyBytes(_buffer + y * _pitch, fb.Address + y * fb.RowBytes, _pitch);
            }
            _frameReady = false;
        }
        FramesShown++;
        if (!ReferenceEquals(_target.Source, _bitmap)) _target.Source = _bitmap;
        _target.InvalidateVisual();
    }

    static unsafe void CopyBytes(IntPtr from, IntPtr to, int count) => Buffer.MemoryCopy((void*)from, (void*)to, count, count);
}
