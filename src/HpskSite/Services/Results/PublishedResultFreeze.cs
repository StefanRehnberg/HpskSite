using HpskSite.Services.SiteSettings;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// Nödbromsen för frysningen av publicerade fält- och springskyttelistor (utanför mästerskapet, F0/F0b). Frysningen är på som standard.
    /// Slås den av (<c>SiteSetting</c> <c>Results:FreezePublished</c> = <c>off</c>) räknas listorna live som före F0 — utan ny deploy, om en
    /// tävlingsdag går snett. Cachen i <see cref="SiteSettingService"/> är 60 s. Saknad rad, ett annat värde eller ett läsfel = på.
    /// Av:  <c>INSERT/UPDATE dbo.SiteSetting SET SettingValue = 'off' WHERE SettingKey = 'Results:FreezePublished'</c>.
    /// </summary>
    public static class PublishedResultFreeze
    {
        public const string SettingKey = "Results:FreezePublished";

        public static bool Enabled(IServiceProvider services)
        {
            try
            {
                var settings = services.GetService(typeof(SiteSettingService)) as SiteSettingService;
                return !string.Equals(settings?.Get(SettingKey, "on"), "off", StringComparison.OrdinalIgnoreCase);
            }
            catch (SiteSettingUnavailableException)
            {
                return true;   // "kunde inte fråga" är inte ett svar — frysningen står kvar
            }
        }
    }
}
