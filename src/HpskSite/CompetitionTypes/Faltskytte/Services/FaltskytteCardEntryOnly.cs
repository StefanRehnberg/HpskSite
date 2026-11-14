using HpskSite.Services.SiteSettings;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Spärren "bara kortinmatning": när sekretariatet matar in papperskorten (stationerna är inte
    /// uppkopplade) ska stationsinmatningen (/station → SaveStationResult) vägra spara. Annars kan en
    /// funktionär som öppnar en station se en tom ruta för ett kort som matats in som "5/3" (fördelning
    /// saknas) och spara nollor över resultatet.
    ///
    /// Lagras som sajtinställningen <c>Falt:KortOnly:{tävling}</c> = "1" (ingen migrering, ingen
    /// kolumn på tävlingen). Slås på/av från sidan Kortinmatning. Ett läsfel ger ALDRIG en spärr: hellre
    /// att stationen fungerar än att ett läsfel stänger inmatningen mitt under en tävling.
    /// </summary>
    public static class FaltskytteCardEntryOnly
    {
        public const string Message =
            "Den här tävlingen matas in via Kortinmatning i sekretariatet. Stationsinmatningen är stängd. " +
            "Be sekretariatet mata in kortet.";

        public static string Key(int competitionId) => $"Falt:KortOnly:{competitionId}";

        public static bool IsOn(SiteSettingService settings, int competitionId)
        {
            try { return settings.Get(Key(competitionId), "0") == "1"; }
            catch { return false; }
        }
    }
}
