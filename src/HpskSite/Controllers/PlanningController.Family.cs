using System;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Familjen (Stefan 2026-10-25, Expertgruppen): en delplanering skapas DIREKT under Översikt (<see cref="CreateSub"/>),
    /// och ett område flyttar mellan huvudplaneringen och en delplanering åt båda hållen (<see cref="MoveAreas"/>). Se
    /// <see cref="PlanningService.CreateSub"/> och <see cref="PlanningService.MoveAreas"/> för vad som händer.
    /// </summary>
    public partial class PlanningController
    {
        /// <summary>
        /// Ny delplanering. <paramref name="areas"/> = huvudplaneringens områden som flyttar med (kommaseparerade id:n).
        /// <paramref name="hostType"/>/<paramref name="hostId"/> = klubb eller krets som sköter den (bara en som någon i
        /// planeringen företräder), annars <paramref name="hostText"/>.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSub(int id, string name, string? discipline, int convenerParticipantId,
            string? targetStart, string? targetEnd, int? hostType, int? hostId, string? hostText, string? areas)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanCreateSub(x.Caller, x.Planning))
                return No(x.Planning.ParentPlanningId > 0
                    ? "En delplanering har inga egna delplaneringar — skapa den i huvudplaneringen."
                    : "Det är den sammankallande som skapar en delplanering.");
            var key = string.IsNullOrWhiteSpace(discipline) ? null : HpskSite.Models.CompetitionTypes.GetById(discipline.Trim())?.Id;
            if (!string.IsNullOrWhiteSpace(discipline) && key == null) return No("Grenen finns inte.");
            if (UnreadableDate(targetStart) || UnreadableDate(targetEnd)) return No(UnreadableDateMessage);
            var (club, region, hostErr) = HostChoice(id, hostType, hostId);
            if (hostErr != null) return No(hostErr);
            var (report, err) = _planning.CreateSub(id, new PlanningService.SubInput(name, key, convenerParticipantId,
                ParseDate(targetStart), ParseDate(targetEnd), club, region, hostText, Ids(areas), x.MemberId));
            if (err != null) return No(err);
            // Den valda sammankallande får veta det (inte den som skapade, och inte huvudplaneringens sammankallande).
            try
            {
                var convener = _planning.Participant(id, convenerParticipantId);
                if (convener?.MemberId > 0 && convener.MemberId != x.MemberId)
                {
                    var pid = _planning.ParticipantForMember(report!.PlanningId, convener.MemberId!.Value)?.Id ?? 0;
                    if (pid > 0) await _notify.InviteAsync(report.PlanningId, pid, x.Caller.Participant, false);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: den sammankallande i delplaneringen kunde inte aviseras.", id); }
            var m = report!.Moved;
            return Ok(new
            {
                success = true, id = report.PlanningId, name = report.Name, url = $"/tavlingsplanering/{report.PlanningId}",
                areas = m?.Areas ?? 0, tasks = m?.Tasks ?? 0, threads = m?.Threads ?? 0, documents = m?.Documents ?? 0,
                detached = m?.Detached ?? 0, joined = m?.Joined ?? new(),
            });
        }

        /// <summary>Vad som flyttar (och vilka som blir med där) om områdena flyttas till <paramref name="targetId"/>. Skriver inget.</summary>
        [HttpGet]
        public async Task<IActionResult> MovePreview(int id, int targetId, string? areas)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanMoveAreas(x.Caller)) return No("Det är den sammankallande som flyttar ett område.");
            var r = _planning.MovePreview(id, targetId, Ids(areas));
            if (r.Error != null) return No(r.Error);
            return Json(new
            {
                success = true, tasks = r.Tasks, openTasks = r.OpenTasks, threads = r.Threads, documents = r.Documents,
                detached = r.Detached, joining = r.Joining,
            });
        }

        /// <summary>
        /// Flyttar områdena till en annan planering i familjen. Den som flyttar är sammankallande här OCH leder målet — vilket
        /// huvudplaneringens sammankallande gör i varje delplanering.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveAreas(int id, int targetId, string? areas)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanMoveAreas(x.Caller)) return No("Det är den sammankallande som flyttar ett område.");
            var target = _planning.Get(targetId);
            if (target == null || !_planning.Family(id).Any(f => f.Id == targetId))
                return No("Områden flyttar bara mellan en huvudplanering och dess delplaneringar.");
            // Leder hen målet? (Huvudplaneringens sammankallande får sin rad i delplaneringen här, om den saknas.)
            var lead = target.ParentPlanningId > 0 ? _planning.EnsureParentOwner(target, x.MemberId, x.Caller.Participant?.DisplayName ?? "") : null;
            lead ??= _planning.ParticipantForMember(targetId, x.MemberId);
            if (lead is not { IsActive: true, Access: PlanningAccessLevel.Owner })
                return No($"Det är den som är sammankallande både här och i {target.Name} som flyttar ett område dit.");
            var (report, err) = _planning.MoveAreas(id, targetId, Ids(areas), x.MemberId);
            if (err != null) return No(err);
            return Ok(new
            {
                success = true, target = report!.TargetName, url = $"/tavlingsplanering/{targetId}",
                areas = report.Areas, tasks = report.Tasks, threads = report.Threads, documents = report.Documents,
                detached = report.Detached, joined = report.Joined,
            });
        }

        /// <summary>Stefan B11: vem som sköter delplaneringen (klubb, krets eller fri text). Den sammankallande.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSubHost(int id, int? hostType, int? hostId, string? hostText)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Det är den sammankallande som väljer vem som sköter delplaneringen.");
            var (club, region, hostErr) = HostChoice(id, hostType, hostId);
            if (hostErr != null) return No(hostErr);
            var err = _planning.SetSubHost(id, club, region, hostText, x.MemberId);
            return err == null ? Ok() : No(err);
        }

        /// <summary>En klubb eller krets som någon i planeringen företräder (samma urval som förslagens styrelser), eller ingen.</summary>
        private (int? Club, int? Region, string? Error) HostChoice(int planningId, int? hostType, int? hostId)
        {
            if (!(hostId > 0)) return (null, null, null);
            if (!BoardTargets(planningId).Any(t => t.OwnerType == hostType && t.OwnerId == hostId))
                return (null, null, "Välj en klubb eller krets som någon i planeringen företräder, eller skriv namnet som text.");
            return hostType == DocumentOwnerType.Club ? (hostId, null, null) : (null, hostId, null);
        }

        private string? HostName(int? club, int? region, string? text) =>
            club > 0 ? OrgName(DocumentOwnerType.Club, club) : region > 0 ? OrgName(DocumentOwnerType.Region, region) : text;

        private const string ParentReaderLabel = "Läser (Kan ändra i huvudplaneringen)";

        /// <summary>Huvudplaneringen, för en delplanering: namnet, om den som frågar kan öppna den, och vem som sköter delen.</summary>
        private object? ParentJson(PlanningRow p, int memberId)
        {
            if (!(p.ParentPlanningId > 0)) return null;
            var parent = _planning.Get(p.ParentPlanningId!.Value);
            if (parent == null) return null;
            var canOpen = memberId > 0 && _planning.ParticipantForMember(parent.Id, memberId) is { IsActive: true };
            return new
            {
                id = parent.Id, name = parent.Name, url = canOpen ? $"/tavlingsplanering/{parent.Id}" : null,
                host = HostName(p.HostClubId, p.HostRegionId, p.HostText),
                hostClubId = p.HostClubId, hostRegionId = p.HostRegionId, hostText = p.HostText,
            };
        }

        /// <summary>
        /// Stefan 2026-10-25 (B3): delplaneringens folk läser huvudplaneringens BESLUT (de som är fattade) — kretsen som sköter
        /// en gren ska veta vad huvudplaneringen bestämt. Bara titel, text och när; inte svaren eller förslagen.
        /// </summary>
        private object ParentDecisionsJson(PlanningRow p)
        {
            if (!(p.ParentPlanningId > 0)) return Array.Empty<object>();
            try
            {
                return _planning.Decisions(p.ParentPlanningId!.Value)
                    .Where(d => d.Status == PlanningDecisionStatus.Decided && d.Outcome == PlanningDecisionOutcome.Approved)
                    .OrderByDescending(d => d.DecidedAt).Take(10)
                    .Select(d => new { id = d.Id, title = d.Title, body = d.DecisionNote ?? d.Body, decidedAt = DT(d.DecidedAt) }).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: huvudplaneringens beslut kunde inte läsas.", p.Id); return Array.Empty<object>(); }
        }

        /// <summary>
        /// Omgång 3 (Stefan 2026-10-24: familjen är veckan): familjens kopplade tävlingar, och vilken av dem planeringens
        /// bemanning gäller. Null när planeringen är ensam (ingen familj).
        /// </summary>
        private object? FamilyJson(PlanningRow p)
        {
            var family = _planning.Family(p.Id);
            if (family.Count <= 1) return null;
            var links = _planning.FamilyLinks(family);
            var comps = links.Select(kv =>
            {
                var comp = _competitions.GetSaved(kv.Value);
                var owner = family.FirstOrDefault(f => f.Id == kv.Key);
                return new
                {
                    planningId = kv.Key, planningName = owner?.Name ?? "", competitionId = kv.Value,
                    name = comp?.DisplayName ?? "Tävlingen finns inte längre", date = D(comp?.CompetitionDate), endDate = D(comp?.CompetitionEndDate),
                    url = comp == null ? "" : _competitionLinks.Url(comp.Id),
                };
            }).OrderBy(x => x.date).ThenBy(x => x.name).ToList();
            // Omgång 3 steg 9: veckoprogrammet — veckans tävlingar (även okopplade delplaneringar) med hur många av
            // funktionärerna som tänker tävla i var och en.
            object? week = null;
            try
            {
                var intents = Intents.ForFamily(p.Id);
                week = Intents.Targets(p.Id).Select(t => new
                {
                    planningId = t.PlanningId, name = t.Name, from = Day(t.From), to = Day(t.To),
                    intents = intents.Count(i => i.TargetPlanningId == t.PlanningId),
                }).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: veckoprogrammet gick inte att läsa.", p.Id); }
            return new
            {
                competitions = comps, week, ownLink = links.ContainsKey(p.Id), staffsFor = p.StaffsForCompetitionId,
                parentCompetitionId = p.ParentPlanningId > 0 && links.TryGetValue(p.ParentPlanningId!.Value, out var pc) ? pc : (int?)null,
                staffingCompetitionId = PlanningFamilyRules.StaffingCompetition(p, links),
                // Flytta ett område: de andra planeringarna i familjen (huvudplaneringen först).
                plannings = family.Where(f => f.Id != p.Id).Select(f => new { id = f.Id, name = f.Name, isParent = !(f.ParentPlanningId > 0) }),
            };
        }

        /// <summary>
        /// Omgång 3 steg 10: vilka av de valbara delarna som visas. En del med innehåll visas alltid; en påslagen del
        /// visas även tom. <c>…On</c> = den sammankallande har slagit på den (Åtgärder kan då slå av den igen).
        /// </summary>
        private object PartsJson(PlanningRow p)
        {
            var (dec, sur) = _planning.PartsWithContent(p.Id);
            var decOn = PlanningParts.Has(p.Parts, PlanningParts.Decisions);
            var surOn = PlanningParts.Has(p.Parts, PlanningParts.Surplus);
            return new
            {
                decisions = dec || decOn, decisionsOn = decOn, decisionsHasContent = dec,
                surplus = sur || surOn, surplusOn = surOn, surplusHasContent = sur,
            };
        }

        /// <summary>Slår på eller av en del (Förslag och beslut, Överskottet). Den som kan ändra planeringen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPart(int id, string part, string? on)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det väljs av den som kan ändra planeringen.");
            var err = _planning.SetPart(id, part ?? "", on == "1");
            return err == null ? Ok() : No(err);
        }

        /// <summary>Omgång 3: "Bemannar för" — vilken av familjens tävlingar planeringens bemanning gäller (0 = huvudplaneringens).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStaffsFor(int id, int? competitionId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Det väljs av den sammankallande.");
            var before = _planning.StaffingCompetitionFor(id);
            var err = _planning.SetStaffsFor(id, competitionId, x.MemberId);
            if (err != null) return No(err);
            var after = _planning.StaffingCompetitionFor(id);
            // Speglingarna (tävlingsansvariga, stationschefer) för både den gamla och den nya tävlingen.
            foreach (var cid in new[] { before, after }.Where(v => v > 0).Distinct())
            {
                try { _staffing.SyncCompetitionMirrors(cid!.Value); }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: speglingen till tävling {Comp} misslyckades.", id, cid); }
            }
            return Ok(new { success = true, staffingCompetitionId = after });
        }

        /// <summary>Delplaneringarnas läge, för huvudplaneringens Översikt. Länken bara för den som kan läsa delplaneringen.</summary>
        private object SubPlanningsJson(PlanningRow p, PlanningCaller c, int memberId)
        {
            if (p.ParentPlanningId > 0 || !c.IsParticipant) return Array.Empty<object>();
            // Sammankallande och Kan ändra läser delplaneringarna (PlanningFamily) — utom en rad som bara kom via tävlingen.
            var reads = PlanningFamily.ReadsSubViaParent(c.Participant) && !c.ViaLink;
            var names = HpskSite.Models.CompetitionTypes.All.ToDictionary(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);
            return _planning.SubPlannings(p.Id, c.ViaLink ? 0 : memberId).Select(s => new
            {
                id = s.Id, name = s.Name, status = s.Status, conveners = s.Conveners, openTasks = s.OpenTasks, tasks = s.Tasks,
                overdue = s.OverdueTasks, nextMilestone = s.NextMilestoneTitle, nextMilestoneDue = D(s.NextMilestoneDue),
                nextMeeting = s.NextMeetingTitle, nextMeetingAt = DT(s.NextMeetingAt), changedAt = DT(s.ChangedAt),
                discipline = s.DisciplineKey != null && names.TryGetValue(s.DisciplineKey, out var dn) ? dn : null,
                host = HostName(s.HostClubId, s.HostRegionId, s.HostText),
                url = reads || s.MemberIsParticipant ? $"/tavlingsplanering/{s.Id}" : null,
            }).ToList();
        }
    }
}
