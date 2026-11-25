using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Halcyon.UI;

/// <summary>
/// The artist photos behind now playing: one picture at a time, slowly drifting and growing, fading into the next
/// every twenty seconds or so. A dark wash over it keeps the song's name and the visualizer readable.
/// </summary>
public class Backdrop : Grid
{
    readonly Image[] _layers = { NewLayer(), NewLayer() };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    List<string> _files = new();
    int _index, _front;
    int _generation;

    /// <summary>Pictures shown since the app started (for the self-test).</summary>
    public int Shown { get; private set; }
    public bool HasPictures => _files.Count > 0;
    public event Action? Changed;

    static Image NewLayer() => new()
    {
        Stretch = Stretch.UniformToFill, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.06, 1.06), new TranslateTransform() } }
    };

    public Backdrop()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        foreach (var layer in _layers) Children.Add(layer);
        Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(0xE6, 0, 0, 0), 0), new(Color.FromArgb(0x8C, 0, 0, 0), 0.55), new(Color.FromArgb(0x73, 0, 0, 0), 1) }, new Point(0, 1), new Point(1, 0))
        });
        _timer.Tick += (_, _) => { if (_files.Count > 1) _ = NextAsync(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && _files.Count > 1) _timer.Start(); else _timer.Stop(); };
    }

    /// <summary>Shows these pictures in turn (an empty list clears the backdrop).</summary>
    public void Show(List<string> files)
    {
        if (files.SequenceEqual(_files)) return;
        _files = files.ToList();
        _generation++;
        _index = -1;
        _timer.Stop();
        if (_files.Count == 0)
        {
            foreach (var layer in _layers) layer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(1.2)));
            Changed?.Invoke();
            return;
        }
        _ = NextAsync();
        if (_files.Count > 1 && IsVisible) _timer.Start();
        Changed?.Invoke();
    }

    async Task NextAsync()
    {
        var generation = _generation;
        _index = (_index + 1) % _files.Count;
        var picture = await Make.PictureAsync(_files[_index], 1920);
        if (picture == null || generation != _generation) return;
        var back = _layers[_front];
        _front = 1 - _front;
        var front = _layers[_front];
        front.Source = picture;
        Panel.SetZIndex(front, 1);
        Panel.SetZIndex(back, 0);
        // a slow push in and a drift to one side, a different way each time
        var group = (TransformGroup)front.RenderTransform;
        var scale = (ScaleTransform)group.Children[0];
        var move = (TranslateTransform)group.Children[1];
        var rnd = Random.Shared;
        var length = TimeSpan.FromSeconds(26);
        var zoomIn = rnd.Next(2) == 0;
        var grow = new DoubleAnimation(zoomIn ? 1.05 : 1.16, zoomIn ? 1.16 : 1.05, length);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        double dx = (rnd.NextDouble() - 0.5) * 50, dy = (rnd.NextDouble() - 0.5) * 30;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dx, dx, length));
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-dy, dy, length));
        front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.4)));
        back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(2.4)));
        Shown++;
    }
}
