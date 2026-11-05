using System.Text.Json;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using Microsoft.Extensions.Logging;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// F0c (Stefan 2026-11-04: "resultat får bara räknas om när en admin ber om det"): EN läsare av den
    /// officiella fältlistans frusna bild, för varje yta som annars räknade om placeringar ur skottraderna.
    ///
    /// ⚠️ Lagkort, Min sida, serieställningar och särskjutningskortet frågar HIT i stället för att läsa
    /// <c>FaltskytteResultEntry</c> själva. Svaret är null när listan inte är officiell, saknar bild, är
    /// nödbromsad (<c>Results:FreezePublished = off</c>) eller bilden inte går att läsa — och då räknar ytan
    /// live som förut. Regeln är <see cref="FaltskytteResultArtifactService.ServeFrozen"/>, densamma som den
    /// publika listan, så ytorna kan inte säga något annat än listan.
    /// </summary>
    public class OfficialFaltskytteList
    {
        private readonly HpskSite.Services.Results.ICompetitionResultStore _results;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly IServiceProvider _services;
        private readonly ILogger<OfficialFaltskytteList> _logger;

        public OfficialFaltskytteList(
            HpskSite.Services.Results.ICompetitionResultStore results,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            IServiceProvider services,
            ILogger<OfficialFaltskytteList> logger)
        {
            _results = results;
            _competitions = competitions;
            _services = services;
            _logger = logger;
        }

        /// <summary>Den frusna listan, eller null (räkna live).</summary>
        public FaltskylteFinalResults? TryGet(int competitionId)
        {
            try
            {
                if (!HpskSite.Services.Results.PublishedResultFreeze.Enabled(_services)) return null;
                var record = _results.Get(competitionId);
                if (record == null || string.IsNullOrWhiteSpace(record.PublishedSnapshot)) return null;
                var competition = _competitions.GetSaved(competitionId);
                if (competition == null) return null;
                if (!FaltskytteResultArtifactService.ServeFrozen(false, null, competition.FaltskytteResultsOfficial, record.PublishedSnapshot))
                    return null;
                return Parse(record.PublishedSnapshot, _services);
            }
            catch (Exception ex)
            {
                // "Kunde inte läsa bilden" är inte ett svar om resultatet — ytan räknar live som förut.
                _logger.LogWarning(ex, "Kunde inte läsa den frusna fältlistan för tävling {CompetitionId}", competitionId);
                return null;
            }
        }

        /// <summary>Läser <c>{success, results}</c> med MVC:s egna JSON-inställningar (samma som skrev bilden).</summary>
        public static FaltskylteFinalResults? Parse(string snapshot, IServiceProvider services)
        {
            var options = (services.GetService(typeof(Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>))
                as Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>)?.Value.JsonSerializerOptions;
            using var doc = JsonDocument.Parse(snapshot);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
                return null;
            return results.Deserialize<FaltskylteFinalResults>(options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        /// <summary>
        /// Skyttens rader ur bilden i den form lagräkningen läser (en per skytt, klass och station). Särskjutningsstationer
        /// ingår aldrig — bilden byggdes utan dem.
        /// </summary>
        public static List<FaltskytteResultEntry> RowsOf(FaltskylteFinalResults list) =>
            list.ClassGroups
                .SelectMany(cg => cg.Shooters)
                .SelectMany(s => s.Stations.Select(st => new FaltskytteResultEntry
                {
                    CompetitionId = list.CompetitionId,
                    MemberId = s.MemberId,
                    ShootingClass = s.ShootingClass,
                    StationNumber = st.StationNumber,
                    Hits = st.Hits,
                    Figures = st.Figures,
                    TiebreakerScore = st.TiebreakerScore
                }))
                .ToList();
    }
}
