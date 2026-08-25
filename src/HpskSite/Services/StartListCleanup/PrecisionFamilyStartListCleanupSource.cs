using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.StartListCleanup
{
    /// <summary>
    /// Precision, Duell, Milsnabb, MagnumPrecision, NationellHelmatch — skjutlag live in a
    /// `precisionStartList` (and `finalsStartList`) node's `configurationData`.
    /// </summary>
    public sealed class PrecisionFamilyStartListCleanupSource : IStartListCleanupSource
    {
        private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            "Precision", "Duell", "Milsnabb", "MagnumPrecision", "NationellHelmatch"
        };

        private readonly IContentService _contentService;
        private readonly StartListHtmlRenderer _renderer;
        private readonly DirektplaceringStartListService _dpService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<PrecisionFamilyStartListCleanupSource> _logger;

        public PrecisionFamilyStartListCleanupSource(
            IContentService contentService,
            StartListHtmlRenderer renderer,
            DirektplaceringStartListService dpService,
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<PrecisionFamilyStartListCleanupSource> logger)
        {
            _contentService = contentService;
            _renderer = renderer;
            _dpService = dpService;
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        public bool Supports(string? competitionType) =>
            string.IsNullOrWhiteSpace(competitionType) || Types.Contains(competitionType.Trim());

        private List<IContent> StartListNodes(IContent competition) =>
            _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .Where(c => c.ContentType.Alias == "precisionStartList" || c.ContentType.Alias == "finalsStartList")
                .ToList();

        private static bool IsPublished(IContent node) =>
            node.ContentType.Alias == "finalsStartList"
                ? node.HasProperty("isOfficialFinalsStartList") && node.GetValue<bool>("isOfficialFinalsStartList")
                : node.HasProperty("isOfficialStartList") && node.GetValue<bool>("isOfficialStartList");

        public Task<List<StartListPlacement>> DescribePlacementsAsync(IContent competition, int memberId)
        {
            var found = new List<StartListPlacement>();

            foreach (var node in StartListNodes(competition))
            {
                var cfg = ReadConfig(node, competition.Id);
                if (cfg?.Teams == null) continue;

                foreach (var team in cfg.Teams)
                {
                    foreach (var shooter in team.Shooters ?? new List<StartListShooter>())
                    {
                        if (shooter.MemberId != memberId) continue;
                        var label = string.IsNullOrWhiteSpace(team.Label) ? "" : $" ({team.Label})";
                        found.Add(new StartListPlacement
                        {
                            ListName = node.Name ?? "Startlista",
                            Where = $"Skjutlag {team.TeamNumber}{label}, plats {shooter.Position}",
                            StartTime = team.StartTime ?? "",
                            ShootingClass = shooter.WeaponClass ?? "",
                            IsPublished = IsPublished(node)
                        });
                    }
                }
            }

            return Task.FromResult(found);
        }

        public async Task<CleanupOutcome> CleanupAsync(IContent competition, int memberId, string? onlyShootingClass = null)
        {
            // Keyed per class here, matching how placement works in this family: each registered
            // class holds its own position in a skjutlag.
            var onlyKey = string.IsNullOrWhiteSpace(onlyShootingClass)
                ? null
                : StartListCoverage.CoverageKeys.Canonical(onlyShootingClass);
            bool Matches(StartListShooter s) =>
                s.MemberId == memberId
                && (onlyKey == null || StartListCoverage.CoverageKeys.Canonical(s.WeaponClass) == onlyKey);

            var warnings = new List<string>();
            var slotsFreed = 0;
            var publishedUpdated = 0;
            var regenerated = false;

            // ⚠️ Direktplacering must be REGENERATED, not patched. DirektplaceringStartListService
            // writes its own anonymous config shape and its own bespoke HTML; deserializing that into
            // a StartListConfiguration and re-rendering would silently replace the whole list's
            // markup. A DP list is fully derived from the registrations, so regenerating is both
            // correct and cheaper. Same rule as RegistrationClubPropagationService.
            var dpConfig = DirektplaceringConfig.Parse(
                competition.HasProperty("direktplaceringConfig") ? competition.GetValue<string>("direktplaceringConfig") : null);

            if (dpConfig != null)
            {
                // A DP list is DERIVED from the registrations, so regenerating removes anything
                // without one — which covers a scoped orphan removal too, and does it for every
                // class at once. Count first, and only what was asked about: after the regeneration
                // there is nothing left to count.
                slotsFreed = (await DescribePlacementsAsync(competition, memberId))
                    .Count(pl => onlyKey == null || StartListCoverage.CoverageKeys.Canonical(pl.ShootingClass) == onlyKey);
                try
                {
                    _dpService.Regenerate(competition.Id, competition, dpConfig);
                    regenerated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Direktplacering regeneration failed after deleting member {MemberId} on competition {CompetitionId}", memberId, competition.Id);
                    warnings.Add("Startlistan kunde inte byggas om automatiskt — kontrollera den manuellt.");
                }
            }
            else
            {
                foreach (var node in StartListNodes(competition))
                {
                    var cfg = ReadConfig(node, competition.Id);
                    if (cfg?.Teams == null) continue;

                    var removedHere = 0;
                    foreach (var team in cfg.Teams)
                    {
                        if (team.Shooters == null) continue;
                        var before = team.Shooters.Count;
                        team.Shooters = team.Shooters.Where(s => !Matches(s)).ToList();
                        removedHere += before - team.Shooters.Count;
                    }
                    if (removedHere == 0) continue;

                    // The vacated position is deliberately left as a GAP rather than renumbered.
                    // Position is a firing point: renumbering would move every shooter after them to
                    // a different lane, mid-competition, for someone else's withdrawal. Springskytte
                    // made the same call for start numbers.
                    slotsFreed += removedHere;

                    var wasPublished = IsPublished(node);
                    node.SetValue("configurationData", JsonConvert.SerializeObject(cfg));
                    try
                    {
                        node.SetValue("startListContent",
                            await _renderer.GenerateStartListHtml(cfg, competition.Name ?? ""));
                    }
                    catch (Exception ex)
                    {
                        // The cached blob is what the public page and the print show. A stale blob is
                        // worse than none of this, so say so instead of failing silently.
                        _logger.LogWarning(ex, "Could not re-render cached start list HTML for node {NodeId}", node.Id);
                        warnings.Add($"Den cachade startlistan för \"{node.Name}\" kunde inte ritas om — generera om listan.");
                    }
                    _contentService.Save(node);

                    // Only re-publish what was ALREADY published: publishing a draft list as a side
                    // effect of a deletion would make an unfinished list public.
                    if (wasPublished)
                    {
                        try
                        {
                            _contentService.Publish(node, new[] { "*" });
                            publishedUpdated++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Cleanup: publish failed for {NodeId} (the saved version is authoritative)", node.Id);
                            warnings.Add($"Den publicerade listan \"{node.Name}\" kunde inte uppdateras — publicera om den.");
                        }
                    }
                }
            }

            var deleted = await DeleteResultRowsAsync(competition, memberId, onlyShootingClass, warnings);

            return new CleanupOutcome
            {
                SlotsFreed = slotsFreed,
                ResultRowsDeleted = deleted,
                PublishedListsUpdated = publishedUpdated,
                Regenerated = regenerated,
                Warnings = warnings
            };
        }

        private StartListConfiguration? ReadConfig(IContent node, int competitionId)
        {
            var json = node.GetValue<string>("configurationData");
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<StartListConfiguration>(json); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unreadable start list {NodeId} on competition {CompetitionId}; left untouched", node.Id, competitionId);
                return null;
            }
        }

        private async Task<int> DeleteResultRowsAsync(IContent competition, int memberId, string? onlyShootingClass, List<string> warnings)
        {
            // ⚠️ No silent fallback here. `For()` answers PrecisionResultEntry for anything unknown,
            // which is right for a READ and dangerous for a DELETE — a typo in the type would delete
            // from the wrong discipline's table.
            var type = competition.GetValue<string>("competitionType");
            if (!CompetitionResultTables.TryFor(type, out var table))
            {
                // Empty type is the documented legacy Precision shape, so that one is safe.
                if (!string.IsNullOrWhiteSpace(type))
                {
                    warnings.Add($"Resultatrader rensades inte — okänd tävlingstyp \"{type}\".");
                    return 0;
                }
                table = "PrecisionResultEntry";
            }

            var deleted = 0;
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                // ⚠️ ShootingClass must be part of the WHERE when a class is named: a shooter can
                // hold results in several classes in the same competition, and the three tables
                // below are all keyed on (competition, member, CLASS). Dropping the class from a
                // scoped delete would wipe a class the shooter is legitimately entered in.
                if (string.IsNullOrWhiteSpace(onlyShootingClass))
                {
                    deleted += await db.ExecuteAsync(
                        $"DELETE FROM [{table}] WHERE CompetitionId=@0 AND MemberId=@1", competition.Id, memberId);
                    // Shoot-off entries and DNS/DNF status live in their own tables, keyed on the same
                    // identity. Leaving them makes a deleted shooter reappear in a tied medal group.
                    deleted += await db.ExecuteAsync(
                        "DELETE FROM CompetitionShootOffEntry WHERE CompetitionId=@0 AND MemberId=@1", competition.Id, memberId);
                    deleted += await db.ExecuteAsync(
                        "DELETE FROM CompetitionParticipantStatus WHERE CompetitionId=@0 AND MemberId=@1", competition.Id, memberId);
                }
                else
                {
                    // The class is stored as an ID here ("C1") but written as a display NAME by
                    // ChangeShooterClass ("C 1"), so compare with the whitespace removed rather than
                    // literally — otherwise a scoped delete silently removes nothing.
                    const string classMatch = "REPLACE(ShootingClass, ' ', '') = REPLACE(@2, ' ', '')";
                    deleted += await db.ExecuteAsync(
                        $"DELETE FROM [{table}] WHERE CompetitionId=@0 AND MemberId=@1 AND {classMatch}",
                        competition.Id, memberId, onlyShootingClass);
                    deleted += await db.ExecuteAsync(
                        $"DELETE FROM CompetitionShootOffEntry WHERE CompetitionId=@0 AND MemberId=@1 AND {classMatch}",
                        competition.Id, memberId, onlyShootingClass);
                    deleted += await db.ExecuteAsync(
                        $"DELETE FROM CompetitionParticipantStatus WHERE CompetitionId=@0 AND MemberId=@1 AND {classMatch}",
                        competition.Id, memberId, onlyShootingClass);
                }
            }
            catch (Exception ex)
            {
                // An un-migrated environment lacks the two side tables; the start-list cleanup above
                // is the valuable half and must not be undone by this.
                _logger.LogWarning(ex, "Cleanup: result row delete failed for competition {CompetitionId} member {MemberId}", competition.Id, memberId);
                warnings.Add("Resultatrader kunde inte rensas helt — kontrollera resultatlistan.");
            }
            return deleted;
        }
    }
}
