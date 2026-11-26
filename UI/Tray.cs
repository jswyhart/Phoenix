using System.Windows;
using Halcyon.Core;
using Forms = System.Windows.Forms;

namespace Halcyon.UI;

/// <summary>
/// Running on in the notification area (settings → display → when the window is closed; off by default). Closing
/// the window then hides it and the music carries on; the icon brings it back, works play and pause, previous and
/// next, and quits Phoenix for good.
/// </summary>
public static class Tray
{
    static Forms.NotifyIcon? _icon;
    static Forms.ToolStripMenuItem? _play;
    static bool _told;

    /// <summary>Set while Phoenix is quitting for good (from the icon's menu, or when the option is off).</summary>
    public static bool Quitting;

    public static bool On => App.Settings.RunInTray;
    public static bool Shown => _icon?.Visible == true;

    /// <summary>Puts the icon in the notification area while the option is on, and takes it away when it is off.</summary>
    public static void Sync()
    {
        if (!On) { Remove(); return; }
        if (_icon != null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Phoenix", null, (_, _) => Restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _play = new Forms.ToolStripMenuItem("Play", null, (_, _) => App.Win.Dispatcher.Invoke(() => App.Player.Toggle()));
        menu.Items.Add(_play);
        menu.Items.Add("Previous", null, (_, _) => App.Win.Dispatcher.Invoke(() => App.Player.Previous()));
        menu.Items.Add("Next", null, (_, _) => App.Win.Dispatcher.Invoke(() => App.Player.Next()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Phoenix", null, (_, _) => App.Win.Dispatcher.Invoke(Quit));
        menu.Opening += (_, _) => { if (_play != null) _play.Text = App.Player.IsPlaying ? "Pause" : "Play"; };
        System.Drawing.Icon? picture = null;
        try { picture = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _icon = new Forms.NotifyIcon { Icon = picture ?? System.Drawing.SystemIcons.Application, Text = "Phoenix", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Restore(); };
        App.Player.CurrentChanged += ShowSong;
        ShowSong();
    }

    /// <summary>The icon's tooltip names what is playing (it can hold 127 characters).</summary>
    static void ShowSong()
    {
        if (_icon == null) return;
        var cur = App.Player.Current;
        var text = cur == null ? "Phoenix" : $"Phoenix: {cur.Title}" + (cur.Subtitle != "" ? $" – {cur.Subtitle}" : "");
        _icon.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    static void Remove()
    {
        if (_icon == null) return;
        App.Player.CurrentChanged -= ShowSong;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    /// <summary>The window is being closed: hide it instead, if the option is on. True when it was hidden.</summary>
    public static bool HideInstead()
    {
        if (!On || Quitting) return false;
        Sync();
        App.Win.Hide();
        if (!_told && !App.SmokeMode)
        {
            _told = true;
            _icon?.ShowBalloonTip(4000, "Phoenix is still running", "It keeps playing here. Click the icon to bring it back; right-click it to quit.", Forms.ToolTipIcon.None);
        }
        return true;
    }

    public static void Restore()
    {
        App.Win.Dispatcher.Invoke(() =>
        {
            App.Win.Show();
            if (App.Win.WindowState == WindowState.Minimized) App.Win.WindowState = WindowState.Normal;
            App.Win.Activate();
        });
    }

    public static void Quit()
    {
        Quitting = true;
        Remove();
        App.Win.Close();
    }

    /// <summary>When Phoenix ends: the icon must not linger in the notification area.</summary>
    public static void Dispose() => Remove();
}
