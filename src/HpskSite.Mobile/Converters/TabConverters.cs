using System.Globalization;

namespace HpskSite.Mobile.Converters;

/// <summary>
/// Converts boolean to background color for tabs (selected = Gray700, unselected = Transparent)
/// Dark mode only.
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
        {
            return Color.FromArgb("#374151"); // Gray700 for dark mode
        }
        return Colors.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to FontAttributes (selected = Bold, unselected = None)
/// </summary>
public class BoolToFontAttributeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
        {
            return FontAttributes.Bold;
        }
        return FontAttributes.None;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to text color for tabs (selected = Primary, unselected = Gray)
/// </summary>
public class BoolToTabTextColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
        {
            // Primary blue color for selected
            return Color.FromArgb("#007bff");
        }
        // Gray for unselected (dark mode only)
        return Color.FromArgb("#9CA3AF"); // Gray400
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts string to boolean (true if not null or empty)
/// Use ConverterParameter="invert" to invert the result
/// </summary>
public class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool result = false;
        if (value is string str)
        {
            result = !string.IsNullOrWhiteSpace(str);
        }

        // Invert if parameter is "invert"
        if (parameter is string paramStr && paramStr.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            result = !result;
        }

        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to stroke thickness (selected = 3, unselected = 1)
/// </summary>
public class BoolToStrokeThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
        {
            return 3.0;
        }
        return 1.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts int to bool.
/// Without parameter: true if value > 0
/// With parameter: true if value >= parameter (threshold)
/// </summary>
public class IntToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            // If parameter is provided, use it as minimum threshold
            if (parameter is string thresholdStr && int.TryParse(thresholdStr, out int threshold))
            {
                return intValue >= threshold;
            }
            // Default: check if > 0
            return intValue > 0;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Gets score from a Scores list by series index (0-based).
/// Parameter is the series index. Returns "-" if index is out of range.
/// </summary>
public class SeriesScoreConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IList scores && parameter is string indexStr)
        {
            if (int.TryParse(indexStr, out int index) && index >= 0 && index < scores.Count)
            {
                var score = scores[index];
                // Use reflection to get Total property since we don't want to reference shared models here
                var totalProp = score?.GetType().GetProperty("Total");
                if (totalProp != null)
                {
                    var total = totalProp.GetValue(score);
                    // Cap series score at 50 (max possible score per series is 5x10 = 50)
                    if (total is int totalInt)
                    {
                        return Math.Min(totalInt, 50).ToString();
                    }
                    return total?.ToString() ?? "-";
                }
            }
        }
        return "-";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns background color based on whether a score exists at the given series index.
/// Green if score exists, light grey if not.
/// Parameter is the series index (0-based).
/// </summary>
public class SeriesScoreBackgroundConverter : IValueConverter
{
    // Define colors for dark mode only
    private static readonly Color ScoreGreenDark = Color.FromArgb("#1e5631"); // Dark green for scores
    private static readonly Color EmptyDark = Color.FromArgb("#374151");      // Dark grey for empty

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasScore = false;

        if (value is System.Collections.IList scores && parameter is string indexStr)
        {
            if (int.TryParse(indexStr, out int index) && index >= 0 && index < scores.Count)
            {
                hasScore = true;
            }
        }

        if (hasScore)
        {
            // Return dark green for cells with values
            return ScoreGreenDark;
        }

