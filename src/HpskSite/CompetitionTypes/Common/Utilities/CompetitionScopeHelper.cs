using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Helpers for interpreting the <c>competitionScope</c> property.
    /// </summary>
    public static class CompetitionScopeHelper
    {
        public const string SvensktMasterskap = "Svenskt Mästerskap";
        public const string Landsdelsmasterskap = "Landsdelsmästerskap";
        public const string Kretsmasterskap = "Kretsmästerskap";
        public const string Klubbmasterskap = "Klubbmästerskap";

        /// <summary>
        /// De fyra värdena i visningsordning — och den ENDA källan varje
        /// omfattnings-dropdown ska renderas ur.
        ///
        /// ⚠️ Varför den finns: värdena var handskrivna på nytt i varje modal, och de
        /// hann drifta. Springskyttemodalen hade <c>value="Kretsmasterskap"</c> UTAN
        /// diakriter medan konstanterna och precisionsmodalen har med. Jämförelserna
        /// här och i <c>CompetitionUrlProvider</c> är <c>StringComparison.Ordinal</c>,
        /// så en springskyttetävling satt till mästerskap via sin egen modal räknades
        /// inte som mästerskap alls: fel URL-form för SM och Landsdel, och
        /// <see cref="IsChampionshipScope"/> svarade false.
        ///
        /// Rendera alltid dropdownen ur den här listan. Skriv aldrig värdena för hand.
        /// </summary>
        public static readonly string[] All =
        {
            SvensktMasterskap, Landsdelsmasterskap, Kretsmasterskap, Klubbmasterskap
        };

        /// <summary>
        /// True for all four mästerskap values (SM, Landsdel, Krets, Klubb).
        /// Championship rules apply: Särskjutning must resolve tied medal positions 1–3.
        /// </summary>
        public static bool IsChampionshipScope(string? scope) =>
            scope is SvensktMasterskap or Landsdelsmasterskap
                  or Kretsmasterskap or Klubbmasterskap;

        /// <summary>
        /// Läser omfattningen ur den publicerade cachen — <b>den enda säkra vägen</b>.
        ///
        /// <para><b>⚠️⚠️ ÄVEN OTYPAD <c>Value("competitionScope")</c> KASTAR.</b> Egenskapen är en
        /// FlexibleDropdown, och dess värdekonverterare kör <c>ConvertSourceToIntermediate</c> även
        /// på den otypade vägen: den JSON-deserialiserar det lagrade värdet. En tävling vars värde
        /// är en ren sträng (<c>Klubbmästerskap</c>, vilket äldre skrivvägar lagrar) får då
        /// <c>'K' is an invalid start of a value</c> — ett ohanterat undantag som tar ner hela
        /// anropet. Kommentaren i <c>CompetitionUrlProvider.ReadScopeValue</c> påstår att den
        /// otypade läsningen inte kastar; det är FEL, och den räddas bara av sin egen try/catch.</para>
        ///
        /// <para><b><c>GetSourceValue()</c> går förbi konverteraren helt</b> och ger det lagrade
        /// värdet som det står. Därför behövs ingen gissning och — viktigare — ingen tyst
        /// <c>null</c>: en try/catch som sväljer undantaget gör att tävlingen faller ur varje
        /// urval utan att något säger ifrån, vilket är precis den sortens lucka som är omöjlig
        /// att upptäcka i en medalj- eller URL-lista.</para>
        ///
        /// <para>Resultatet är normaliserat: en JSON-inpackad array (<c>["Kretsmästerskap"]</c>)
        /// skalas av, så anroparen kan jämföra rakt mot konstanterna ovan.</para>
        /// </summary>
        public static string ReadScope(IPublishedContent? competition)
        {
            if (competition == null) return "";

            object? raw = null;
            try
            {
                raw = competition.GetProperty("competitionScope")?.GetSourceValue();
            }
            catch
            {
                // Råvärdet är det säkra; faller det ändå är den typade vägen inte säkrare.
            }

            if (raw == null)
            {
                try { raw = competition.Value("competitionScope"); }
                catch { return ""; }
            }

            var text = raw switch
            {
                string s => s,
                string[] arr => arr.FirstOrDefault() ?? "",
                IEnumerable<string> e => e.FirstOrDefault() ?? "",
                _ => raw?.ToString() ?? ""
            };

            return NormalizeScopeValue(text);
        }

        /// <summary>
        /// Skalar av en eventuell JSON-array (<c>["Kretsmästerskap"]</c>) till bara värdet.
        /// Samma avskalning som <c>ChampionshipCategory.NormalizeScope</c> gör, upprepad här
        /// för att <see cref="ReadScope"/> ska kunna svara färdignormaliserat utan att dra in
        /// ett beroende åt fel håll.
        /// </summary>
        public static string NormalizeScopeValue(string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) return "";
            var s = scope.Trim();
            if (s.StartsWith("[") && s.EndsWith("]"))
                s = s.Trim('[', ']').Trim().Trim('"');
            return s.Trim();
        }
    }
}
