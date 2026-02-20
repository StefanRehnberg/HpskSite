namespace HpskSite.Helpers;

public static class ClubNameHelper
{
    public static string Shorten(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Order matters — longer matches first to avoid partial replacements
        return name
            .Replace("Pistolskytteförening", "PSF", StringComparison.OrdinalIgnoreCase)
            .Replace("Pistolskytteklubb", "PSK", StringComparison.OrdinalIgnoreCase)
            .Replace("Pistolklubb", "PK", StringComparison.OrdinalIgnoreCase)
            .Replace("Sportskytteklubb", "SSK", StringComparison.OrdinalIgnoreCase)
            .Replace("Skytteklubb", "SK", StringComparison.OrdinalIgnoreCase)
            .Replace("Skytteförening", "SF", StringComparison.OrdinalIgnoreCase);
    }
}
