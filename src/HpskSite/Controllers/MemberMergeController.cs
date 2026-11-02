using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Duplicate-member cleanup for club admins (see Documentation/MEMBER_DATABASE.md §8).
    ///
    /// Three steps, each its own endpoint so nothing is written before the admin has seen it:
    /// Find → Compare → Merge. Only the last one writes.
    ///
    /// Club-admin / site-admin gated per club, and every endpoint additionally checks that the
    /// members being touched actually belong to that club (or to no club at all) — the club id in
    /// the request must never be a key to someone else's roster.
    /// </summary>
    public class MemberMergeController : SurfaceController
    {
        private readonly MemberMergeService _mergeService;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<MemberMergeController> _logger;

        public MemberMergeController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            MemberMergeService mergeService,
            IMemberService memberService,
            AdminAuthorizationService authService,
            IMemberManager memberManager,
            ILogger<MemberMergeController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _mergeService = mergeService;
            _memberService = memberService;
            _authService = authService;
            _memberManager = memberManager;
            _logger = logger;
        }

        /// <summary>Candidate duplicate pairs for the club. Read-only.</summary>
        [HttpGet]
        public async Task<IActionResult> FindDuplicates(int clubId)
        {
            if (!await IsAuthorizedAsync(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var candidates = _mergeService.FindCandidates(clubId);

                _logger.LogInformation("[MemberMerge.Find] Club {ClubId}: {Count} candidate pairs", clubId, candidates.Count);

                return Json(new
                {
                    success = true,
                    count = candidates.Count,
                    candidates = candidates.Select(c => new
                    {
                        score = c.Score,
                        level = Level(c.Score),
                        reasons = c.Reasons,
                        a = Summary(c.A),
                        b = Summary(c.B)
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemberMerge.Find] Failed for club {ClubId}", clubId);
                return Json(new { success = false, message = "Kunde inte söka efter dubbletter: " + ex.Message });
            }
        }

        /// <summary>Side-by-side comparison of two members, with a suggested survivor. Read-only.</summary>
        [HttpGet]
        public async Task<IActionResult> Compare(int clubId, int memberAId, int memberBId)
        {
            if (!await IsAuthorizedAsync(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }
            if (!MembersAreInScope(clubId, memberAId, memberBId, out var scopeError))
            {
                return Json(new { success = false, message = scopeError });
            }

            try
            {
                var c = _mergeService.Compare(memberAId, memberBId, clubId);

                // Säkerhetsplanen 2026-11-02: vilken av de två som INTE får tas bort av den som slår ihop (administratörsroll),
                // och varför. Klienten spärrar knappen och visar texten — Merge prövar samma regel igen.
                var isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
                var managed = (await _authService.GetManagedClubIds()).ToList();
                var refusals = new Dictionary<int, string>();
                foreach (var m in new[] { c.Survivor, c.Loser })
                {
                    var why = MergeRefusalFor(m.Id, m.Name, isSiteAdmin, managed);
                    if (why != null) refusals[m.Id] = why;
                }

                return Json(new
                {
                    success = true,
                    refusals = refusals,
                    suggestedSurvivorId = c.SuggestedSurvivorId,
                    suggestedReason = c.SuggestedReason,
                    survivor = Summary(c.Survivor),
                    loser = Summary(c.Loser),
                    fields = c.Fields.Select(f => new
                    {
                        alias = f.Alias,
                        label = f.Label,
                        survivorValue = f.SurvivorValue,
                        loserValue = f.LoserValue,
                        conflict = f.Conflict,
                        takeFromLoser = f.TakeFromLoser
                    }),
                    counts = c.Counts
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemberMerge.Compare] Failed for {A}/{B}", memberAId, memberBId);
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Performs the merge. The only endpoint here that writes — and it is irreversible, so it
        /// re-checks scope rather than trusting that Compare was called first.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Merge(int clubId, int survivorMemberId, int loserMemberId, string? takeFieldsJson)
        {
            if (!await IsAuthorizedAsync(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }
            if (!MembersAreInScope(clubId, survivorMemberId, loserMemberId, out var scopeError))
            {
                return Json(new { success = false, message = scopeError });
            }

            List<string> takeFields;
            try
            {
                takeFields = JsonSerializer.Deserialize<List<string>>(takeFieldsJson ?? "[]") ?? new List<string>();
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ogiltigt dataformat: " + ex.Message });
            }

            // ⚠️ Säkerhetsplanen 2026-11-02 (A5/H4): ett konto med en administratörsroll som aktören inte får föra över
            // kan inte tas bort av en sammanslagning — annars kunde en klubbadmin ta över en sajtadmins roll och radera kontot.
            var actorIsSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var actorManagedClubIds = (await _authService.GetManagedClubIds()).ToList();
            var loserForRefusal = _memberService.GetById(loserMemberId);
            var refusalText = loserForRefusal == null ? null
                : MergeRefusalFor(loserForRefusal.Id, loserForRefusal.Name ?? "Kontot", actorIsSiteAdmin, actorManagedClubIds);
            if (refusalText != null)
            {
                _logger.LogWarning("SEC: Sammanslagning nekades i controllern — förloraren {Loser}, klubb {ClubId}", loserMemberId, clubId);
                return Json(new { success = false, message = refusalText });
            }

            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();

                var result = _mergeService.Merge(new MergeRequest
                {
                    ClubId = clubId,
                    SurvivorMemberId = survivorMemberId,
                    LoserMemberId = loserMemberId,
                    ActorIsSiteAdmin = actorIsSiteAdmin,
                    ActorManagedClubIds = actorManagedClubIds,
                    MergedByMemberId = currentMember != null ? int.Parse(currentMember.Id!) : null,
                    TakeFromLoser = new HashSet<string>(takeFields, StringComparer.OrdinalIgnoreCase)
                });

                return Json(new
                {
                    success = true,
                    survivorMemberId = result.SurvivorMemberId,
                    loserName = result.LoserName,
                    loserEmail = result.LoserEmail,
                    fieldsTaken = result.FieldsTaken.Count,
                    rolesTaken = result.RolesTaken,
                    rowsMoved = result.RowsMoved,
                    rowsMovedTotal = result.RowsMoved.Values.Sum(),
                    conflicts = result.Conflicts,
                    clubMembershipsUnioned = result.ClubMembershipsUnioned,
                    registrationsMoved = result.RegistrationsMoved,
                    errors = result.Errors
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemberMerge.Merge] Failed merging {Loser} into {Survivor} for club {ClubId}",
                    loserMemberId, survivorMemberId, clubId);
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>Svensk förklaring om medlemmen inte får tas bort av en sammanslagning (administratörsroll), annars null.</summary>
        private string? MergeRefusalFor(int memberId, string? name, bool actorIsSiteAdmin, IReadOnlyCollection<int> managedClubIds) =>
            HpskSite.Services.Security.MergeRoleRules.LoserRefusal(
                string.IsNullOrWhiteSpace(name) ? "Kontot" : name,
                _memberService.GetAllRoles(memberId),
                actorIsSiteAdmin,
                id => managedClubIds.Contains(id));

        /// <summary>
        /// Both members must be on this club's roster, or be club-less. Without this an admin
        /// could hand-post any two member ids and merge people from another club.
        /// </summary>
        private bool MembersAreInScope(int clubId, int memberAId, int memberBId, out string error)
        {
            error = "";
            foreach (var id in new[] { memberAId, memberBId })
            {
                var m = _memberService.GetById(id);
                if (m == null)
                {
                    error = $"Medlem {id} finns inte.";
                    return false;
                }
                if (MemberMergeService.IsInClub(m, clubId)) continue;

                var primary = m.GetValue("primaryClubId")?.ToString();
                var csv = m.GetValue("memberClubIDs")?.ToString() ?? "";
                bool clubless = string.IsNullOrWhiteSpace(primary) &&
                                csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0;
                if (clubless) continue;

                error = $"{m.Name} tillhör en annan klubb och kan inte slås ihop härifrån.";
                return false;
            }
            return true;
        }

        private static string Level(int score) => score switch
        {
            >= 95 => "saker",
            >= 80 => "trolig",
            >= 70 => "mojlig",
            _ => "svag"
        };

        private static object Summary(MemberSummary s) => new
        {
            id = s.Id,
            name = s.Name,
            email = s.Email,
            personNumber = s.PersonNumber,
            shooterIdNumber = s.ShooterIdNumber,
            phoneNumber = s.PhoneNumber,
            created = s.Created.ToString("yyyy-MM-dd"),
            lastLogin = s.LastLogin?.ToString("yyyy-MM-dd"),
            inClub = s.InClub,
            filledFields = s.FilledFields
        };

        private async Task<bool> IsAuthorizedAsync(int clubId)
        {
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            if (isSiteAdmin)
            {
                return true;
            }
            return await _authService.IsClubAdminForClub(clubId);
        }
    }
}
