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
        PhonePairing.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Reload(); });
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
        TurnOnBtn.Visibility = s.SyncEnabled ? Visibility.Collapsed : Visibility.Visible;
        OptionsBtn.Content = "SYNC OPTIONS";
        if (!s.SyncEnabled)
        {
            StatusText.Text = "PHONE SYNC IS OFF";
            DetailText.Text = "Turn it on to copy music, likes and playlists to Phoenix on your Android phone over Wi-Fi, and to use the phone as a remote.";
        }
        else if (!App.Sync.IsRunning)
        {
            StatusText.Text = "PHONE SYNC COULD NOT START";
            DetailText.Text = App.Sync.LastError ?? "";
        }
        else
        {
            var phones = PhonePairing.Phones.OrderByDescending(x => x.LastSeen).ToList();
            StatusText.Text = "READY TO SYNC OVER WI-FI";
            if (phones.Count == 0)
            {
                DetailText.Text = "No phone is paired yet. Choose PAIR A PHONE and point the phone's camera at the QR code, or in Phoenix on the phone tap settings → sync with PC → pair with my pc.";
                OptionsBtn.Content = "PAIR A PHONE";
            }
            else
                DetailText.Text = string.Join("\n\n", phones.Select(x => x.Name + "\n" + (x.LastSync > 0 ? $"last synced {Fmt.Ago(x.LastSync)}" : $"paired {Fmt.Ago(x.Paired)}, not synced yet")))
                                  + "\n\nThe phone copies this list, and keeps it up to date by itself whenever Phoenix is opened on it.";
        }
    }

    void TurnOn_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.SyncEnabled = true;
        App.Settings.Save();
        App.Sync.Start();
        Reload();
        App.Win.SetStatus(App.Sync.IsRunning ? "phone sync is on: pair a phone under sync options" : "phone sync couldn't start");
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
        if (p.TrackIds.Count > 1 && Halcyon.UI.MessageBox.Show(App.Win, $"Take all {p.TrackIds.Count} songs off the phone sync list? The phone removes its copies the next time it syncs (unless it chose them itself).",
                "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
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
