using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Halcyon.Core;

namespace Halcyon.UI;

public record PicFolder(string Name, string Path, int Count, List<PictureItem>? Items = null);
/// <summary>File is a local path or a full-size image URL; ArtPath is what the thumbnail loader reads.</summary>
public record PictureItem(string Name, string File, string ArtPath);

public partial class PicturesView : UserControl
{
    List<PictureItem> _pics = new();
    int _viewIndex;
    readonly DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    bool _loaded;

    /// <summary>Sorting and filtering for the pictures of the folder on show.</summary>
    public ViewOptions Options { get; } = new("pictures", new[] { ("", "name"), ("date", "date") });
    List<PictureItem> _folderPics = new();

    public List<PictureItem> Shown => _pics;

    static DateTime DateOf(PictureItem p) { try { return File.Exists(p.File) ? File.GetLastWriteTimeUtc(p.File) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

    void ShowPics()
    {
        _pics = Options.Order(_folderPics.Where(p => Options.Matches(p.Name)), key => key == "date" ? list => list.OrderByDescending(DateOf) : null);
        PicCap.Text = _pics.Count == _folderPics.Count ? $"PICTURES  {_pics.Count}" : $"PICTURES  {_pics.Count} of {_folderPics.Count}";
        PicGrid.SetItems(_pics);
    }

    public PicturesView()
    {
        InitializeComponent();
        Options.Margin = new Thickness(0, -6, 18, 4);
        DockPanel.SetDock(Options, Dock.Right);
        PicBar.Children.Insert(1, Options);
        Options.Changed += ShowPics;
        // photos open on a single click
        PicGrid.DrawWith(TileStyle.Picture, o => o is PictureItem pic ? new TileLook(pic.ArtPath, Tip: pic.Name) : default);
        PicGrid.ItemClicked += o => { PicGrid.SelectedItem = o; OpenViewer(_pics.IndexOf((PictureItem)o), false); };
        _slideTimer.Tick += (_, _) => Step(1);
        IsVisibleChanged += (_, _) => { if (IsVisible && !_loaded) _ = ReloadAsync(); };
    }

    public async Task ReloadAsync()
    {
        _loaded = true;
        var roots = App.Settings.PictureFolders.ToList();
        var folders = await Task.Run(() =>
        {
            var list = new List<PicFolder>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var root in roots.Where(Directory.Exists))
                foreach (var dir in new[] { root }.Concat(Directory.EnumerateDirectories(root, "*", opts)))
                {
                    int n;
                    try { n = Directory.EnumerateFiles(dir).Count(f => Library.PictureExts.Contains(System.IO.Path.GetExtension(f))); }
                    catch { continue; }
                    if (n > 0) list.Add(new PicFolder(dir == root ? System.IO.Path.GetFileName(root.TrimEnd('\\')) : System.IO.Path.GetRelativePath(root, dir), dir, n));
                }
            return list;
        });
        // Photo libraries on connected servers sit alongside the local folders.
        foreach (var fetch in new Func<Task<List<Services.RemotePhoto>>>[] { App.Jellyfin.FetchPhotosAsync, App.Plex.FetchPhotosAsync })
            try
            {
                foreach (var g in (await fetch()).GroupBy(p => p.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    folders.Add(new PicFolder(g.Key, "", g.Count(), g.Select(p => new PictureItem(p.Name, p.FullUrl, p.ThumbUrl)).ToList()));
            }
            catch { /* server offline: show what we have */ }
        FolderCap.Text = $"FOLDERS  {folders.Count}";
        FolderList.ItemsSource = folders;
        EmptyText.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (folders.Count > 0) FolderList.SelectedIndex = 0; else { _folderPics = new(); ShowPics(); }
    }

    void Folder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not PicFolder f) return;
        try
        {
            _folderPics = f.Items ?? Directory.EnumerateFiles(f.Path).Where(p => Library.PictureExts.Contains(System.IO.Path.GetExtension(p)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => new PictureItem(System.IO.Path.GetFileName(p), p, "shell:" + p)).ToList();
        }
        catch { _folderPics = new(); }
        ShowPics();
    }

    // ---------- viewer ----------

    void OpenViewer(int index, bool slideshow)
    {
        if (_pics.Count == 0) return;
        _viewIndex = Math.Max(0, index);
        Viewer.Visibility = Visibility.Visible;
        Focus();
        ShowCurrent();
        if (slideshow) _slideTimer.Start();
    }

    async void ShowCurrent()
    {
        var pic = _pics[_viewIndex];
        ViewerCaption.Text = $"{pic.Name}   ·   {_viewIndex + 1} of {_pics.Count}";
        var src = await Art.LoadAsync(pic.File, 2400, cache: false);
        if (_viewIndex < _pics.Count && _pics[_viewIndex] == pic) ViewerImage.Source = src;
    }

    void Step(int delta)
    {
        if (_pics.Count == 0) return;
        _viewIndex = (_viewIndex + delta + _pics.Count) % _pics.Count;
        ShowCurrent();
    }

    void CloseViewer()
    {
        _slideTimer.Stop();
        Viewer.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
    }

    void Slideshow_Click(object sender, RoutedEventArgs e) => OpenViewer(0, true);
    void ViewerPrev_Click(object sender, RoutedEventArgs e) { _slideTimer.Stop(); Step(-1); }
    void ViewerNext_Click(object sender, RoutedEventArgs e) { _slideTimer.Stop(); Step(1); }
    void ViewerClose_Click(object sender, RoutedEventArgs e) => CloseViewer();
    void Viewer_Click(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) CloseViewer(); }

    void View_KeyDown(object sender, KeyEventArgs e)
    {
        if (Viewer.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.Escape: CloseViewer(); e.Handled = true; break;
            case Key.Left: _slideTimer.Stop(); Step(-1); e.Handled = true; break;
            case Key.Right: _slideTimer.Stop(); Step(1); e.Handled = true; break;
        }
    }
}