        // Return dark grey for empty cells
        return EmptyDark;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Gets X count from a Scores list by series index (0-based).
/// Returns the X count as a string like "3x" if > 0, or empty string if 0 or no score.
/// </summary>
public class SeriesXCountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IList scores && parameter is string indexStr)
        {
            if (int.TryParse(indexStr, out int index) && index >= 0 && index < scores.Count)
            {
                var score = scores[index];
                var xCountProp = score?.GetType().GetProperty("XCount");
                if (xCountProp != null)
                {
                    var xCount = xCountProp.GetValue(score);
                    if (xCount is int xVal && xVal > 0)
                    {
                        return $"{xVal}x";
                    }
                }
            }
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if there's an X count > 0 at the given series index.
/// Used for IsVisible binding.
/// </summary>
public class SeriesHasXCountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IList scores && parameter is string indexStr)
        {
            if (int.TryParse(indexStr, out int index) && index >= 0 && index < scores.Count)
            {
                var score = scores[index];
                var xCountProp = score?.GetType().GetProperty("XCount");
                if (xCountProp != null)
                {
                    var xCount = xCountProp.GetValue(score);
                    if (xCount is int xVal && xVal > 0)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Formats handicap value for display (e.g., "+2.50" or "-1.25")
/// Returns empty string if value is null or 0.
/// </summary>
public class HandicapFormatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal hcp && hcp != 0)
        {
            // Round to quarter points
            var rounded = Math.Round(hcp * 4) / 4;
            var sign = rounded >= 0 ? "+" : "";
            // Show decimals only if not a whole number
            var formatted = rounded == Math.Truncate(rounded)
                ? rounded.ToString("0")
                : rounded.ToString("0.00");
            return sign + formatted;
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if the participant has a handicap value (including 0).
/// A HCP of 0 is still a valid handicap that should be displayed.
/// </summary>
public class HasHandicapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return false;

        // Show handicap badge for any value including 0
        if (value is decimal)
        {
            return true;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts string to boolean (true if string is not null/empty)
/// Used for avatar display - shows image when ProfilePictureUrl is available
/// </summary>
public class StringToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrEmpty(value as string);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Inverted StringToBool - returns true if string IS null/empty
/// Used for avatar display - shows initials when ProfilePictureUrl is not available
/// </summary>
public class InvertedStringToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Calculates subtotal score from a Scores list up to a specific series count.
/// Parameter is the number of series to include (1-based, e.g., "6" means series 1-6).
/// </summary>
public class SubtotalScoreConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IList scores && parameter is string countStr)
        {
            if (int.TryParse(countStr, out int seriesCount) && seriesCount > 0)
            {
                int total = 0;
                int actualCount = Math.Min(seriesCount, scores.Count);

                for (int i = 0; i < actualCount; i++)
                {
                    var score = scores[i];
                    var totalProp = score?.GetType().GetProperty("Total");
                    if (totalProp != null)
                    {
                        var propValue = totalProp.GetValue(score);
                        if (propValue is int scoreInt)
                        {
                            // Cap individual series at 50
                            total += Math.Min(scoreInt, 50);
                        }
                    }
                }

                return total > 0 ? total.ToString() : "-";
            }
        }
        return "-";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Calculates subtotal X count from a Scores list up to a specific series count.
/// Parameter is the number of series to include (1-based).
/// Returns "Nx" format if > 0, empty string otherwise.
/// </summary>
public class SubtotalXCountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IList scores && parameter is string countStr)
        {
            if (int.TryParse(countStr, out int seriesCount) && seriesCount > 0)
            {
                int totalX = 0;
                int actualCount = Math.Min(seriesCount, scores.Count);

                for (int i = 0; i < actualCount; i++)
                {
                    var score = scores[i];
                    var xCountProp = score?.GetType().GetProperty("XCount");
                    if (xCountProp != null)
                    {
                        var propValue = xCountProp.GetValue(score);
                        if (propValue is int xCount)
                        {
                            totalX += xCount;
                        }
                    }
                }

                return totalX > 0 ? $"{totalX}x" : "";
            }
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if the subtotal X count is greater than 0.
/// Used for IsVisible binding on subtotal X count labels.
/// </summary>
public class SubtotalHasXCountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.IList scores && parameter is string countStr)
        {
            if (int.TryParse(countStr, out int seriesCount) && seriesCount > 0)
            {
                int totalX = 0;
                int actualCount = Math.Min(seriesCount, scores.Count);

                for (int i = 0; i < actualCount; i++)
                {
                    var score = scores[i];
                    var xCountProp = score?.GetType().GetProperty("XCount");
                    if (xCountProp != null)
                    {
                        var propValue = xCountProp.GetValue(score);
                        if (propValue is int xCount)
                        {
                            totalX += xCount;
                        }
                    }
                }

                return totalX > 0;
            }
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns highlighted background color if the current user's reaction matches the parameter emoji.
/// Used for reaction button highlighting in photo viewer.
/// </summary>
public class ReactionSelectedConverter : IValueConverter
{
    private static readonly Color SelectedColor = Color.FromArgb("#3b5998"); // Highlighted blue
    private static readonly Color UnselectedColor = Color.FromArgb("#4b5563"); // Dark gray

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string currentReaction && parameter is string targetEmoji)
        {
            return currentReaction == targetEmoji ? SelectedColor : UnselectedColor;
        }
        return UnselectedColor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Inverts a boolean value for IsVisible binding.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
