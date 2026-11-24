using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// ui:Tr.Text on a TextBlock shows a name and, when translating is switched on and the name is in another language,
/// its translation beside it in a quieter colour: "夜に駆ける  Racing into the Night".
/// </summary>
public static class Tr
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Tr), new PropertyMetadata(null, OnChanged));
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    static async void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = e.NewValue as string ?? "";
        if (!Translator.Needs(text)) { tb.Text = text; return; }
        Show(tb, text, Translator.Lookup(text));
        var translated = await Translator.GetAsync(text);
        if (GetText(tb) == text) Show(tb, text, translated);
    }

    static void Show(TextBlock tb, string text, string translated)
    {
        if (translated == "") { tb.Text = text; return; }
        tb.Inlines.Clear();
        tb.Inlines.Add(new Run(text));
        // big headings carry the translation smaller; rows carry it at the same size, just quieter
        var side = new Run("   " + translated) { FontStyle = FontStyles.Normal };
        if (tb.FontSize >= 20) side.FontSize = Math.Max(13, tb.FontSize * 0.55);
        side.SetResourceReference(TextElement.ForegroundProperty, "FgFaint");
        tb.Inlines.Add(side);
        if (tb.ToolTip == null) tb.ToolTip = $"{text}\n{translated}";
    }

    /// <summary>The text a block shows, translation included (for the self-test).</summary>
    public static string Shown(TextBlock tb) => tb.Inlines.Count == 0 ? tb.Text : string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text));
}
