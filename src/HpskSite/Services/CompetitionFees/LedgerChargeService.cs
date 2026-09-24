using HpskSite.Models;
using HpskSite.Models.CompetitionFees;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.CompetitionFees
{
    /// <summary>
    /// Fakturan till en klubb — utfärdad av arrangören i efterhand, över klubbens öppna avgifter.
    ///
    /// <para><b>⚠️⚠️ SAMLINGSFAKTURAN UPPHÖR SOM BEGREPP.</b> Det finns inga barnfakturor: klubben
    /// får EN faktura per utskick, med en rad per anmälan eller lag. Avgiftsraderna makuleras och
    /// pekar på fakturan (<see cref="LedgerPayment.CoveredByChargeId"/>) i samma transaktion som
    /// fakturan skrivs — det finns alltså aldrig ett läge där samma avgift går att betala på två
    /// sätt, och ingenting behöver undantas ur summor.</para>
    ///
    /// <para><b>Arrangörens handling, ett steg:</b> välj klubb, bocka rader, <i>Skapa och skicka</i>
    /// (<c>organiser-side-consolidation</c>: motivationen ligger hos arrangören, och klubben ska
    /// aldrig behöva förstå begreppet). Mejlet bär en länk som fungerar utan inloggning.</para>
    ///
    /// <para><b>⚠️ Bokförs inte vid utfärdandet.</b> Föreningen bokför enligt kontantmetoden: det är
    /// betalningen av fakturan som blir en verifikation (<see cref="LedgerSourceType.CompetitionInvoice"/>).
    /// Obetalda fakturor är kundfordringar i bokslutet och syns i fordringsexporten.</para>
    /// </summary>
    public class LedgerChargeService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly LedgerNumberAllocator _allocator;
        private readonly LedgerIssuerResolver _issuers;
        private readonly LedgerPaymentService _payments;
        private readonly CompetitionFeeService _fees;
        private readonly CompetitionPaymentModelService _models;
        private readonly ConsolidatedInvoiceService _payees;
        private readonly ClubService _clubService;
        private readonly ILogger<LedgerChargeService> _logger;

        public LedgerChargeService(
            IUmbracoDatabaseFactory databaseFactory,
            IContentService contentService,
            IUmbracoContextFactory contextFactory,
            LedgerNumberAllocator allocator,
            LedgerIssuerResolver issuers,
            LedgerPaymentService payments,
            CompetitionFeeService fees,
            CompetitionPaymentModelService models,
            ConsolidatedInvoiceService payees,
            ClubService clubService,
            ILogger<LedgerChargeService> logger)
        {
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _contextFactory = contextFactory;
            _allocator = allocator;
            _issuers = issuers;
            _payments = payments;
            _fees = fees;
            _models = models;
            _payees = payees;
            _clubService = clubService;
            _logger = logger;
        }

        public sealed class ChargeResult
        {
            public bool Success => Error is null;
            public string? Error { get; set; }
            public LedgerCharge? Charge { get; set; }
            public static ChargeResult Failed(string error) => new() { Error = error };
        }

        // ── Utfärda ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ställer ut en faktura till EN klubb över valda öppna avgifter på EN tävling.
        ///
        /// <para><b>⚠️ Varje rad prövas mot databasen, inte mot klientens urval:</b> den måste höra
        /// till tävlingen, vara öppen (varken påstådd, mottagen eller makulerad) och ha klubben som
        /// anmälans klubb. Grupperingen sker på ANMÄLANS klubb, aldrig medlemmens primärklubb — en
        /// skytt som tävlar för klubb Y ska inte hamna på X räkning.</para>
        ///
        /// <para><b>⚠️ Makuleringen av avgiftsraderna villkoras i WHERE</b> och måste träffa exakt
        /// lika många rader som valts. Har någon hunnit betala eller påstå betalning under tiden
        /// rullas allt tillbaka — fakturan skrivs inte, numret återlämnas (transaktionen).</para>
        /// </summary>
        public ChargeResult CreateInvoice(
            int competitionId, int recipientClubId, IReadOnlyCollection<int> paymentIds,
            DateTime? dueDate, string? note, string? recipientEmail, int byMemberId)
        {
            if (!_models.IsLedger(competitionId))
                return ChargeResult.Failed("Tävlingen använder det gamla fakturasättet.");
            if (recipientClubId <= 0) return ChargeResult.Failed("Välj vilken klubb som ska faktureras.");
            if (paymentIds == null || paymentIds.Count == 0) return ChargeResult.Failed("Välj minst en avgift.");

            var issuer = _issuers.ResolveForCompetition(competitionId);
            if (issuer == null) return ChargeResult.Failed("Arrangören går inte att avgöra.");

            var all = _fees.LoadFeeRows(competitionId).ToDictionary(r => r.Id);
            var chosen = new List<LedgerPayment>();
            foreach (var id in paymentIds.Distinct())
            {
                if (!all.TryGetValue(id, out var row))
                    return ChargeResult.Failed("En av avgifterna hör inte till tävlingen.");
                if (row.VoidedUtc is not null || row.ConfirmedUtc is not null || row.ClaimedUtc is not null)
                    return ChargeResult.Failed($"Avgiften för {row.PayerName} är inte längre öppen — ladda om listan.");
                if (row.PayerClubId != recipientClubId)
                    return ChargeResult.Failed($"{row.PayerName} är anmäld för en annan klubb och kan inte ligga på den här fakturan.");
                chosen.Add(row);
            }

            var competition = _contentService.GetById(competitionId);
            var competitionName = competition?.GetValue<string>("competitionName") ?? competition?.Name ?? $"Tävling {competitionId}";
            var recipientName = _clubService.GetClubNameById(recipientClubId) ?? $"Förening #{recipientClubId}";
            var details = LoadIssuerDetails(issuer.Value.Type, issuer.Value.Id);
            var payee = _payees.ResolvePayee(competitionId);
            var settings = LoadVatRegistered(issuer.Value.Type, issuer.Value.Id);
            var issueDate = DateTime.Today;

            var lines = chosen
                .OrderBy(r => r.SourceType == LedgerSourceType.TeamFee)
                .ThenBy(r => r.PayerName)
                .Select(r => new LedgerChargeLine
                {
                    PaymentId = r.Id,
                    ItemType = r.SourceType,
                    ItemId = r.SourceItemId,
                    Description = DescribeLine(r),
                    Amount = r.Amount
                })
                .ToList();

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuer.Value.Id);
            try
            {
                using var tx = ldb.GetTransaction();

                var (seriesId, number, prefix) = _allocator.Allocate(
                    db, issuer.Value.Type, issuer.Value.Id, issueDate.Year, LedgerSeriesKind.Invoice);
                var numberText = LedgerNumberAllocator.Format(prefix, number);

                var charge = new LedgerCharge
                {
                    IssuerType = issuer.Value.Type,
                    IssuerId = issuer.Value.Id,
                    Kind = LedgerChargeKind.Invoice,
                    RecipientType = DocumentOwnerType.Club,
                    RecipientId = recipientClubId,
                    RecipientName = recipientName,
                    RecipientEmail = string.IsNullOrWhiteSpace(recipientEmail) ? null : recipientEmail.Trim(),
                    SourceType = LedgerSourceType.CompetitionInvoice,
                    SourceId = competitionId,
                    SourceName = competitionName,
                    SeriesId = seriesId,
                    Number = number,
                    NumberText = numberText,
                    Reference = ReferenceFor(competitionId, numberText),
                    IssueDate = issueDate,
                    DueDate = (dueDate ?? issueDate.AddDays(30)).Date,
                    Amount = lines.Sum(l => l.Amount),
                    IssuerName = details.Name,
                    IssuerOrgNumber = details.OrgNumber,
                    IssuerAddress = details.Address,
                    IssuerEmail = details.Email,
                    IssuerBankgiro = string.IsNullOrWhiteSpace(payee.BgNumber) ? null : payee.BgNumber,
                    IssuerSwish = string.IsNullOrWhiteSpace(payee.SwishNumber) ? null : payee.SwishNumber,
                    IssuerIsVatRegistered = settings,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    CreatedUtc = DateTime.UtcNow,
                    CreatedByMemberId = byMemberId
                };

                charge.Id = InsertCharge(ldb, charge);
                foreach (var l in lines)
                {
                    l.ChargeId = charge.Id;
                    InsertLine(ldb, l);
                }

                var covered = ldb.Execute(
                    @"UPDATE dbo.LedgerPayment
                         SET VoidedUtc = @1, VoidedByMemberId = @2, VoidReason = @3, CoveredByChargeId = @4
                       WHERE Id IN (@0) AND VoidedUtc IS NULL AND ConfirmedUtc IS NULL AND ClaimedUtc IS NULL",
                    chosen.Select(c => c.Id).ToList(), DateTime.UtcNow, byMemberId,
                    $"Fakturerad {recipientName} — faktura {numberText}", charge.Id);

                if (covered != chosen.Count)
                {
                    // Transaktionen fullbordas inte → allt rullas tillbaka, även numret.
                    return ChargeResult.Failed(
                        "Någon hann betala eller anmäla betalning för en av avgifterna under tiden. "
                        + "Ingen faktura skapades — ladda om listan och försök igen.");
                }

                tx.Complete();
                charge.Lines = lines;
                return new ChargeResult { Charge = charge };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fakturan till klubb {ClubId} för tävling {CompetitionId} kunde inte skapas.",
                    recipientClubId, competitionId);
                return ChargeResult.Failed("Fakturan kunde inte skapas. Ingenting sparades — försök igen.");
            }
        }

        /// <summary>Referensen: tävlingens id och fakturanumret utan bindestreck ("8695-F12").</summary>
        public static string ReferenceFor(int competitionId, string numberText)
            => $"{competitionId}-{numberText.Replace("-", "")}";

        private static string DescribeLine(LedgerPayment r)
        {
            var what = r.SourceType == LedgerSourceType.TeamFee ? "Lagavgift" : "Anmälningsavgift";
            var part = r.FeePart == CompetitionFeePart.Club ? " (klubbens del)" : "";
            return $"{what}{part} — {r.PayerName}";
        }

        // ── Makulera och kreditera ───────────────────────────────────────────────────────────

        /// <summary>
        /// Makulerar en <b>obetald</b> faktura. Avgifterna blir öppna igen (nya begärda rader) —
        /// synken gör det, eftersom en makulerad faktura inte täcker något.
        /// <para>⚠️ En faktura med en mottagen eller påstådd betalning makuleras inte: den krediteras.</para>
        /// </summary>
        public ChargeResult Void(int chargeId, string reason, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(reason)) return ChargeResult.Failed("Ange varför fakturan makuleras.");
            var charge = Get(chargeId);
            if (charge == null) return ChargeResult.Failed("Fakturan finns inte.");
            if (charge.IsVoided) return ChargeResult.Failed("Fakturan är redan makulerad.");
            if (charge.IsCredit) return ChargeResult.Failed("En kreditnota makuleras inte.");

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, charge.IssuerId);
            var moneyOrClaim = ldb.ExecuteScalar<int>(
                @"SELECT COUNT(1) FROM dbo.LedgerPayment
                   WHERE ChargeId = @0 AND VoidedUtc IS NULL AND (ConfirmedUtc IS NOT NULL OR ClaimedUtc IS NOT NULL)",
                chargeId);
            if (moneyOrClaim > 0)
                return ChargeResult.Failed("Fakturan har en betalning. Kreditera den i stället för att makulera.");
            var credits = ldb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.LedgerCharge WHERE CreditsChargeId = @0 AND VoidedUtc IS NULL", chargeId);
            if (credits > 0)
                return ChargeResult.Failed("Fakturan har redan krediterats delvis. Kreditera resten i stället.");

            ldb.Execute(
                @"UPDATE dbo.LedgerCharge SET VoidedUtc = @1, VoidedByMemberId = @2, VoidReason = @3
                   WHERE Id = @0 AND VoidedUtc IS NULL",
                chargeId, DateTime.UtcNow, byMemberId, reason.Trim());

            // Öppna betalningsbegäranden på fakturan makuleras också.
            ldb.Execute(
                @"UPDATE dbo.LedgerPayment SET VoidedUtc = @1, VoidedByMemberId = @2, VoidReason = @3
                   WHERE ChargeId = @0 AND VoidedUtc IS NULL AND ConfirmedUtc IS NULL AND ClaimedUtc IS NULL",
                chargeId, DateTime.UtcNow, byMemberId, "Fakturan makulerades.");

            ResyncItems(charge, byMemberId);
            return new ChargeResult { Charge = Get(chargeId) };
        }

        /// <summary>
        /// Kreditnota över valda rader av en utfärdad faktura. Raderna släpps: finns anmälan kvar och
        /// är fortfarande skyldig får den en ny öppen avgift; är den avanmäld finns inget att begära.
        /// <para>⚠️ Kreditnotan får ett nummer ur SAMMA serie som fakturorna — en kreditfaktura är en
        /// faktura. Den pekar på exakt en faktura och kan inte kreditera mer än som återstår per rad.</para>
        /// </summary>
        public ChargeResult Credit(int chargeId, IReadOnlyCollection<int> lineIds, string reason, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(reason)) return ChargeResult.Failed("Ange varför fakturan krediteras.");
            var invoice = Get(chargeId);
            if (invoice == null || invoice.IsCredit) return ChargeResult.Failed("Fakturan finns inte.");
            if (invoice.IsVoided) return ChargeResult.Failed("Fakturan är makulerad.");

            var alreadyCredited = CreditedPerLine(invoice);
            var chosen = invoice.Lines
                .Where(l => lineIds.Contains(l.Id))
                .Select(l => (Line: l, Left: l.Amount + alreadyCredited.GetValueOrDefault(l.PaymentId ?? -l.Id)))
                .Where(x => x.Left > 0)
                .ToList();
            if (chosen.Count == 0) return ChargeResult.Failed("Välj minst en rad som inte redan är krediterad.");

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, invoice.IssuerId);
            try
            {
                using var tx = ldb.GetTransaction();
                var issueDate = DateTime.Today;
                var (seriesId, number, prefix) = _allocator.Allocate(
                    db, invoice.IssuerType, invoice.IssuerId, issueDate.Year, LedgerSeriesKind.Invoice);
                var numberText = LedgerNumberAllocator.Format(prefix, number);

                var credit = new LedgerCharge
                {
                    IssuerType = invoice.IssuerType,
                    IssuerId = invoice.IssuerId,
                    Kind = LedgerChargeKind.Credit,
                    CreditsChargeId = invoice.Id,
                    RecipientType = invoice.RecipientType,
                    RecipientId = invoice.RecipientId,
                    RecipientName = invoice.RecipientName,
                    RecipientEmail = invoice.RecipientEmail,
                    SourceType = invoice.SourceType,
                    SourceId = invoice.SourceId,
                    SourceName = invoice.SourceName,
                    SeriesId = seriesId,
                    Number = number,
                    NumberText = numberText,
                    Reference = invoice.Reference,
                    IssueDate = issueDate,
                    DueDate = null,
                    Amount = -chosen.Sum(x => x.Left),
                    IssuerName = invoice.IssuerName,
                    IssuerOrgNumber = invoice.IssuerOrgNumber,
                    IssuerAddress = invoice.IssuerAddress,
                    IssuerEmail = invoice.IssuerEmail,
                    IssuerBankgiro = invoice.IssuerBankgiro,
                    IssuerSwish = invoice.IssuerSwish,
                    IssuerIsVatRegistered = invoice.IssuerIsVatRegistered,
                    Note = reason.Trim(),
                    CreatedUtc = DateTime.UtcNow,
                    CreatedByMemberId = byMemberId
                };
                credit.Id = InsertCharge(ldb, credit);
                foreach (var (line, left) in chosen)
                {
                    InsertLine(ldb, new LedgerChargeLine
                    {
                        ChargeId = credit.Id,
                        PaymentId = line.PaymentId,
                        ItemType = line.ItemType,
                        ItemId = line.ItemId,
                        Description = "Kredit: " + line.Description,
                        Amount = -left
                    });
                }
                tx.Complete();

                ResyncItems(invoice, byMemberId);
                return new ChargeResult { Charge = Get(credit.Id) };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kreditnota för faktura {ChargeId} kunde inte skapas.", chargeId);
                return ChargeResult.Failed("Kreditnotan kunde inte skapas. Ingenting sparades — försök igen.");
            }
        }

        /// <summary>Redan krediterat per avgiftsrad (negativt), över kreditnotor som inte är makulerade.</summary>
        private Dictionary<int, decimal> CreditedPerLine(LedgerCharge invoice)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, invoice.IssuerId);
            return ldb.Fetch<LedgerChargeLine>(
                    @"SELECT l.* FROM dbo.LedgerChargeLine l
                        JOIN dbo.LedgerCharge c ON c.Id = l.ChargeId
                       WHERE c.CreditsChargeId = @0 AND c.VoidedUtc IS NULL",
                    invoice.Id)
                .GroupBy(l => l.PaymentId ?? 0)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        }

        private void ResyncItems(LedgerCharge charge, int byMemberId)
        {
            foreach (var line in charge.Lines.Where(l => l.ItemId is > 0).GroupBy(l => (l.ItemType, l.ItemId)).Select(g => g.First()))
            {
                try
                {
                    if (line.ItemType == LedgerSourceType.TeamFee)
                        _fees.SyncTeam(charge.SourceId, line.ItemId!.Value, byMemberId);
                    else
                        _fees.SyncRegistration(charge.SourceId, line.ItemId!.Value, byMemberId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Avgiften för {ItemType} {ItemId} kunde inte räknas om efter faktura {ChargeId}.",
                        line.ItemType, line.ItemId, charge.Id);
                }
            }
        }

        // ── Betala ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Arrangören registrerar att fakturans pengar kommit — oftast en bankgiroinbetalning.
        /// <para>Skapar betalningsraden och bekräftar den i samma handling (kvitto + verifikation via
        /// <see cref="LedgerPaymentService.Confirm"/>). Återanvänder en öppen eller påstådd rad.</para>
        /// </summary>
        public LedgerPaymentService.ConfirmResult RegisterPayment(
            int chargeId, decimal amount, DateTime? paymentDate, string method, int byMemberId)
        {
            if (amount <= 0) return LedgerPaymentService.ConfirmResult.Failed("Ange ett belopp större än noll.");
            if (!LedgerPaymentMethod.IsValid(method)) return LedgerPaymentService.ConfirmResult.Failed("Välj hur betalningen kom in.");

            var charge = Get(chargeId);
            if (charge == null || charge.IsCredit) return LedgerPaymentService.ConfirmResult.Failed("Fakturan finns inte.");
            if (charge.IsVoided) return LedgerPaymentService.ConfirmResult.Failed("Fakturan är makulerad.");

            var open = PaymentsFor(charge).FirstOrDefault(p => p.VoidedUtc is null && p.ConfirmedUtc is null);
            int paymentId;
            if (open != null) paymentId = open.Id;
            else
            {
                var id = _payments.Request(NewChargePayment(charge, amount, method));
                if (id == null) return LedgerPaymentService.ConfirmResult.Failed("Betalningen kunde inte skapas.");
                paymentId = id.Value;
            }
            return _payments.Confirm(paymentId, byMemberId, paymentDate, amount);
        }

        /// <summary>
        /// Den betalande klubben säger att den betalat. <b>Inte pengar</b> — arrangören bekräftar.
        /// </summary>
        public bool RegisterClaim(int chargeId, int byMemberId)
        {
            var charge = Get(chargeId);
            if (charge == null || charge.IsCredit || charge.IsVoided) return false;
            var balance = BalanceOf(charge);
            if (balance.Outstanding <= 0) return false;

            var open = PaymentsFor(charge).FirstOrDefault(p => p.VoidedUtc is null && p.ConfirmedUtc is null);
            if (open?.ClaimedUtc is not null) return true;
            var id = open?.Id ?? _payments.Request(NewChargePayment(charge, balance.Outstanding, LedgerPaymentMethod.BankGiro));
            return id != null && _payments.RegisterClaim(id.Value, byMemberId);
        }

        private static LedgerPayment NewChargePayment(LedgerCharge charge, decimal amount, string method) => new()
        {
            IssuerType = charge.IssuerType,
            IssuerId = charge.IssuerId,
            SourceType = LedgerSourceType.CompetitionInvoice,
            SourceId = charge.SourceId,
            ChargeId = charge.Id,
            PayerClubId = charge.RecipientId,
            PayerName = charge.RecipientName,
            Amount = amount,
            Method = method
        };

        // ── Läsa ─────────────────────────────────────────────────────────────────────────────

        public LedgerCharge? Get(int chargeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, chargeId);
            var charge = ldb.SingleOrDefault<LedgerCharge>("SELECT * FROM dbo.LedgerCharge WHERE Id = @0", chargeId);
            if (charge == null) return null;
            charge.Lines = ldb.Fetch<LedgerChargeLine>(
                "SELECT * FROM dbo.LedgerChargeLine WHERE ChargeId = @0 ORDER BY Id", chargeId);
            return charge;
        }

        public List<LedgerPayment> PaymentsFor(LedgerCharge charge)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, charge.IssuerId);
            return ldb.Fetch<LedgerPayment>("SELECT * FROM dbo.LedgerPayment WHERE ChargeId = @0 ORDER BY Id", charge.Id);
        }

        public List<LedgerCharge> CreditsFor(LedgerCharge invoice)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, invoice.IssuerId);
            return ldb.Fetch<LedgerCharge>("SELECT * FROM dbo.LedgerCharge WHERE CreditsChargeId = @0 ORDER BY Id", invoice.Id);
        }

        public LedgerChargeBalance BalanceOf(LedgerCharge invoice)
            => LedgerChargeBalance.For(invoice, CreditsFor(invoice), PaymentsFor(invoice));

        /// <summary>En faktura med allt som behövs för att visa den: rader, kreditnotor, betalningar, saldo.</summary>
        public sealed class ChargeView
        {
            public LedgerCharge Charge { get; set; } = new();
            public List<LedgerCharge> Credits { get; set; } = new();
            public List<LedgerPayment> Payments { get; set; } = new();
            public LedgerChargeBalance Balance { get; set; }
            public bool IsOverdue => !Charge.IsVoided && Balance.Outstanding > 0
                                     && Charge.DueDate is DateTime d && d.Date < DateTime.Today;
        }

        /// <summary>
        /// Fakturor enligt ett urval. Läser allt i tre frågor och sätter ihop i minnet.
        /// <para>⚠️ Kreditnotor listas under sin faktura, aldrig som egna rader — annars hade en
        /// summering över listan dragit av krediten två gånger.</para>
        /// </summary>
        public List<ChargeView> List(string where, params object[] args)
        {
            using var db = _databaseFactory.CreateDatabase();
            // LEDGER-SEAM-OK: fakturor till klubbar ställs ut från tävlingens SKARPA utställare, som
            // avgifterna. dbo med flit.
            var charges = db.Fetch<LedgerCharge>($"SELECT * FROM dbo.LedgerCharge WHERE {where} ORDER BY IssueDate DESC, Id DESC", args);
            if (charges.Count == 0) return new List<ChargeView>();

            var ids = charges.Select(c => c.Id).ToList();
            var lines = new List<LedgerChargeLine>();
            var pays = new List<LedgerPayment>();
            var credits = new List<LedgerCharge>();
            foreach (var chunk in ids.Chunk(500))
            {
                var list = chunk.ToList();
                lines.AddRange(db.Fetch<LedgerChargeLine>("SELECT * FROM dbo.LedgerChargeLine WHERE ChargeId IN (@0)", list));
                pays.AddRange(db.Fetch<LedgerPayment>("SELECT * FROM dbo.LedgerPayment WHERE ChargeId IN (@0)", list));
                credits.AddRange(db.Fetch<LedgerCharge>("SELECT * FROM dbo.LedgerCharge WHERE CreditsChargeId IN (@0)", list));
            }
            // Kreditnotornas egna rader, för visningen.
            var creditIds = credits.Select(c => c.Id).Except(ids).ToList();
            foreach (var chunk in creditIds.Chunk(500))
                lines.AddRange(db.Fetch<LedgerChargeLine>("SELECT * FROM dbo.LedgerChargeLine WHERE ChargeId IN (@0)", chunk.ToList()));

            var linesBy = lines.GroupBy(l => l.ChargeId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var c in charges.Concat(credits)) c.Lines = linesBy.GetValueOrDefault(c.Id) ?? new List<LedgerChargeLine>();

            return charges
                .Where(c => !c.IsCredit)
                .Select(c =>
                {
                    var myCredits = credits.Where(k => k.CreditsChargeId == c.Id).ToList();
                    var myPays = pays.Where(p => p.ChargeId == c.Id).ToList();
                    return new ChargeView
                    {
                        Charge = c,
                        Credits = myCredits,
                        Payments = myPays,
                        Balance = LedgerChargeBalance.For(c, myCredits, myPays)
                    };
                })
                .ToList();
        }

        public List<ChargeView> ListForCompetition(int competitionId)
            => List("SourceType = @0 AND SourceId = @1", LedgerSourceType.CompetitionInvoice, competitionId);

        public List<ChargeView> ListForIssuer(int issuerType, int issuerId)
            => List("IssuerType = @0 AND IssuerId = @1", issuerType, issuerId);

        /// <summary>Fakturor TILL en klubb — dess inkommande räkningar från andra arrangörer.</summary>
        public List<ChargeView> ListForRecipient(int recipientType, int recipientId)
            => List("RecipientType = @0 AND RecipientId = @1", recipientType, recipientId);

        /// <summary>Utskicket får skrivas efter utfärdandet — det enda utöver makuleringen.</summary>
        public void MarkSent(int chargeId, string email)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, chargeId);
            ldb.Execute("UPDATE dbo.LedgerCharge SET SentUtc = @1, SentToEmail = @2 WHERE Id = @0",
                chargeId, DateTime.UtcNow, email);
        }

        // ── Snapshot av utställaren ──────────────────────────────────────────────────────────

        private readonly record struct IssuerDetails(string Name, string? OrgNumber, string? Address, string? Email);

        /// <summary>
        /// Utställarens namn, org.nr, adress och e-post ur noden — fryses på fakturan.
        /// <para>⚠️ Samma källa som kvittot (<c>LedgerPaymentService</c>), inte <c>ClubService</c>:
        /// dess <c>ClubInfo</c> saknar adressblocket.</para>
        /// </summary>
        private IssuerDetails LoadIssuerDetails(int issuerType, int issuerId)
        {
            try
            {
                using var cref = _contextFactory.EnsureUmbracoContext();
                var node = cref.UmbracoContext.Content?.GetById(issuerId);
                if (node is null) return new IssuerDetails("", null, null, null);

                var name = issuerType == DocumentOwnerType.Club
                    ? node.Value<string>("clubName") ?? node.Name ?? ""
                    : node.Name ?? "";
                var street = node.Value<string>("address");
                var zipCity = string.Join(" ", new[] { node.Value<string>("postalCode"), node.Value<string>("city") }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                var address = string.Join(", ", new[] { street, zipCity }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var email = node.Value<string>("receiptEmail");
                if (string.IsNullOrWhiteSpace(email)) email = node.Value<string>("contactEmail");

                return new IssuerDetails(name, node.Value<string>("orgNumber"),
                    string.IsNullOrWhiteSpace(address) ? null : address, email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa utställarens uppgifter för {Typ}/{Id}.", issuerType, issuerId);
                return new IssuerDetails("", null, null, null);
            }
        }

        private bool LoadVatRegistered(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>(
                LedgerSchema.Sql(issuerId,
                    "SELECT COUNT(1) FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1 AND IsVatRegistered = 1"),
                issuerType, issuerId) > 0;
        }

        // ── SQL ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ SCOPE_IDENTITY, inte OUTPUT: tabellen bär en trigger, och SQL Server vägrar OUTPUT
        /// utan INTO då (samma fälla som kvittot i <c>LedgerPaymentService.Confirm</c>).
        /// </summary>
        private static int InsertCharge(LedgerDb ldb, LedgerCharge c)
            => ldb.ExecuteScalar<int>(
                @"INSERT INTO dbo.LedgerCharge
                    (IssuerType, IssuerId, Kind, CreditsChargeId, RecipientType, RecipientId, RecipientName,
                     RecipientEmail, SourceType, SourceId, SourceName, SeriesId, Number, NumberText, Reference,
                     IssueDate, DueDate, Amount, IssuerName, IssuerOrgNumber, IssuerAddress, IssuerEmail,
                     IssuerBankgiro, IssuerSwish, IssuerIsVatRegistered, Note, CreatedUtc, CreatedByMemberId)
                  VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19,@20,@21,@22,@23,@24,@25,@26,@27);
                  SELECT CAST(SCOPE_IDENTITY() AS int);",
                c.IssuerType, c.IssuerId, c.Kind, (object?)c.CreditsChargeId ?? DBNull.Value,
                c.RecipientType, c.RecipientId, c.RecipientName, (object?)c.RecipientEmail ?? DBNull.Value,
                c.SourceType, c.SourceId, c.SourceName, c.SeriesId, c.Number, c.NumberText, c.Reference,
                c.IssueDate, (object?)c.DueDate ?? DBNull.Value, c.Amount, c.IssuerName,
                (object?)c.IssuerOrgNumber ?? DBNull.Value, (object?)c.IssuerAddress ?? DBNull.Value,
                (object?)c.IssuerEmail ?? DBNull.Value, (object?)c.IssuerBankgiro ?? DBNull.Value,
                (object?)c.IssuerSwish ?? DBNull.Value, c.IssuerIsVatRegistered, (object?)c.Note ?? DBNull.Value,
                c.CreatedUtc, c.CreatedByMemberId);

        private static void InsertLine(LedgerDb ldb, LedgerChargeLine l)
            => ldb.Execute(
                @"INSERT INTO dbo.LedgerChargeLine (ChargeId, PaymentId, ItemType, ItemId, Description, Amount)
                  VALUES (@0,@1,@2,@3,@4,@5)",
                l.ChargeId, (object?)l.PaymentId ?? DBNull.Value, (object?)l.ItemType ?? DBNull.Value,
                (object?)l.ItemId ?? DBNull.Value, l.Description, l.Amount);
    }
}
