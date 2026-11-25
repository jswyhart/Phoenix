using System.IO;
using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Library care: the same song stored twice, and songs whose files have gone.</summary>
public class CareView : UserControl
{
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 0, 0) };
    readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 14, 0, 0) };
    readonly TextBlock _status = Make.Text("", 13, "Accent", new Thickness(6, 10, 28, 6), wrap: true);
    readonly StackPanel _list = new() { Margin = new Thickness(6, 4, 28, 30) };
    string _tab = "duplicates";
    List<LibraryCare.DupGroup> _groups = new();
    readonly Dictionary<LibraryCare.DupGroup, Track> _keep = new();
    List<Track> _missing = new();
    Dictionary<Track, string> _located = new();
    CancellationTokenSource? _listening;
    bool _busy;

    public List<LibraryCare.DupGroup> Groups => _groups;
    public List<Track> MissingShown => _missing;

    public CareView()
    {
        var head = new StackPanel { Margin = new Thickness(22, 8, 0, 0) };
        head.Children.Add(Make.Styled("library care", "ZuneHead"));
        head.Children.Add(_tabs);
        head.Children.Add(_actions);
        head.Children.Add(_status);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = _list, Margin = new Thickness(22, 0, 0, 0) });
        Content = dock;
        IsVisibleChanged += (_, _) => { if (IsVisible) Show(_tab); };
    }

    public void Show(string tab)
    {
        _tab = tab;
        _tabs.Children.Clear();
        foreach (var (id, label) in new[] { ("duplicates", "duplicates"), ("missing", "missing files") })
        {
            var b = Make.Button(label, "TextBtn", () => Show(id), margin: new Thickness(0, 0, 22, 0));
            b.Opacity = id == _tab ? 1 : 0.5;
            b.FontSize = 15;
            _tabs.Children.Add(b);
        }
        _actions.Children.Clear();
        if (tab == "duplicates")
        {
            _actions.Children.Add(Make.Button("FIND BY NAME", "PillBtn", FindByName, "Songs with the same artist, title and length", new Thickness(0)));
            var listen = Make.Button("CHECK THESE BY SOUND", "PillBtn", () => _ = ConfirmAsync(), "Listens to each group to tell real copies from different versions", new Thickness(10, 0, 0, 0));
            var all = Make.Button("COMPARE EVERYTHING BY SOUND", "PillBtn", () => _ = BySoundAsync(), "Listens to every song on this PC; finds copies whose names differ. Slow the first time.", new Thickness(10, 0, 0, 0));
            listen.IsEnabled = all.IsEnabled = LibraryCare.CanFingerprint;
            _actions.Children.Add(listen);
            _actions.Children.Add(all);
            _actions.Children.Add(Make.Button("KEEP THE BEST OF EACH", "PillBtn", ResolveAll, "In every group, keeps the best copy and takes the others out of the library", new Thickness(10, 0, 0, 0)));
            if (_groups.Count == 0) FindByName(); else ShowGroups();
            if (!LibraryCare.CanFingerprint) _status.Text += (_status.Text == "" ? "" : "   ") + "Checking by sound needs fpcalc (settings → metadata → identify songs by sound).";
        }
        else
        {
            _actions.Children.Add(Make.Button("LOOK FOR MOVED FILES", "PillBtn", () => _ = LocateAsync(), "Searches your music folders for files of the same name", new Thickness(0)));
            _actions.Children.Add(Make.Button("REMOVE ALL FROM LIBRARY", "PillBtn", RemoveMissing, "Takes these songs out of the library", new Thickness(10, 0, 0, 0)));
            ShowMissing();
        }
    }

    // ---------- duplicates ----------

    void FindByName()
    {
        _groups = LibraryCare.ByName();
        _keep.Clear();
        ShowGroups();
    }

    async Task ConfirmAsync()
    {
        if (_busy || _groups.Count == 0) return;
        _busy = true;
        try
        {
            for (var i = 0; i < _groups.Count; i++)
            {
                _status.Text = $"listening… group {i + 1} of {_groups.Count}";
                await LibraryCare.ConfirmAsync(_groups[i]);
            }
        }
        finally { _busy = false; }
        ShowGroups();
    }

    async Task BySoundAsync()
    {
        if (_busy) { _listening?.Cancel(); return; }
        _busy = true;
        _listening = new CancellationTokenSource();
        try
        {
            _groups = await LibraryCare.BySoundAsync((done, total) => Dispatcher.BeginInvoke(() => _status.Text = $"listening to your songs… {done} of {total}   (press the button again to stop)"), _listening.Token);
            _keep.Clear();
        }
        catch (OperationCanceledException) { _status.Text = "stopped; what was heard so far is remembered"; return; }
        catch (Exception ex) { _status.Text = "couldn't finish: " + ex.Message; return; }
        finally { _busy = false; _listening = null; }
        ShowGroups();
    }

    void ShowGroups()
    {
        _list.Children.Clear();
        var extra = _groups.Sum(g => g.Tracks.Count - 1);
        _status.Text = _groups.Count == 0 ? "No duplicates found." : $"{_groups.Count} song{(_groups.Count == 1 ? "" : "s")} stored more than once ({extra} extra cop{(extra == 1 ? "y" : "ies")}). The copy marked is the one that would be kept.";
        foreach (var g in _groups.Take(300))
        {
            var group = g;
            if (!_keep.ContainsKey(g) || !g.Tracks.Contains(_keep[g])) _keep[g] = g.Best;
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var title = new TextBlock { FontSize = 15 };
            Tr.SetText(title, $"{g.Tracks[0].Title}");
            box.Children.Add(title);
            box.Children.Add(Make.Text($"{g.Tracks[0].Artist}   ·   {g.Why}", 12, "FgDim", new Thickness(0, 0, 0, 4)));
            foreach (var t in g.Tracks)
            {
                var track = t;
                var pick = new RadioButton { GroupName = "keep" + g.GetHashCode(), IsChecked = ReferenceEquals(_keep[g], t), Margin = new Thickness(0, 3, 0, 3), ToolTip = "Keep this copy" };
                pick.SetResourceReference(ForegroundProperty, "Fg");
                pick.Content = Make.Column(Make.Text(t.SourceId, 12.5), Make.Text($"{LibraryCare.Describe(t)}   ·   {t.Album}", 11.5, "FgFaint"));
                pick.Checked += (_, _) => _keep[group] = track;
                box.Children.Add(pick);
            }
            box.Children.Add(Make.Row(
                Make.Button("keep marked, remove the rest from the library", "TextBtn", () => Resolve(group, false), "The other files stay on disk", new Thickness(0, 4, 18, 0)),
                Make.Button("…and move their files to the Recycle Bin", "TextBtn", () => Resolve(group, true), null, new Thickness(0, 4, 18, 0)),
                Make.Button("not duplicates", "TextBtn", () => { _groups.Remove(group); ShowGroups(); }, "Leave these alone", new Thickness(0, 4, 0, 0))));
            _list.Children.Add(box);
        }
        if (_groups.Count > 300) _list.Children.Add(Make.Text($"…and {_groups.Count - 300} more, shown once these are dealt with.", 12, "FgFaint"));
    }

    /// <summary>Keeps one copy of a group. Asks first when files are to be moved to the Recycle Bin.</summary>
    public void Resolve(LibraryCare.DupGroup g, bool recycle, bool ask = true)
    {
        var keep = _keep.GetValueOrDefault(g) ?? g.Best;
        var others = g.Tracks.Count - 1;
        if (recycle && ask && MessageBox.Show(App.Win, $"Move {others} file{(others == 1 ? "" : "s")} to the Recycle Bin and keep\n\n{keep.SourceId}\n\nPlays, likes and playlist places pass to the copy that is kept.",
                "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var failed = LibraryCare.Resolve(g, keep, recycle);
        _groups.Remove(g);
        ShowGroups();
        if (failed > 0) _status.Text = $"{failed} file{(failed == 1 ? "" : "s")} could not be moved to the Recycle Bin (in use or read-only); they were taken out of the library.";
    }

    void ResolveAll()
    {
        if (_groups.Count == 0) return;
        var extra = _groups.Sum(g => g.Tracks.Count - 1);
        var answer = MessageBox.Show(App.Win, $"Keep the marked copy of each of the {_groups.Count} songs and take the other {extra} out of the library?\n\nYes: also move those {extra} files to the Recycle Bin.\nNo: leave the files on disk.",
            "Phoenix", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        var failed = 0;
        foreach (var g in _groups.ToList()) failed += LibraryCare.Resolve(g, _keep.GetValueOrDefault(g) ?? g.Best, answer == MessageBoxResult.Yes);
        _groups.Clear();
        ShowGroups();
        _status.Text = $"{extra} extra cop{(extra == 1 ? "y" : "ies")} removed" + (failed > 0 ? $"; {failed} file(s) could not be moved to the Recycle Bin" : "") + ".";
    }

    // ---------- missing files ----------

    void ShowMissing()
    {
        _missing = LibraryCare.Missing();
        _located = _located.Where(kv => _missing.Contains(kv.Key) && File.Exists(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        _list.Children.Clear();
        _status.Text = _missing.Count == 0 ? "Every song's file is where the library expects it."
            : $"{_missing.Count} song{(_missing.Count == 1 ? "" : "s")} in the library {(_missing.Count == 1 ? "has" : "have")} no file. If a drive is not connected, connect it and look again.";
        if (_located.Count > 0)
            _list.Children.Add(Make.Button($"POINT THE {_located.Count} FOUND SONG{(_located.Count == 1 ? "" : "S")} AT THEIR NEW PLACES", "PillBtn", Relink, "Plays, likes and playlists are kept", new Thickness(0, 0, 0, 14)));
        foreach (var t in _missing.Take(500))
        {
            var track = t;
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var title = new TextBlock { FontSize = 14 };
            Tr.SetText(title, t.Title);
            box.Children.Add(title);
            box.Children.Add(Make.Text($"{t.Artist}   ·   {t.Album}", 12, "FgDim"));
            box.Children.Add(Make.Text("was at  " + t.SourceId, 11.5, "FgFaint"));
            if (_located.TryGetValue(t, out var now)) box.Children.Add(Make.Text("found at  " + now, 11.5, "Accent"));
            box.Children.Add(Make.Row(
                Make.Button("choose the file…", "TextBtn", () => Choose(track), null, new Thickness(0, 2, 18, 0)),
                Make.Button("remove from library", "TextBtn", () => { App.Lib.RemoveTracks(new[] { track }); ShowMissing(); }, null, new Thickness(0, 2, 0, 0))));
            _list.Children.Add(box);
        }
        if (_missing.Count > 500) _list.Children.Add(Make.Text($"…and {_missing.Count - 500} more.", 12, "FgFaint"));
    }

    async Task LocateAsync()
    {
        if (_busy) return;
        _busy = true;
        _status.Text = "looking through your music folders…";
        try
        {
            var missing = LibraryCare.Missing();
            _located = await Task.Run(() => LibraryCare.Locate(missing));
        }
        finally { _busy = false; }
        ShowMissing();
        _status.Text += _located.Count == 0 ? "  None of them turned up elsewhere in your music folders." : $"  {_located.Count} turned up elsewhere.";
    }

    void Relink()
    {
        var done = _located.Count(kv => App.Lib.Relink(kv.Key, kv.Value));
        _located.Clear();
        App.Lib.NotifyChanged();
        ShowMissing();
        _status.Text = $"{done} song{(done == 1 ? "" : "s")} pointed at {(done == 1 ? "its" : "their")} new place.  " + _status.Text;
    }

    void Choose(Track t)
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = $"Where is “{t.Title}”?", Filter = "Music|" + string.Join(";", Library.AudioExts.Select(e => "*" + e)) + "|All files|*.*", FileName = Path.GetFileName(t.SourceId) };
        if (pick.ShowDialog(App.Win) != true) return;
        if (!App.Lib.Relink(t, pick.FileName)) { _status.Text = "That file already belongs to another song in the library."; return; }
        App.Lib.NotifyChanged();
        ShowMissing();
    }

    void RemoveMissing()
    {
        var missing = LibraryCare.Missing();
        if (missing.Count == 0) return;
        if (MessageBox.Show(App.Win, $"Take {missing.Count} song{(missing.Count == 1 ? "" : "s")} with no file out of the library? Their plays, likes and playlist places go with them.",
                "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        App.Lib.RemoveTracks(missing);
        ShowMissing();
    }
}
