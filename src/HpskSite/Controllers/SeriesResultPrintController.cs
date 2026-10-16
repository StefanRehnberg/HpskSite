using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Seriens resultatlista för utskrift — /serieresultat/{seriesId}.
    ///
    /// Samma beräkning som seriesidan (<see cref="SeriesCalculationService"/>), men ritad på servern med
    /// resultatlistans utseende (<c>CompetitionResult.cshtml</c>), så att en utskrift av en serie ser ut som
    /// våra övriga listor. Öppen som seriesidan: serieresultatet är publikt där. Routad controller, ingen
    /// Umbraco-nod — samma mönster som /prisutdelning.
    /// </summary>
    [Route("serieresultat")]
    public class SeriesResultPrintController : Controller
    {
        private readonly SeriesCalculationService _seriesCalculation;
        private readonly HpskSite.Services.Competitions.ICompetitionSeriesStore _series;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<SeriesResultPrintController> _logger;

        public SeriesResultPrintController(
            SeriesCalculationService seriesCalculation,
            HpskSite.Services.Competitions.ICompetitionSeriesStore series,
            IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<SeriesResultPrintController> logger)
        {
            _seriesCalculation = seriesCalculation;
            _series = series;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
        }

        [HttpGet("{seriesId:int}")]
        public async Task<IActionResult> Index(int seriesId)
        {
            // En publicerad serie — samma som seriesidan visar. ⚠️ Inte IsVisible: seriesidan visar även en
            // serie märkt "Inaktiv" med sina resultat (Säters fältserie 2026 i prod), och utskriften ska
            // inte vara strängare än sidan den skriver ut.
            var series = _series.Get(seriesId);
            if (series == null) return NotFound();

            SeriesResultData? result = null;
            string? error = null;
            try
            {
                result = await _seriesCalculation.CalculateSeriesResults(seriesId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Serieresultatet för utskrift kunde inte räknas fram för serie {SeriesId}", seriesId);
                error = "Serieresultatet kunde inte räknas fram just nu. Försök igen om en stund.";
            }

            var seriesUrl = _umbracoContextAccessor.TryGetUmbracoContext(out var ctx)
                ? ctx.Content?.GetById(seriesId)?.Url() ?? ""
                : "";

            return View("~/Views/SeriesResultPrint.cshtml", new SeriesResultPrintModel
            {
                SeriesId = seriesId,
                SeriesName = series.DisplayName,
                SeriesUrl = seriesUrl,
                Result = result,
                Error = error,
            });
        }
    }

    public sealed class SeriesResultPrintModel
    {
        public int SeriesId { get; init; }
        public string SeriesName { get; init; } = "";
        public string SeriesUrl { get; init; } = "";
        public SeriesResultData? Result { get; init; }
        public string? Error { get; init; }

        /// <summary>Omgångarna (aldrig null), för vyn.</summary>
        public IReadOnlyList<SeriesCompetitionInfo> Competitions => Result?.Competitions ?? (IReadOnlyList<SeriesCompetitionInfo>)Array.Empty<SeriesCompetitionInfo>();

        /// <summary>Sektionerna med klubbtävlingen först, som på seriesidan (aldrig null).</summary>
        public IReadOnlyList<SeriesResultSection> Sections => (Result?.Sections ?? new List<SeriesResultSection>())
            .OrderBy(s => s.SectionType == "Club" ? 0 : 1)
            .ToList();
    }
}
