using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Planera dagen steg 3b: <b>Bygg startlistan runt funktionärerna</b> (fält). Förhandsvisningen skriver ingenting; tillämpningen tar en backup, skriver
    /// i EN transaktion och kan ångras. Rätten: den som får ändra bemanningen <i>och</i> har tävlingens personalbehörighet (startlistans ägare) — ingen av dem ensam.
    /// </summary>
    public partial class StaffingController
    {
        private async Task<StaffOwner?> PatrolOwnerAsync(int competitionId)
        {
            // Inloggad först (authn före authz), sedan tävlingens personalbehörighet — samma som de gamla knapparna (skjutledare, roster-åtkomst, krets-värd).
            // Kräver ingen bemanningsplanering: en liten klubbtävling utan planering får också göra sin patrullista.
            if (competitionId <= 0 || await ResolveViewerAsync() == null) return null;
            if (!await HasCompetitionAccessAsync(competitionId)) return null;
            return _owners.ForCompetition(competitionId) ?? new StaffOwner(0, competitionId);
        }

        /// <summary>Den avancerade generatorns inställningar ur anropet; negativa värden = förval.</summary>
        private static HpskSite.CompetitionTypes.Faltskytte.Services.FaltPlanOptions EngineOptions(double minRest, double wishRest, string? close, int prio, string? order, string? champ, int glideEvery, int glideMin, double roundExtra, CancellationToken ct, int iters = -1, double roundMin = -1, EngineExtra? x = null)
        {
            double? closeMin = null;
            if (TimeSpan.TryParse(close, System.Globalization.CultureInfo.InvariantCulture, out var c)) closeMin = c.TotalMinutes;
            return new HpskSite.CompetitionTypes.Faltskytte.Services.FaltPlanOptions
            {
                MinRest = minRest >= 0 && minRest <= 600 ? minRest : null, WishRest = wishRest >= 0 && wishRest <= 600 ? wishRest : null, CloseMinutes = closeMin,
                Priority = Math.Clamp(prio, 0, 2), Order = order is "auto" or "sep" or "ar" or "cbar" or "all" ? order : null, Champion = champ is "C" or "B" or "A" or "R" ? champ : null,
                GlideEvery = Math.Clamp(glideEvery, 0, 100), GlideMinutes = Math.Clamp(glideMin, 0, 60), RoundExtraSeconds = Math.Clamp(roundExtra, 0, 600), RoundMinutes = roundMin >= 10 && roundMin <= 600 ? roundMin : null,
                PauseBlockMin = x != null && x.PauseBlock >= 0 && x.PauseBlock <= 600 ? x.PauseBlock : null, PauseExtMin = x != null && x.PauseExt >= 0 && x.PauseExt <= 600 ? x.PauseExt : null,
                Day2 = x?.Day2 ?? false, Open2Min = Clock(x?.Open2), Close2Min = Clock(x?.Close2), DayBreak = Math.Clamp(x?.DayBreak ?? 1, 1, 3), ArTogether = x?.ArTogether ?? true, PreCount = Math.Clamp(x?.Pre ?? 0, 0, 10), PreStartMin = Clock(x?.PreStart), UseExistingGroups = false, Iterations = iters >= 0 ? iters : null, Ct = ct,
            };
        }

        /// <summary>Extra inställningar för den avancerade generatorn (paus mellan block, två dagar, A+R, förstart).</summary>
        private sealed record EngineExtra(double PauseBlock, double PauseExt, bool Day2, string? Open2, string? Close2, int DayBreak, bool ArTogether, int Pre, string? PreStart);
        private static double? Clock(string? hhmm) => TimeSpan.TryParse(hhmm, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t.TotalMinutes : null;

        private static string Hm(DateTime? t) => t?.ToString("HH\\:mm") ?? "";

        /// <summary>Räknar ut en ny startlista och visar vad som skulle ändras. Ingenting sparas.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPatrolPlan(int competitionId, int patrolSize = 0, int intervalMinutes = 0, string? firstStart = null, string? grouping = null, int gap = 0, bool keep = true,
            bool engine = false, double minRest = -1, double wishRest = -1, string? close = null, int prio = 1, string? order = null, string? champ = null, int glideEvery = 0, int glideMin = 0, double roundExtra = 60, int iters = -1, double roundMin = -1,
            double pauseBlock = -1, double pauseExt = -1, bool day2 = false, string? open2 = null, string? close2 = null, int dayBreak = 1, bool arTogether = true, int pre = 0, string? preStart = null,
            string? scenario = null)
        {
            // Övning: ett scenario (påhittade skyttar) i stället för en tävling. Samma endpoint, samma motor, samma svar — bara underlaget kommer från ScenarioBuilder i stället
            // för databasen. Ingen tävling, ingen skrivväg: Tillämpa/Ångra/lås kräver fortfarande ett tävlings-id och tävlingens personalbehörighet.
            StaffOwner? owner = null; PatrolPlanService.PatrolBasis? practice = null; IDisposable? lease = null; string? scenarioHash = null;
            if (scenario != null)
            {
                var viewer = await ResolveViewerAsync();
                if (viewer == null) return Json(new { success = false, message = "Logga in för att öva." });
                var sc = ScenarioBuilder.Parse(scenario);
                var bad = sc == null ? "Scenariot kunde inte läsas." : ScenarioBuilder.Validate(sc);
                if (bad != null) return Json(new { success = false, message = bad });
                lease = HttpContext.RequestServices.GetRequiredService<PatrolPracticeThrottle>().TryEnter(viewer.Id, DateTime.UtcNow, out var why);
                if (lease == null) return Json(new { success = false, message = why });
                practice = ScenarioBuilder.Build(sc!); scenarioHash = ScenarioBuilder.Hash(sc!);
            }
            else
            {
                owner = await PatrolOwnerAsync(competitionId);
                if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            }
            try
            {
                var svc = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>();
                var first = DateTime.TryParse(firstStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f) ? f : (DateTime?)null;
                var settings = practice != null
                    ? new PatrolPlanSettings
                    {
                        PatrolSize = patrolSize is >= 1 and <= 12 ? patrolSize : 6, IntervalMinutes = intervalMinutes is >= 1 and <= 120 ? intervalMinutes : 15,
                        FirstStart = first ?? (practice.Date ?? DateTime.Today).Date.AddHours(9), WeaponGrouping = grouping is "MixAll" or "CombineAR" ? grouping : "Separate",
                        MultiClassGapMinutes = gap is > 0 and <= 600 ? gap : 0, KeepFitting = keep,
                    }
                    : svc.Normalize(owner!.Comp, patrolSize, intervalMinutes, first, grouping, gap, keep);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); cts.CancelAfter(TimeSpan.FromSeconds(10));   // tidsgräns: aldrig en delvis lista
                if (practice != null) iters = iters < 0 || iters > 60000 ? 60000 : iters;   // övningen är öppen för medlemmar: draget klampas
                var options = engine || practice != null ? EngineOptions(minRest, wishRest, close, prio, order, champ, glideEvery, glideMin, roundExtra, cts.Token, iters, roundMin, new EngineExtra(pauseBlock, pauseExt, day2, open2, close2, dayBreak, arTogether, pre, preStart)) : null;
                var pv = practice != null ? PatrolPlanService.PreviewOf(practice, settings, options) : await svc.PreviewAsync(owner!, settings, options);
                if (pv.BlockedReason != null && pv.Plan.Patrols.Count == 0 && pv.Fingerprint == "")
                    return Json(new { success = false, message = pv.BlockedReason });
                var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
                return Json(new
                {
                    success = true, fingerprint = pv.Fingerprint, blocked = pv.BlockedReason,
                    settings = new { patrolSize = settings.PatrolSize, intervalMinutes = settings.IntervalMinutes, firstStart = settings.FirstStart?.ToString("yyyy-MM-ddTHH:mm"), grouping = settings.WeaponGrouping, gap = settings.MultiClassGapMinutes, keep = settings.KeepFitting },
                    keptFitting = pv.KeptFitting,
                    existing = new { patrols = pv.ExistingPatrols, shooters = pv.ExistingShooters },
                    change = new { moved = pv.Moved, unchanged = pv.Unchanged, added = pv.Added, removed = pv.Removed },
                    patrols = pv.Plan.Patrols.Select(p => new
                    {
                        number = p.PatrolNumber, time = Hm(p.StartTime), group = p.WeaponGroup,
                        members = p.Members.Select(m => new { memberId = m.MemberId, name = m.Name, club = m.Club, shootingClass = m.ShootingClass }),
                    }),
                    unavoidable = pv.Plan.Unavoidable.Select(u => new { memberId = u.MemberId, name = u.Name, time = Hm(u.StartTime) }),
                    current = (pv.Current ?? new()).Select(c => new { patrolMemberId = c.PatrolMemberId, memberId = c.MemberId, name = c.Name, shootingClass = c.ShootingClass, number = c.PatrolNumber, time = Hm(c.StartTime), locked = c.Locked }),
                    moves = (pv.Moves ?? new()).Select(m => new { kind = m.Kind, memberId = m.MemberId, name = m.Name, shootingClass = m.ShootingClass, fromPatrol = m.FromPatrol, fromTime = Hm(m.FromTime), toPatrol = m.ToPatrol, toTime = Hm(m.ToTime), why = m.Why }),
                    published = pv.BlockedReason != null,
                    groups = (pv.Groups ?? new()).Select(g => new { label = g.Label, firstStart = g.FirstStart.ToString("HH\\:mm"), interval = g.IntervalMinutes, patrolSize = g.PatrolSize, patrols = g.PatrolCount }),
                    ungrouped = pv.Plan.Ungrouped,
                    metrics = pv.Plan.Metrics,
                    timeline = pv.Plan.Timeline,
                    reservesLeftOut = pv.Plan.ReservesLeftOut,
                    lockedKept = pv.Plan.LockedKept, lockedDropped = pv.Plan.LockedDropped, lockedOverflow = pv.Plan.LockedOverflow,
                    undoable = owner != null ? (int?)svc.LatestUndoable(owner) : null,
                    practice = practice != null,
                });
            }
            catch (OperationCanceledException) when (practice != null)
            {
                return Json(new { success = false, message = "Scenariot var för stort för att räknas inom tidsgränsen. Minska antalet skyttar." });
            }
            catch (Exception ex)
            {
                if (practice != null) _logger.LogError(ex, "Staffing: GetPatrolPlan failed for practice scenario {Hash}", scenarioHash);
                else _logger.LogError(ex, "Staffing: GetPatrolPlan failed for competition {Comp}", owner!.Comp);
                return Json(new { success = false, message = "Kunde inte räkna ut en startlista." });
            }
            finally { lease?.Dispose(); }
        }

        /// <summary>Bygger om startlistan. Fingeravtrycket från förhandsvisningen måste stämma; en backup tas först.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyPatrolPlan([FromBody] ApplyPatrolPlanRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await PatrolOwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var svc = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>();
                var first = DateTime.TryParse(request.FirstStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f) ? f : (DateTime?)null;
                var settings = svc.Normalize(owner.Comp, request.PatrolSize, request.IntervalMinutes, first, request.Grouping, request.Gap, request.Keep);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); cts.CancelAfter(TimeSpan.FromSeconds(10));
                var options = request.Engine ? EngineOptions(request.MinRest, request.WishRest, request.Close, request.Prio, request.Order, request.Champ, request.GlideEvery, request.GlideMin, request.RoundExtra, cts.Token, -1, request.RoundMin, new EngineExtra(request.PauseBlock, request.PauseExt, request.Day2, request.Open2, request.Close2, request.DayBreak, request.ArTogether, request.Pre, request.PreStart)) : null;
                var o = await svc.ApplyAsync(owner, settings, request.Fingerprint, request.Force, viewer.Id, options);
                return Json(new { success = o.Ok, message = o.Message, backupId = o.BackupId, patrols = o.Patrols, shooters = o.Shooters });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ApplyPatrolPlan failed for competition {Comp}", owner.Comp);
                return Json(new { success = false, message = "Kunde inte bygga om startlistan. Inget ändrades." });
            }
        }

        /// <summary>Återställer startlistan som den var före en ombyggnad. Vägras om någon ändrat den sedan (om inte <c>Force</c>).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UndoPatrolPlan([FromBody] UndoPatrolPlanRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.BackupId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await PatrolOwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var (ok, msg) = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>().Undo(owner, request.BackupId, request.Force);
                return Json(new { success = ok, message = msg });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: UndoPatrolPlan failed for competition {Comp}", owner.Comp);
                return Json(new { success = false, message = "Kunde inte återställa. Inget ändrades." });
            }
        }

        /// <summary>Låser eller låser upp en skytts plats i startlistan. Loggas med vem som gjorde det.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPatrolLock([FromBody] SetPatrolLockRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.PatrolMemberId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await PatrolOwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            var n = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>().SetLocked(owner, request.PatrolMemberId, request.Locked);
            if (n == 0) return Json(new { success = false, message = "Platsen finns inte i den här tävlingen." });
            _logger.LogInformation("Startlistan: medlem {By} {Action} plats {PatrolMember} i tävling {Comp}", viewer.Id, request.Locked ? "låste" : "låste upp", request.PatrolMemberId, owner.Comp);
            return Json(new { success = true });
        }
    }

    public class ApplyPatrolPlanRequest
    {
        public int CompetitionId { get; set; }
        public string? Fingerprint { get; set; }
        public int PatrolSize { get; set; }
        public int IntervalMinutes { get; set; }
        public string? FirstStart { get; set; }
        public string? Grouping { get; set; }
        public int Gap { get; set; }
        /// <summary>Behåll de som redan har en start som passar.</summary>
        public bool Keep { get; set; } = true;
        /// <summary>Bygg om även när startlistan är publicerad eller en patrull gått ut.</summary>
        public bool Force { get; set; }
        /// <summary>Den avancerade generatorn (nya motorn) i stället för den enkla.</summary>
        public bool Engine { get; set; }
        public double MinRest { get; set; } = -1;
        public double WishRest { get; set; } = -1;
        public string? Close { get; set; }
        public int Prio { get; set; } = 1;
        public string? Order { get; set; }
        public string? Champ { get; set; }
        public int GlideEvery { get; set; }
        public int GlideMin { get; set; }
        public double RoundExtra { get; set; } = 60;
        /// <summary>Rundtid per patrull i minuter; under 10 = uppskattningen (10 min per station).</summary>
        public double RoundMin { get; set; } = -1;
        public double PauseBlock { get; set; } = -1;
        public double PauseExt { get; set; } = -1;
        public bool Day2 { get; set; }
        public string? Open2 { get; set; }
        public string? Close2 { get; set; }
        public int DayBreak { get; set; } = 1;
        public bool ArTogether { get; set; } = true;
        public int Pre { get; set; }
        public string? PreStart { get; set; }
    }

    public class UndoPatrolPlanRequest
    {
        public int CompetitionId { get; set; }
        public int BackupId { get; set; }
        public bool Force { get; set; }
    }

    public class SetPatrolLockRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolMemberId { get; set; }
        public bool Locked { get; set; }
    }
}
