using System.Globalization;

namespace HpskSite.Mobile.Converters;

/// <summary>
/// Converts profile picture URLs for platform compatibility.
/// On Android emulator, replaces localhost with 10.0.2.2 and uses HTTP to avoid SSL issues.
/// </summary>
public class ProfilePictureUrlConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrEmpty(url))
            return null;

#if DEBUG
        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            // Dev server uses HTTP:18233 - replace localhost with 10.0.2.2 for Android emulator
            var result = url
                .Replace("http://localhost:18233", "http://10.0.2.2:18233")
                .Replace("http://localhost:", "http://10.0.2.2:")
                .Replace("https://localhost:44317", "http://10.0.2.2:18233");

            System.Diagnostics.Debug.WriteLine($"ProfilePictureUrlConverter: {url} -> {result}");
            return result;
        }
#endif
        return url;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
