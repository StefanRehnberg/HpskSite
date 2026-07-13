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
using HpskSite.Services;

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

        // ── Helpers ──────────────────────────────────────────────────────────────────────

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
