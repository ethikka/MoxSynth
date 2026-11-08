using System.Globalization;
using System.Text.RegularExpressions;
using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Controls;

/// <summary>Trend → green / red / grey (from the app resources).</summary>
public sealed class TrendColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Resource(value switch
        {
            Trend.Up => "TrendUp",
            Trend.Down => "TrendDown",
            _ => "TrendFlat",
        });

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Color Resource(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var c) == true && c is Color color ? color : Colors.Gray;
}

/// <summary>"{2}{U}{U}" → coloured mana symbols as spans.</summary>
public sealed partial class ManaCostConverter : IValueConverter
{
    [GeneratedRegex(@"\{([^}]+)\}|(//)")]
    private static partial Regex Symbol();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var formatted = new FormattedString();
        if (value is not string cost || cost.Length == 0) return formatted;

        foreach (Match m in Symbol().Matches(cost))
        {
            if (m.Groups[2].Success)
            {
                formatted.Spans.Add(new Span { Text = " // ", FontSize = 12 });
                continue;
            }
            var symbol = m.Groups[1].Value;
            var (back, fore) = ColorsOf(symbol);
            formatted.Spans.Add(new Span
            {
                Text = $" {symbol.Replace("/", "")} ",
                BackgroundColor = back,
                TextColor = fore,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
            });
            formatted.Spans.Add(new Span { Text = " ", FontSize = 6 });
        }
        return formatted;
    }

    private static (Color Back, Color Fore) ColorsOf(string symbol) => symbol switch
    {
        "W" => (Color.FromArgb("#F8E7B9"), Colors.Black),
        "U" => (Color.FromArgb("#0E68AB"), Colors.White),
        "B" => (Color.FromArgb("#3D3534"), Colors.White),
        "R" => (Color.FromArgb("#D3202A"), Colors.White),
        "G" => (Color.FromArgb("#00733E"), Colors.White),
        _ => (Color.FromArgb("#CAC5C0"), Colors.Black),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class IsNotNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.Length > 0 : value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertedBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Removed copies / stale prices are shown dimmed.</summary>
public sealed class DimmedOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 0.45 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
