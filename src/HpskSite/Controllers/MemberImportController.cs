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
        private readonly MemberAccessKeyService _accessKeyService;
        private readonly MarkenLedgerService _markenLedger;
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

        /// <summary>
        /// Aliases that are ACTIONS, not scalar fields — they map to other tables/systems
        /// (Märken ledger, MemberAccessKey, member roles) and are handled explicitly in
        /// <see cref="Commit"/>. They must never be written as a member property or a
        /// ClubMembership column, so both <see cref="ApplyValues"/> and
        /// <see cref="UpsertClubMembership"/> skip them.
        /// </summary>
        private static readonly HashSet<string> ActionAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            "guldmarkeNumber", "guldmarkeAwarded", "nyckel", "skjutledare"
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
            MemberAccessKeyService accessKeyService,
            MarkenLedgerService markenLedger,
            ILogger<MemberImportController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _authService = authService;
            _memberManager = memberManager;
            _clubMembershipService = clubMembershipService;
            _accessKeyService = accessKeyService;
            _markenLedger = markenLedger;
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
        public async Task<IActionResult> Commit(int clubId, string mappingJson, string rowsJson, bool force = false)
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

            // The acting admin — recorded as the "entered/created by" on any action rows
            // (Märken badge, access key) materialized from this import.
            int actingMemberId = 0;
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember != null && !string.IsNullOrWhiteSpace(currentMember.Email))
            {
                actingMemberId = _memberService.GetByEmail(currentMember.Email)?.Id ?? 0;
            }

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

            // ---- Pre-flight: refuse a run that would duplicate the club's whole roster ----
            // WHY: on 2026-08-21 an export whose email cells carried a trailing ';' matched NONE of
            // the club's existing members and silently created 141 duplicates. The tell was visible
            // before a single write — every row a "create", none an update — and nothing looked at it.
            // A club's genuine FIRST import also creates everyone, so an empty roster is not
            // suspicious; matching zero of a roster that already exists is. Confirmable, not fatal:
            // the admin may legitimately be adding a wholly new group of people.
            if (!force)
            {
                var (wouldMatch, wouldCreate) = PreflightMatchCounts(rows, mapping, byPnr, byEmail);
                int clubMemberCount = CountClubMembers(allMembers, clubId);

                if (wouldMatch == 0 && wouldCreate >= 10 && clubMemberCount >= 10)
                {
                    bool pnrMapped = mapping.Values.Any(a => string.Equals((a ?? "").Trim(), "personNumber", StringComparison.OrdinalIgnoreCase));

                    _logger.LogWarning(
                        "[MemberImport.Commit] Pre-flight blocked club {ClubId}: {Create} rows would ALL be created, " +
                        "0 matched an existing member, club already has {Existing} members (personNumber mapped: {PnrMapped})",
                        clubId, wouldCreate, clubMemberCount, pnrMapped);

                    var reason = pnrMapped
                        ? "Kontrollera att e-post och personnummer i filen är skrivna exakt som på pistol.nu."
                        : "Filen har ingen personnummerkolumn mappad, så e-posten är enda nyckeln – ett extra tecken i "
                          + "e-postcellen (till exempel ett avslutande semikolon) räcker för att matchningen ska missa.";

                    return Json(new
                    {
                        success = false,
                        requiresConfirmation = true,
                        wouldCreate,
                        clubMemberCount,
                        message = $"Stopp: alla {wouldCreate} rader skulle skapas som NYA medlemmar och ingen av dem "
                                + $"matchar någon av klubbens {clubMemberCount} befintliga medlemmar. Det brukar betyda "
                                + $"att matchningsnyckeln inte stämmer – inte att alla är nya. {reason} "
                                + "Är personerna verkligen nya kan du köra ändå."
                    });
                }
            }

            int rowIndex = 0;
            foreach (var row in rows)
            {
                rowIndex++;
                try
                {
                    var values = CollapseRow(row, mapping);

                    values.TryGetValue("email", out var email);
                    values.TryGetValue("personNumber", out var personNumber);
                    values.TryGetValue("firstName", out var firstName);
                    values.TryGetValue("lastName", out var lastName);
                    values.TryGetValue("birthDate", out var birthDate);

                    // Clean the address BEFORE it is used as a dedup key — a stray trailing ';'
                    // or wrapping quote from the export would otherwise match nothing and create
                    // a duplicate account (see NormalizeEmail).
                    var rawEmail = (email ?? "").Trim();
                    email = NormalizeEmail(rawEmail);
                    if (email.Length > 0)
                    {
                        values["email"] = email;
                    }
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
                        // The person may already be a member of ANOTHER club. Make sure they're
                        // affiliated with THIS club too, so they show in its roster — their new
                        // ClubMembership row alone isn't enough (the roster filters on
                        // primaryClubId / memberClubIds).
                        EnsureClubAffiliation(member, clubId);
                    }
                    else
                    {
                        // Creating requires a valid email (Umbraco username/login).
                        if (string.IsNullOrWhiteSpace(email))
                        {
                            skipped++;
                            errors.Add($"Rad {rowIndex}: saknar e-post och matchar ingen befintlig medlem – hoppar över.");
                            continue;
                        }
                        // A non-empty but malformed value would create a member with a broken,
                        // un-loginable account. Skip + report it the same as a blank email.
                        if (!IsValidEmail(email))
                        {
                            skipped++;
                            // Show the value as it stands in the FILE when cleaning changed it —
                            // otherwise the admin can't find the row they need to correct.
                            var shown = string.Equals(email, rawEmail, StringComparison.Ordinal)
                                ? $"\"{rawEmail}\""
                                : $"\"{rawEmail}\" (tolkad som \"{email}\")";
                            errors.Add($"Rad {rowIndex}: ogiltig e-postadress {shown} – hoppar över (e-post krävs som inloggning). Står det flera adresser i samma cell måste de delas upp.");
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

                    // Register the new member in the dedup indexes. Without this, a person listed
                    // TWICE in the same file is matched on neither pass — the index was built once
                    // before the loop — and the second row tries to create a second account on the
                    // same login. Same-file repeats now update the row we just created.
                    if (isNew)
                    {
                        if (!string.IsNullOrEmpty(pnrKey)) byPnr.TryAdd(pnrKey, member);
                        if (!string.IsNullOrWhiteSpace(member.Email)) byEmail.TryAdd(member.Email, member);
                    }

                    // ---- Club-specific action columns (map to other tables/systems) ----
                    values.TryGetValue("skjutledare", out var skjutledareValue);
                    values.TryGetValue("nyckel", out var nyckelValue);
                    values.TryGetValue("guldmarkeNumber", out var guldmarkeNumber);
                    values.TryGetValue("guldmarkeAwarded", out var guldmarkeAwarded);

                    // Resolve a usable Guldmärke award year/date — used both for the note decision
                    // in (a) and the badge import in (c). Full date wins; else a bare 4-digit year.
                    int guldYear = 0;
                    DateTime? guldDate = null;
                    if (!string.IsNullOrWhiteSpace(guldmarkeNumber))
                    {
                        var awardDate = ParseDate(guldmarkeAwarded);
                        if (awardDate.HasValue)
                        {
                            guldYear = awardDate.Value.Year;
                            guldDate = awardDate.Value;
                        }
                        else if (!string.IsNullOrWhiteSpace(guldmarkeAwarded)
                                 && System.Text.RegularExpressions.Regex.IsMatch(guldmarkeAwarded.Trim(), @"^\d{4}$"))
                        {
                            guldYear = int.Parse(guldmarkeAwarded.Trim());
                            guldDate = null;
                        }
                    }

                    // (a) Merge note additions into memberNotes BEFORE UpsertClubMembership so they
                    //     land in ClubMembership.MemberNotes.
                    var noteAdditions = new List<string>();
                    if (!string.IsNullOrWhiteSpace(skjutledareValue))
                    {
                        noteAdditions.Add($"Skjutledare: {skjutledareValue.Trim()}");
                    }
                    if (!string.IsNullOrWhiteSpace(guldmarkeNumber) && guldYear == 0)
                    {
                        // Number present but no usable award year — keep it as a note.
                        noteAdditions.Add($"Guldmärke: {guldmarkeNumber.Trim()}");
                    }
                    if (noteAdditions.Count > 0)
                    {
                        values.TryGetValue("memberNotes", out var existingNotes);
                        values["memberNotes"] = string.IsNullOrWhiteSpace(existingNotes)
                            ? string.Join(" | ", noteAdditions)
                            : existingNotes + " | " + string.Join(" | ", noteAdditions);
                    }

                    // (b) Club-scoped facts go on the per-club ClubMembership record, not the member.
                    UpsertClubMembership(member.Id, clubId, values);

                    // (c) Guldmärke → Märken ledger (Pistolskyttemärket Guld).
                    if (!string.IsNullOrWhiteSpace(guldmarkeNumber) && guldYear > 0)
                    {
                        await _markenLedger.ImportGuldBadgeAsync(member.Id, guldmarkeNumber.Trim(), guldYear, guldDate, actingMemberId);
                    }

                    // (d) Nyckel → MemberAccessKey. Preserve the RAW value; don't parse deposit/date.
                    if (!string.IsNullOrWhiteSpace(nyckelValue))
                    {
                        var rawKey = nyckelValue.Trim();
                        var identifier = rawKey.Length > 100 ? rawKey.Substring(0, 100) : rawKey;
                        var existingKeys = _accessKeyService.GetForMember(member.Id);
                        bool dup = existingKeys.Any(k => string.Equals(k.Identifier, identifier, StringComparison.Ordinal));
                        if (!dup)
                        {
                            _accessKeyService.Add(new MemberAccessKey
                            {
                                MemberId = member.Id,
                                ClubId = clubId,
                                KeyType = "Nyckel",
                                Identifier = identifier,
                                Notes = rawKey.Length > 100 ? rawKey : null,
                                CreatedByMemberId = actingMemberId
                            });
                        }
                    }

                    // (e) Skjutledare → club role (only for a truthy value).
                    if (IsActionTruthy(skjutledareValue))
                    {
                        await _authService.EnsureSkjutledareGroup(clubId);
                        _memberService.AssignRole(member.Id, $"Skjutledare_{clubId}");
                    }

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

        /// <summary>
        /// Collapses one source row into alias → value using the confirmed mapping. Blank cells are
        /// dropped (so a mapped-but-empty column never blanks stored data), the first mapped column
        /// wins per alias, and several columns mapped to memberNotes are concatenated.
        /// Shared by the pre-flight check and the write loop so both see identical values.
        /// </summary>
        private static Dictionary<string, string> CollapseRow(
            Dictionary<string, string> row, Dictionary<string, string> mapping)
        {
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

            return values;
        }

        /// <summary>
        /// How many rows would MATCH an existing member, and how many would be created new.
        /// Read-only — same keys and same order (personnummer, then email) as the write loop.
        /// </summary>
        private static (int WouldMatch, int WouldCreate) PreflightMatchCounts(
            List<Dictionary<string, string>> rows,
            Dictionary<string, string> mapping,
            Dictionary<string, IMember> byPnr,
            Dictionary<string, IMember> byEmail)
        {
            int match = 0, create = 0;

            foreach (var row in rows)
            {
                var values = CollapseRow(row, mapping);
                values.TryGetValue("email", out var rawEmail);
                values.TryGetValue("personNumber", out var pnr);

                var email = NormalizeEmail(rawEmail);
                var pnrKey = NormalizePnrKey(pnr);

                bool matched = (!string.IsNullOrEmpty(pnrKey) && byPnr.ContainsKey(pnrKey))
                               || (!string.IsNullOrEmpty(email) && byEmail.ContainsKey(email));

                if (matched) match++;
                else if (IsValidEmail(email)) create++;   // rows without a usable login are skipped, not created
            }

            return (match, create);
        }

        /// <summary>
        /// Members already affiliated with the club — its primary club or listed in memberClubIds.
        /// Used only to tell a club's FIRST import (roster legitimately empty, everyone is new)
        /// apart from a re-import that matched nothing because the dedup key was broken.
        /// </summary>
        private static int CountClubMembers(List<IMember> allMembers, int clubId)
        {
            var id = clubId.ToString();
            return allMembers.Count(m =>
            {
                if (m.GetValue("primaryClubId")?.ToString() == id) return true;
                var csv = m.GetValue("memberClubIds")?.ToString() ?? "";
                return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Contains(id);
            });
        }

        // ---------------------------------------------------------------
        // Field application
        // ---------------------------------------------------------------
        private void ApplyValues(IMember member, Dictionary<string, string> values,
            string email, string? firstName, string? lastName, string? birthDate,
            bool pnrIncomplete, bool isNew)
        {
            // Native / core fields. For an EXISTING member these are fill-empty-only — a second
            // club's import must never overwrite the person's shared profile (they/their original
            // club own it). See SetPersonField + the default case below.
            if (!string.IsNullOrWhiteSpace(firstName)) SetPersonField(member, "firstName", firstName, isNew);
            if (!string.IsNullOrWhiteSpace(lastName)) SetPersonField(member, "lastName", lastName, isNew);
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

                // Action aliases map to other tables/systems, handled explicitly in Commit.
                if (ActionAliases.Contains(alias))
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
                        SetPersonField(member, alias, value, isNew);
                        break;
                }
            }

            // pnrIncomplete reflects the member's ACTUAL stored personnummer after this import
            // (for an existing member the pnr may have been kept rather than overwritten).
            var storedPnr = member.HasProperty("personNumber") ? (member.GetValue("personNumber")?.ToString() ?? "") : "";
            SetIfPresent(member, "pnrIncomplete", !IsPnrComplete(storedPnr));
        }

        // Writes a person-level field. For a NEW member: set it. For an EXISTING member:
        // fill-empty-only — never overwrite a value the person / their original club already has.
        private static void SetPersonField(IMember member, string alias, string value, bool isNew)
        {
            if (!member.HasProperty(alias)) return;
            if (isNew)
            {
                member.SetValue(alias, value);
                return;
            }
            var current = member.GetValue(alias)?.ToString();
            if (string.IsNullOrWhiteSpace(current)) member.SetValue(alias, value);
        }

        // Ensures the member is affiliated with clubId (so the importing club's roster shows them).
        // No primary club yet → make this it; otherwise add to the memberClubIds CSV if missing.
        private static void EnsureClubAffiliation(IMember member, int clubId)
        {
            var primary = member.GetValue("primaryClubId")?.ToString();
            if (int.TryParse(primary, out var pid))
            {
                if (pid == clubId) return; // already the primary club
            }
            else
            {
                // No usable primary club → set this one and stop.
                member.SetValue("primaryClubId", clubId);
                return;
            }

            var csv = member.GetValue("memberClubIds")?.ToString() ?? "";
            var ids = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!ids.Contains(clubId.ToString()))
            {
                ids.Add(clubId.ToString());
                member.SetValue("memberClubIds", string.Join(",", ids));
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

        /// <summary>
        /// A club-column action value counts as "on" when it's non-empty and not an explicit
        /// negative (nej/no/0/false/-). Used to gate the Skjutledare role assignment.
        /// </summary>
        private static bool IsActionTruthy(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            return v != "nej" && v != "no" && v != "0" && v != "false" && v != "-";
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

        /// <summary>
        /// Characters that cannot occur in either half of an address here. The old check only
        /// excluded '@' and whitespace, which let a trailing ';' through as "valid" — see
        /// <see cref="NormalizeEmail"/>. Keep in sync with miEmailCharsOk() in MemberImportModal.cshtml.
        /// </summary>
        private const string EmailForbiddenChars = @";,:""<>()\[\]\\";

        /// <summary>
        /// Cleans an email cell before it is used as a dedup key or a login.
        ///
        /// WHY THIS EXISTS: the 2026-08-21 import file quoted the address WITH a trailing
        /// semicolon inside the quotes — "ghaarnes@gmail.com;" — on 141 of 142 rows. The parser
        /// trims whitespace only, so the value kept the ';'. Since the file had no personnummer
        /// column, email was the ONLY dedup key, and `x@y.com;` matches no existing member: every
        /// one of them was missed and 141 duplicate accounts were created, each with an
        /// un-loginable address. Stripping the wrapper punctuation is what makes the key match.
        ///
        /// Deliberately conservative: only LEADING/TRAILING punctuation and wrapping quotes or
        /// angle brackets are removed. A separator left in the MIDDLE ("a@b.se;c@d.se") is not
        /// guessed at — it fails <see cref="IsValidEmail"/> and the row is reported and skipped,
        /// which is safer than silently importing one of two addresses.
        /// Keep in sync with miNormalizeEmail() in MemberImportModal.cshtml.
        /// </summary>
        private static string NormalizeEmail(string? email)
        {
            var value = (email ?? "").Trim();
            if (value.Length == 0) return "";

            // "a@b.se" / 'a@b.se' / <a@b.se> — wrappers some exports add around the cell.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'') ||
                 (value[0] == '<' && value[^1] == '>')))
            {
                value = value.Substring(1, value.Length - 2).Trim();
            }

            // Trailing/leading list separators and stray sentence punctuation.
            value = value.Trim(';', ',', ':', '.', '"', '\'', '<', '>', ' ', '\t');

            return value.Trim();
        }

        /// <summary>
        /// Non-empty and shaped like local@domain.tld, with no whitespace and none of
        /// <see cref="EmailForbiddenChars"/> in either half. Run on the value AFTER
        /// <see cref="NormalizeEmail"/>.
        /// Keep in sync with miIsValidEmail() in MemberImportModal.cshtml (preview-time report).
        /// </summary>
        private static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var part = @"[^@\s" + EmailForbiddenChars + "]+";
            return System.Text.RegularExpressions.Regex.IsMatch(
                email.Trim(), $"^{part}@{part}\\.{part}$");
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
