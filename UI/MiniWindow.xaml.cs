using System.Windows;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>Compact always-on-top player; closing it brings the main window back.</summary>
public partial class MiniWindow : Window
{
    static Player P => App.Player;

    public MiniWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
        P.CurrentChanged += Refresh;
        P.StateChanged += Refresh;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var total = P.Duration.TotalSeconds;
            Progress.Width = P.Current != null && total > 0 ? Math.Clamp(P.Position.TotalSeconds / total, 0, 1) * ((FrameworkElement)Progress.Parent).ActualWidth : 0;
        };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); P.CurrentChanged -= Refresh; P.StateChanged -= Refresh; };
        Refresh();
    }

    void Refresh()
    {
        var cur = P.Current;
        TitleText.Text = cur?.Title ?? "nothing playing";
        ArtistText.Text = cur?.Subtitle ?? "";
        Art.SetPath(ArtImage, cur?.ArtPath);
        PlayBtn.Content = P.IsPlaying ? "" : "";
        HeartBtn.IsEnabled = cur?.Track != null;
        HeartBtn.Content = cur?.Track?.RatingGlyph ?? "";
    }

    void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Close(); else DragMove();
    }

    void Restore_Click(object sender, RoutedEventArgs e) => Close();
    void Prev_Click(object sender, RoutedEventArgs e) => P.Previous();
    void Play_Click(object sender, RoutedEventArgs e) => P.Toggle();
    void Next_Click(object sender, RoutedEventArgs e) => P.Next();
    void Heart_Click(object sender, RoutedEventArgs e) { Cmd.Rate.Execute(P.Current?.Track); Refresh(); }
}
