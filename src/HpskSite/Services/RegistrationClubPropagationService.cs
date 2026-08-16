using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Springskytte.Controllers;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>What a club correction actually touched, so the caller can tell the operator.</summary>
    public class ClubPropagationResult
    {
        /// <summary>Names of the start lists whose rows were rewritten.</summary>
        public List<string> UpdatedStartLists { get; } = new();

        /// <summary>How many Fältskytte patrol rows were rewritten.</summary>
        public int UpdatedPatrolRows { get; set; }

        /// <summary>True when the direktplacering start list was regenerated instead of patched.</summary>
        public bool RegeneratedDirektplacering { get; set; }

        public bool AnythingChanged =>
            UpdatedStartLists.Count > 0 || UpdatedPatrolRows > 0 || RegeneratedDirektplacering;
    }

    /// <summary>
    /// Pushes a corrected registration club out to everything that already SNAPSHOTTED the old
    /// club name.
    ///
    /// <para><b>Why this exists.</b> A registration's <c>clubId</c> is the source of truth only
    /// until a start list is generated. From that moment the club is a plain string copied into
    /// the start list (<c>StartListShooter.Club</c> / <c>SpringskytteStartListEntry.Club</c>) or
    /// into a Fältskytte patrol row (<c>FaltskyttePatrolMember.ClubName</c>), and the result list
    /// reads the START LIST first (<c>CompetitionResultsController.GetShooterNameAndClub</c>) —
    /// so correcting the registration alone would change the Anmälningar table and leave the
    /// public start list and result list showing the wrong club, with nothing on screen saying so.</para>
    ///
    /// <para><b>Rewrite in place, never regenerate.</b> Regenerating a start list reshuffles
    /// skjutlag, positions and start times; a shooter's club is the one field that must be
    /// fixable at any point in a competition, including between series. This walks the stored
    /// JSON, replaces the club on the matching shooter, and re-renders only the cached HTML blob.</para>
    ///
    /// <para><b>Stafett lists are deliberately out of scope.</b> A stafett row's <c>Club</c> is the
    /// TEAM's club (from <c>CompetitionTeam.ClubId</c>), not the individual's, so an individual's
    /// club correction must not touch it.</para>
    /// </summary>
    public class RegistrationClubPropagationService
    {
        private static readonly string[] StartListAliases = { "precisionStartList", "finalsStartList" };

        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly StartListHtmlRenderer _renderer;
        private readonly DirektplaceringStartListService _dpStartListService;
        private readonly ILogger<RegistrationClubPropagationService> _logger;

        public RegistrationClubPropagationService(
            IContentService contentService,
            IUmbracoDatabaseFactory databaseFactory,
            StartListHtmlRenderer renderer,
            DirektplaceringStartListService dpStartListService,
            ILogger<RegistrationClubPropagationService> logger)
        {
            _contentService = contentService;
            _databaseFactory = databaseFactory;
            _renderer = renderer;
            _dpStartListService = dpStartListService;
            _logger = logger;
        }

        /// <summary>
        /// Rewrite <paramref name="memberId"/>'s club to <paramref name="newClubName"/> everywhere it
        /// was snapshotted for this competition. Best-effort per target: one unreadable start list
        /// must not stop the others, and none of it may take the caller's save down — the
        /// registration itself is already committed by the time we get here.
        /// </summary>
        public async Task<ClubPropagationResult> PropagateAsync(int competitionId, int memberId, string newClubName)
        {
            var result = new ClubPropagationResult();
            if (competitionId <= 0 || memberId <= 0 || string.IsNullOrWhiteSpace(newClubName))
                return result;

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return result;

            // Direktplacering writes its precisionStartList node with its OWN config shape and its
            // OWN bespoke HTML. Patching it here would deserialize that shape into a
            // StartListConfiguration and re-render it with the precision renderer — silently
            // replacing the whole list's markup. Its list is fully derived from the registrations,
            // so regenerating is both correct and the routine operation on that path.
            var dpConfig = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
            if (dpConfig != null)
            {
                try
                {
                    _dpStartListService.Regenerate(competitionId, competition, dpConfig);
                    result.RegeneratedDirektplacering = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Could not regenerate direktplacering start list after a club change (competition {CompetitionId})",
                        competitionId);
                }
            }
            else
            {
                foreach (var node in _contentService.GetPagedChildren(competition.Id, 0, 200, out _)
                                                    .Where(c => StartListAliases.Contains(c.ContentType.Alias)))
                {
                    try
                    {
                        if (await PatchStartListAsync(node, competition.Name ?? "", memberId, newClubName))
                            result.UpdatedStartLists.Add(node.Name ?? $"Startlista {node.Id}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Club propagation failed for start list {NodeId} (competition {CompetitionId}, member {MemberId})",
                            node.Id, competitionId, memberId);
                    }
                }
            }

            try
            {
                result.UpdatedPatrolRows = PatchFaltskyttePatrols(competitionId, memberId, newClubName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Club propagation failed for Fältskytte patrols (competition {CompetitionId}, member {MemberId})",
                    competitionId, memberId);
            }

            return result;
        }

        /// <summary>
        /// Rewrite the club inside one start-list node. Returns true when something actually changed —
        /// a no-op must not re-save and re-publish the node, because publishing a start list is a
        /// visible event (it fires participant notifications elsewhere in the codebase).
        /// </summary>
        private async Task<bool> PatchStartListAsync(IContent node, string competitionName, int memberId, string newClubName)
        {
            var configData = node.GetValue<string>("configurationData");
            if (string.IsNullOrWhiteSpace(configData)) return false;

            // Parsed loosely on purpose: precisionStartList is a SHARED doctype carrying at least
            // three different config shapes (precision Teams, Springskytte Starters, stafett Teams).
            // Probing the JSON is what lets one routine serve all of them without a discriminator
            // that a future shape could forget to set.
            JObject root;
            try { root = JObject.Parse(configData); }
            catch (JsonException) { return false; }

            // Stafett: Teams carry a TEAM club, not the shooter's. Leave it alone entirely.
            var teamFormat = root.Value<string>("TeamFormat") ?? root.Value<string>("teamFormat");
            if (string.Equals(teamFormat, "SpringskytteStafett", StringComparison.OrdinalIgnoreCase))
                return false;

            var changed = false;

            // Springskytte individual: flat Starters[].
            var starters = root["Starters"] as JArray ?? root["starters"] as JArray;
            if (starters != null)
                changed |= SetClubOnMatchingRows(starters, memberId, newClubName);

            // Precision family / direktplacering / finals: Teams[].Shooters[].
            var teams = root["Teams"] as JArray ?? root["teams"] as JArray;
            if (teams != null)
            {
                foreach (var team in teams.OfType<JObject>())
                {
                    var shooters = team["Shooters"] as JArray ?? team["shooters"] as JArray;
                    if (shooters != null)
                        changed |= SetClubOnMatchingRows(shooters, memberId, newClubName);
                }
            }

            if (!changed) return false;

            node.SetValue("configurationData", root.ToString(Formatting.None));

            // Refresh the cached blob too. The public /startlista page reads configurationData
            // directly, but admin preview, print and e-mail read this — leaving it stale would
            // just move the wrong club somewhere less visible.
            try
            {
                if (starters != null)
                {
                    var patched = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(root.ToString(Formatting.None));
                    if (patched?.Starters != null)
                        node.SetValue("startListContent",
                            SpringskytteController.BuildStartListHtml(patched.Starters, patched));
                }
                else if (teams != null)
                {
                    var patched = JsonConvert.DeserializeObject<StartListConfiguration>(root.ToString(Formatting.None));
                    if (patched != null)
                        node.SetValue("startListContent", await _renderer.GenerateStartListHtml(patched, competitionName));
                }
            }
            catch (Exception ex)
            {
                // configurationData is the authority; a failed re-render is a stale cache, not a
                // lost correction, so the save below still goes ahead.
                _logger.LogWarning(ex, "Could not re-render cached HTML for start list {NodeId}", node.Id);
            }

            var saveResult = _contentService.Save(node);
            if (!saveResult.Success) return false;

            // Only re-publish a list that was already published — publishing a draft list here
            // would make an unfinished start list public as a side effect of a club correction.
            if (node.Published)
                _contentService.Publish(node, new[] { "*" }, -1);

            return true;
        }

        /// <summary>Set Club on every row of <paramref name="rows"/> whose MemberId matches.</summary>
        private static bool SetClubOnMatchingRows(JArray rows, int memberId, string newClubName)
        {
            var changed = false;
            foreach (var row in rows.OfType<JObject>())
            {
                var rowMemberId = row.Value<int?>("MemberId") ?? row.Value<int?>("memberId");
                if (rowMemberId != memberId) continue;

                var clubProp = row.Property("Club") ?? row.Property("club");
                if (clubProp != null)
                {
                    if (string.Equals(clubProp.Value.Value<string>(), newClubName, StringComparison.Ordinal)) continue;
                    clubProp.Value = newClubName;
                }
                else
                {
                    row["Club"] = newClubName;
                }
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Fältskytte snapshots the club onto the patrol row. Targeted UPDATE rather than a
        /// read-modify-write so a concurrent patrol edit can't be clobbered.
        /// </summary>
        private int PatchFaltskyttePatrols(int competitionId, int memberId, string newClubName)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Execute(
                @"UPDATE pm SET pm.ClubName = @0
                  FROM FaltskyttePatrolMember pm
                  INNER JOIN FaltskyttePatrol p ON p.Id = pm.PatrolId
                  WHERE p.CompetitionId = @1 AND pm.MemberId = @2 AND pm.ClubName <> @0",
                newClubName, competitionId, memberId);
        }
    }
}
