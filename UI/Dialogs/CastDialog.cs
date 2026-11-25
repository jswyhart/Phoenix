using System.Windows;
using System.Windows.Controls;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Finds speakers and TVs on the network and lets you choose one to play on.</summary>
public class CastDialog : Window
{
    readonly ListBox _list = new() { Height = 250 };
    readonly TextBlock _note = Make.Text("", 12, "FgDim", new Thickness(0, 8, 0, 0), wrap: true);
    CancellationTokenSource? _search;

    public CastTarget? Chosen { get; private set; }

    CastDialog()
    {
        Title = "Play on another device";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var ok = Make.Button("play there", "PillBtn", Pick);
        ok.IsDefault = true; ok.MinWidth = 96;
        var again = Make.Button("look again", "PillBtn", () => _ = FindAsync());
        var cancel = Make.Button("cancel", "PillBtn", Close, margin: new Thickness(0));
        cancel.IsCancel = true;
        var buttons = Make.Row(again, ok, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Make.Text("Speakers, receivers and TVs on your network that can play music: DLNA devices and Chromecasts.", 13, "Fg", new Thickness(0, 0, 0, 10), wrap: true));
        panel.Children.Add(_list);
        panel.Children.Add(_note);
        panel.Children.Add(buttons);
        Content = panel;
        _list.MouseDoubleClick += (_, _) => Pick();
        Loaded += (_, _) => _ = FindAsync();
        Closed += (_, _) => _search?.Cancel();
    }

    void Pick()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not CastTarget target) return;
        Chosen = target;
        DialogResult = true;
    }

    async Task FindAsync()
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        _list.Items.Clear();
        _note.Text = "looking for devices…";
        try
        {
            await CastFinder.FindAsync(target => Dispatcher.BeginInvoke(() =>
            {
                if (search.IsCancellationRequested) return;
                var item = new ListBoxItem { Tag = target, Padding = new Thickness(8, 6, 8, 6), Content = Make.Column(Make.Text(target.Name, 14), Make.Text(target.Detail, 11.5, "FgFaint")) };
                _list.Items.Add(item);
                if (_list.Items.Count == 1) _list.SelectedIndex = 0;
            }), search.Token);
        }
        catch { }
        if (search.IsCancellationRequested) return;
        await Task.Delay(300);   // let the last finds reach the list
        _note.Text = _list.Items.Count == 0
            ? "Nothing answered. The device has to be on and on the same network as this PC, and Windows Firewall has to let Phoenix through on private networks."
            : "The first time, Windows may ask whether Phoenix may be reached from your network: the device fetches the music from this PC, so answer yes for private networks.";
    }

    /// <summary>Asks which device to play on; null if none was chosen.</summary>
    public static CastTarget? Ask()
    {
        var d = new CastDialog { Owner = App.Win };
        return d.ShowDialog() == true ? d.Chosen : null;
    }
}
