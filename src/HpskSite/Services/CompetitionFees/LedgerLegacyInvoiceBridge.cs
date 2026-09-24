using HpskSite.Models;
using HpskSite.Models.CompetitionFees;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.CompetitionFees
{
    /// <summary>
    /// Läsbryggan från den GAMLA fakturamodellen till liggaren (Stefans beslut 2026-09-24).
    ///
    /// <para><b>⚠️⚠️ VARFÖR DEN BEHÖVS.</b> En tävling som fick sina första anmälningar före bytet
    /// lever kvar i den gamla modellen hela sin livstid. Betalas en sådan faktura EFTER att
    /// föreningen börjat bokföra i pistol.nu, hamnar pengarna annars utanför bokföringen — tyst.
    /// Bankavstämningen hade visat en insättning som ingen verifikation förklarar.</para>
    ///
    /// <para><b>⚠️ Den gamla fakturan rörs ALDRIG.</b> Den är fryst historik som aldrig räknas om.
    /// Bryggan läser den och skriver en verifikation; spärren mot dubbelbokföring är liggaren själv
    /// (<c>SourceType = legacy-invoice</c>, <c>SourceId = fakturans nod-id</c>), samma form som
    /// medlemsavgiftsbryggan.</para>
    ///
    /// <para><b>⚠️ Vad som INTE bokförs:</b> barnfakturor som täcks av en samlingsfaktura (den gamla
    /// kaskaden skrev "Paid" på dem fast pengarna kom via föräldern — fel 3 — så de hade bokförts två
    /// gånger), kreditfakturor, och fakturor betalda före föreningens första räkenskapsår (de ingår i
    /// de ingående balanserna).</para>
    /// </summary>
    public class LedgerLegacyInvoiceBridge
    {
        public const string SourceType = "legacy-invoice";

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly LedgerPostingService _posting;
        private readonly LedgerIssuerResolver _issuers;
        private readonly LedgerSourceProjectResolver _projects;
        private readonly ILogger<LedgerLegacyInvoiceBridge> _logger;

        public LedgerLegacyInvoiceBridge(
            IUmbracoDatabaseFactory databaseFactory,
            IContentService contentService,
            LedgerPostingService posting,
            LedgerIssuerResolver issuers,
            LedgerSourceProjectResolver projects,
            ILogger<LedgerLegacyInvoiceBridge> logger)
        {
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _posting = posting;
            _issuers = issuers;
            _projects = projects;
            _logger = logger;
        }

        public sealed class LegacyPaidInvoice
        {
            public int InvoiceId { get; set; }
            public int CompetitionId { get; set; }
            public string CompetitionName { get; set; } = "";
            public string InvoiceNumber { get; set; } = "";
            public string PayerName { get; set; } = "";
            public decimal Amount { get; set; }
            public DateTime PaidDate { get; set; }
            public string Method { get; set; } = "";
        }

        /// <summary>
        /// Betalda gamla fakturor som ännu inte är bokförda, för en förening som bokför här.
        /// Tom lista för en förening som inte bokför i pistol.nu — då finns ingen verifikation att skriva.
        /// </summary>
        public List<LegacyPaidInvoice> Unposted(int ownerType, int ownerId)
        {
            var result = new List<LegacyPaidInvoice>();
            if (ownerId <= 0) return result;

            using var db = _databaseFactory.CreateDatabase();
            // LEDGER-SEAM-OK: gamla fakturor finns bara i den levande världen, som medlemsavgifterna.
            var settings = db.FirstOrDefault<LedgerIssuerSettings>(
                "SELECT * FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1", ownerType, ownerId);
            if (!LedgerIssuerShape.KeepsBooks(settings?.Shape)) return result;

            var start = db.ExecuteScalar<DateTime?>(
                "SELECT MIN(StartDate) FROM dbo.LedgerFiscalYear WHERE IssuerType = @0 AND IssuerId = @1", ownerType, ownerId);
            if (start == null) return result;

            var legacyComps = db.Fetch<int>(
                "SELECT CompetitionId FROM dbo.CompetitionPaymentModel WHERE Model = @0", CompetitionPaymentModels.Legacy);

            foreach (var compId in legacyComps)
            {
                var issuer = _issuers.ResolveForCompetition(compId);
                if (issuer == null || issuer.Value.Type != ownerType || issuer.Value.Id != ownerId) continue;

                var competition = _contentService.GetById(compId);
                if (competition == null) continue;
                var compName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";

                foreach (var inv in LoadInvoices(competition))
                {
                    var status = (inv.GetValue<string>("paymentStatus") ?? "").Trim().Trim('[', ']').Trim('"', '\'').Trim();
                    if (status != "Paid") continue;
                    var kind = inv.GetValue<string>("invoiceKind") ?? "";
                    if (kind == "creditNote") continue;
                    // ⚠️ Barnet till en samlingsfaktura: pengarna kom via föräldern.
                    var settledBy = inv.HasProperty("settledByInvoiceId") ? inv.GetValue<string>("settledByInvoiceId") : null;
                    if (!string.IsNullOrWhiteSpace(settledBy) && settledBy != "0") continue;

                    var paid = inv.GetValue<DateTime?>("paymentDate");
                    if (paid == null || paid.Value.Date < start.Value.Date) continue;

                    var amount = inv.GetValue<decimal?>("actualPaidAmount") ?? inv.GetValue<decimal>("totalAmount");
                    if (amount <= 0) continue;

                    result.Add(new LegacyPaidInvoice
                    {
                        InvoiceId = inv.Id,
                        CompetitionId = compId,
                        CompetitionName = compName,
                        InvoiceNumber = inv.GetValue<string>("invoiceNumber") ?? "",
                        PayerName = inv.GetValue<string>("memberName") ?? "",
                        Amount = amount,
                        PaidDate = paid.Value.Date,
                        Method = inv.GetValue<string>("paymentMethod") ?? ""
                    });
                }
            }

            if (result.Count == 0) return result;
            var posted = PostedIds(db, result.Select(r => r.InvoiceId));
            return result.Where(r => !posted.Contains(r.InvoiceId)).OrderBy(r => r.PaidDate).ToList();
        }

        private IEnumerable<IContent> LoadInvoices(IContent competition)
        {
            var hubs = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .Where(c => c.ContentType.Alias == "registrationInvoicesHub").ToList();
            foreach (var hub in hubs)
                foreach (var inv in _contentService.GetPagedChildren(hub.Id, 0, 5000, out _)
                             .Where(c => c.ContentType.Alias == "registrationInvoice" && !c.Trashed))
                    yield return inv;
        }

        /// <summary>
        /// Bokför betalda gamla fakturor. Betaldagen är bokföringsdagen (kontantmetoden).
        /// Returnerar hur många som bokfördes.
        /// </summary>
        public int PostAll(int ownerType, int ownerId, int byMemberId)
        {
            var count = 0;
            foreach (var inv in Unposted(ownerType, ownerId))
                if (Post(ownerType, ownerId, inv, byMemberId) != null) count++;
            return count;
        }

        private int? Post(int ownerType, int ownerId, LegacyPaidInvoice inv, int byMemberId)
        {
            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                    if (PostedIds(db, new[] { inv.InvoiceId }).Count > 0) return null;

                // Samma projekt som den nya modellens avgifter för tävlingen — tävlingen är projektet.
                var projectId = _projects.Resolve(ownerType, ownerId, LedgerSourceType.CompetitionRegistration,
                    inv.CompetitionId, byMemberId);

                var bankRole = string.Equals(inv.Method, "Kontant", StringComparison.OrdinalIgnoreCase)
                    ? LedgerPaymentMethod.RoleFor(LedgerPaymentMethod.Cash)
                    : LedgerAccountRoles.BankAccount;

                var result = _posting.Post(new LedgerPostingRequest
                {
                    IssuerType = ownerType,
                    IssuerId = ownerId,
                    AccountingDate = inv.PaidDate,
                    EventDate = inv.PaidDate,
                    Description = $"Anmälningsavgift, faktura {inv.InvoiceNumber} ({inv.CompetitionName})",
                    CounterpartyName = inv.PayerName,
                    // ⚠️ SourceType/SourceId ÄR spärren mot dubbelbokföring — se PostedIds.
                    SourceType = SourceType,
                    SourceId = inv.InvoiceId,
                    ProjectId = projectId,
                    CreatedByMemberId = byMemberId,
                    Lines = new List<LedgerPostingLine>
                    {
                        new() { Role = bankRole, Debit = inv.Amount, VatRate = 0 },
                        new() { Role = LedgerAccountRoles.RevenueParticipationFee, Credit = inv.Amount, Text = inv.PayerName }
                    }
                });

                if (!result.Success)
                {
                    _logger.LogWarning("Gammal faktura {InvoiceId} kunde inte bokföras: {Fel}", inv.InvoiceId, result.Error);
                    return null;
                }
                return result.EntryId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bokföringen av gammal faktura {InvoiceId} fallerade.", inv.InvoiceId);
                return null;
            }
        }

        /// <summary>Bara poster som står kvar — en rättad post räknas inte som bokförd.</summary>
        private static HashSet<int> PostedIds(IUmbracoDatabase db, IEnumerable<int> invoiceIds)
        {
            var ids = invoiceIds.ToList();
            if (ids.Count == 0) return new HashSet<int>();
            var result = new HashSet<int>();
            foreach (var chunk in ids.Chunk(500))
            {
                foreach (var id in db.Fetch<int>(
                             $@"SELECT e.SourceId FROM dbo.LedgerJournalEntry e
                                 WHERE e.SourceType = @0 AND e.SourceId IN ({string.Join(",", chunk)})
                                   AND e.CorrectsEntryId IS NULL
                                   AND NOT EXISTS (SELECT 1 FROM dbo.LedgerJournalEntry c WHERE c.CorrectsEntryId = e.Id)",
                             SourceType))
                    result.Add(id);
            }
            return result;
        }
    }
}
