using System.Globalization;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Dialogen "Medlemskap" (Stefan 2026-11-26): EN yta för en medlems typ, status och tillhörighet (förbund, familj, klubbar).
    /// Öppnas från Medlemmar (klubbadmin) och från Ekonomi → Medlemsavgifter (kassören). Behörighet = klubbadmin ELLER kassör
    /// (<see cref="MembershipAccess"/>); avgifter (belopp, tillägg, tillvalens priser, betald, mejl) rörs aldrig här.
    ///
    /// <para><b>EN sparväg.</b> Allt valideras FÖRST (typ, datum, förbund, familj, klubbar) — ogiltig inmatning sparar ingenting.
    /// Därefter skrivs delarna i en bestämd ordning. De olika lagringarna (medlemsraden, tillvalen, hushållet, medlemmens
    /// egenskaper) delar ingen databastransaktion; ett körtidsfel mitt i säger därför vad som hann sparas.</para>
    /// </summary>
    public partial class ClubMembershipController
    {
        // Orsakerna i dialogen. "Avliden" ger status Avliden, övriga Utträdd. Texten sparas som anledning.
        private static readonly string[] EndReasons = { "Flyttat", "Vill inte vara med längre", "Avliden", "Annat" };

        private static readonly Dictionary<string, string> StandardHelp = new(StringComparer.OrdinalIgnoreCase)
        {
            [MembershipTypeKeys.Senior] = "Vuxen medlem",
            [MembershipTypeKeys.Junior] = "Ung medlem",
            [MembershipTypeKeys.Veteran] = "Äldre medlem",
            [MembershipTypeKeys.Hedersmedlem] = "Betalar ingen avgift",
            [MembershipTypeKeys.Standig] = "Betalar ingen avgift",
            [MembershipTypeKeys.Stodjande] = "Stödjer klubben",
        };

        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör; medlemmen prövas mot klubben")]
        public async Task<IActionResult> GetCard(int memberId, int clubId, int year = 0)
        {
            if (!await _access.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var (member, row, deny) = LoadMemberOfClub(memberId, clubId);
            if (member == null) return Json(new { success = false, message = deny });

            var feeYear = year > 0 ? year : MembershipAgeLimits.CurrentYear;
            var canSeeFees = await _feeAccess.CanWriteAsync(clubId);
            var canEditClubs = await _authService.CanEditMemberAsync(memberId);

            var first = member.GetValue<string>("firstName") ?? "";
            var name = $"{first} {member.GetValue<string>("lastName")}".Trim();
            if (string.IsNullOrEmpty(name)) name = member.Name ?? "";

            // Typer: klubbens aktiva (och den medlemmen har nu, även om den är dold). Familj väljs aldrig här — den följer av hushållet.
            var steps = SafeSteps(clubId);
            var current = row?.MembershipType;
            var types = _types.Types(clubId)
                .Where(t => !MembershipTypeKeys.IsFamilj(t.Key) && (t.IsActive || MembershipTypeKeys.Same(t.Key, current)))
                .Select(t => new
                {
                    key = t.Key,
                    name = t.Name,
                    help = TypeHelp(t.Key, steps)
                }).ToList();

            // Åldersförslaget: bara när klubben har åldersgränser, åldern är känd och typen avviker.
            object? suggestion = null;
            var inHousehold = row != null && !string.IsNullOrWhiteSpace(row.HouseholdId);
            if (steps.Count > 0 && !inHousehold && !MembershipTypeKeys.IsNeverAgeBased(current))
            {
                var age = _ageLimits.AgeFor(memberId, feeYear);
                var want = age.HasValue ? AgeTypeRules.TypeFor(age.Value, steps) : null;
                if (want != null && !MembershipTypeKeys.Same(want, current))
                {
                    var t = _types.Types(clubId).FirstOrDefault(x => MembershipTypeKeys.Same(x.Key, want));
                    if (t != null) suggestion = new { key = t.Key, name = t.Name, age };
                }
            }

            // Status
            var raw = row?.MembershipStatus ?? "Aktiv";
            var status = ClubAffiliation.IsEnded(raw) ? "Slut" : string.Equals(raw, "Vilande", StringComparison.OrdinalIgnoreCase) ? "Vilande" : "Medlem";
            string reason = "", reasonText = "";
            if (status == "Slut")
            {
                if (string.Equals(raw, "Avliden", StringComparison.OrdinalIgnoreCase)) reason = "Avliden";
                else if (EndReasons.Contains(row?.EndReason ?? "", StringComparer.OrdinalIgnoreCase)) reason = EndReasons.First(r => string.Equals(r, row!.EndReason, StringComparison.OrdinalIgnoreCase));
                else { reason = "Annat"; reasonText = row?.EndReason ?? ""; }
            }

            // Förbund = klubbens aktiva tillval. Namn och id, aldrig priser (priserna är kassörens).
            var options = _feeSettings.GetOptions(clubId, includeInactive: false).Select(o => new { id = o.Id, name = o.Name }).ToList();
            var chosen = _feeSettings.GetMemberOptionIds(clubId, memberId);

            // Familj
            var householdMembers = new List<object>();
            string mainName = "";
            if (row != null && inHousehold)
            {
                var hh = _membershipService.GetByHousehold(row.HouseholdId!, clubId);
                if (row.HouseholdPrimary)
                    householdMembers = hh.Where(h => h.MemberId != memberId).Select(h => (object)new { id = h.MemberId, name = MemberName(h.MemberId) }).ToList();
                else
                {
                    var main = hh.FirstOrDefault(h => h.HouseholdPrimary);
                    if (main != null) mainName = MemberName(main.MemberId);
                }
            }
            var candidates = _roster.Active(clubId).Where(r => r.MemberId != memberId)
                .Select(r => new { id = r.MemberId, name = $"{r.FirstName} {r.LastName}".Trim() })
                .OrderBy(r => r.name, StringComparer.Create(new CultureInfo("sv-SE"), true)).ToList();

            // Klubbar (bara den som får ändra personen ser dem)
            object? clubs = null;
            if (canEditClubs)
            {
                int? primary = int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var p) ? p : null;
                var extra = (member.GetValue("memberClubIDs")?.ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();
                clubs = new
                {
                    homeClubId = primary,
                    additionalClubIds = extra,
                    all = _clubService.GetAllClubs().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new { id = c.Id, name = c.Name }).ToList()
                };
            }

            // Avgiften: läget för alla (så varningen kan visas), texten bara för kassören.
            var chargeState = "none"; string? chargeText = null;
            try
            {
                var ch = _feeService.GetChargesForClubYear(clubId, feeYear).FirstOrDefault(c => c.MemberId == memberId);
                if (ch != null)
                {
                    chargeState = ch.PaymentStatus == "Paid" ? "paid" : ch.IsRequestSent ? "sent" : "unsent";
                    if (canSeeFees) chargeText = $"{ch.Amount:0.##} kr" + (chargeState == "paid" ? " (betald)" : chargeState == "sent" ? " (skickad)" : " (inte skickad)");
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Avgiften kunde inte läsas för medlem {MemberId} i klubb {ClubId}", memberId, clubId); }

            return Json(new
            {
                success = true,
                data = new
                {
                    memberId, clubId, name, firstName = first, year = feeYear,
                    canSeeFees, canEditClubs,
                    type = current ?? "", types, suggestion,
                    typeLocked = inHousehold,                       // i ett hushåll styrs typen av hushållet (Familj)
                    status, reason, reasonText, reasons = EndReasons,
                    memberSince = Fmt(row?.MemberSince), memberUntil = Fmt(row?.MemberUntil),
                    options, chosenOptionIds = chosen, legacyFederations = row?.Federations ?? "",
                    household = new
                    {
                        inHousehold,
                        isPrimary = row?.HouseholdPrimary ?? false,
                        mainName,
                        members = householdMembers,
                        candidates
                    },
                    clubs,
                    backgroundCheckApproved = row?.BackgroundCheckApproved ?? false,
                    backgroundCheckDate = Fmt(row?.BackgroundCheckDate),
                    registeredInMap = row?.RegisteredInMap ?? false,
                    memberNotes = row?.MemberNotes ?? "",
                    charge = new { state = chargeState, text = chargeText }
                }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör; medlemmen prövas mot klubben; klubbarna kräver CanEditMemberAsync")]
        public async Task<IActionResult> SaveCard(
            int memberId, int clubId, int year = 0,
            string membershipType = "", string status = "Medlem", string reason = "", string reasonText = "",
            string memberSince = "", string memberUntil = "",
            bool backgroundCheckApproved = false, string backgroundCheckDate = "",
            bool registeredInMap = false, string memberNotes = "",
            int[]? optionIds = null, bool optionsTouched = false,
            int[]? familyMemberIds = null, bool familyTouched = false,
            int? homeClubId = null, string additionalClubIds = "", bool clubsTouched = false)
        {
            if (!await _access.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var (member, existing, deny) = LoadMemberOfClub(memberId, clubId);
            if (member == null) return Json(new { success = false, message = deny });

            var who = $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}".Trim();
            if (string.IsNullOrEmpty(who)) who = member.Name ?? "Medlemmen";
            IActionResult Fail(string msg) => Json(new { success = false, message = $"{who} sparades inte. {msg}" });

            // ── 1. Validera allt, skriv inget ────────────────────────────────────────
            var catalog = _types.Types(clubId);
            string? newType = null;
            var inHousehold = existing != null && !string.IsNullOrWhiteSpace(existing.HouseholdId);
            if (!string.IsNullOrWhiteSpace(membershipType))
            {
                var entry = catalog.FirstOrDefault(t => MembershipTypeKeys.Same(t.Key, membershipType.Trim()));
                if (entry == null) return Fail("Medlemstypen finns inte hos klubben.");
                if (MembershipTypeKeys.IsFamilj(entry.Key) && !inHousehold && !(familyTouched && (familyMemberIds?.Length ?? 0) > 0))
                    return Fail("Familj väljs under Familj, genom att lägga till familjemedlemmar.");
                newType = entry.Key;
            }

            string newStatus; DateTime? until = null; string? endReason = null;
            switch ((status ?? "").Trim())
            {
                case "Medlem": newStatus = "Aktiv"; break;
                case "Vilande": newStatus = "Vilande"; break;
                case "Slut":
                    if (!EndReasons.Contains(reason ?? "")) return Fail("Välj varför medlemskapet har slutat.");
                    newStatus = reason == "Avliden" ? "Avliden" : "Utträdd";
                    endReason = reason == "Annat" ? (string.IsNullOrWhiteSpace(reasonText) ? "Annat" : reasonText.Trim()) : reason;
                    if (endReason.Length > 200) return Fail("Orsaken får vara högst 200 tecken.");
                    break;
                default: return Fail("Välj om medlemskapet är aktivt, vilande eller har slutat.");
            }

            var (since, sinceErr) = ParseIso(memberSince, "Medlem sedan");
            if (sinceErr != null) return Fail(sinceErr);
            if (newStatus is "Utträdd" or "Avliden")
            {
                var (u, uErr) = ParseIso(memberUntil, "Slutade den");
                if (uErr != null) return Fail(uErr);
                until = u ?? DateTime.Today;
            }
            var (bgDate, bgErr) = ParseIso(backgroundCheckDate, "Datum för belastningsregistret");
            if (bgErr != null) return Fail(bgErr);
            if ((memberNotes ?? "").Length > 4000) return Fail("Anteckningarna är för långa.");

            if (optionsTouched)
            {
                var valid = _feeSettings.GetOptions(clubId, includeInactive: false).Select(o => o.Id).ToHashSet();
                if ((optionIds ?? Array.Empty<int>()).Any(id => !valid.Contains(id))) return Fail("Förbundet finns inte hos klubben.");
            }

            var selectedFamily = new List<int>();
            if (familyTouched)
            {
                if (inHousehold && !(existing!.HouseholdPrimary))
                    return Fail("Medlemmen ingår i någon annans familj. Ändra familjen hos huvudmedlemmen.");
                selectedFamily = (familyMemberIds ?? Array.Empty<int>()).Where(i => i != memberId).Distinct().ToList();
                foreach (var id in selectedFamily)
                {
                    var (fm, _, fdeny) = LoadMemberOfClub(id, clubId);
                    if (fm == null) return Fail(fdeny);
                }
            }

            List<int>? newExtra = null; int? newHome = null;
            if (clubsTouched)
            {
                if (!await _authService.CanEditMemberAsync(memberId)) return Fail("Du får inte ändra medlemmens klubbar.");
                var active = _clubService.GetAllClubs().Where(c => c.IsActive).Select(c => c.Id).ToHashSet();
                newHome = homeClubId is > 0 ? homeClubId : null;
                if (newHome.HasValue && !active.Contains(newHome.Value)) return Fail("Hemmaklubben finns inte.");
                newExtra = (additionalClubIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).Distinct().ToList();
                if (newExtra.Any(id => !active.Contains(id))) return Fail("En av klubbarna finns inte.");
                if (newHome != clubId && !newExtra.Contains(clubId))
                    return Fail("Medlemmen måste vara kvar i den här klubben. Välj ”Har slutat” under Medlemskapet om medlemmen ska ut.");
            }

            // ── 2. Skriv ─────────────────────────────────────────────────────────────
            var done = new List<string>();
            try
            {
                var before = (existing?.MembershipType, existing?.MembershipStatus);
                var m = existing ?? new ClubMembership { MemberId = memberId, ClubId = clubId };
                if (!inHousehold) m.MembershipType = newType;            // i ett hushåll styr hushållet typen
                m.MembershipStatus = newStatus;
                m.MemberSince = since;
                m.MemberUntil = until;
                m.EndReason = endReason;
                m.BackgroundCheckApproved = backgroundCheckApproved;
                m.BackgroundCheckDate = bgDate;
                m.RegisteredInMap = registeredInMap;
                m.MemberNotes = NullIfEmpty(memberNotes ?? "");
                _membershipService.Save(m);
                done.Add("medlemskapet");

                var feeChanged = !MembershipTypeKeys.Same(before.Item1, m.MembershipType) || before.Item2 != m.MembershipStatus;

                if (optionsTouched)
                {
                    var err = _feeSettings.SetMemberOptions(clubId, memberId, optionIds ?? Array.Empty<int>());
                    if (err != null) throw new InvalidOperationException(err);
                    done.Add("förbunden");
                    feeChanged = true;
                }
                if (familyTouched && !(inHousehold == false && selectedFamily.Count == 0))
                {
                    ApplyHousehold(_membershipService, memberId, clubId, selectedFamily);
                    done.Add("familjen");
                    feeChanged = true;
                }
                if (clubsTouched)
                {
                    member.SetValue("primaryClubId", newHome);
                    member.SetValue("memberClubIds", string.Join(",", newExtra!.Where(id => id != newHome)));
                    _memberService.Save(member);
                    done.Add("klubbarna");
                }
                SyncAffiliation(member, clubId, belongs: !ClubAffiliation.IsEnded(m.MembershipStatus));

                // Oskickade avgifter räknas om vid nästa skapande. Skickade och betalda rörs aldrig.
                var cleared = 0;
                if (feeChanged)
                    foreach (var y in new[] { MembershipAgeLimits.CurrentYear, MembershipAgeLimits.CurrentYear + 1, year }.Where(y => y > 0).Distinct())
                        cleared += _feeService.ClearUnsentClubCharges(clubId, y);

                var typeName = string.IsNullOrWhiteSpace(m.MembershipType) ? "" : _types.LabelFor(clubId, m.MembershipType);
                return Json(new
                {
                    success = true,
                    summary = new { name = who, typeName, status = m.MembershipStatus, statusText = StatusText(newStatus) },
                    chargesCleared = cleared
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlemskapet kunde inte sparas för medlem {MemberId} i klubb {ClubId}", memberId, clubId);
                var saved = done.Count == 0 ? "Inget hann sparas." : $"Det hann sparas: {string.Join(", ", done)}.";
                return Json(new { success = false, message = $"{who} sparades inte helt. {saved} Öppna dialogen igen och försök på nytt." });
            }
        }

        private List<AgeStep> SafeSteps(int clubId)
        {
            try { return _ageLimits.Steps(clubId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Åldersgränserna kunde inte läsas för klubb {ClubId}", clubId); return new List<AgeStep>(); }
        }

        private static string TypeHelp(string key, IReadOnlyList<AgeStep> steps)
        {
            var parts = new List<string>();
            if (StandardHelp.TryGetValue(key, out var h)) parts.Add(h);
            var g = AgeTypeRules.DescribeGroup(key, steps);
            if (g != null) parts.Add(g);
            return string.Join(" · ", parts);
        }

        private static string StatusText(string status) => status switch
        {
            "Vilande" => "vilande",
            "Utträdd" => "har slutat",
            "Avliden" => "har slutat",
            _ => "medlem"
        };

        private static (DateTime? Value, string? Error) ParseIso(string? s, string label)
        {
            if (string.IsNullOrWhiteSpace(s)) return (null, null);
            return DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? (d, null)
                : (null, $"Datumet för ”{label}” går inte att läsa. Välj ett datum i kalendern.");
        }
    }
}
