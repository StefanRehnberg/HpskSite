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
    /// SAFETY: refuses to run unless the target is a Springskytte competition that is either
    /// isClubOnly=true OR clearly named as practice (ÖVNING/TEST/SIM). It can never dump dummies
    /// into the real, public SM competition. Safe to delete this whole file after the SM.
    ///
    /// Usage (open in browser while logged in as site admin):
    ///   /umbraco/surface/SpringskytteSimulation/SeedSimulation?competitionId=1234&amp;count=50
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

        /// <summary>
        /// Seed the practice competition with dummy shooters + Pending/Paid invoices.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SeedSimulation(
            int competitionId, int count = 50, double paidRatio = 0.6, string club = "Ankeborg")
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
            bool looksLikePractice =
                name.Contains("ÖVNING", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OVNING", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TEST", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SIM", StringComparison.OrdinalIgnoreCase);
            if (!isClubOnly && !looksLikePractice)
                return Json(new
                {
                    success = false,
                    message = "Säkerhetsspärr: vägrar seeda en publik tävling. Sätt isClubOnly=true "
                            + "ELLER ha ÖVNING/TEST/SIM i tävlingsnamnet innan du kör."
                });

            // Springskytte classes are stored as composite "A-D 21","C-H 35" strings in shootingClassIds.
            var classes = ParseCompositeClasses(competition.GetValue<string>("shootingClassIds") ?? "");
            if (classes.Count == 0)
                return Json(new { success = false, message = "Tävlingen har inga Springskytte-klasser (shootingClassIds tomt). Konfigurera klasser först." });

            var clubInfo = _clubService.GetAllClubs()
                .FirstOrDefault(c => c.Name.Contains(club, StringComparison.OrdinalIgnoreCase));
            if (clubInfo == null)
                return Json(new { success = false, message = $"Hittade ingen klubb som matchar '{club}'. Skapa klubben först." });

            count = Math.Clamp(count, 1, 200);
            paidRatio = Math.Clamp(paidRatio, 0d, 1d);

            var hub = GetOrCreateRegistrationsHub(competition);
            var existing = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .ToList();
            var alreadyRegistered = new HashSet<int>(existing.Select(r => r.GetValue<int>("memberId")));

            int membersCreated = 0, registered = 0, markedPaid = 0, skipped = 0;
            // Deterministic paid/unpaid spread seeded on the comp id — avoids Math.Random pitfalls and
            // makes re-runs reproducible.
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

                var cls = classes[(i - 1) % classes.Count];

                var reg = _contentService.Create($"{member.Name} - övning", hub.Id, "competitionRegistration");
                reg.SetValue("competitionId", competitionId);
                reg.SetValue("memberId", member.Id);
                reg.SetValue("memberName", member.Name);
                reg.SetValue("clubId", clubInfo.Id);
                var entries = new List<HpskSite.Models.ShootingClassEntry>
                {
                    new() { Class = cls, StartPreference = "Inget", TeamNumber = null }
                };
                reg.SetValue("shootingClasses",
                    HpskSite.Models.CompetitionRegistrationDocument.SerializeShootingClasses(entries));
                reg.SetValue("registrationDate", DateTime.Now);
                reg.SetValue("registeredBy", SeedTag);
                reg.SetValue("isActive", true);
                if (reg.HasProperty("isSubCompetition"))
                    reg.SetValue("isSubCompetition", false);
                _contentService.Save(reg);
                _contentService.Publish(reg, new[] { "*" }, -1);
                registered++;
                alreadyRegistered.Add(member.Id);

                // Eager Pending invoice — exactly like a real registration.
                await _paymentService.EnsureRegistrationInvoiceAsync(competitionId, reg.Id);

                // Mark a share as already Paid so the registration desk can practice both the
                // "already paid → just check in" and the "not paid → take payment" cases.
                if (rnd.NextDouble() < paidRatio && TryMarkInvoicePaid(reg.Id))
                    markedPaid++;
            }

            return Json(new
            {
                success = true,
                message = $"Simulering seedad på \"{name}\".",
                competitionId,
                club = clubInfo.Name,
                classesUsed = classes,
                membersCreated,
                registered,
                skippedAlreadyRegistered = skipped,
                markedPaid,
                leftUnpaid = registered - markedPaid,
                note = "Ingen är incheckad (avsiktligt — disken övar incheckning). "
                     + "Generera startlistor i tävlingsadmin (fliken Startlistor) så att skotträknare "
                     + "för Vapengrupp C kan slå upp skyttar på startnummer."
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
            bool looksLikePractice =
                name.Contains("ÖVNING", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OVNING", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TEST", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SIM", StringComparison.OrdinalIgnoreCase);
            if (!isClubOnly && !looksLikePractice)
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

        private const string SeedTag = "Simulering (seed)";

        private (string First, string Last) DummyName(int i)
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
