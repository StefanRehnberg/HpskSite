using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Svenska Lag → pistol.nu member import (see Documentation/MEMBER_DATABASE.md §7).
    /// Two-step flow: Preview parses an uploaded .xlsx/.csv and returns a suggested
    /// column mapping + sample rows; Commit creates/updates members from the client's
    /// confirmed mapping and the parsed rows posted back.
    ///
    /// Club-admin / site-admin gated per club.
    /// </summary>
    public class MemberImportController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authService;
        private readonly IMemberManager _memberManager;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly ILogger<MemberImportController> _logger;

        private const string ClubMemberTypeAlias = "hpskClub";

        /// <summary>
        /// Aliases that describe a person's relationship to a SPECIFIC club. These are written
        /// to the per-club <see cref="ClubMembership"/> record, never to the shared member/login.
        /// </summary>
        private static readonly HashSet<string> ClubScopedAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            "membershipType", "membershipStatus", "memberSince", "memberUntil", "endReason",
            "backgroundCheckApproved", "backgroundCheckDate", "registeredInMap", "federations",
            "memberNotes", "householdId", "householdPrimary"
        };

        public MemberImportController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            AdminAuthorizationService authService,
            IMemberManager memberManager,
            ClubMembershipService clubMembershipService,
            ILogger<MemberImportController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _authService = authService;
            _memberManager = memberManager;
            _clubMembershipService = clubMembershipService;
            _logger = logger;
        }

        // ---------------------------------------------------------------
        // Preview — parse the uploaded file, suggest a mapping
        // ---------------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Preview(int clubId, IFormFile file)
        {
            if (!await IsAuthorizedAsync(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Ingen fil uppladdad" });
            }

            if (file.Length > 15 * 1024 * 1024)
            {
                return Json(new { success = false, message = "Filen är för stor. Max 15 MB." });
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".xlsx" && ext != ".csv")
            {
                return Json(new { success = false, message = "Ogiltig filtyp. Ladda upp .xlsx eller .csv." });
            }

            try
            {
                MemberImportParser.ParseResult parsed;
                using (var stream = file.OpenReadStream())
                {
                    parsed = MemberImportParser.Parse(stream, file.FileName);
                }

                if (parsed.Headers.Count == 0)
                {
                    return Json(new { success = false, message = "Kunde inte läsa några kolumner ur filen." });
                }

                var suggested = MemberImportParser.SuggestMapping(parsed.Headers);
                var sampleRows = parsed.Rows.Take(5).ToList();
                var targetFields = MemberImportParser.TargetFields
                    .Select(t => new { alias = t.Alias, label = t.Label })
                    .ToList();

                return Json(new
                {
                    success = true,
                    headers = parsed.Headers,
                    suggestedMapping = suggested,
                    sampleRows = sampleRows,
                    rows = parsed.Rows,
                    totalRows = parsed.Rows.Count,
                    targetFields = targetFields
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemberImport.Preview] Failed to parse file for club {ClubId}", clubId);
                return Json(new { success = false, message = "Fel vid inläsning av filen: " + ex.Message });
            }
        }

        // ---------------------------------------------------------------
        // Commit — create/update members from the confirmed mapping
        // ---------------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Commit(int clubId, string mappingJson, string rowsJson)
        {
            if (!await IsAuthorizedAsync(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            Dictionary<string, string> mapping;
            List<Dictionary<string, string>> rows;
            try
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson ?? "{}")
                          ?? new Dictionary<string, string>();
                rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(rowsJson ?? "[]")
                       ?? new List<Dictionary<string, string>>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemberImport.Commit] Bad payload for club {ClubId}", clubId);
                return Json(new { success = false, message = "Ogiltigt dataformat: " + ex.Message });
            }

            int created = 0, updated = 0, skipped = 0, pnrIncompleteCount = 0;
            var errors = new List<string>();

            // Pre-load all members once (performance rule: no per-row lookups).
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
                .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                .ToList();

            var byPnr = new Dictionary<string, IMember>();
            var byEmail = new Dictionary<string, IMember>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in allMembers)
            {
                var pnrKey = NormalizePnrKey(m.GetValue("personNumber")?.ToString());
                if (!string.IsNullOrEmpty(pnrKey) && !byPnr.ContainsKey(pnrKey))
                {
                    byPnr[pnrKey] = m;
                }
                if (!string.IsNullOrWhiteSpace(m.Email) && !byEmail.ContainsKey(m.Email))
                {
                    byEmail[m.Email] = m;
                }
            }

            int rowIndex = 0;
            foreach (var row in rows)
            {
                rowIndex++;
                try
                {
                    // Collapse mapped source columns → alias values. Multiple columns can
                    // map to memberNotes; concatenate those.
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var notes = new List<string>();

                    foreach (var kvp in mapping)
                    {
                        var sourceHeader = kvp.Key;
                        var alias = (kvp.Value ?? "").Trim();
                        if (string.IsNullOrEmpty(alias))
                        {
                            continue;
                        }
                        if (!row.TryGetValue(sourceHeader, out var raw))
                        {
                            continue;
                        }
                        var value = (raw ?? "").Trim();
                        if (string.IsNullOrEmpty(value))
                        {
                            continue;
                        }

                        if (alias == "memberNotes")
                        {
                            notes.Add($"{StripDedupSuffix(sourceHeader)}: {value}");
                        }
                        else if (!values.ContainsKey(alias))
                        {
                            values[alias] = value;
                        }
                    }

                    if (notes.Count > 0)
                    {
                        values["memberNotes"] = string.Join(" | ", notes);
                    }

                    values.TryGetValue("email", out var email);
                    values.TryGetValue("personNumber", out var personNumber);
                    values.TryGetValue("firstName", out var firstName);
                    values.TryGetValue("lastName", out var lastName);
                    values.TryGetValue("birthDate", out var birthDate);

                    email = (email ?? "").Trim();
                    var pnrKey = NormalizePnrKey(personNumber);

                    // Dedup: personNumber first, then email.
                    IMember? existing = null;
                    if (!string.IsNullOrEmpty(pnrKey) && byPnr.TryGetValue(pnrKey, out var pm))
                    {
                        existing = pm;
                    }
                    else if (!string.IsNullOrEmpty(email) && byEmail.TryGetValue(email, out var em))
                    {
                        existing = em;
                    }

                    bool pnrComplete = IsPnrComplete(personNumber);
                    bool pnrIncomplete = !pnrComplete;

                    IMember member;
                    bool isNew;

                    if (existing != null)
                    {
                        member = existing;
                        isNew = false;
                    }
                    else
                    {
                        // Creating requires an email (Umbraco username/login).
                        if (string.IsNullOrWhiteSpace(email))
                        {
                            skipped++;
                            errors.Add($"Rad {rowIndex}: saknar e-post och matchar ingen befintlig medlem – hoppar över.");
                            continue;
                        }

                        var displayName = $"{firstName} {lastName}".Trim();
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            displayName = email;
                        }

                        member = _memberService.CreateMember(email, email, displayName, "hpskMember");
                        member.SetValue("primaryClubId", clubId);
                        member.IsApproved = true;
                        isNew = true;
                    }

                    // Apply mapped fields (all guarded by HasProperty except native email/name).
                    ApplyValues(member, values, email, firstName, lastName, birthDate, pnrIncomplete, isNew);

                    _memberService.Save(member);

                    // Club-scoped facts go on the per-club ClubMembership record, not the member.
                    UpsertClubMembership(member.Id, clubId, values);

                    if (isNew)
                    {
                        _memberService.AssignRole(member.Id, "Users");
                        created++;

                        // Keep in-memory lookups fresh so later rows dedup against this one.
                        if (!string.IsNullOrEmpty(pnrKey) && !byPnr.ContainsKey(pnrKey))
                        {
                            byPnr[pnrKey] = member;
                        }
                        if (!string.IsNullOrWhiteSpace(member.Email) && !byEmail.ContainsKey(member.Email))
                        {
                            byEmail[member.Email] = member;
                        }
                    }
                    else
                    {
                        updated++;
                    }

                    if (pnrIncomplete)
                    {
                        pnrIncompleteCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MemberImport.Commit] Row {Row} failed for club {ClubId}", rowIndex, clubId);
                    skipped++;
                    errors.Add($"Rad {rowIndex}: {ex.Message}");
                }
            }

            _logger.LogInformation(
                "[MemberImport.Commit] Club {ClubId}: created {Created}, updated {Updated}, skipped {Skipped}, pnrIncomplete {Pnr}",
                clubId, created, updated, skipped, pnrIncompleteCount);

            return Json(new
            {
                success = true,
                created,
                updated,
                skipped,
                pnrIncompleteCount,
                errors
            });
        }

        // ---------------------------------------------------------------
        // Field application
        // ---------------------------------------------------------------
        private void ApplyValues(IMember member, Dictionary<string, string> values,
            string email, string? firstName, string? lastName, string? birthDate,
            bool pnrIncomplete, bool isNew)
        {
            // Native / core fields
            if (!string.IsNullOrWhiteSpace(firstName)) member.SetValue("firstName", firstName);
            if (!string.IsNullOrWhiteSpace(lastName)) member.SetValue("lastName", lastName);
            // Never overwrite an existing member's email with a blank; only set when creating
            // (email is set at CreateMember) — leave alone on update to avoid login breakage.

            foreach (var kvp in values)
            {
                var alias = kvp.Key;
                var value = kvp.Value ?? "";

                // Club-scoped fields live on ClubMembership (written in Commit), never on the member.
                if (ClubScopedAliases.Contains(alias))
                {
                    continue;
                }

                switch (alias)
                {
                    case "email":
                    case "firstName":
                    case "lastName":
                        // handled above / at create time
                        break;

                    default:
                        SetIfPresent(member, alias, value);
                        break;
                }
            }

            // pnrIncomplete flag (only set true; don't clear an existing flag needlessly).
            if (pnrIncomplete)
            {
                SetIfPresent(member, "pnrIncomplete", true);
            }
            else
            {
                SetIfPresent(member, "pnrIncomplete", false);
            }
        }

        /// <summary>
        /// Build/update the per-club <see cref="ClubMembership"/> record from the row's mapped
        /// values. Only fields whose alias is actually present in <paramref name="values"/> are
        /// overwritten — re-importing a subset of columns never blanks out existing membership data.
        /// A brand-new membership takes model defaults for any field the import didn't provide.
        /// </summary>
        private void UpsertClubMembership(int memberId, int clubId, Dictionary<string, string> values)
        {
            var membership = _clubMembershipService.Get(memberId, clubId)
                             ?? new ClubMembership { MemberId = memberId, ClubId = clubId };

            if (values.TryGetValue("membershipType", out var membershipType))
            {
                membership.MembershipType = string.IsNullOrWhiteSpace(membershipType) ? null : membershipType.Trim();
            }

            if (values.TryGetValue("membershipStatus", out var membershipStatus)
                && !string.IsNullOrWhiteSpace(membershipStatus))
            {
                membership.MembershipStatus = membershipStatus.Trim();
            }

            if (values.TryGetValue("memberSince", out var memberSince))
            {
                membership.MemberSince = ParseDate(memberSince);
            }

            if (values.TryGetValue("memberUntil", out var memberUntil))
            {
                membership.MemberUntil = ParseDate(memberUntil);
            }

            if (values.TryGetValue("endReason", out var endReason))
            {
                membership.EndReason = string.IsNullOrWhiteSpace(endReason) ? null : endReason.Trim();
            }

            // BackgroundCheckDate + BackgroundCheckApproved (a non-empty date implies approved).
            DateTime? bgDate = null;
            bool bgDateProvided = values.TryGetValue("backgroundCheckDate", out var bgDateRaw);
            if (bgDateProvided)
            {
                bgDate = ParseDate(bgDateRaw);
                membership.BackgroundCheckDate = bgDate;
            }

            if (values.ContainsKey("backgroundCheckApproved") || bgDateProvided)
            {
                bool approved = false;
                if (values.TryGetValue("backgroundCheckApproved", out var bgApproved))
                {
                    approved = ToBool(bgApproved);
                }
                membership.BackgroundCheckApproved = approved || bgDate.HasValue;
            }

            if (values.TryGetValue("registeredInMap", out var registeredInMap))
            {
                membership.RegisteredInMap = ToBool(registeredInMap);
            }

            if (values.TryGetValue("federations", out var federations))
            {
                membership.Federations = string.IsNullOrWhiteSpace(federations) ? null : federations.Trim();
            }

            if (values.TryGetValue("memberNotes", out var memberNotes))
            {
                membership.MemberNotes = string.IsNullOrWhiteSpace(memberNotes) ? null : memberNotes;
            }

            if (values.TryGetValue("householdId", out var householdId))
            {
                membership.HouseholdId = string.IsNullOrWhiteSpace(householdId) ? null : householdId.Trim();
            }

            if (values.TryGetValue("householdPrimary", out var householdPrimary))
            {
                membership.HouseholdPrimary = ToBool(householdPrimary);
            }

            _clubMembershipService.Save(membership);
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------
        private async Task<bool> IsAuthorizedAsync(int clubId)
        {
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            if (isSiteAdmin)
            {
                return true;
            }
            return await _authService.IsClubAdminForClub(clubId);
        }

        private static void SetIfPresent(IMember member, string alias, object value)
        {
            if (member.HasProperty(alias))
            {
                member.SetValue(alias, value);
            }
        }

        private static bool ToBool(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            return v == "ja" || v == "yes" || v == "true" || v == "1" || v == "on" || v == "x";
        }

        /// <summary>Parse an imported "yyyy-MM-dd" date; null on blank/invalid.</summary>
        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
            {
                return d;
            }
            return null;
        }

        /// <summary>Digits only; keyed on the last 10 digits so 10- and 12-digit forms match.</summary>
        private static string NormalizePnrKey(string? pnr)
        {
            if (string.IsNullOrWhiteSpace(pnr)) return "";
            var digits = new string(pnr.Where(char.IsDigit).ToArray());
            if (digits.Length >= 10)
            {
                return digits.Substring(digits.Length - 10);
            }
            return digits;
        }

        /// <summary>A complete personnummer normalizes to 12 digits (ÅÅÅÅMMDD-XXXX).</summary>
        private static bool IsPnrComplete(string? pnr)
        {
            if (string.IsNullOrWhiteSpace(pnr)) return false;
            var digits = new string(pnr.Where(char.IsDigit).ToArray());
            return digits.Length == 12;
        }

        private static string StripDedupSuffix(string header)
        {
            var match = System.Text.RegularExpressions.Regex.Match(header ?? "", @"^(.*?)\s+\(\d+\)$");
            return match.Success ? match.Groups[1].Value : (header ?? "");
        }
    }
}
