using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// What will sync to the phone. Music dragged to the phone icon lands in a playlist called "Phone sync", which the
/// Android app copies (with the rest of what was chosen on the phone) the next time it syncs over Wi-Fi.
/// </summary>
public partial class PhoneView : UserControl
{
    public const string ListName = "Phone sync";
    List<Track> _songs = new();

    public PhoneView()
    {
        InitializeComponent();
        SongHdr.Changed += Reload;
        App.Lib.Changed += Reload;
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = TrackDrag.Tracks(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (TrackDrag.Tracks(e.Data) is { } t) Add(t); };
        Reload();
    }

    public static Playlist? List => App.Lib.Playlists.FirstOrDefault(p => p.Name == ListName);

    /// <summary>Adds songs to the phone sync list (creating it on first use); returns how many were new.</summary>
    public static int Add(IEnumerable<Track> tracks)
    {
        var list = List ?? App.Lib.CreatePlaylist(ListName);
        var have = list.TrackIds.ToHashSet();
        // only music on this PC can be copied to the phone; server music streams there directly
        var fresh = tracks.Where(t => t.Source == "local" && have.Add(t.Id)).ToList();
        if (fresh.Count > 0) App.Lib.AddToPlaylist(list, fresh);
        return fresh.Count;
    }

    void Reload()
    {
        var s = App.Settings;
        _songs = SongHdr.Sort.Apply(List is { } p ? App.Lib.TracksOf(p) : new List<Track>());
        SongList.ItemsSource = _songs;
        EmptyText.Visibility = _songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListCap.Text = $"PHONE SYNC LIST  {_songs.Count}   ·   {Fmt.Time(_songs.Sum(t => t.DurationMs))}";
        if (!s.SyncEnabled)
        {
            StatusText.Text = "PHONE SYNC IS OFF";
            DetailText.Text = "Turn it on under sync options, then pair the companion app on your Android phone with this PC.";
        }
        else if (!App.Sync.IsRunning)
        {
            StatusText.Text = "PHONE SYNC COULD NOT START";
            DetailText.Text = App.Sync.LastError ?? "";
        }
        else
        {
            var addresses = SyncServer.LocalAddresses();
            StatusText.Text = "READY TO SYNC OVER WI-FI";
            DetailText.Text = $"Pairing code  {s.SyncCode}\n" + (addresses.Count == 0 ? "This PC is not on a network." : "This PC's address  " + string.Join("  or  ", addresses)) +
                              "\n\nOn the phone: settings → sync with PC. Everything you drag to the phone icon is copied the next time the phone syncs.";
        }
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        App.Win.Navigate("settings");
        App.Win.Page<SettingsView>("settings").ShowCategory("phone");
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List is not { } p) return;
        var gone = SongList.SelectedItems.Cast<Track>().Select(t => t.Id).ToHashSet();
        if (gone.Count == 0) return;
        p.TrackIds = p.TrackIds.Where(id => !gone.Contains(id)).ToList();
        App.Lib.SavePlaylist(p);
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (List is not { } p || p.TrackIds.Count == 0) return;
        p.TrackIds = new List<long>();
        App.Lib.SavePlaylist(p);
    }

    void Song_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) Menus.Play(_songs, t);
    }

    void Song_RightClick(object sender, MouseButtonEventArgs e)
    {
        var sel = SongList.SelectedItems.Cast<Track>().ToList();
        if (sel.Count > 0) Menus.Open(Menus.ForTracks(sel, sel.Count == 1 ? _songs : null), SongList);
    }
}
