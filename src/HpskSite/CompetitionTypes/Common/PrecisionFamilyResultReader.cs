using HpskSite.CompetitionTypes.Duell.Models;
using HpskSite.CompetitionTypes.MagnumPrecision.Models;
using HpskSite.CompetitionTypes.Milsnabb.Models;
using HpskSite.CompetitionTypes.NationellHelmatch.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Sportpistol.Models;
using HpskSite.CompetitionTypes.Standardpistol.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Läser precisionsfamiljens resultatrader ur RÄTT tabell, som rätt KÖRTIDSTYP. ENDA stället.
    ///
    /// ⚠️⚠️ Finalkedjan (grundomgångens rangordning, låsningens kontrollsumma, "placera om efter
    /// resultat") läste hårdkodat <c>PrecisionResultEntry</c>. För Magnumprecision, Duell,
    /// Standardpistol, Sportpistol, Milsnabb och Nationell helmatch gav det NOLL rader — finalkortet
    /// sa "inga resultat" på en tävling full av resultat, och en finalstartlista gick inte att skapa
    /// alls. Resultatlistan läste rätt hela tiden, så felet syntes bara i finalen. Rättat 2026-10-03.
    ///
    /// ⚠️ Körtidstypen är inte kosmetik: NPoco väljer tabell utifrån den, så en typad gren per gren
    /// och inte en <c>SELECT * FROM [tabellnamn]</c>. Tabellnamnen bor i
    /// <see cref="CompetitionResultTables"/>; <c>PrecisionFamilyResultReaderTests</c> faller om en gren
    /// där läggs till utan en gren här.
    ///
    /// <paramref name="whereAndOrder"/> är en NPoco-fras utan SELECT ("WHERE CompetitionId = @0 …").
    /// </summary>
    public static class PrecisionFamilyResultReader
    {
        /// <summary>Grenarna den här läsaren kan läsa. Fält och spring har egen radform.</summary>
        public static readonly IReadOnlyCollection<string> SupportedTypes = new[]
        {
            "Precision", "Milsnabb", "Duell", "NationellHelmatch", "MagnumPrecision", "Standardpistol", "Sportpistol"
        };

        /// <summary>Den ENTITETSTYP som läser grenens tabell. Tom/okänd typ = Precision (äldre noder).</summary>
        public static Type EntityTypeFor(string? competitionType) => (competitionType ?? "").Trim() switch
        {
            "Milsnabb" => typeof(MilsnabbResultEntry),
            "Duell" => typeof(DuellResultEntry),
            "NationellHelmatch" => typeof(NationellHelmatchResultEntry),
            "MagnumPrecision" => typeof(MagnumPrecisionResultEntry),
            "Standardpistol" => typeof(StandardpistolResultEntry),
            "Sportpistol" => typeof(SportpistolResultEntry),
            _ => typeof(PrecisionResultEntry)
        };

        public static async Task<List<PrecisionResultEntry>> FetchAsync(
            IUmbracoDatabase db, string? competitionType, string whereAndOrder, params object[] args)
        {
            switch ((competitionType ?? "").Trim())
            {
                case "Milsnabb":
                    return (await db.FetchAsync<MilsnabbResultEntry>(whereAndOrder, args)).Cast<PrecisionResultEntry>().ToList();
                case "Duell":
                    return (await db.FetchAsync<DuellResultEntry>(whereAndOrder, args)).Cast<PrecisionResultEntry>().ToList();
                case "NationellHelmatch":
                    return (await db.FetchAsync<NationellHelmatchResultEntry>(whereAndOrder, args)).Cast<PrecisionResultEntry>().ToList();
                case "MagnumPrecision":
                    return (await db.FetchAsync<MagnumPrecisionResultEntry>(whereAndOrder, args)).Cast<PrecisionResultEntry>().ToList();
                case "Standardpistol":
                    return (await db.FetchAsync<StandardpistolResultEntry>(whereAndOrder, args)).Cast<PrecisionResultEntry>().ToList();
                case "Sportpistol":
                    return (await db.FetchAsync<SportpistolResultEntry>(whereAndOrder, args)).Cast<PrecisionResultEntry>().ToList();
                default:
                    return await db.FetchAsync<PrecisionResultEntry>(whereAndOrder, args);
            }
        }

        /// <summary>
        /// Grundomgångens längd. ⚠️ <c>numberOfSeriesOrStations</c> är TOTALEN, finalserierna
        /// inräknade (10 serier varav 3 final = 7 grundserier). Aldrig under 0.
        /// </summary>
        public static int QualifyingSeriesCount(int numberOfSeries, int numberOfFinalSeries) =>
            Math.Max(0, numberOfFinalSeries > 0 ? numberOfSeries - numberOfFinalSeries : numberOfSeries);
    }
}
