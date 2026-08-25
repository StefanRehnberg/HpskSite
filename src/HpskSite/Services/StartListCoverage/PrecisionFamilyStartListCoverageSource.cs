using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.CompetitionTypes.Precision.Models;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.StartListCoverage
{
    /// <summary>
    /// Precision, Duell, Milsnabb, MagnumPrecision, NationellHelmatch — every one of them keeps its
    /// skjutlag in a `precisionStartList` node's `configurationData`.
    /// </summary>
    public sealed class PrecisionFamilyStartListCoverageSource : IStartListCoverageSource
    {
        private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            "Precision", "Duell", "Milsnabb", "MagnumPrecision", "NationellHelmatch"
        };

        private readonly IContentService _contentService;
        private readonly UmbracoStartListRepository _repository;
        private readonly ILogger<PrecisionFamilyStartListCoverageSource> _logger;

        public PrecisionFamilyStartListCoverageSource(
            IContentService contentService,
            UmbracoStartListRepository repository,
            ILogger<PrecisionFamilyStartListCoverageSource> logger)
        {
            _contentService = contentService;
            _repository = repository;
            _logger = logger;
        }

        // An empty/unknown competitionType falls to Precision, the same fallback legacy nodes rely
        // on in PrecisionFamilySeriesScoreSource.
        public bool Supports(string? competitionType) =>
            string.IsNullOrWhiteSpace(competitionType) || Types.Contains(competitionType.Trim());

        public async Task<StartListCoverageResult> BuildAsync(IContent competition)
        {
            var startListNodes = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .Where(c => c.ContentType.Alias == "precisionStartList")
                .ToList();

            // ⚠️ Qualifying lists ONLY. A finalsStartList is a deliberate SUBSET (the cut), so a
            // shooter absent from it is not unplaced — counting it would also mask the real fault
            // of someone who reached the final without a qualifying slot.
            var placedRows = new List<CoverageBuilder.PlacedRow>();
            foreach (var node in startListNodes)
            {
                var json = node.GetValue<string>("configurationData");
                if (string.IsNullOrWhiteSpace(json)) continue;

                StartListConfiguration? cfg = null;
                try { cfg = JsonConvert.DeserializeObject<StartListConfiguration>(json); }
                catch (Exception ex)
                {
                    // A list we cannot read must not silently report its shooters as unplaced —
                    // that would send the organiser hunting for people who already have a time.
                    _logger.LogWarning(ex, "Unreadable start list {NodeId} on competition {CompetitionId}; coverage may over-report",
                        node.Id, competition.Id);
                    continue;
                }

                // Direktplacering writes its own anonymous config, but with the same
                // Teams[].Shooters[] shape and the same property names, so it deserializes here.
                foreach (var team in cfg?.Teams ?? new List<StartListTeam>())
                    foreach (var shooter in team.Shooters ?? new List<StartListShooter>())
                        if (shooter.MemberId > 0)
                            placedRows.Add(new CoverageBuilder.PlacedRow(
                                shooter.MemberId, shooter.Name ?? "", shooter.Club ?? "",
                                shooter.WeaponClass ?? "", shooter.WeaponClass ?? ""));
            }

            var registrations = await _repository.GetCompetitionRegistrations(competition.Id);

            // One row per (member, class) already — but a class can legitimately appear twice on a
            // registration edited over time, so collapse on the key we compare with.
            var required = registrations
                .Where(r => r.MemberId > 0 && !string.IsNullOrWhiteSpace(r.MemberClass))
                .GroupBy(r => CoverageKeys.For(r.MemberId, r.MemberClass))
                .Select(g => g.First())
                .ToList();

            return CoverageBuilder.Build(required
                .Select(r => new CoverageBuilder.Row(
                    r.MemberId, r.MemberName ?? "", r.MemberClub ?? "", r.MemberClass ?? "",
                    // Keyed per CLASS here: each registered class gets its own position in a skjutlag.
                    KeyClass: r.MemberClass ?? ""))
                .ToList(),
                placedRows, startListNodes.Count > 0, "skjutlag");
        }
    }
}
