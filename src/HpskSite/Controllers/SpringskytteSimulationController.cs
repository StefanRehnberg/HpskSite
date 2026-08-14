using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using Newtonsoft.Json;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Springskytte.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// THROWAWAY simulation seeder. Fills a HIDDEN Springskytte practice competition with dummy
    /// Ankeborg shooters (Kalle Anka &amp; co) so organizers can rehearse the SM flow — check-in,
    /// payment at the desk, shot-counting and timekeeping — before the real Swedish Championship.
    ///
    /// Site-admin only. Registrations on pistol.nu are Umbraco content nodes (not SQL rows), so the
    /// only correct way to bulk-create them is through the content service, exactly as
    /// RegistrationAdminController.AddLateRegistration does — mirrored here.
    ///
    /// Realistic profile (revised 2026-07-12 after organizer feedback):
    ///   - Most shooters register in BOTH weapon groups C and A (few shoot only one).
    ///   - ~half are police entering the "Polis SM" deltävling — which is CLASS C ONLY. A police
    ///     shooter gets a separate C registration node flagged isSubCompetition=true, and (usually)
    ///     a separate A node that is NOT flagged.
    ///   - Age/gender classes follow a weighted spread (adult/veteran-heavy, mostly men), not a
    ///     flat round-robin. Tune the weight tables below to match real SM registrations.
    ///
    /// KNOWN LIMITATION surfaced by this data: the Springskytte deltävling filter
    /// (SpringskytteController.GetSpringskytteResults / CalculateSpringskytteSubFinalResults) selects
    /// the sub-competition subset BY MEMBER — every result of a member with any isSubCompetition
    /// registration is included, both C and A. So a police shooter's class-A result will also show
    /// up in the "Polis SM" list even though Polis SM is C-only. If that's wrong for the real event,
    /// it's a code fix (restrict the sub subset to weapon class C), not a seeder change.
    ///
    /// SAFETY: refuses to run unless the target is a Springskytte competition that is either
    /// isClubOnly=true OR clearly named as practice (ÖVNING/TEST/SIM). It can never dump dummies
    /// into the real, public SM competition. Safe to delete this whole file after the SM.
    ///
    /// Usage (open in browser while logged in as site admin):
    ///   /umbraco/surface/SpringskytteSimulation/SeedSimulation?competitionId=1234&amp;count=60
    ///   /umbraco/surface/SpringskytteSimulation/ClearSimulationRegistrations?competitionId=1234
    /// </summary>
    public class SpringskytteSimulationController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _authService;
        private readonly ClubService _clubService;
        private readonly PaymentService _paymentService;
        // Results live in SQL (SpringskytteResultEntry), unlike registrations which are content nodes,
        // so the results seeder needs the database factory the base class doesn't expose.
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public SpringskytteSimulationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IContentService contentService,
            AdminAuthorizationService authService,
            ClubService clubService,
            PaymentService paymentService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _contentService = contentService;
            _authService = authService;
            _clubService = clubService;
            _paymentService = paymentService;
            _databaseFactory = databaseFactory;
        }

        // Obviously-fake Kalle Anka (Ankeborg) universe names. Combined with the surnames below +
        // a per-index email, so a seeder re-run reuses the same dummy members instead of duplicating.
        private static readonly string[] FirstNames =
        {
            "Kalle", "Kajsa", "Knatte", "Fnatte", "Tjatte", "Joakim", "Alexander", "Ludwig",
            "Georg", "Mimmi", "Musse", "Långben", "Klasse", "Klarabella", "Petter", "Magica",
            "Ivar", "Jocke", "Anki", "Rune", "Sigge", "Turbo", "Ludde", "Stampe"
        };
        private static readonly string[] LastNames =
        {
            "Anka", "von Anka", "Pigg", "Kanin", "Ko", "Räv", "Björn", "Mus"
        };

        // ── Demographic model (TUNE HERE to match real SM registrations) ─────────────────────
        // Age codes must match the competition's configured Springskytte classes (jun/21/35/…).
        // Only ages actually present on the competition are used; the rest are ignored.
        // Calibrated to organizer input (2026-07-12): bulk in age 30–65, and the 65+ tail
        // (65+70) heavier than the under-30 tail (jun+21).
        private static readonly (string Age, int Weight)[] CivilianAgeWeights =
        {
            ("jun", 3), ("21", 8), ("35", 20), ("50", 22), ("60", 18), ("65", 15), ("70", 9)
        };
        // Police (Polis SM) skew working-age — few veterans/juniors.
        private static readonly (string Age, int Weight)[] PoliceAgeWeights =
        {
            ("jun", 1), ("21", 18), ("35", 30), ("50", 30), ("60", 14), ("65", 5), ("70", 2)
        };
        // ~70% men overall at a 35% police mix: 0.35·0.80 + 0.65·0.65 ≈ 0.70. Rest = women (Dam).
        private const double CivilianMaleRatio = 0.65;
        private const double PoliceMaleRatio   = 0.80;
        private const double CivBothRatio      = 0.65;   // civilian shoots both C + A
        private const double CivConlyRatio     = 0.20;   // civilian shoots C only (rest = A only)
        private const double PoliceAlsoARatio  = 0.70;   // police C-shooter who ALSO shoots A

        private const string SeedTag = "Simulering (seed)";

        /// <summary>
        /// Seed the practice competition with dummy shooters + Pending/Paid invoices.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SeedSimulation(
            int competitionId, int count = 60, double paidRatio = 0.6, double policeRatio = 0.35, string club = "Ankeborg")
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast webbplatsadministratör kan köra simuleringen." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition")
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            // ── SAFETY GUARD ─────────────────────────────────────────────────────────────
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (!string.Equals(compType, "Springskytte", StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = $"Tävlingen är inte Springskytte (competitionType = '{compType}')." });

            var name = competition.GetValue<string>("competitionName");
            if (string.IsNullOrWhiteSpace(name)) name = competition.Name ?? "";
            var isClubOnly = competition.GetValue<bool>("isClubOnly");
            if (!isClubOnly && !LooksLikePractice(name))
                return Json(new
                {
                    success = false,
                    message = "Säkerhetsspärr: vägrar seeda en publik tävling. Sätt isClubOnly=true "
                            + "ELLER ha ÖVNING/TEST/SIM i tävlingsnamnet innan du kör."
                });

            // Springskytte classes are stored as composite "A-D 21","C-H 35" strings in shootingClassIds.
            var composites = ParseCompositeClasses(competition.GetValue<string>("shootingClassIds") ?? "");
            if (composites.Count == 0)
                return Json(new { success = false, message = "Tävlingen har inga Springskytte-klasser (shootingClassIds tomt). Konfigurera klasser först." });

            // Split available composites into per-weapon sets of ageGender strings ("D 21", "H 35", …).
            var cClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in composites)
            {
                var idx = c.IndexOf('-');
                if (idx <= 0 || idx >= c.Length - 1) continue;
                var weapon = c.Substring(0, idx).Trim();
                var ageGender = c.Substring(idx + 1).Trim();
                if (weapon.StartsWith("C", StringComparison.OrdinalIgnoreCase)) cClasses.Add(ageGender);
                else if (weapon.StartsWith("A", StringComparison.OrdinalIgnoreCase)) aClasses.Add(ageGender);
            }
            var unionClasses = new HashSet<string>(cClasses.Concat(aClasses), StringComparer.OrdinalIgnoreCase);
            if (unionClasses.Count == 0)
                return Json(new { success = false, message = "Kunde inte tolka några vapenklasser (A/C) ur klasslistan." });

            var clubInfo = _clubService.GetAllClubs()
                .FirstOrDefault(c => c.Name.Contains(club, StringComparison.OrdinalIgnoreCase));
            if (clubInfo == null)
                return Json(new { success = false, message = $"Hittade ingen klubb som matchar '{club}'. Skapa klubben först." });

            var subName = competition.GetValue<string>("subCompetitionName");

            count = Math.Clamp(count, 1, 300);
            paidRatio = Math.Clamp(paidRatio, 0d, 1d);
            policeRatio = Math.Clamp(policeRatio, 0d, 1d);

            var hub = GetOrCreateRegistrationsHub(competition);
            var existing = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .ToList();
            var alreadyRegistered = new HashSet<int>(existing.Select(r => r.GetValue<int>("memberId")));

            int membersCreated = 0, registeredShooters = 0, skipped = 0;
            int shootersInC = 0, shootersInA = 0, shootersInBoth = 0, polisSmParticipants = 0;
            int nodesCreated = 0, paidShooters = 0;

            // Deterministic spread seeded on the comp id — reproducible re-runs, no Math.Random pitfalls.
            var rnd = new Random(competitionId);

            for (int i = 1; i <= count; i++)
            {
                var email = $"ovningsskytt-{i:D3}@ankeborg.invalid";
                var member = _memberService.GetByEmail(email);
                if (member == null)
                {
                    var (first, last) = DummyName(i);
                    var fullName = $"{first} {last}";
                    member = _memberService.CreateMember(email, email, fullName, "hpskMember");
                    member.SetValue("firstName", first);
                    member.SetValue("lastName", last);
                    member.SetValue("primaryClubId", clubInfo.Id);
                    member.IsApproved = true;
                    _memberService.Save(member);
                    _memberService.AssignRoles(new[] { member.Id }, new[] { "Users" });
                    membersCreated++;
                }

                if (alreadyRegistered.Contains(member.Id)) { skipped++; continue; }

                bool isPolice = rnd.NextDouble() < policeRatio;
                string gender = rnd.NextDouble() < (isPolice ? PoliceMaleRatio : CivilianMaleRatio) ? "H" : "D";
                var ageWeights = isPolice ? PoliceAgeWeights : CivilianAgeWeights;
                string ageGender = PickAgeGender(rnd, gender, ageWeights, unionClasses);

                bool hasC = cClasses.Contains(ageGender);
                bool hasA = aClasses.Contains(ageGender);

                // Build the registration node(s): (composites, isSubCompetition).
                var plannedNodes = new List<(List<string> Comps, bool IsSub)>();

                if (isPolice)
                {
                    // Polis SM = class C only → its own flagged node. Most also shoot A (separate, unflagged).
                    if (hasC) plannedNodes.Add((new List<string> { $"C-{ageGender}" }, true));
                    if (hasA && rnd.NextDouble() < PoliceAlsoARatio)
                        plannedNodes.Add((new List<string> { $"A-{ageGender}" }, false));
                    // Edge: this ageGender has no C class → fall back to an unflagged A registration.
                    if (plannedNodes.Count == 0 && hasA)
                        plannedNodes.Add((new List<string> { $"A-{ageGender}" }, false));
                }
                else
                {
                    double roll = rnd.NextDouble();
                    var comps = new List<string>();
                    if (roll < CivBothRatio)                       // both C + A (one node)
                    {
                        if (hasC) comps.Add($"C-{ageGender}");
                        if (hasA) comps.Add($"A-{ageGender}");
                    }
                    else if (roll < CivBothRatio + CivConlyRatio)  // C only (fall back to A if no C)
                    {
                        if (hasC) comps.Add($"C-{ageGender}"); else if (hasA) comps.Add($"A-{ageGender}");
                    }
                    else                                            // A only (fall back to C if no A)
                    {
                        if (hasA) comps.Add($"A-{ageGender}"); else if (hasC) comps.Add($"C-{ageGender}");
                    }
                    if (comps.Count > 0) plannedNodes.Add((comps, false));
                }

                if (plannedNodes.Count == 0) { skipped++; continue; }  // no valid class for this shooter

                var createdRegIds = new List<int>();
                foreach (var (comps, isSub) in plannedNodes)
                {
                    var reg = _contentService.Create($"{member.Name} - övning", hub.Id, "competitionRegistration");
                    reg.SetValue("competitionId", competitionId);
                    reg.SetValue("memberId", member.Id);
                    reg.SetValue("memberName", member.Name);
                    reg.SetValue("clubId", clubInfo.Id);
                    var entries = comps
                        .Select(c => new HpskSite.Models.ShootingClassEntry { Class = c, StartPreference = "Inget", TeamNumber = null })
                        .ToList();
                    reg.SetValue("shootingClasses",
                        HpskSite.Models.CompetitionRegistrationDocument.SerializeShootingClasses(entries));
                    reg.SetValue("registrationDate", DateTime.Now);
                    reg.SetValue("registeredBy", SeedTag);
                    reg.SetValue("isActive", true);
                    if (reg.HasProperty("isSubCompetition"))
                        reg.SetValue("isSubCompetition", isSub);
                    _contentService.Save(reg);
                    _contentService.Publish(reg, new[] { "*" }, -1);
                    createdRegIds.Add(reg.Id);
                    nodesCreated++;

                    // Eager Pending invoice — exactly like a real registration.
                    await _paymentService.EnsureRegistrationInvoiceAsync(competitionId, reg.Id);
                }

                bool inC = plannedNodes.Any(n => n.Comps.Any(c => c.StartsWith("C", StringComparison.OrdinalIgnoreCase)));
                bool inA = plannedNodes.Any(n => n.Comps.Any(c => c.StartsWith("A", StringComparison.OrdinalIgnoreCase)));
                if (inC) shootersInC++;
                if (inA) shootersInA++;
                if (inC && inA) shootersInBoth++;
                if (plannedNodes.Any(n => n.IsSub)) polisSmParticipants++;

                registeredShooters++;
                alreadyRegistered.Add(member.Id);

                // Mark the whole shooter (all their invoices) Paid or leave Pending — so the desk
                // practices both "already paid → check in" and "not paid → take payment".
                if (rnd.NextDouble() < paidRatio)
                {
                    foreach (var rid in createdRegIds) TryMarkInvoicePaid(rid);
                    paidShooters++;
                }
            }

            return Json(new
            {
                success = true,
                message = $"Simulering seedad på \"{name}\".",
                competitionId,
                club = clubInfo.Name,
                subCompetitionName = string.IsNullOrWhiteSpace(subName) ? null : subName,
                subCompetitionWarning = string.IsNullOrWhiteSpace(subName)
                    ? "OBS: tävlingen saknar 'subCompetitionName'. Anmälningarna flaggas som Polis SM (isSubCompetition), "
                    + "men ingen deltävlingslista visas förrän du satt deltävlingens namn (t.ex. \"Polis SM\") på tävlingen."
                    : null,
                membersCreated,
                shooters = registeredShooters,
                skippedAlreadyRegistered = skipped,
                shootersInClassC = shootersInC,
                shootersInClassA = shootersInA,
                shootersInBoth = shootersInBoth,
                polisSmParticipants,
                registrationNodes = nodesCreated,
                paidShooters,
                unpaidShooters = registeredShooters - paidShooters,
                classesAvailable = composites,
                note = "Ingen är incheckad (avsiktligt — disken övar incheckning). Polis SM = klass C: "
                     + "polisernas C-anmälan är flaggad som deltävling, deras ev. A-anmälan är det inte. "
                     + "OBS: deltävlingsfiltret är medlemsbaserat, så en polisskytts A-resultat följer med i "
                     + "Polis SM-listan — vill ni ha Polis SM strikt C-only är det en kodändring. "
                     + "Generera startlistor per vapengrupp (C dag 1, A dag 2) i tävlingsadmin."
            });
        }

        /// <summary>
        /// Remove all seeded dummy registrations (and their invoices) for a practice competition,
        /// so a fresh seed can be run. Dummy members in the test club are kept for reuse.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ClearSimulationRegistrations(int competitionId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast webbplatsadministratör." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition")
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var isClubOnly = competition.GetValue<bool>("isClubOnly");
            var name = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";
            if (!isClubOnly && !LooksLikePractice(name))
                return Json(new { success = false, message = "Säkerhetsspärr: vägrar rensa en publik tävling." });

            var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null)
                return Json(new { success = true, message = "Inga anmälningar att rensa.", removed = 0 });

            var regs = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration"
                         && string.Equals(c.GetValue<string>("registeredBy"), SeedTag, StringComparison.Ordinal))
                .ToList();

            int removed = 0, invoicesRemoved = 0;
            foreach (var reg in regs)
            {
                var invoiceId = reg.GetValue<int>("invoiceId");
                if (invoiceId > 0)
                {
                    var invoice = _contentService.GetById(invoiceId);
                    if (invoice != null && invoice.ContentType.Alias == "registrationInvoice")
                    {
                        _contentService.Delete(invoice);
                        invoicesRemoved++;
                    }
                }
                _contentService.Delete(reg);
                removed++;
            }

            return Json(new { success = true, message = "Seedade anmälningar rensade.", removed, invoicesRemoved });
        }

        /// <summary>
        /// Fill a practice competition with plausible RESULTS, so the /live board has a real field to
        /// page through. Seeds one SpringskytteResultEntry per (member, weapon class) registration.
        ///
        /// Rows are written through the SAME scoring service the real save path uses
        /// (CalculateShootingScore + CalculateTotalTime), and with the same shot tokens, so they are
        /// indistinguishable from hand-entered results rather than merely looking right on screen.
        ///
        /// Deliberately does NOT give everyone a finishing time: `runningRatio` are left with no result
        /// row at all (still out on the course) and a few get DNS/DNF. That is what exercises the board's
        /// finishers-only ranking, the "Nyss startat" band, and the DNF-at-the-bottom rule — a field where
        /// everybody has finished tests none of it.
        ///
        /// Idempotent: MERGE on (CompetitionId, MemberId, WeaponClass), deterministic per competition id.
        ///
        ///   /umbraco/surface/SpringskytteSimulation/SeedSimulationResults?competitionId=5123
        ///   /umbraco/surface/SpringskytteSimulation/ClearSimulationResults?competitionId=5123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SeedSimulationResults(
            int competitionId, double runningRatio = 0.10, double dnfRatio = 0.03, double dnsRatio = 0.02)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast webbplatsadministratör kan köra simuleringen." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition")
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            // ── SAME SAFETY GUARD AS THE REGISTRATION SEEDER ─────────────────────────────
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (!string.Equals(compType, "Springskytte", StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = $"Tävlingen är inte Springskytte (competitionType = '{compType}')." });
            var name = competition.GetValue<string>("competitionName");
            if (string.IsNullOrWhiteSpace(name)) name = competition.Name ?? "";
            if (!competition.GetValue<bool>("isClubOnly") && !LooksLikePractice(name))
                return Json(new
                {
                    success = false,
                    message = "Säkerhetsspärr: vägrar skriva resultat på en publik tävling. Sätt isClubOnly=true "
                            + "ELLER ha ÖVNING/TEST/SIM i tävlingsnamnet innan du kör."
                });

            var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null)
                return Json(new { success = false, message = "Inga anmälningar — kör SeedSimulation först." });

            // One result row per (member, weapon class). A shooter registered in both C and A gets two,
            // which is the normal shape here (most simulation shooters register in both).
            var regs = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .ToList();

            // Classes live in `shootingClasses` as a serialized ShootingClassEntry LIST — NOT a plain
            // `shootingClass` string — and a single registration node can carry SEVERAL classes, so every
            // entry has to be walked. Reading the wrong property yields an empty list on every node and
            // the seeder silently finds no starters at all.
            var starters = new List<(int MemberId, string Weapon, string AgeGender)>();
            foreach (var reg in regs)
            {
                var memberId = reg.GetValue<int>("memberId");
                if (memberId <= 0) continue;

                var entries = HpskSite.Models.CompetitionRegistrationDocument
                    .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "");
                var composites = entries.Select(e => e.Class ?? "").Where(c => c.Length > 0).ToList();
                // Fallback for any registration written by an older/other path as a single string.
                if (composites.Count == 0)
                {
                    var single = reg.GetValue<string>("shootingClass") ?? "";
                    if (single.Length > 0) composites.Add(single);
                }

                foreach (var composite in composites)
                {
                    // Springskytte composite class: "C-H 35" → weapon "C", ageGender "H 35".
                    var idx = composite.IndexOf('-');
                    if (idx <= 0 || idx >= composite.Length - 1) continue;
                    var weapon = composite.Substring(0, idx).Trim().ToUpperInvariant();
                    var ageGender = composite.Substring(idx + 1).Trim();
                    if (weapon != "C" && weapon != "A") continue;
                    // Results are keyed (comp, member, weapon class) — one row per weapon group, so a
                    // shooter entered in both C and A gets two, and duplicates across nodes collapse.
                    if (starters.Any(s => s.MemberId == memberId && s.Weapon == weapon)) continue;
                    starters.Add((memberId, weapon, ageGender));
                }
            }

            if (starters.Count == 0)
                return Json(new { success = false, message = "Hittade inga anmälningar med tolkningsbar Springskytte-klass." });

            runningRatio = Math.Clamp(runningRatio, 0d, 0.9d);
            dnfRatio = Math.Clamp(dnfRatio, 0d, 0.5d);
            dnsRatio = Math.Clamp(dnsRatio, 0d, 0.5d);

            var rnd = new Random(competitionId);
            var scoring = new SpringskytteScoringService();
            var now = DateTime.Now;

            // EnteredBy is informational; 0 is what the real save path also stores when it can't resolve
            // a member, so seeded rows stay valid without pulling IMemberManager into this throwaway file.
            const int enteredBy = 0;

            // Start numbers run per weapon class (one series each) — matches how Springskytte numbers
            // its start lists. Overridden at read time when a published start list exists.
            var startOrder = new Dictionary<string, int> { ["C"] = 0, ["A"] = 0 };

            int written = 0, dnf = 0, dns = 0, leftRunning = 0;

            using var db = _databaseFactory.CreateDatabase();

            foreach (var s in starters.OrderBy(x => x.Weapon).ThenBy(x => x.MemberId))
            {
                startOrder[s.Weapon] = startOrder[s.Weapon] + 1;
                var no = startOrder[s.Weapon];

                var roll = rnd.NextDouble();
                if (roll < runningRatio) { leftRunning++; continue; }   // no row at all — still on the course

                string? status = null;
                if (roll < runningRatio + dnfRatio) { status = "DNF"; dnf++; }
                else if (roll < runningRatio + dnfRatio + dnsRatio) { status = "DNS"; dns++; }

                // Skill factor per shooter: correlates run speed with marksmanship a little, so the
                // leaderboard has believable spread instead of uniform noise.
                var skill = rnd.NextDouble();

                string shotsJson = "[]";
                decimal? sprint = null;
                int score = 0;
                decimal? total = null;

                if (status == null)
                {
                    var series = s.Weapon == "C"
                        ? BuildClassCShots(rnd, skill)
                        : BuildClassAShots(rnd, skill);
                    shotsJson = JsonConvert.SerializeObject(series);
                    score = scoring.CalculateShootingScore(shotsJson, s.Weapon);

                    // ~6 legs of roughly 1 km. Faster shooters (high skill) run nearer the low end.
                    var baseSeconds = 1500 + (1 - skill) * 900 + rnd.NextDouble() * 120;
                    sprint = Math.Round((decimal)baseSeconds, 1);
                    total = scoring.CalculateTotalTime(sprint, score, 1);
                }

                var mergeSql = @"
                    MERGE INTO [SpringskytteResultEntry] AS target
                    USING (SELECT @0 AS CompetitionId, @1 AS MemberId, @2 AS WeaponClass) AS source
                    ON target.CompetitionId = source.CompetitionId
                       AND target.MemberId = source.MemberId
                       AND target.WeaponClass = source.WeaponClass
                    WHEN MATCHED THEN
                        UPDATE SET AgeGenderClass = @3, StartOrder = @4, SprintTimeSeconds = @5, Shots = @6,
                                   ShootingScore = @7, PenaltyMultiplier = 1, TotalTimeSeconds = @8,
                                   Status = @9, EnteredBy = @10, LastModified = @11, ScoreModified = @11
                    WHEN NOT MATCHED THEN
                        INSERT (CompetitionId, MemberId, WeaponClass, AgeGenderClass, StartOrder,
                                SprintTimeSeconds, Shots, ShootingScore, PenaltyMultiplier, TotalTimeSeconds,
                                Status, EnteredBy, EnteredAt, LastModified, ScoreModified)
                        VALUES (@0, @1, @2, @3, @4, @5, @6, @7, 1, @8, @9, @10, @11, @11, @11);";

                await db.ExecuteAsync(mergeSql,
                    competitionId,                                   // @0
                    s.MemberId,                                      // @1
                    s.Weapon,                                        // @2
                    s.AgeGender,                                     // @3
                    no,                                              // @4
                    status != null ? (decimal?)null : sprint,         // @5
                    shotsJson,                                       // @6
                    status != null ? (int?)null : score,              // @7
                    status != null ? (decimal?)null : total,          // @8
                    status,                                          // @9
                    enteredBy,                                       // @10
                    now);                                            // @11

                written++;
            }

            return Json(new
            {
                success = true,
                message = $"Seedade {written} resultatrader för '{name}'.",
                starters = starters.Count,
                written,
                withTime = written - dnf - dns,
                dnf,
                dns,
                leftRunning,
                note = "Publicera INTE resultatlistan om tävlingen bara ska användas för att testa tavlan — "
                     + "opublicerat håller dummyresultaten borta från publika resultatlistor och medaljregistret."
            });
        }

        /// <summary>Wipe seeded results for a practice competition so a fresh seed can be run.</summary>
        [HttpGet]
        public async Task<IActionResult> ClearSimulationResults(int competitionId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast webbplatsadministratör." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition")
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var name = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";
            if (!competition.GetValue<bool>("isClubOnly") && !LooksLikePractice(name))
                return Json(new { success = false, message = "Säkerhetsspärr: vägrar rensa resultat på en publik tävling." });

            using var db = _databaseFactory.CreateDatabase();
            var removed = await db.ExecuteAsync(
                "DELETE FROM [SpringskytteResultEntry] WHERE CompetitionId = @0", competitionId);

            return Json(new { success = true, message = "Seedade resultat rensade.", removed });
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Class C: 6 stations × 5 shots, "H" = hit, "B" = miss. Both tokens matter — the scoring
        /// service counts only "B" as a miss, while the board's dot renderer paints anything that is
        /// not "H" red, so any other token would score and display inconsistently.
        /// </summary>
        private static List<List<string>> BuildClassCShots(Random rnd, double skill)
        {
            var hitChance = 0.62 + skill * 0.33;              // ~62–95 % per shot
            var series = new List<List<string>>();
            for (int station = 0; station < 6; station++)
            {
                var shots = new List<string>();
                for (int i = 0; i < 5; i++)
                    shots.Add(rnd.NextDouble() < hitChance ? "H" : "B");
                series.Add(shots);
            }
            return series;
        }

        /// <summary>
        /// Class A: 6 targets, each [ring1, ring2, ring3, ring4, bom] as COUNTS summing to 5 shots
        /// (penalty = ring3×1 + ring4×2 + bom×3, see SpringskytteScoringService.CalculateClassAScore).
        /// </summary>
        private static List<List<string>> BuildClassAShots(Random rnd, double skill)
        {
            var series = new List<List<string>>();
            for (int target = 0; target < 6; target++)
            {
                var counts = new int[5];
                for (int i = 0; i < 5; i++)
                {
                    var r = rnd.NextDouble() * (1.15 - skill * 0.55);   // better shooters skew to ring 1–2
                    int zone = r < 0.45 ? 0 : r < 0.70 ? 1 : r < 0.85 ? 2 : r < 0.95 ? 3 : 4;
                    counts[zone]++;
                }
                series.Add(counts.Select(c => c.ToString()).ToList());
            }
            return series;
        }

        private static bool LooksLikePractice(string name)
        {
            name ??= "";
            return name.Contains("ÖVNING", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OVNING", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TEST", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SIM", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Weighted age pick for a given gender, restricted to ageGender classes the competition
        /// actually offers ("H 35" must exist in C or A). Falls back gracefully if the weight table
        /// and the competition's classes don't overlap.
        /// </summary>
        private static string PickAgeGender(
            Random rnd, string gender, (string Age, int Weight)[] ageWeights, HashSet<string> unionClasses)
        {
            var candidates = ageWeights
                .Where(x => unionClasses.Contains($"{gender} {x.Age}"))
                .ToArray();

            if (candidates.Length == 0)
            {
                var forGender = unionClasses.Where(a => a.StartsWith(gender + " ", StringComparison.OrdinalIgnoreCase)).ToList();
                if (forGender.Count > 0) return forGender[rnd.Next(forGender.Count)];
                var any = unionClasses.ToList();
                return any.Count > 0 ? any[rnd.Next(any.Count)] : $"{gender} 21";
            }

            int total = candidates.Sum(x => x.Weight);
            int roll = rnd.Next(total);
            int acc = 0;
            foreach (var (age, w) in candidates)
            {
                acc += w;
                if (roll < acc) return $"{gender} {age}";
            }
            return $"{gender} {candidates[^1].Age}";
        }

        private static (string First, string Last) DummyName(int i)
        {
            var first = FirstNames[(i - 1) % FirstNames.Length];
            var last = LastNames[((i - 1) / FirstNames.Length) % LastNames.Length];
            return (first, last);
        }

        private bool TryMarkInvoicePaid(int registrationId)
        {
            var reg = _contentService.GetById(registrationId);
            var invoiceId = reg?.GetValue<int>("invoiceId") ?? 0;
            if (invoiceId <= 0) return false;

            var invoice = _contentService.GetById(invoiceId);
            if (invoice == null || invoice.ContentType.Alias != "registrationInvoice") return false;

            // Direct property write (no PaymentService transition) — deliberate for the simulation:
            // we don't want betalningsbekräftelse emails going to @ankeborg.invalid addresses.
            invoice.SetValue("paymentStatus", "Paid");
            invoice.SetValue("paymentDate", DateTime.Now);
            var total = invoice.GetValue<decimal>("totalAmount");
            if (invoice.HasProperty("actualPaidAmount"))
                invoice.SetValue("actualPaidAmount", total);
            _contentService.Save(invoice);
            _contentService.Publish(invoice, new[] { "*" }, -1);
            return true;
        }

        private IContent GetOrCreateRegistrationsHub(IContent competition)
        {
            var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null)
            {
                hub = _contentService.Create("Anmälningar", competition.Id, "competitionRegistrationsHub");
                _contentService.Save(hub);
                _contentService.Publish(hub, new[] { "*" }, -1);
            }
            return hub;
        }

        private static List<string> ParseCompositeClasses(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            raw = raw.Trim();
            if (raw.StartsWith("["))
            {
                try { list.AddRange(System.Text.Json.JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>()); }
                catch { list.AddRange(raw.Split(',')); }
            }
            else
            {
                list.AddRange(raw.Split(','));
            }
            // Keep only real Springskytte composites (weapon-ageGender, e.g. "A-D 21").
            return list.Select(s => s.Trim()).Where(s => s.Contains('-')).Distinct().ToList();
        }
    }
}
