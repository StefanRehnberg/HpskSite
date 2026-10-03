namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Vilka grenar har en FINALOMGÅNG? ENDA stället regeln bor.
    ///
    /// SHB 2026 har en finalomgång bara i precisionsskjutning: C.3.6.1.1 — mästerskap skjuts i två
    /// omgångar, 7 + 3 serier, och till andra omgången går bästa sjättedelen per vapengrupp (minst
    /// 10). Ingen annan gren har det:
    ///   • Magnumprecision (G.6.1) — "minst sex serier", SM i M1–M7 utan omgångar.
    ///   • Snabbskjutning = Duell (H.6.1) — 6 serier.
    ///   • Nationell helmatch (I.6.1) — tre delmoment om 4 serier, alla skjuter allt.
    ///   • Militär snabbmatch = Milsnabb (J.6.1) — tre TIDSomgångar (10/8/6 s) som alla skjuter;
    ///     det är inte en utslagning.
    ///   • Standardpistol och Sportpistol finns inte i SHB.
    ///
    /// ⚠️ Före 2026-10-03 lät guiden och redigeringsdialogen ange finalserier i de flesta av dem.
    /// Följden var en tävling där finalen aldrig gick att skapa, och där medaljlogiken räknade med
    /// en finalomgång utan finalister — alltså inga medaljörer. Klubbarnas egna sätt att köra
    /// finalen (samma ordning / omplacering efter resultat / gallring enligt SHB) är tre lägen
    /// INOM precisionens final, inte ett skäl att öppna finalen för andra grenar.
    ///
    /// Speglas i webbläsaren av <c>HPSK_FINALS_TYPES</c>, som renderas ur <see cref="SupportedTypes"/>.
    /// </summary>
    public static class CompetitionFinals
    {
        /// <summary>Grenar med finalomgång. Tom/okänd typ räknas som Precision (äldre noder).</summary>
        public static readonly IReadOnlyCollection<string> SupportedTypes = new[] { "Precision" };

        public const string ShbReference = "SHB C.3.6.1.1";

        public static bool Supports(string? competitionType)
        {
            var t = (competitionType ?? "").Trim();
            return t.Length == 0 || SupportedTypes.Contains(t, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Det antal finalserier som GÄLLER — 0 för en gren utan finalomgång.</summary>
        public static int Effective(string? competitionType, int storedFinalSeries) =>
            Supports(competitionType) ? Math.Max(0, storedFinalSeries) : 0;

        /// <summary>Skälet att vägra, eller null. Används av varje skrivväg.</summary>
        public static string? Refusal(string? competitionType, int finalSeries)
        {
            if (finalSeries <= 0 || Supports(competitionType)) return null;
            var name = HpskSite.Models.CompetitionTypes.All
                .FirstOrDefault(c => string.Equals(c.Id, (competitionType ?? "").Trim(), StringComparison.OrdinalIgnoreCase))?.Name
                ?? competitionType;
            return $"{name} har ingen finalomgång. Finalserier finns bara i precisionsskjutning ({ShbReference}). " +
                   "Ange 0 finalserier — alla serier räknas då som en tävling.";
        }
    }
}
