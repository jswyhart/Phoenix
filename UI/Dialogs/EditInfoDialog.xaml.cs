using System.Windows;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>Edits one song, or the shared fields of several songs / a whole album. Blank shared fields are left unchanged.</summary>
public partial class EditInfoDialog : Window
{
    readonly List<Track> _tracks;
    readonly bool _single;

    public EditInfoDialog(List<Track> tracks, bool album)
    {
        InitializeComponent();
        Owner = App.Win;
        _tracks = tracks;
        _single = tracks.Count == 1 && !album;
        var f = tracks[0];

        string Shared(Func<Track, string> get) => tracks.All(t => get(t) == get(f)) ? get(f) : "";

        Heading.Text = _single ? "edit song info" : album ? "edit album info" : $"edit {tracks.Count} songs";
        Hint.Text = _single ? "" : "Changes apply to every selected song. Fields left blank keep their current values.";
        if (f.Source != "local") Hint.Text += (Hint.Text == "" ? "" : " ") + $"This music comes from {f.Source}: your changes are kept in Phoenix and survive the next sync; the server itself is not changed.";
        Hint.Visibility = Hint.Text == "" ? Visibility.Collapsed : Visibility.Visible;

        TitleBox.Text = _single ? f.Title : "";
        TrackBox.Text = _single && f.TrackNo > 0 ? f.TrackNo.ToString() : "";
        TitleBox.IsEnabled = TrackBox.IsEnabled = _single;
        ArtistBox.Text = Shared(t => t.Artist);
        AlbumBox.Text = Shared(t => t.Album);
        AlbumArtistBox.Text = Shared(t => t.AlbumArtist);
        GenreBox.Text = Shared(t => t.Genre);
        YearBox.Text = Shared(t => t.Year > 0 ? t.Year.ToString() : "");
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _tracks)
        {
            void Apply(string value, Action<string> set)
            {
                value = value.Trim();
                if (value != "" || _single) set(value);
            }
            if (_single)
            {
                if (TitleBox.Text.Trim() != "") t.Title = TitleBox.Text.Trim();
                t.TrackNo = int.TryParse(TrackBox.Text, out var n) ? n : 0;
            }
            Apply(ArtistBox.Text, v => t.Artist = v == "" ? "Unknown Artist" : v);
            Apply(AlbumBox.Text, v => t.Album = v == "" ? "Unknown Album" : v);
            Apply(AlbumArtistBox.Text, v => t.AlbumArtist = v == "" ? t.Artist : v);
            Apply(GenreBox.Text, v => t.Genre = v);
            Apply(YearBox.Text, v => t.Year = int.TryParse(v, out var y) ? y : 0);
        }
        var failed = App.Lib.SaveTracks(_tracks);
        if (failed > 0)
            MessageBox.Show(this, $"Saved in Phoenix, but {failed} file(s) couldn't be updated (in use or read-only).", "Phoenix", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }
}
