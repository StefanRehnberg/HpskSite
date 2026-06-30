namespace HpskSite.Helpers;

public static class ClubNameHelper
{
    public static string Shorten(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Special cases first — well-known clubs with an established acronym that the
        // generic suffix rules below can't produce.
        if (name.Equals("Göteborgs Pistol-och Sportskk Pistolsektionen", StringComparison.OrdinalIgnoreCase))
            return "GPSSK";
        // Order matters — longer matches first to avoid partial replacements
        return name
            .Replace("Pistolskytteförening", "PSF", StringComparison.OrdinalIgnoreCase)
            .Replace("Pistolskytteklubb", "PSK", StringComparison.OrdinalIgnoreCase)
            .Replace("Pistolskyttar", "PS", StringComparison.OrdinalIgnoreCase)
            .Replace("Pistolklubb", "PK", StringComparison.OrdinalIgnoreCase)
            .Replace("Handeldvapenförening", "HF", StringComparison.OrdinalIgnoreCase)
            .Replace("Sportskytteklubb", "SSK", StringComparison.OrdinalIgnoreCase)
            .Replace("Skytteklubb", "SK", StringComparison.OrdinalIgnoreCase)
            .Replace("Skytteförening", "SF", StringComparison.OrdinalIgnoreCase);
    }
}
