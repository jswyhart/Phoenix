using System.Windows;
using Halcyon.Core;
using Windows.Media;

namespace Halcyon.UI;

/// <summary>Windows System Media Transport Controls: hardware media keys and the system media flyout.</summary>
public class Smtc
{
    SystemMediaTransportControls? _c;

    public void Init(IntPtr hwnd)
    {
        try
        {
            _c = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _c.IsEnabled = true;
            _c.IsPlayEnabled = _c.IsPauseEnabled = _c.IsNextEnabled = _c.IsPreviousEnabled = true;
            _c.ButtonPressed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                switch (e.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                    case SystemMediaTransportControlsButton.Pause: App.Player.Toggle(); break;
                    case SystemMediaTransportControlsButton.Next: App.Player.Next(); break;
                    case SystemMediaTransportControlsButton.Previous: App.Player.Previous(); break;
                }
            });
        }
        catch { _c = null; }
    }

    public void Update(PlayItem? item, bool playing)
    {
        if (_c == null) return;
        try
        {
            _c.PlaybackStatus = item == null ? MediaPlaybackStatus.Closed : playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (item == null) return;
            var u = _c.DisplayUpdater;
            u.Type = MediaPlaybackType.Music;
            u.MusicProperties.Title = item.Title;
            u.MusicProperties.Artist = item.Subtitle;
            u.MusicProperties.AlbumTitle = item.Album;
            u.Update();
        }
        catch { }
    }
}
