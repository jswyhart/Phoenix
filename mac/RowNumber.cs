using Avalonia;
using Avalonia.Controls;

namespace Halcyon.Desktop;

/// <summary>
/// Numbers the rows of a song list 1, 2, 3… down the list as shown (the Windows app does the same), instead of each
/// song's track number on its album. Turn it on with <see cref="Enable"/>; row templates bind to Number on their ListBoxItem.
/// </summary>
public static class Row
{
    public static readonly AttachedProperty<string> NumberProperty = AvaloniaProperty.RegisterAttached<ListBoxItem, string>("Number", typeof(Row), "");
    public static string GetNumber(ListBoxItem item) => item.GetValue(NumberProperty);
    public static void SetNumber(ListBoxItem item, string value) => item.SetValue(NumberProperty, value);

    public static void Enable(ListBox list)
    {
        list.ContainerPrepared += (_, e) => { if (e.Container is ListBoxItem item) SetNumber(item, (e.Index + 1).ToString()); };
        list.ContainerIndexChanged += (_, e) => { if (e.Container is ListBoxItem item) SetNumber(item, (e.NewIndex + 1).ToString()); };
    }
}
