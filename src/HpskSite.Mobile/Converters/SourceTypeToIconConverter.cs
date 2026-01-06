using System.Globalization;

namespace HpskSite.Mobile.Converters;

/// <summary>
/// Converts SourceType to an icon emoji
/// - "Training" (self-entered) → 📝
/// - "TrainingMatch" (app match) → 🎯
/// - "Competition" or "Official" → 🏆
/// </summary>
public class SourceTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string sourceType)
            return "📝";

        return sourceType switch
        {
            "TrainingMatch" => "🎯",
            "Competition" or "Official" => "🏆",
            _ => "📝" // Training or unknown
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
