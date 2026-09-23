using HpskSite.Models;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.Ledger;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Revisorns egen ingång: inbjudan, kontot, och sidan där räkenskaperna är samlade.
    ///
    /// <para><b>⚠️⚠️ EN REVISION ÄR INTE EN LÄSNING — DET ÄR ETT SAMTAL ÖVER VECKOR.</b> Revisorn
    /// kommer tillbaka, jämför med förra året, hittar en post och vill se underlaget. Därför en
    /// SIDA och inte en utskriven mapp: Skatteverkets krav att revisorn <i>självständigt</i> ska
    /// kunna följa kedjan åt båda hållen är i praktiken ett krav på navigering.</para>
    ///
    /// <para><b>⚠️ Klubbrevisorn har oftast inget konto hos oss</b>, och det är varför det börjar
    /// med en inbjudningslänk. Kontot som skapas här är <b>inte en klubbmedlem</b>: ingen
    /// klubbtillhörighet, inget <c>Users</c>-medlemskap, ingen <c>ClubMembership</c>. Annars
    /// förorenar revisorn medlemsregister, statistik och GDPR-omfång i en förening hen inte
    /// tillhör.</para>
    ///
    /// <para>Routad MVC utan Umbraco-nod — samma mönster som <c>ForeningsintygPrintController</c>
    /// och <c>ReceiptController</c>.</para>
    /// </summary>
    [Route("revision")]
    public class RevisionController : Controller
    {
        /// <summary>
        /// Medlemsgruppen som märker ut ett revisorskonto.
        ///
        /// <para><b>⚠️ Finns för att kunna EXKLUDERA.</b> Ett revisorskonto ska inte räknas som
        /// medlem någonstans — inte i klubbens register, inte i statistiken, inte i ett utskick.
        /// Gruppen ger INGEN behörighet; den kommer uteslutande ur <c>LedgerAuditorGrant</c>.</para>
        /// </summary>
        public const string AuditorRole = "Revisor";

        private readonly LedgerAuditorService _auditors;
        private readonly LedgerAccessService _access;
        private readonly LedgerClosingService _closing;
        private readonly LedgerSetupService _setup;
        private readonly LedgerBankImportService _bank;
        private readonly LedgerAttachmentService _attachments;
        private readonly BoardMeetingService _meetings;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<RevisionController> _logger;

        public RevisionController(
            LedgerAuditorService auditors,
            LedgerAccessService access,
            LedgerClosingService closing,
            LedgerSetupService setup,
            LedgerBankImportService bank,
            LedgerAttachmentService attachments,
            BoardMeetingService meetings,
            IMemberService memberService,
            IMemberManager memberManager,
            IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<RevisionController> logger)
        {
            _auditors = auditors;
            _access = access;
            _closing = closing;
            _setup = setup;
            _bank = bank;
            _attachments = attachments;
            _meetings = meetings;
            _memberService = memberService;
            _memberManager = memberManager;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
        }

        /// <summary>
        /// Inbjudningslänken. <b>Skriver ingenting</b> — den visar bara vad inbjudan gäller.
        ///
        /// <para>⚠️ En länk som öppnas av misstag, i en förhandsvisning eller av en
        /// e-postskanner, får inte ta emot inbjudan. Mottagandet är ett eget POST.</para>
        /// </summary>
        [HttpGet("inbjudan")]
        public async Task<IActionResult> Invite(string? t)
        {
            var grant = _auditors.FindByToken(t);

            var model = new RevisionInviteModel { Token = t ?? "" };

            if (grant is null)
            {
                model.Problem = "Länken gäller inte. Be föreningen skicka en ny inbjudan.";
                return View("~/Views/RevisionInvite.cshtml", model);
            }

            model.OwnerName = OwnerName(grant.OwnerType, grant.OwnerId);
            model.InvitedName = grant.Name;
            model.InvitedEmail = grant.Email;
            model.ExpiresUtc = grant.ExpiresUtc;

            if (grant.IsRevoked)
                model.Problem = "Föreningen har återkallat inbjudan.";
            else if (grant.ExpiresUtc <= DateTime.UtcNow)
                model.Problem = "Inbjudan har gått ut. Be föreningen skicka en ny.";
            else if (grant.IsAccepted)
                model.Problem = "Inbjudan är redan mottagen. Logga in med kontot den kopplades till.";

            // ⚠️ Finns adressen redan som medlem behövs inget nytt konto — då är det en inloggning
            //    som ska kopplas, inte ett lösenord som ska sättas. Vanligare än man tror: en
            //    klubbrevisor är ofta medlem i en ANNAN klubb.
            model.AccountExists = _memberService.GetByEmail(grant.Email) is not null;

            var me = await CurrentMemberIdAsync();
            model.SignedInMemberId = me;

            return View("~/Views/RevisionInvite.cshtml", model);
        }

        /// <summary>
        /// Tar emot inbjudan: skapar kontot om det behövs, och knyter uppdraget till det.
        ///
        /// <para><b>⚠️ Ordningen är inte valfri.</b> Kontot skapas FÖRE uppdraget knyts, och
        /// uppdraget knyts med ett villkorat UPDATE — två samtidiga öppningar av samma länk får
        /// aldrig båda lyckas.</para>
        /// </summary>
        [HttpPost("inbjudan")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Accept(string token, string? password)
        {
            var grant = _auditors.FindByToken(token);

            if (grant is null || !grant.CanBeAccepted)
                return Json(new { success = false, message = "Länken gäller inte längre." });

            var existing = _memberService.GetByEmail(grant.Email);
            int memberId;

            if (existing is not null)
            {
                // ⚠️ Kontot finns redan. Vi rör det INTE — inget lösenord, ingen grupp, inget
                //    namnbyte. Det tillhör en människa som kan vara medlem i en annan klubb, och
                //    en inbjudan får aldrig ändra någons befintliga konto.
                memberId = existing.Id;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                    return Json(new { success = false, message = "Välj ett lösenord på minst 8 tecken." });

                var created = await CreateAuditorAccountAsync(grant.Email, grant.Name, password);

                if (created is null)
                    return Json(new { success = false, message = "Kontot kunde inte skapas. Försök igen." });

                memberId = created.Value;
            }

            var (ok, error) = _auditors.Accept(token, memberId);

            if (!ok) return Json(new { success = false, message = error! });

            _logger.LogInformation(
                "Revision: {Namn} tog emot revisorsuppdrag {Id} för {Typ}/{Agare}.",
                grant.Name, grant.Id, grant.OwnerType, grant.OwnerId);

            return Json(new
            {
                success = true,
                // ⚠️ Den som SKAPADE ett konto är inte inloggad än. Svaret säger det, i stället
                //    för att skicka hen till en sida som svarar "ingen behörighet".
                needsLogin = existing is null || await CurrentMemberIdAsync() != memberId,
                url = "/revision"
            });
        }

        /// <summary>Revisionssidan — räkenskaperna för de föreningar man är revisor för.</summary>
        [HttpGet("")]
        public async Task<IActionResult> Index(int? type, int? id, int? year)
        {
            var me = await CurrentMemberIdAsync();

            if (me <= 0)
                return View("~/Views/Revision.cshtml", new RevisionModel { NeedsLogin = true });

            var grants = _auditors.GrantsForMember(me);

            var model = new RevisionModel
            {
                Assignments = grants.Select(g => new RevisionAssignment
                {
                    OwnerType = g.OwnerType,
                    OwnerId = g.OwnerId,
                    OwnerName = OwnerName(g.OwnerType, g.OwnerId),
                    ExpiresUtc = g.ExpiresUtc
                }).ToList()
            };

            if (model.Assignments.Count == 0) return View("~/Views/Revision.cshtml", model);

            // Förvalet är det enda uppdraget; med flera väljer revisorn.
            var pick = model.Assignments.FirstOrDefault(a =>
                           type.HasValue && id.HasValue && a.OwnerType == type && a.OwnerId == id)
                       ?? (model.Assignments.Count == 1 ? model.Assignments[0] : null);

            if (pick is null) return View("~/Views/Revision.cshtml", model);

            model.Selected = pick;
            pick.FiscalYearId = year;

            // ⚠️⚠️ GRINDEN FRÅGAS OM PÅ NYTT här, trots att uppdraget redan lästs. Det är samma
            //    upplösning som resten av ekonomidelen använder, och den ska vara den enda
            //    sanningen om vad den inloggade får se — inte en parallell kontroll som kan glida.
            var access = await _access.ResolveAsync(pick.OwnerType, pick.OwnerId);

            if (!access.CanRead)
            {
                model.Selected = null;
                return View("~/Views/Revision.cshtml", model);
            }

            // ⚠️ Revisorn ska ALDRIG kunna skriva härifrån. Sidan visar inget som skriver, och
            //    endpointarna grindar var för sig — men om åtkomsten av någon anledning är Write
            //    är det en kassör som tittar, och då är revisionssidan fel yta.
            model.ReadOnly = !access.CanWrite;

            _auditors.Touch(pick.OwnerType, pick.OwnerId, me);

            LoadYear(model, pick);

            return View("~/Views/Revision.cshtml", model);
        }

        /// <summary>
        /// Året som granskas: resultat, balans, checklistan, avstämningen och underlagsläget.
        ///
        /// <para>⚠️ Läser bara. Allt som visas här finns redan som endpoints på ekonomidelen;
        /// sidan samlar dem, den räknar inte om något själv.</para>
        /// </summary>
        private void LoadYear(RevisionModel model, RevisionAssignment pick)
        {
            try
            {
                // ⚠️⚠️ ÅRET VÄLJS PÅ SAMMA SÄTT SOM BOKSLUTSYTAN, inte "senaste". Den som granskar
                //    gör det i januari–mars, och då är innevarande år ofta redan upplagt medan det
                //    som ska granskas är det FÖREGÅENDE. `ResolveFiscalYear`-formen i
                //    EkonomiAdminController tar det år som rymmer dagens datum; här behöver
                //    revisorn i stället se vad checklistan pekar på.
                var years = _setup.GetStatus(pick.OwnerType, pick.OwnerId).FiscalYears
                            ?? new List<LedgerFiscalYear>();

                if (years.Count == 0)
                {
                    model.Problem = "Föreningen har inget räkenskapsår upplagt ännu.";
                    return;
                }

                // ⚠️ `years.Count == 0` ovan, aldrig `yearId <= 0`. En sandlådas id är negativt,
                //    och den sign-kontrollen har låst ute sandlådan fyra gånger i den här modulen.
                model.Years = years.OrderByDescending(y => y.Year)
                    .Select(y => new RevisionYear { Id = y.Id, Year = y.Year, Status = y.Status })
                    .ToList();

                var yearId = pick.FiscalYearId
                             ?? years.OrderByDescending(y => y.Year).Select(y => y.Id).First();

                model.SelectedYearId = yearId;

                model.Checklist = _closing.Checklist(pick.OwnerType, pick.OwnerId, yearId);
                model.Statements = _closing.Statements(pick.OwnerType, pick.OwnerId, yearId);

                var (missing, total) = _attachments.MissingForYear(pick.OwnerType, pick.OwnerId, yearId);
                model.AttachmentsMissing = missing;
                model.AttachmentsTotal = total;

                LoadProtokoll(model, pick);

                var summary = _bank.Summary(pick.OwnerType, pick.OwnerId);
                if (summary is not null)
                {
                    model.BankRows = summary.Value.Rows;
                    model.BankUnmatched = summary.Value.Unmatched;
                    model.BankPeriodTo = summary.Value.PeriodTo;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Revisionssidan kunde inte byggas för {Typ}/{Id}.",
                    pick.OwnerType, pick.OwnerId);

                model.Problem = "Räkenskaperna kunde inte läsas just nu.";
            }
        }

        /// <summary>
        /// Protokollen revisorn får läsa.
        ///
        /// <para><b>⚠️⚠️ BARA JUSTERADE.</b> Ett ojusterat protokoll kan fortfarande ändras, och
        /// att visa det för en revisor är att visa något som inte är en handling än. Samma regel
        /// som kodbasen har överallt: publicerad startlista, officiell resultatlista, antagen
        /// budget.</para>
        ///
        /// <para><b>⚠️ Antalet ojusterade SÄGS.</b> En lista som tyst utelämnar möten läses som
        /// komplett — och då upptäcker revisorn aldrig att det finns beslut hen inte sett.</para>
        /// </summary>
        private void LoadProtokoll(RevisionModel model, RevisionAssignment pick)
        {
            try
            {
                var all = _meetings.GetMeetings(pick.OwnerType, pick.OwnerId) ?? new List<BoardMeeting>();

                model.Protokoll = all
                    .Where(m => m.IsActive && m.Status == "Justerat")
                    .OrderByDescending(m => m.MeetingDate)
                    .Take(40)
                    .Select(m => new RevisionProtokoll
                    {
                        Id = m.Id,
                        Title = string.IsNullOrWhiteSpace(m.Title) ? m.TypeLabel : m.Title,
                        TypeLabel = m.TypeLabel,
                        MeetingDate = m.MeetingDate
                    })
                    .ToList();

                // ⚠️ Räknar bara möten som HÅLLITS. Ett planerat möte i framtiden är inte ett
                //    ojusterat protokoll, och att räkna det hade gett ett larm om ingenting.
                model.ProtokollPending = all.Count(m =>
                    m.IsActive && m.Status != "Justerat" && m.MeetingDate.Date <= DateTime.Today);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Protokollen kunde inte läsas för {Typ}/{Id}.",
                    pick.OwnerType, pick.OwnerId);

                // ⚠️ -1 = gick inte att läsa. Aldrig 0, som betyder "inga ojusterade".
                model.ProtokollPending = -1;
            }
        }

        /// <summary>
        /// Skapar revisorns konto.
        ///
        /// <para><b>⚠️⚠️ INGEN KLUBBTILLHÖRIGHET, INGET <c>Users</c>-MEDLEMSKAP.</b> En revisor är
        /// inte medlem i föreningen hen granskar. Sätts <c>primaryClubId</c> dyker hen upp i
        /// klubbens medlemsregister, i statistiken och i utskick — och <c>Users</c> är vad
        /// medlemsplockare och lagurval filtrerar på. Behörigheten kommer uteslutande ur
        /// <c>LedgerAuditorGrant</c>.</para>
        ///
        /// <para>⚠️ Lösenordet sätts via återställningstoken, samma väg som
        /// <c>MemberAdminController</c> — medlemmen skapas utan lösenord, så
        /// <c>ChangePasswordAsync</c> (som kräver det gamla) är inte användbar.</para>
        /// </summary>
        private async Task<int?> CreateAuditorAccountAsync(string email, string name, string password)
        {
            try
            {
                var member = _memberService.CreateMember(email, email, name, "hpskMember");

                var space = name.LastIndexOf(' ');
                member.SetValue("firstName", space > 0 ? name[..space] : name);
                member.SetValue("lastName", space > 0 ? name[(space + 1)..] : "");

                // Godkänd direkt: föreningen har bjudit in hen. En revisor i
                // godkännandekön hade fastnat bakom en klubbadmin som inte vet varför.
                member.IsApproved = true;

                _memberService.Save(member);
                _memberService.AssignRole(member.Id, AuditorRole);

                var identity = await _memberManager.FindByEmailAsync(email);
                if (identity is null) return null;

                var token = await _memberManager.GeneratePasswordResetTokenAsync(identity);
                var result = await _memberManager.ResetPasswordAsync(identity, token, password);

                if (!result.Succeeded)
                {
                    _logger.LogWarning("Revisorskontot {Epost} fick inget lösenord: {Fel}",
                        email, string.Join("; ", result.Errors.Select(e => e.Description)));
                    return null;
                }

                return member.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte skapa revisorskonto för {Epost}.", email);
                return null;
            }
        }

        private string OwnerName(int ownerType, int ownerId)
        {
            try
            {
                _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
                var node = ctx?.Content?.GetById(ownerId);

                if (node is null) return $"Förening {ownerId}";

                return ownerType == DocumentOwnerType.Club
                    ? node.Value<string>("clubName") ?? node.Name ?? ""
                    : node.Name ?? "";
            }
            catch
            {
                return $"Förening {ownerId}";
            }
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }
    }

    /// <summary>Inbjudningssidans modell.</summary>
    public class RevisionInviteModel
    {
        public string Token { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string InvitedName { get; set; } = "";
        public string InvitedEmail { get; set; } = "";
        public DateTime? ExpiresUtc { get; set; }

        /// <summary>Varför inbjudan inte går att ta emot. Null när allt är i sin ordning.</summary>
        public string? Problem { get; set; }

        /// <summary>Adressen finns redan som konto — då ska inget lösenord sättas.</summary>
        public bool AccountExists { get; set; }

        public int SignedInMemberId { get; set; }
    }

    /// <summary>Revisionssidans modell.</summary>
    public class RevisionModel
    {
        public bool NeedsLogin { get; set; }

        public List<RevisionAssignment> Assignments { get; set; } = new();

        public RevisionAssignment? Selected { get; set; }

        public bool ReadOnly { get; set; } = true;

        public LedgerFinancialStatements? Statements { get; set; }

        public LedgerClosingChecklist? Checklist { get; set; }

        /// <summary>−1 = går inte att avgöra. Aldrig 0, som betyder "inga saknas".</summary>
        public int AttachmentsMissing { get; set; } = -1;

        public int AttachmentsTotal { get; set; } = -1;

        public int BankRows { get; set; }

        public int BankUnmatched { get; set; }

        public DateTime? BankPeriodTo { get; set; }

        public string? Problem { get; set; }

        public List<RevisionProtokoll> Protokoll { get; set; } = new();

        /// <summary>Hållna möten som ännu inte justerats. −1 = gick inte att läsa.</summary>
        public int ProtokollPending { get; set; }

        public List<RevisionYear> Years { get; set; } = new();

        public int SelectedYearId { get; set; }
    }

    /// <summary>Ett justerat protokoll, så som revisionssidan listar det.</summary>
    public class RevisionProtokoll
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string TypeLabel { get; set; } = "";
        public DateTime MeetingDate { get; set; }
    }

    /// <summary>En förening man är revisor för.</summary>
    public class RevisionAssignment
    {
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }

        /// <summary>Året revisorn valt, eller null för det senaste.</summary>
        public int? FiscalYearId { get; set; }
    }

    /// <summary>
    /// Ett räkenskapsår att välja bland.
    /// <para><b>⚠️ Revisorn ska kunna jämföra med föregående år</b> — det är en del av
    /// granskningen, inte en bekvämlighet. Därför visas ALLA år, inte bara det som ska
    /// fastställas.</para>
    /// </summary>
    public class RevisionYear
    {
        public int Id { get; set; }
        public int Year { get; set; }
        public string Status { get; set; } = "";
    }
}
