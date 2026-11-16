using HpskSite.CompetitionTypes.Springskytte.Services;
using HpskSite.Models.Competitions;
using HpskSite.Models.Registrations;
using HpskSite.Services.CompetitionFees;
using HpskSite.Services.Competitions;
using HpskSite.Services.Hosting;
using HpskSite.Services.Mail;
using HpskSite.Services.StartListCleanup;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// <b>Avanmälan — den enda vägen som tar bort en anmälan</b>, hur den än påbörjas.
    ///
    /// <para>Två ingångar, ETT städflöde (<see cref="DeleteRegistrationAsync"/>):</para>
    /// <list type="bullet">
    ///   <item><b>Arrangören</b> (<c>RegistrationAdmin/DeleteCompetitionRegistration</c>) — behörigheten prövas i controllern.</item>
    ///   <item><b>Skytten själv</b> (<see cref="WithdrawSelfAsync"/>, <c>Competition/WithdrawFromCompetition</c>) — får bara
    ///     avanmäla sig själv, och bara innan tävlingen börjat. <b>Även när avgiften är betald</b> (Stefan 2026-11-16):
    ///     då skrivs en rad i <c>CompetitionWithdrawal</c> och arrangören mejlas att skytten ska få pengarna tillbaka.</item>
    /// </list>
    ///
    /// <para><b>Pengarna rörs aldrig av avanmälan.</b> Öppna (obetalda) fakturor och avgiftsbegäranden makuleras;
    /// betalda ligger kvar som underlag för bokföringen. Återbetalningen är arrangörens beslut och görs utanför
    /// sajten — sajten bär bara påminnelsen (raden + mejlet) tills arrangören markerat den som gjord.</para>
    ///
    /// <para><b>Aldrig tyst:</b> att raden eller mejlet inte kunde skrivas fäller inte avanmälan (anmälan är redan
    /// borta), men loggas som fel och svaret säger det rakt ut, så att skytten kan höra av sig till arrangören.</para>
    /// </summary>
    public sealed class RegistrationWithdrawalService
    {
        private readonly ICompetitionRegistrationStore _registrations;
        private readonly ICompetitionStore _competitions;
        private readonly PaymentService _payments;
        private readonly StartListCleanupService _cleanup;
        private readonly SpringskytteWithdrawalCleanup _springCleanup;
        private readonly LegacyInvoiceHubs _invoiceHubs;
        private readonly IContentService _content;
        private readonly CompetitionPaymentModelService _models;
        private readonly CompetitionFeeService _fees;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _replyContacts;
        private readonly IHttpContextAccessor _http;
        private readonly ClubService _clubs;
        private readonly ILogger<RegistrationWithdrawalService> _logger;

        public RegistrationWithdrawalService(
            ICompetitionRegistrationStore registrations, ICompetitionStore competitions, PaymentService payments,
            StartListCleanupService cleanup, SpringskytteWithdrawalCleanup springCleanup, LegacyInvoiceHubs invoiceHubs,
            IContentService content, CompetitionPaymentModelService models, CompetitionFeeService fees,
            IUmbracoDatabaseFactory databaseFactory, EmailService email, ReplyContactResolver replyContacts,
            IHttpContextAccessor http, ClubService clubs, ILogger<RegistrationWithdrawalService> logger)
        {
            _registrations = registrations;
            _competitions = competitions;
            _payments = payments;
            _cleanup = cleanup;
            _springCleanup = springCleanup;
            _invoiceHubs = invoiceHubs;
            _content = content;
            _models = models;
            _fees = fees;
            _databaseFactory = databaseFactory;
            _email = email;
            _replyContacts = replyContacts;
            _http = http;
            _clubs = clubs;
            _logger = logger;
        }

        // ── Får skytten avanmäla sig? ────────────────────────────────────────────────────────

        /// <summary>
        /// Skyttens egen avanmälan stängs när tävlingen börjar (tävlingens första dag) — samma regel som
        /// <c>MemberController.CanUnregister</c>. Därefter hör avanmälan till arrangören, som kan ta bort anmälan och
        /// bedömer vad som hänt med resultat och startlista.
        /// <para>⚠️ Startlistans publicering och sista anmälningsdag stänger INTE avanmälan: de stänger nyanmälan.
        /// En skytt som inte kan komma ska kunna säga det hela vägen fram till tävlingsdagen.</para>
        /// </summary>
        public static (bool Allowed, string? Reason) CanWithdraw(CompetitionInfo competition, DateTime now)
        {
            if (competition.IsExternal)
                return (false, "Det här är en extern tävling — avanmälan sker hos arrangören.");
            if (competition.CompetitionDate is { } date && date <= now)
                return (false, "Tävlingen har börjat eller är över, så du kan inte längre avanmäla dig här. Kontakta arrangören.");
            return (true, null);
        }

        // ── Skyttens egen avanmälan ──────────────────────────────────────────────────────────

        public sealed class WithdrawResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = "";
            /// <summary>Bekräftat betalt (ska återbetalas).</summary>
            public decimal PaidAmount { get; set; }
            /// <summary>Angivet som betalt men inte bekräftat.</summary>
            public decimal ClaimedAmount { get; set; }
            public bool OrganiserNotified { get; set; }
            public string OrganiserName { get; set; } = "";
            public string OrganiserEmail { get; set; } = "";
            public List<string> Warnings { get; } = new();
        }

        /// <summary>
        /// Skytten avanmäler sig från tävlingen: ALLA hens anmälningar i den (äldre tävlingar har en per klass).
        /// Anropet ägs av skytten — <paramref name="memberId"/> är den inloggade medlemmen, aldrig ett id från klienten.
        /// </summary>
        public async Task<WithdrawResult> WithdrawSelfAsync(int competitionId, int memberId, string memberName)
        {
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null)
                return new WithdrawResult { Message = "Tävlingen kunde inte hittas." };

            var regs = _registrations.ListForMemberInCompetition(competitionId, memberId);
            if (regs.Count == 0)
                return new WithdrawResult { Message = "Du är inte anmäld till den här tävlingen (kanske är anmälan redan borttagen)." };

            var (allowed, reason) = CanWithdraw(competition, DateTime.Now);
            if (!allowed)
                return new WithdrawResult { Message = reason! };

            var result = new WithdrawResult();
            var withdrawals = new List<CompetitionWithdrawal>();

            foreach (var reg in regs)
            {
                // Pengarna mäts INNAN anmälan tas bort — efteråt finns inget att räkna ur.
                var money = MeasureMoney(competition, reg);

                var outcome = await DeleteRegistrationAsync(competition, reg, memberId, memberName);
                if (!outcome.Deleted)
                {
                    result.Message = outcome.Error ?? "Kunde inte avanmäla dig. Försök igen eller kontakta arrangören.";
                    // Redan borttagna anmälningar i samma anrop står kvar som avanmälda — säg det.
                    if (withdrawals.Count > 0) result.Warnings.Add("En del av din anmälan hann tas bort innan felet uppstod.");
                    return result;
                }

                if (outcome.Cleanup.Supported == false && IsSpringskytte(competition))
                {
                    var (freed, publishedChanged) = _springCleanup.Clean(competitionId, memberId);
                    if (freed > 0) _logger.LogInformation("Avanmälan: {Freed} springskyttelista(or) städade för medlem {MemberId} (tävling {CompetitionId}, publicerad ändrad: {Pub}).", freed, memberId, competitionId, publishedChanged);
                }
                foreach (var w in outcome.Cleanup.Warnings) result.Warnings.Add(w);

                withdrawals.Add(new CompetitionWithdrawal
                {
                    CompetitionId = competitionId,
                    RegistrationId = reg.Id,
                    MemberId = memberId,
                    MemberName = string.IsNullOrWhiteSpace(reg.MemberName) ? memberName : reg.MemberName,
                    ClubName = ClubNameOf(reg.ClubId),
                    Classes = string.Join(", ", reg.Classes.Select(c => HpskSite.Models.ShootingClasses.DisplayName(c.Class)).Where(s => !string.IsNullOrWhiteSpace(s))),
                    WithdrawnAt = DateTime.Now,
                    WithdrawnByMemberId = memberId,
                    WithdrawnBy = WithdrawnByKinds.Self,
                    PaidAmount = money.Paid,
                    ClaimedAmount = money.Claimed,
                    PaymentModel = money.Ledger ? "ledger" : "legacy",
                    Note = money.Note,
                    RefundStatus = money.Paid + money.Claimed > 0 ? RefundStatuses.Pending : RefundStatuses.None
                });
                result.PaidAmount += money.Paid;
                result.ClaimedAmount += money.Claimed;
            }

            result.Success = true;

            var recorded = Record(withdrawals);
            if (!recorded && (result.PaidAmount + result.ClaimedAmount) > 0)
                result.Warnings.Add("Arrangören kunde inte få en notis i sajten om återbetalningen. Hör av dig till arrangören och säg att du avanmält dig och betalat avgiften.");

            if (result.PaidAmount + result.ClaimedAmount > 0)
            {
                var (mailed, orgName, orgEmail) = await NotifyOrganiserAsync(competition, memberId, withdrawals, result.PaidAmount, result.ClaimedAmount);
                result.OrganiserNotified = mailed;
                result.OrganiserName = orgName;
                result.OrganiserEmail = orgEmail;
            }

            result.Message = BuildShooterMessage(competition, result);
            return result;
        }

        private static string BuildShooterMessage(CompetitionInfo competition, WithdrawResult r)
        {
            var msg = $"Du är avanmäld från {competition.DisplayName}.";
            if (r.PaidAmount > 0)
            {
                msg += $" Du har redan betalat {Kr(r.PaidAmount)}. Arrangören har fått besked om att du ska få pengarna tillbaka" +
                       (r.OrganiserNotified ? "" : " (vi kunde inte mejla dem — kontakta arrangören)") + ".";
            }
            else if (r.ClaimedAmount > 0)
            {
                msg += $" Du angav att du betalat {Kr(r.ClaimedAmount)}. Arrangören har fått besked och kontrollerar om pengarna kommit, och återbetalar i så fall.";
            }
            if (r.PaidAmount + r.ClaimedAmount > 0 && !string.IsNullOrWhiteSpace(r.OrganiserEmail))
                msg += $" Frågor om återbetalningen: {r.OrganiserEmail}.";
            return msg;
        }

        private static string Kr(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")) + " kr";

        private static bool IsSpringskytte(CompetitionInfo c) =>
            string.Equals(c.CompetitionType, "Springskytte", StringComparison.OrdinalIgnoreCase);

        // ── Den delade borttagningen ─────────────────────────────────────────────────────────

        public sealed class DeleteOutcome
        {
            public bool Deleted { get; set; }
            public string? Error { get; set; }
            public CleanupOutcome Cleanup { get; set; } = new() { Supported = false };
        }

        /// <summary>
        /// Städflödet som BÅDE arrangörens och skyttens avanmälan går genom:
        /// 1) makulera öppna fakturor (betalda rörs inte), 2) ta bort anmälan, 3) räkna om liggarens avgifter
        /// (öppna begäranden makuleras), 4) ta skytten ur startlistor och resultat (precision och fält; springskytte
        /// städas av anroparen). 4 är efteråt och best-effort — anmälan är redan borta.
        /// </summary>
        public async Task<DeleteOutcome> DeleteRegistrationAsync(CompetitionInfo? competition, RegistrationRecord registration,
            int? actorId, string? actorName)
        {
            var competitionId = registration.CompetitionId;

            // Pending-fakturor makuleras FÖRE borttagningen — annars blir de kvar som "utestående" utan någon att kräva.
            if (competition != null)
            {
                foreach (var inv in InvoicesFor(competition, registration.Id))
                {
                    if (NormalizeStatus(inv.GetValue<string>("paymentStatus")) != "Pending") continue;
                    await _payments.UpdatePaymentStatusAsync(
                        invoiceId: inv.Id, paymentStatus: "Cancelled", notes: "Anmälan borttagen",
                        actorMemberId: actorId, actorMemberName: actorName, sendReceiptOnPaid: false);
                }
            }

            var memberId = registration.MemberId;
            if (!_registrations.Delete(competitionId, registration.Id))
                return new DeleteOutcome { Error = "Kunde inte ta bort anmälan." };

            if (competitionId > 0)
                _payments.SyncLedgerFees(competitionId, registrationId: registration.Id);

            var cleanup = competition != null && memberId > 0
                ? await _cleanup.CleanupAsync(competition, memberId)
                : new CleanupOutcome { Supported = false };

            return new DeleteOutcome { Deleted = true, Cleanup = cleanup };
        }

        // ── Pengarna ─────────────────────────────────────────────────────────────────────────

        public sealed class Money
        {
            public decimal Paid { get; set; }
            public decimal Claimed { get; set; }
            public bool Ledger { get; set; }
            public string? Note { get; set; }
        }

        /// <summary>
        /// Vad skytten betalat för anmälan, i den modell tävlingen har. Läser bara — får aldrig fälla avanmälan
        /// (ett fel loggas och ger 0, men då saknas påminnelsen; därför är felet en <c>LogError</c>).
        /// </summary>
        public Money MeasureMoney(CompetitionInfo competition, RegistrationRecord reg)
        {
            var money = new Money();
            try
            {
                if (_models.IsLedger(competition.Id))
                {
                    money.Ledger = true;
                    var rows = _fees.LoadFeeRows(competition.Id)
                        .Where(r => r.SourceType == HpskSite.Models.Ledger.LedgerSourceType.CompetitionRegistration && r.SourceItemId == reg.Id)
                        .ToList();
                    var fee = RegistrationFeeCalculator.Calculate(competition, reg.ClassIds, reg.IsSubCompetition);
                    var st = FeeItemStatus.For(fee, rows, _fees.LoadCoverage(rows));
                    money.Paid = st.Paid;
                    money.Claimed = st.Claimed;
                    if (st.Invoiced + st.InvoicePaid > 0)
                        money.Note = $"Klubbens del ({Kr(st.Invoiced + st.InvoicePaid)}) är fakturerad till klubben och hanteras med kreditfaktura.";
                }
                else
                {
                    foreach (var inv in InvoicesFor(competition, reg.Id))
                    {
                        if (NormalizeStatus(inv.GetValue<string>("paymentStatus")) == "Paid")
                            money.Paid += inv.GetValue<decimal?>("totalAmount") ?? 0m;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Avanmälan: kunde inte mäta vad som betalats för anmälan {RegistrationId} (tävling {CompetitionId}). Arrangören får ingen återbetalningsnotis om det fanns betalt.", reg.Id, competition.Id);
            }
            return money;
        }

        private List<IContent> InvoicesFor(CompetitionInfo competition, int registrationId)
        {
            var hub = _invoiceHubs.Hub(competition.Id);
            if (hub == null) return new List<IContent>();
            return _content.GetPagedChildren(hub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "registrationInvoice")
                .Where(c => NormalizeStatus(c.GetValue<string>("paymentStatus")) != "Cancelled")
                .Where(c =>
                {
                    if (c.GetValue<int>("registrationId") == registrationId) return true;
                    var related = c.GetValue<string>("relatedRegistrationIds") ?? "";
                    if (string.IsNullOrEmpty(related)) return false;
                    return related.Split(new[] { '[', ']', ',', ' ', '"' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(p => int.TryParse(p, out var id) && id == registrationId);
                })
                .OrderByDescending(c => c.Id)
                .ToList();
        }

        private static string NormalizeStatus(string? s) =>
            (s ?? "").Trim().Trim('[', ']').Trim('"', '\'').Trim();

        // ── Raden och mejlet till arrangören ─────────────────────────────────────────────────

        private string? ClubNameOf(int clubId)
        {
            try { return clubId > 0 ? _clubs.GetClubNameById(clubId) : null; }
            catch { return null; }
        }

        private bool Record(List<CompetitionWithdrawal> rows)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                foreach (var r in rows) db.Insert(r);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "AVANMÄLAN KUNDE INTE REGISTRERAS: finns dbo.CompetitionWithdrawal? Kör Migrations/create-competition-withdrawal-table.sql. Berörda anmälningar: {Ids}",
                    string.Join(",", rows.Select(r => $"{r.CompetitionId}/{r.RegistrationId}")));
                return false;
            }
        }

        private async Task<(bool Mailed, string OrganiserName, string OrganiserEmail)> NotifyOrganiserAsync(
            CompetitionInfo competition, int memberId, List<CompetitionWithdrawal> rows, decimal paid, decimal claimed)
        {
            string orgName = "", orgEmail = "";
            try
            {
                var organiser = _replyContacts.ForCompetitionOrganiser(competition.Id);
                if (organiser.Kind != MailReplyTo.ReplyKind.Address)
                {
                    _logger.LogWarning("Avanmälan med betald avgift på tävling {CompetitionId}: arrangören saknar e-postadress — ingen notis kunde mejlas (raden i Anmälningar finns).", competition.Id);
                    return (false, "", "");
                }
                orgName = organiser.Name;
                orgEmail = organiser.Email;

                var first = rows[0];
                var baseUrl = _http.HttpContext?.Request.PublicBase() ?? "https://pistol.nu";
                var url = $"{baseUrl}/competitionmanagement?competitionId={competition.Id}";
                var classes = string.Join(", ", rows.Select(r => r.Classes).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

                var ok = await _email.SendCompetitionWithdrawalAsync(
                    organiser.Email, organiser.Name, competition.DisplayName, first.MemberName, first.ClubName, classes,
                    paid, claimed, rows[0].PaymentModel == "ledger", url, _replyContacts.ForMember(memberId));

                if (ok)
                {
                    using var db = _databaseFactory.CreateDatabase();
                    foreach (var r in rows.Where(r => r.Id > 0))
                        db.Execute("UPDATE dbo.CompetitionWithdrawal SET OrganiserMailedAt = @0 WHERE Id = @1", DateTime.Now, r.Id);
                }
                return (ok, orgName, orgEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Avanmälan med betald avgift på tävling {CompetitionId}: arrangören kunde inte mejlas.", competition.Id);
                return (false, orgName, orgEmail);
            }
        }

        // ── Arrangörens ruta: "Ska få pengarna tillbaka" ─────────────────────────────────────

        /// <summary>Avanmälningar där pengar är inblandade: väntande först, därefter de redan återbetalade (senaste 90 dagarna).</summary>
        public List<CompetitionWithdrawal> ListForOrganiser(int competitionId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.Fetch<CompetitionWithdrawal>(
                    @"SELECT * FROM dbo.CompetitionWithdrawal
                      WHERE CompetitionId = @0 AND RefundStatus <> @1
                        AND (RefundStatus = @2 OR WithdrawnAt >= DATEADD(DAY, -90, GETDATE()))
                      ORDER BY CASE RefundStatus WHEN @2 THEN 0 ELSE 1 END, WithdrawnAt DESC",
                    competitionId, RefundStatuses.None, RefundStatuses.Pending);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Avanmälningar för tävling {CompetitionId} kunde inte läsas — finns dbo.CompetitionWithdrawal?", competitionId);
                throw;
            }
        }

        /// <summary>Arrangören markerar återbetalningen som gjord (eller ångrar markeringen). Prövas mot tävlingen.</summary>
        public bool SetRefunded(int competitionId, int withdrawalId, bool done, int? byMemberId, string? note)
        {
            using var db = _databaseFactory.CreateDatabase();
            var n = done
                ? db.Execute(@"UPDATE dbo.CompetitionWithdrawal
                               SET RefundStatus = @0, RefundedAt = @1, RefundedByMemberId = @2, RefundNote = @3
                               WHERE Id = @4 AND CompetitionId = @5 AND RefundStatus = @6",
                    RefundStatuses.Done, DateTime.Now, (object?)byMemberId ?? DBNull.Value, (object?)(string.IsNullOrWhiteSpace(note) ? null : note!.Trim()) ?? DBNull.Value, withdrawalId, competitionId, RefundStatuses.Pending)
                : db.Execute(@"UPDATE dbo.CompetitionWithdrawal
                               SET RefundStatus = @0, RefundedAt = NULL, RefundedByMemberId = NULL, RefundNote = NULL
                               WHERE Id = @1 AND CompetitionId = @2 AND RefundStatus = @3",
                    RefundStatuses.Pending, withdrawalId, competitionId, RefundStatuses.Done);
            return n > 0;
        }
    }
}
