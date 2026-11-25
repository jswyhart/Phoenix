using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// One row of a song list, drawn directly (number, title, artist, album, tags, time, heart) instead of being built
/// from a dozen separate controls. A row like this costs a fraction of a millisecond to make, which is what lets a
/// long list scroll without late frames on a fast display. The columns match <see cref="SongHeader"/>.
/// </summary>
public class SongRowView : FrameworkElement
{
    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(SongRowView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Number, title, time and heart only (the song list beside an album).</summary>
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    const double RowHeight = 30, NumberWidth = 34, TimeWidth = 56, CompactTimeWidth = 52, HeartWidth = 30, Gap = 12;
    Track? _track;
    ListBoxItem? _item;
    static readonly DependencyPropertyDescriptor Alternation = DependencyPropertyDescriptor.FromProperty(ItemsControl.AlternationIndexProperty, typeof(ListBoxItem));

    public SongRowView()
    {
        Height = RowHeight;
        SnapsToDevicePixels = true;
        DataContextChanged += (_, e) =>
        {
            if (_track != null) _track.PropertyChanged -= TrackChanged;
            _track = e.NewValue as Track;
            if (_track != null) _track.PropertyChanged += TrackChanged;
            InvalidateVisual();
        };
        Loaded += (_, _) =>
        {
            // the row's number is its place in the list, which changes when rows are added, removed or reused
            for (DependencyObject? o = this; o != null; o = VisualTreeHelper.GetParent(o))
                if (o is ListBoxItem item) { _item = item; Alternation.AddValueChanged(item, NumberChanged); break; }
            if (_track != null) { _track.PropertyChanged -= TrackChanged; _track.PropertyChanged += TrackChanged; }
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            if (_item != null) Alternation.RemoveValueChanged(_item, NumberChanged);
            _item = null;
            if (_track != null) _track.PropertyChanged -= TrackChanged;
        };
    }

    void NumberChanged(object? sender, EventArgs e) => InvalidateVisual();
    void TrackChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>The row's number as drawn (for the self-test).</summary>
    public string NumberText => _item != null && ItemsControl.GetAlternationIndex(_item) is var i and >= 0 ? (i + 1).ToString() : "";
    /// <summary>The words drawn in the row, left to right (for the self-test).</summary>
    public string Drawn { get; private set; } = "";

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    FormattedText Text(string text, Brush brush, double width, double size, Typeface face)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        ft.MaxTextWidth = Math.Max(1, width);
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, RowHeight));   // the whole row takes clicks
        if (_track is not { } t || w < 80) { Drawn = ""; return; }
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var size = TextElement.GetFontSize(this);
        Brush fg = Res("Fg"), dim = Res("FgDim"), faint = Res("FgFaint");
        var drawn = new List<string>();

        void Left(string text, Brush brush, double x, double width, double fontSize)
        {
            if (text == "" || width < 8) return;
            var ft = Text(text, brush, width - Gap, fontSize, face);
            dc.DrawText(ft, new Point(x, (RowHeight - ft.Height) / 2));
            drawn.Add(text);
        }
        void Right(string text, Brush brush, double right, double width, double fontSize)
        {
            if (text == "") return;
            var ft = Text(text, brush, width, fontSize, face);
            dc.DrawText(ft, new Point(right - ft.WidthIncludingTrailingWhitespace, (RowHeight - ft.Height) / 2));
            drawn.Add(text);
        }
        // a name, and its translation after it in a quieter colour when there is one
        void Name(string text, Brush brush, double x, double width)
        {
            if (text == "" || width < 8) return;
            var translated = Translator.Needs(text) ? Translator.Lookup(text) : "";
            if (translated == "" && Translator.Needs(text)) Ask(text);
            var ft = Text(text, brush, width - Gap, size, face);
            dc.DrawText(ft, new Point(x, (RowHeight - ft.Height) / 2));
            drawn.Add(text);
            var used = ft.WidthIncludingTrailingWhitespace + 10;
            if (translated != "" && width - Gap - used > 24)
            {
                var side = Text(translated, faint, width - Gap - used, size, face);
                dc.DrawText(side, new Point(x + used, (RowHeight - side.Height) / 2));
                drawn.Add(translated);
            }
        }

        Right(NumberText, faint, NumberWidth - Gap, NumberWidth, size);
        var heartLeft = w - HeartWidth;
        if (Compact)
        {
            Name(t.Title, fg, NumberWidth, heartLeft - CompactTimeWidth - NumberWidth);
            Right(t.DurationText, dim, heartLeft, CompactTimeWidth, size);
        }
        else
        {
            // 3 : 2 : 2 : 1.5 shares of what the fixed columns leave, as in the header
            var unit = (w - NumberWidth - TimeWidth - HeartWidth) / 8.5;
            var x = NumberWidth;
            Name(t.Title, fg, x, unit * 3); x += unit * 3;
            Name(t.Artist, dim, x, unit * 2); x += unit * 2;
            Name(t.Album, dim, x, unit * 2); x += unit * 2;
            Left(t.TagText, Res("Accent"), x, unit * 1.5, 12);
            Right(t.DurationText, dim, heartLeft, TimeWidth, size);
        }
        // the heart: like, dislike or (faint) not rated
        var icons = new Typeface(TryFindResource("Icons") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var heart = new FormattedText(t.RatingGlyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, icons, 12, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.PushOpacity(_overHeart ? Math.Max(0.6, t.RatingOpacity) : t.RatingOpacity);
        dc.DrawText(heart, new Point(heartLeft + (HeartWidth - heart.Width) / 2 - 1, (RowHeight - heart.Height) / 2));
        dc.Pop();
        Drawn = string.Join(" | ", drawn);
    }

    async void Ask(string text)
    {
        var track = _track;
        var result = await Translator.GetAsync(text);
        if (result != "" && ReferenceEquals(track, _track)) InvalidateVisual();
    }

    // ---- the heart takes clicks; the rest of the row belongs to the list ----

    bool _overHeart;
    bool OnHeart(MouseEventArgs e) => e.GetPosition(this).X >= ActualWidth - HeartWidth;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var over = OnHeart(e);
        if (over != _overHeart) { _overHeart = over; ToolTip = over ? "Like / dislike" : null; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_overHeart) { _overHeart = false; ToolTip = null; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!OnHeart(e) || _track == null) return;
        Cmd.Rate.Execute(_track);
        e.Handled = true;   // rating a song does not select or play it
    }
}
