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

            /// <summary>
            /// Verifikationer kassören redan bokfört som troligen ÄR den här betalningen — se
            /// <see cref="FindDuplicateCandidates"/>. Tom = inget tyder på att den redan är bokförd.
            /// </summary>
            public List<DuplicateCandidate> Candidates { get; set; } = new();
        }

        public sealed class DuplicateCandidate
        {
            public int EntryId { get; set; }
            public string Number { get; set; } = "";
            public DateTime AccountingDate { get; set; }
            public string Description { get; set; } = "";
            public int AccountNumber { get; set; }
            /// <summary>Projektet på verifikationens rader, om något. Null = utan projekt.</summary>
            public string? ProjectName { get; set; }
        }

        public sealed class LinkedInvoice
        {
            public int InvoiceId { get; set; }
            public string InvoiceNumber { get; set; } = "";
            public string CompetitionName { get; set; } = "";
            public decimal Amount { get; set; }
            public int EntryId { get; set; }
            public string EntryNumber { get; set; } = "";
            public DateTime LinkedUtc { get; set; }
        }

        /// <summary>
        /// ⚠️ Källtyperna en KASSÖR själv bokför med. Bara de kan vara "samma pengar en gång till":
        /// en automatisk post (medlemsavgift, kretsavgift, den nya modellens tävlingsavgifter) hör
        /// till en annan betalning, och att föreslå den som dubblett vore att föreslå fel koppling.
        /// </summary>
        private static readonly string[] HumanSources =
        {
            LedgerSourceType.Manual, LedgerSourceType.BankImport, LedgerSourceType.SieImport
        };

        public const string MissingTableMessage =
            "Kopplingen går inte att spara — tabellen LedgerLegacyInvoiceLink saknas. "
            + "Kör Migrations/create-ledger-legacy-invoice-link.sql.";

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
            var linkedIds = LinkTableExists(db) ? LinkedInvoiceIds(db) : new HashSet<int>();
            if (DismissalTableExists(db)) linkedIds.UnionWith(DismissedInvoiceIds(db));
            var open = result.Where(r => !posted.Contains(r.InvoiceId) && !linkedIds.Contains(r.InvoiceId))
                             .OrderBy(r => r.PaidDate).ToList();
            FindDuplicateCandidates(db, ownerType, ownerId, open);
            return open;
        }

        /// <summary>
        /// ⚠️⚠️ VARFÖR. Kortet säger att de här fakturorna ska bli verifikationer — men har kassören
        /// redan bokfört Swish-insättningarna från kontoutdraget är det PÅSTÅENDET som är fel, och
        /// "Bokför" skulle bokföra samma pengar två gånger (Michael Henriksson 2026-10-01).
        ///
        /// <para>En kandidat är en verifikation kassören bokfört själv (<see cref="HumanSources"/>)
        /// med en rad på ett eget pengakonto (19xx) där pengar KOM IN med exakt fakturans belopp,
        /// inom samma tolerans som bankavstämningen (±7 dagar — Swish kan landa dagen efter).
        /// Rättade och rättande poster räknas inte, och en verifikation som redan förklarar en annan
        /// faktura erbjuds inte igen.</para>
        ///
        /// <para>⚠️ Det är ett FÖRSLAG, aldrig ett beslut: tre betalningar på 160 kr samma vecka är
        /// vardag. Kopplingen görs bara när kassören väljer den.</para>
        /// </summary>
        private void FindDuplicateCandidates(IUmbracoDatabase db, int ownerType, int ownerId, List<LegacyPaidInvoice> invoices)
        {
            if (invoices.Count == 0) return;
            var tol = LedgerBankMatching.DateToleranceDays;
            var from = invoices.Min(i => i.PaidDate).AddDays(-tol);
            var to = invoices.Max(i => i.PaidDate).AddDays(tol);
            var linkExists = LinkTableExists(db);

            List<CandidateRow> rows;
            try
            {
                rows = db.Fetch<CandidateRow>(
                    $@"SELECT e.Id AS EntryId, s.Prefix, e.Number, e.AccountingDate, e.Description,
                              l.AccountNumber, l.Debit,
                              (SELECT TOP 1 x.ProjectName FROM dbo.LedgerJournalEntryLine x
                                WHERE x.JournalEntryId = e.Id AND x.ProjectId IS NOT NULL) AS ProjectName
                         FROM dbo.LedgerJournalEntryLine l
                         JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                         LEFT JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                        WHERE e.IssuerType = @0 AND e.IssuerId = @1
                          AND e.AccountingDate >= @2 AND e.AccountingDate <= @3
                          AND l.AccountNumber BETWEEN @4 AND @5 AND l.Debit > 0
                          AND (e.SourceType IS NULL OR e.SourceType IN (@6, @7, @8))
                          AND e.CorrectsEntryId IS NULL
                          AND NOT EXISTS (SELECT 1 FROM dbo.LedgerJournalEntry c WHERE c.CorrectsEntryId = e.Id)
                          {(linkExists ? "AND NOT EXISTS (SELECT 1 FROM dbo.LedgerLegacyInvoiceLink k WHERE k.JournalEntryId = e.Id)" : "")}",
                    ownerType, ownerId, from.Date, to.Date,
                    LedgerMoneyAccount.First, LedgerMoneyAccount.Last,
                    HumanSources[0], HumanSources[1], HumanSources[2]);
            }
            catch (Exception ex)
            {
                // Dubblettkontrollen får inte ta ner kortet — men den får inte heller tystna:
                // utan den är "Bokför" exakt så farlig som rapporten beskrev.
                _logger.LogError(ex, "Dubblettkontrollen för gamla fakturor kunde inte köras ({Typ}/{Id}).", ownerType, ownerId);
                throw;
            }

            foreach (var inv in invoices)
            {
                inv.Candidates = rows
                    .Where(r => r.Debit == inv.Amount
                                && Math.Abs((r.AccountingDate.Date - inv.PaidDate.Date).TotalDays) <= tol)
                    .GroupBy(r => r.EntryId).Select(g => g.First())
                    .OrderBy(r => Math.Abs((r.AccountingDate.Date - inv.PaidDate.Date).TotalDays))
                    .ThenBy(r => r.AccountingDate)
                    .Select(r => new DuplicateCandidate
                    {
                        EntryId = r.EntryId,
                        Number = LedgerNumberAllocator.Format(r.Prefix ?? "", r.Number),
                        AccountingDate = r.AccountingDate.Date,
                        Description = r.Description ?? "",
                        AccountNumber = r.AccountNumber,
                        ProjectName = string.IsNullOrWhiteSpace(r.ProjectName) ? null : r.ProjectName
                    })
                    .ToList();
            }
        }

        private sealed class CandidateRow
        {
            public int EntryId { get; set; }
            public string? Prefix { get; set; }
            public int Number { get; set; }
            public DateTime AccountingDate { get; set; }
            public string? Description { get; set; }
            public int AccountNumber { get; set; }
            public decimal Debit { get; set; }
            public string? ProjectName { get; set; }
        }

        /// <summary>
        /// Kassörens svar "den här fakturan är redan bokförd i den här verifikationen".
        ///
        /// <para>⚠️ Både fakturan och verifikationen prövas om på SERVERN mot samma regel som kortet
        /// visade: fakturan måste vara en av föreningens obokförda, och verifikationen en av dess
        /// kandidater. Ett handpostat id ska inte kunna koppla en annan förenings verifikation, eller
        /// en verifikation på ett annat belopp.</para>
        /// </summary>
        public (bool Ok, string? Error) Link(int ownerType, int ownerId, int invoiceId, int entryId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (!LinkTableExists(db)) return (false, MissingTableMessage);

            var inv = Unposted(ownerType, ownerId).FirstOrDefault(i => i.InvoiceId == invoiceId);
            if (inv == null)
                return (false, "Fakturan finns inte bland de obokförda — den kan redan vara bokförd eller kopplad.");
            if (!inv.Candidates.Any(c => c.EntryId == entryId))
                return (false, "Verifikationen stämmer inte med fakturan (belopp eller datum), eller förklarar redan en annan faktura.");

            try
            {
                db.Execute(
                    @"INSERT INTO dbo.LedgerLegacyInvoiceLink
                        (IssuerType, IssuerId, InvoiceId, JournalEntryId, InvoiceNumber, CompetitionName,
                         Amount, LinkedByMemberId, LinkedUtc)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8)",
                    ownerType, ownerId, invoiceId, entryId,
                    Trunc(inv.InvoiceNumber, 100), Trunc(inv.CompetitionName, 300), inv.Amount,
                    byMemberId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // Det unika indexet är spärren mot att två samtidiga klick kopplar samma verifikation.
                _logger.LogWarning(ex, "Gammal faktura {Invoice} kunde inte kopplas till verifikation {Entry}.", invoiceId, entryId);
                return (false, "Kopplingen sparades inte — verifikationen kan precis ha kopplats till en annan faktura. Ladda om sidan.");
            }
            _logger.LogInformation("Gammal faktura {Invoice} kopplad till befintlig verifikation {Entry} av medlem {Member}.",
                invoiceId, entryId, byMemberId);
            return (true, null);
        }

        /// <summary>Tar bort en koppling (fel verifikation). Skriver ingenting i liggaren.</summary>
        public bool Unlink(int ownerType, int ownerId, int invoiceId)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (!LinkTableExists(db)) return false;
            return db.Execute(
                "DELETE FROM dbo.LedgerLegacyInvoiceLink WHERE InvoiceId = @0 AND IssuerType = @1 AND IssuerId = @2",
                invoiceId, ownerType, ownerId) > 0;
        }

        public List<LinkedInvoice> Linked(int ownerType, int ownerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (!LinkTableExists(db)) return new List<LinkedInvoice>();
            // ⚠️ Numret formateras av LedgerNumberAllocator.Format ("V-316"), samma form som
            //    Verifikationer visar — aldrig ihopklistrat i SQL.
            return db.Fetch<LinkedRow>(
                @"SELECT k.InvoiceId, k.InvoiceNumber, k.CompetitionName, k.Amount, k.JournalEntryId AS EntryId,
                         s.Prefix, e.Number, k.LinkedUtc
                    FROM dbo.LedgerLegacyInvoiceLink k
                    JOIN dbo.LedgerJournalEntry e ON e.Id = k.JournalEntryId
                    LEFT JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                   WHERE k.IssuerType = @0 AND k.IssuerId = @1
                   ORDER BY k.LinkedUtc DESC", ownerType, ownerId)
                .Select(r => new LinkedInvoice
                {
                    InvoiceId = r.InvoiceId, InvoiceNumber = r.InvoiceNumber, CompetitionName = r.CompetitionName,
                    Amount = r.Amount, EntryId = r.EntryId, LinkedUtc = r.LinkedUtc,
                    EntryNumber = LedgerNumberAllocator.Format(r.Prefix ?? "", r.Number)
                })
                .ToList();
        }

        private sealed class LinkedRow
        {
            public int InvoiceId { get; set; }
            public string InvoiceNumber { get; set; } = "";
            public string CompetitionName { get; set; } = "";
            public decimal Amount { get; set; }
            public int EntryId { get; set; }
            public string? Prefix { get; set; }
            public int Number { get; set; }
            public DateTime LinkedUtc { get; set; }
        }

        public sealed class DismissedInvoice
        {
            public int InvoiceId { get; set; }
            public string InvoiceNumber { get; set; } = "";
            public string CompetitionName { get; set; } = "";
            public string PayerName { get; set; } = "";
            public decimal Amount { get; set; }
            public string Reason { get; set; } = "";
            public string DismissedByName { get; set; } = "";
            public DateTime DismissedUtc { get; set; }
        }

        public const string MissingDismissalTableMessage =
            "Valet går inte att spara — tabellen LedgerLegacyInvoiceDismissal saknas. "
            + "Kör Migrations/create-ledger-legacy-invoice-dismissal.sql.";

        /// <summary>
        /// "Pengarna kom aldrig in": fakturan står som betald i den gamla modellen, men ingen
        /// insättning finns. Den tas bort ur listan utan att något bokförs.
        ///
        /// <para>⚠️⚠️ Alternativen var båda fel: att bokföra den gör att bokföringen visar pengar som
        /// aldrig kom, och att låta den stå gör att raden aldrig försvinner (Michael Henriksson
        /// 2026-10-01). Liggaren och den gamla fakturan rörs inte; ett skäl är obligatoriskt så att
        /// beslutet går att förstå i efterhand, och det kan ångras.</para>
        /// </summary>
        public (bool Ok, string? Error) Dismiss(int ownerType, int ownerId, int invoiceId, string? reason,
            int byMemberId, string byName)
        {
            var why = (reason ?? "").Trim();
            if (why.Length == 0) return (false, "Skriv varför — till exempel \"ingen insättning på kontot\".");
            if (why.Length > 400) why = why[..400];

            using var db = _databaseFactory.CreateDatabase();
            if (!DismissalTableExists(db)) return (false, MissingDismissalTableMessage);

            // Prövas om mot samma lista som ytan visade: bara en av föreningens obokförda.
            var inv = Unposted(ownerType, ownerId).FirstOrDefault(i => i.InvoiceId == invoiceId);
            if (inv == null)
                return (false, "Fakturan finns inte bland de obokförda — den kan redan vara bokförd eller hanterad.");

            try
            {
                db.Execute(
                    @"INSERT INTO dbo.LedgerLegacyInvoiceDismissal
                        (IssuerType, IssuerId, InvoiceId, InvoiceNumber, CompetitionName, PayerName, Amount,
                         Reason, DismissedByMemberId, DismissedByName, DismissedUtc)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10)",
                    ownerType, ownerId, invoiceId, Trunc(inv.InvoiceNumber, 100), Trunc(inv.CompetitionName, 300),
                    Trunc(inv.PayerName, 200), inv.Amount, why, byMemberId, Trunc(byName ?? "", 200), DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gammal faktura {Invoice} kunde inte markeras som ej inkommen.", invoiceId);
                return (false, "Valet sparades inte — fakturan kan precis ha hanterats i ett annat fönster. Ladda om sidan.");
            }
            _logger.LogInformation("Gammal faktura {Invoice} ({Belopp} kr) markerad \"pengarna kom aldrig in\" av {Namn}: {Skal}",
                invoiceId, inv.Amount, byName, why);
            return (true, null);
        }

        public bool Undismiss(int ownerType, int ownerId, int invoiceId)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (!DismissalTableExists(db)) return false;
            return db.Execute(
                "DELETE FROM dbo.LedgerLegacyInvoiceDismissal WHERE InvoiceId = @0 AND IssuerType = @1 AND IssuerId = @2",
                invoiceId, ownerType, ownerId) > 0;
        }

        public List<DismissedInvoice> Dismissed(int ownerType, int ownerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (!DismissalTableExists(db)) return new List<DismissedInvoice>();
            return db.Fetch<DismissedInvoice>(
                @"SELECT InvoiceId, InvoiceNumber, CompetitionName, PayerName, Amount, Reason, DismissedByName, DismissedUtc
                    FROM dbo.LedgerLegacyInvoiceDismissal
                   WHERE IssuerType = @0 AND IssuerId = @1
                   ORDER BY DismissedUtc DESC", ownerType, ownerId);
        }

        private static bool DismissalTableExists(IUmbracoDatabase db) =>
            db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.LedgerLegacyInvoiceDismissal') IS NULL THEN 0 ELSE 1 END") == 1;

        private static HashSet<int> DismissedInvoiceIds(IUmbracoDatabase db) =>
            db.Fetch<int>("SELECT InvoiceId FROM dbo.LedgerLegacyInvoiceDismissal").ToHashSet();

        private static bool LinkTableExists(IUmbracoDatabase db) =>
            db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.LedgerLegacyInvoiceLink') IS NULL THEN 0 ELSE 1 END") == 1;

        private static HashSet<int> LinkedInvoiceIds(IUmbracoDatabase db) =>
            db.Fetch<int>("SELECT InvoiceId FROM dbo.LedgerLegacyInvoiceLink").ToHashSet();

        private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

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
        ///
        /// <para><b>⚠️⚠️ En faktura med en trolig dubblett bokförs INTE av "Bokför"</b> — den väntar på
        /// kassörens beslut: koppla till den befintliga verifikationen, eller bokför ändå
        /// (<paramref name="forceInvoiceIds"/>). Annars vore knappen exakt den dubbelbokföring
        /// rapporten handlade om.</para>
        /// </summary>
        public (int Posted, int Attempted, int Skipped) PostAll(int ownerType, int ownerId, int byMemberId,
            IReadOnlyCollection<int>? forceInvoiceIds = null)
        {
            var force = forceInvoiceIds?.ToHashSet() ?? new HashSet<int>();
            var all = Unposted(ownerType, ownerId);
            // Explicit urval = bara de raderna. Inget urval = alla utan trolig dubblett.
            var chosen = force.Count > 0
                ? all.Where(i => force.Contains(i.InvoiceId)).ToList()
                : all.Where(i => i.Candidates.Count == 0).ToList();
            var count = 0;
            foreach (var inv in chosen)
                if (Post(ownerType, ownerId, inv, byMemberId) != null) count++;
            return (count, chosen.Count, all.Count - chosen.Count);
        }

        private int? Post(int ownerType, int ownerId, LegacyPaidInvoice inv, int byMemberId)
        {
            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    if (PostedIds(db, new[] { inv.InvoiceId }).Count > 0) return null;
                    // Kopplad under tiden (ett annat fönster) = redan bokförd. Bokför inte en gång till.
                    if (LinkTableExists(db) && db.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM dbo.LedgerLegacyInvoiceLink WHERE InvoiceId = @0", inv.InvoiceId) > 0)
                        return null;
                    // "Pengarna kom aldrig in" under tiden = ska inte bokföras.
                    if (DismissalTableExists(db) && db.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM dbo.LedgerLegacyInvoiceDismissal WHERE InvoiceId = @0", inv.InvoiceId) > 0)
                        return null;
                }

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
