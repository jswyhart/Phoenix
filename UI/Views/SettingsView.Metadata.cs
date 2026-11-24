using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Settings: where album details and covers come from, and how much each source is trusted.</summary>
public partial class SettingsView
{
    bool _sourcesBuilt;

    void BuildSources()
    {
        if (_sourcesBuilt) return;
        _sourcesBuilt = true;
        foreach (var source in Metadata.Sources)
        {
            var setting = Metadata.Setting(source.Id);
            var on = new CheckBox { Content = source.Name, IsChecked = setting.Enabled, FontSize = 14, Tag = source.Id };
            var gives = new TextBlock { Text = source.Gives, FontSize = 12, Margin = new Thickness(24, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
            gives.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

            var weightLabel = new TextBlock { Text = "weight", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 10, 0) };
            weightLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
            var weight = new Slider { Minimum = 0, Maximum = 10, Value = setting.Weight, Width = 150, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 2, VerticalAlignment = VerticalAlignment.Center, Tag = source.Id };
            var weightValue = new TextBlock { Text = setting.Weight.ToString(), Width = 26, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(weightLabel);
            line.Children.Add(weight);
            line.Children.Add(weightValue);

            var state = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            state.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            void ShowState() => state.Text = source.KeyHelp != null && !source.Ready ? "no key" : "";
            if (source.KeyHelp != null)
            {
                var keyLabel = new TextBlock { Text = "key", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 10, 0) };
                keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
                var key = new PasswordBox { Width = 230, Password = source.Id == "discogs" ? S.DiscogsToken : Metadata.Key(source.Id), ToolTip = source.KeyHelp, Tag = source.Id };
                key.LostFocus += (_, _) =>
                {
                    var value = key.Password.Trim();
                    if (source.Id == "discogs") { if (S.DiscogsToken == value) return; S.DiscogsToken = value; }
                    else { if (Metadata.Key(source.Id) == value) return; S.ApiKeys[source.Id] = value; }
                    if (!App.SmokeMode) S.Save();
                    ShowState();
                    DiscogsStatus.Text = value == "" ? $"{source.Name} key removed" : $"{source.Name} key saved";
                };
                line.Children.Add(keyLabel);
                line.Children.Add(key);
            }
            line.Children.Add(state);
            ShowState();

            on.Click += (_, _) => { setting.Enabled = on.IsChecked == true; if (!App.SmokeMode) S.Save(); ShowOrder(); };
            weight.ValueChanged += (_, _) =>
            {
                setting.Weight = (int)Math.Round(weight.Value);
                weightValue.Text = setting.Weight.ToString();
                if (!App.SmokeMode) S.Save();
                ShowOrder();
            };

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            block.Children.Add(on);
            block.Children.Add(gives);
            block.Children.Add(line);
            if (source.KeyHelp != null)
            {
                var help = new TextBlock { Text = source.KeyHelp, FontSize = 11, Margin = new Thickness(24, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                help.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
                block.Children.Add(help);
            }
            SourcesPanel.Children.Add(block);
        }
        AcoustKey.Password = Metadata.Key("acoustid");
        FpcalcBox.Text = S.FpcalcPath;
        ShowOrder();
    }

    void ShowOrder()
    {
        var order = Metadata.Ranked();
        SourceOrder.Text = order.Count == 0 ? "No source is switched on and ready."
            : "Asked in this order: " + string.Join("  →  ", order.Select(s => s.Name)) + ".";
    }

    /// <summary>For the self-test: the per-source rows.</summary>
    public int SourceRowCount => SourcesPanel.Children.Count;
    public string SourceOrderText => SourceOrder.Text;

    void AcoustSave(object sender, RoutedEventArgs e)
    {
        var key = AcoustKey.Password.Trim();
        var path = FpcalcBox.Text.Trim().Trim('"');
        if (Metadata.Key("acoustid") == key && S.FpcalcPath == path) return;
        S.ApiKeys["acoustid"] = key;
        S.FpcalcPath = path;
        if (!App.SmokeMode) S.Save();
        DiscogsStatus.Text = Metadata.CanIdentify ? "ready: right-click a song and choose “Identify by sound…”"
            : key == "" ? "AcoustID needs its key" : "fpcalc.exe was not found at that place";
    }

    void FpcalcBrowse_Click(object sender, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "Where is fpcalc.exe?", Filter = "fpcalc|fpcalc.exe|Programs|*.exe" };
        if (pick.ShowDialog(App.Win) != true) return;
        FpcalcBox.Text = pick.FileName;
        AcoustSave(sender, e);
    }

    void WriteTags_Click(object sender, RoutedEventArgs e)
    {
        S.WriteTagsToFiles = WriteTags.IsChecked == true;
        S.Save();
    }

    async void AutoMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_autoMatch != null) { _autoMatch.Cancel(); return; }
        var order = Metadata.Ranked();
        if (order.Count == 0) { DiscogsStatus.Text = "switch on at least one source first (a source with a key needs its key)"; return; }
        var albums = App.Lib.Albums.Where(a => a.Title != "Unknown Album" && (a.ArtPath == "" || a.Year == 0 || a.Genre == "") && Metadata.For(a).Count > 0).ToList();
        if (albums.Count == 0) { DiscogsStatus.Text = "every album already has a cover, year and genre"; return; }
        if (MessageBox.Show(App.Win, $"{albums.Count} albums are missing a cover, year or genre.\n\nPhoenix will ask your sources in order of weight ({string.Join(", ", order.Select(s => s.Name))}) " +
                "and fill in only what is missing" + (S.WriteTagsToFiles ? ", and write it into your music files" : "") +
                ". Albums tied to one source from their right-click menu use only that source. This takes a few seconds per album.",
                "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _autoMatch = new CancellationTokenSource();
        AutoMatchBtn.Content = "stop";
        int done = 0, matched = 0;
        try
        {
            foreach (var album in albums)
            {
                if (_autoMatch.IsCancellationRequested) break;
                DiscogsStatus.Text = $"matching {++done} of {albums.Count}: {album.Artist} – {album.Title}";
                try
                {
                    var found = await Metadata.FillAsync(album, _autoMatch.Token);
                    if (!found.Any) continue;
                    // only the gaps are filled: the album keeps its own title and artist
                    var fill = new DiscogsRelease(0, "", "", found.Year, found.Genre, "", found.CoverUrl, new());
                    await App.Lib.ApplyReleaseAsync(album, fill, albumInfo: found.Year > 0 || found.Genre != "", titles: false, cover: found.Cover != null, coverBytes: found.Cover);
                    matched++;
                }
                catch { /* skip this album */ }
            }
            DiscogsStatus.Text = $"filled in {matched} of {albums.Count} albums" + (_autoMatch.IsCancellationRequested ? " (stopped)" : "");
        }
        finally
        {
            _autoMatch = null;
            AutoMatchBtn.Content = "fill in albums missing info";
        }
    }
}
