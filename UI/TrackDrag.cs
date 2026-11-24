using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Lets songs be dragged out of a list or grid, as in the Zune software, where you drag music onto the phone, disc and
/// playlist icons in the lower left. The data carried is a list of songs.
/// </summary>
public static class TrackDrag
{
    public const string Format = "HalcyonTracks";

    /// <summary>
    /// Makes <paramref name="source"/> a drag source. <paramref name="resolve"/> turns the item under the pointer into
    /// songs (or null if it is not draggable). With <paramref name="selection"/>, dragging one of several selected songs
    /// takes all of them.
    /// </summary>
    public static void Attach(FrameworkElement source, Func<object, IEnumerable<Track>?> resolve, ListBox? selection = null)
    {
        Point start = default;
        List<Track>? pending = null;
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pending = null;
            if (e.OriginalSource is not DependencyObject hit || IsInteractive(hit)) return;
            var item = (hit as FrameworkElement)?.DataContext ?? (hit as FrameworkContentElement)?.DataContext;
            if (item == null) return;
            // taken at mouse-down, before the click collapses a multi-selection to the one row
            var selected = selection?.SelectedItems.OfType<Track>().ToList();
            var tracks = item is Track t && selected != null && selected.Count > 1 && selected.Contains(t) ? selected : resolve(item)?.ToList();
            if (tracks == null || tracks.Count == 0) return;
            pending = tracks;
            start = e.GetPosition(source);
        };
        source.PreviewMouseMove += (_, e) =>
        {
            if (pending == null || e.LeftButton != MouseButtonState.Pressed) { pending = null; return; }
            var d = e.GetPosition(source) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var tracks = pending;
            pending = null;
            Dragging = true;
            try { DragDrop.DoDragDrop(source, new DataObject(Format, tracks), DragDropEffects.Copy); }
            finally { Dragging = false; }
        };
        source.PreviewMouseLeftButtonUp += (_, _) => pending = null;
    }

    /// <summary>True while a drag started here is in progress.</summary>
    public static bool Dragging { get; private set; }

    public static List<Track>? Tracks(IDataObject data) => data.GetDataPresent(Format) ? data.GetData(Format) as List<Track> : null;

    // buttons, sliders, scroll bars and text boxes keep their own mouse behaviour
    static bool IsInteractive(DependencyObject hit)
    {
        for (var d = hit; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.TextBoxBase or Slider) return true;
            if (d is ListBoxItem or ListBox) return false;
        }
        return false;
    }
}
