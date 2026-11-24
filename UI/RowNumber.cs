using System.Globalization;
using System.Windows.Data;

namespace Halcyon.UI;

/// <summary>Shows a list row's position (1, 2, 3…) instead of the song's track number on its album.</summary>
public sealed class RowNumber : IValueConverter
{
    public static readonly RowNumber Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i && i >= 0 ? (i + 1).ToString() : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
