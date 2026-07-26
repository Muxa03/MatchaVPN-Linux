using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace MatchaLab.App;

public sealed class FlagToCodeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var sb = new StringBuilder();
        foreach (var rune in s.EnumerateRunes())
            if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
                sb.Append((char)('A' + (rune.Value - 0x1F1E6)));
        return sb.Length > 0 ? sb.ToString() : "··";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
