using HpskSite.Models.Ledger;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Bygger ekonomiöversiktens tre paneler. <b>Läser bara.</b>
    ///
    /// <para><b>⚠️ TRE FRÅGOR, INTE TRE FRÅGOR PER RAD.</b> Panel 3 frestar till att anropa
    /// <c>Completeness</c> en gång per tävling, och en klubb med femtio tävlingar hade då gjort
    /// femtio rundor till databasen varje gång någon öppnar fliken. Allt grupperas i stället ur
    /// EN hämtning. Samma fälla som fakturaadminsidan gick i.</para>
    ///
    /// <para><b>⚠️ Namnen slås upp med <c>Content.GetById</c>, aldrig genom att skanna trädet.</b></para>
    /// </summary>
    public class LedgerOverviewService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly IContentService _contentService;
        private readonly ILogger<LedgerOverviewService> _logger;

        /// <summary>Hur många mottagna betalningar panel 2 visar. Den är en puls, inte ett arkiv.</summary>
        private const int ReceivedLimit = 15;

        /// <summary>Hur många verifikationer panel 4 visar. Samma skäl som ovan — en puls.</summary>
        private const int JournalLimit = 15;

        public LedgerOverviewService(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextFactory contextFactory,
            IContentService contentService,
            ILogger<LedgerOverviewService> logger)
        {
            _databaseFactory = databaseFactory;
            _contextFactory = contextFactory;
            _contentService = contentService;
            _logger = logger;
        }

        public LedgerOverview Build(int issuerType, int issuerId)
        {
            var overview = new LedgerOverview();

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                // EN hämtning av allt som inte är makulerat. Betalningsrader är få per förening
                // och år; det är tävlingarna som är många, och de ligger i den här listan.
                var payments = ldb.Fetch<LedgerPayment>(
                    @"SELECT * FROM dbo.LedgerPayment
                       WHERE IssuerType = @0 AND IssuerId = @1 AND VoidedUtc IS NULL",
                    issuerType, issuerId);

                BuildOutstanding(overview, payments);
                BuildReceived(ldb, overview, payments);
                BuildPerSource(overview, payments);
                BuildJournal(ldb, overview, issuerType, issuerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Kunde inte bygga ekonomiöversikten för utställare {Typ}/{Id}.",
                    issuerType, issuerId);
            }

            return overview;
        }

        /// <summary>Panel 1 — vad som begärts men inte kommit in, per avgiftsslag.</summary>
        private static void BuildOutstanding(LedgerOverview overview, List<LedgerPayment> payments)
        {
            var groups = payments
                .Where(p => !p.IsMoney)
                .GroupBy(p => p.SourceType)
                .Select(g => new OutstandingGroup
                {
                    Label = LabelForSourceType(g.Key),
                    Count = g.Count(),
                    Amount = g.Sum(p => p.Amount)
                })
                .OrderByDescending(g => g.Amount)
                .ToList();

            overview.Outstanding.AddRange(groups);
            overview.OutstandingTotal = groups.Sum(g => g.Amount);
        }

        /// <summary>Panel 2 — de senast mottagna, med kvittonummer.</summary>
        private void BuildReceived(
            LedgerDb db,
            LedgerOverview overview,
            List<LedgerPayment> payments)
        {
            var received = payments
                .Where(p => p.IsMoney)
                .OrderByDescending(p => p.ConfirmedUtc)
                .Take(ReceivedLimit)
                .ToList();

            if (received.Count == 0) return;

            // Kvittonumren i EN fråga. Prefixet bor på serien, inte på kvittot, så den måste med.
            // ⚠️ IN-listan är begränsad till ReceivedLimit — parametertaket (~2100) kan aldrig nås
            // härifrån, till skillnad från listor som byggs ur ett helt år.
            var ids = received.Select(p => p.Id).ToArray();

            var receipts = db.Fetch<ReceiptNumberRow>(
                    $@"SELECT r.PaymentId, r.Id AS ReceiptId, r.Number, s.Prefix
                         FROM dbo.LedgerReceipt r
                         JOIN dbo.LedgerNumberSeries s ON s.Id = r.SeriesId
                        WHERE r.PaymentId IN ({string.Join(",", ids)})")
                .ToDictionary(r => r.PaymentId);

            foreach (var p in received)
            {
                receipts.TryGetValue(p.Id, out var r);

                overview.Received.Add(new ReceivedRow
                {
                    PaymentId = p.Id,
                    Date = (p.ConfirmedUtc ?? p.CreatedUtc).Date,
                    PayerName = p.PayerName,
                    What = NameForSource(p.SourceType, p.SourceId),
                    Amount = p.SettledAmount,
                    ReceiptId = r?.ReceiptId,
                    ReceiptNumber = r is null ? null : LedgerNumberAllocator.Format(r.Prefix, r.Number)
                });
            }
        }

        /// <summary>
        /// Panel 3 — per tävling: förväntat, betalt, saknas.
        ///
        /// <para><b>⚠️ Rader utan källa hoppas över.</b> En manuell betalning eller en medlemsavgift
        /// hör inte till en tävling, och att visa dem som "tävling: (okänd)" hade gjort panelen
        /// obrukbar precis där den ska svara på en enda fråga.</para>
        /// </summary>
        private void BuildPerSource(LedgerOverview overview, List<LedgerPayment> payments)
        {
            var groups = payments
                .Where(p => p.SourceId is > 0
                            && (p.SourceType == LedgerSourceType.CompetitionRegistration
                                || p.SourceType == LedgerSourceType.TeamFee
                                || p.SourceType == LedgerSourceType.CompetitionInvoice
                                || p.SourceType == LedgerSourceType.Event))
                .GroupBy(p => (p.SourceType, SourceId: p.SourceId!.Value))
                .Select(g => new SourceCompleteness
                {
                    SourceType = g.Key.SourceType,
                    SourceId = g.Key.SourceId,
                    Name = NameForSource(g.Key.SourceType, g.Key.SourceId),
                    Expected = g.Count(),
                    Settled = g.Count(p => p.IsMoney),
                    MissingAmount = g.Where(p => !p.IsMoney).Sum(p => p.Amount)
                })
                // Det som saknas överst — panelen är en arbetslista, inte ett register.
                .OrderByDescending(s => s.MissingCount)
                .ThenByDescending(s => s.MissingAmount)
                .ToList();

            overview.PerSource.AddRange(groups);
        }

        /// <summary>
        /// Tävlingens eller händelsens namn.
        ///
        /// <para><b>⚠️⚠️ TVÅ UPPSLAG, OCH BÅDA BEHÖVS.</b> Den publicerade cachen känner inte en
        /// <b>opublicerad</b> nod, och en tävling som ännu inte publicerats har ändå avgifter —
        /// det är till och med det vanliga läget medan anmälan förbereds. Utan
        /// <c>IContentService</c> som andra steg stod halva panelen namnlös.</para>
        ///
        /// <para><b>⚠️ En raderad nod får ett EGET namn med sitt id, aldrig avgiftsslaget.</b>
        /// Föll alla raderade tillbaka på "Evenemangsavgifter" blev det flera rader med samma
        /// namn i en lista vars enda syfte är att säga <i>vilken</i> tävling som saknar pengar —
        /// och två identiska rader går inte att arbeta med. Mätt i dev 2026-09-21: fem av sju
        /// källor var borttagna noder.</para>
        /// </summary>
        private string NameForSource(string sourceType, int? sourceId)
        {
            if (sourceId is not > 0) return LabelForSourceType(sourceType);

            try
            {
                using var ctx = _contextFactory.EnsureUmbracoContext();
                var node = ctx.UmbracoContext.Content?.GetById(sourceId.Value);

                if (node is not null)
                {
                    var name = node.Value<string>("competitionName")
                               ?? node.Value<string>("eventName")
                               ?? node.Name;

                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch
            {
                // Faller vidare till innehållstjänsten.
            }

            try
            {
                var content = _contentService.GetById(sourceId.Value);

                if (content is not null)
                {
                    var name = content.GetValue<string>("competitionName")
                               ?? content.GetValue<string>("eventName")
                               ?? content.Name;

                    // Opublicerat är ett eget läge, inte ett fel — men kassören ska se det,
                    // för en opublicerad tävling tar inte emot anmälningar.
                    if (!string.IsNullOrWhiteSpace(name))
                        return content.Published ? name : $"{name} (opublicerad)";
                }
            }
            catch
            {
                // Faller vidare till det namngivna bortfallet.
            }

            return sourceType switch
            {
                LedgerSourceType.Event => $"Borttagen händelse (#{sourceId})",
                LedgerSourceType.TeamFee => $"Borttagen tävling (#{sourceId})",
                LedgerSourceType.CompetitionInvoice => $"Borttagen tävling (#{sourceId})",
                LedgerSourceType.CompetitionRegistration => $"Borttagen tävling (#{sourceId})",
                _ => $"{LabelForSourceType(sourceType)} (#{sourceId})"
            };
        }

        /// <summary>
        /// Avgiftsslaget i klartext, i plural — panel 1 räknar rader.
        /// <para>⚠️ Skild från <c>LedgerPaymentService.DescribeFor</c>, som är singular och hamnar
        /// på en verifikation. Samma ord i två grammatiska former är inte samma sträng.</para>
        /// </summary>
        private static string LabelForSourceType(string sourceType) => sourceType switch
        {
            LedgerSourceType.CompetitionRegistration => "Anmälningsavgifter",
            LedgerSourceType.TeamFee => "Lagavgifter",
            LedgerSourceType.CompetitionInvoice => "Fakturerade anmälningsavgifter",
            LedgerSourceType.Event => "Evenemangsavgifter",
            LedgerSourceType.MembershipFee => "Medlemsavgifter",
            LedgerSourceType.RegionFee => "Kretsavgifter",
            LedgerSourceType.Manual => "Övrigt",
            _ => "Övrigt"
        };

        /// <summary>
        /// Panel 4 för den som bokför här — de senast bokförda verifikationerna.
        ///
        /// <para><b>⚠️ Ingen föreningsform läses av.</b> Finns det verifikationer bokför föreningen
        /// här; finns det inga står panelen kvar på betalningsraderna. Det gör panelen riktig utan
        /// en andra uppgift att hålla i takt — en avläst <c>Shape</c> som glidit från verkligheten
        /// hade tömt panelen för exakt den förening som har mest i den.</para>
        ///
        /// <para><b>⚠️ Ordnat på REGISTRERINGSTID, inte på bokföringsdatum.</b> "Vad hände senast?"
        /// är en fråga om registreringsordningen. Ett bokföringsdatum får ligga bakåt i tiden, så
        /// en datumsortering hade lagt en nyss inmatad rättelse av fjolåret längst ned — alltså
        /// gömt just den post kassören sist rörde.</para>
        ///
        /// <para><b>⚠️⚠️ OCH INTE PÅ <c>Id DESC</c> — SANDLÅDANS ID:N RÄKNAS NEDÅT.</b> Sandlådan
        /// är <c>IDENTITY(-1,-1)</c>, så den först registrerade posten har det STÖRSTA id:t och
        /// <c>Id DESC</c> ger exakt omvänd ordning där. Mätt i sandlåda -132: panelen började på
        /// verifikation 1 av 9 och kallade den "senast". <b>Tie-break är <c>Number</c>, aldrig
        /// <c>Id</c></b> — numret är luckfritt och stigande i BÅDA schemana, vilket id:t inte är.
        /// Samma fälla som varje <c>&lt;= 0</c>-kontroll som låst ute sandlådan, en tredje gång.</para>
        ///
        /// <para><b>⚠️ Beloppet summeras i EN underfråga per verifikation-topplista</b>, inte i en
        /// runda per rad: <c>JournalLimit</c> rader in, en fråga ut.</para>
        /// </summary>
        private static void BuildJournal(
            LedgerDb db, LedgerOverview overview, int issuerType, int issuerId)
        {
            var rows = db.Fetch<JournalEntryRow>(
                $@"SELECT TOP ({JournalLimit})
                          e.Id, e.Number, s.Prefix, e.AccountingDate, e.Description,
                          e.CounterpartyName,
                          (SELECT SUM(l.Debit)
                             FROM dbo.LedgerJournalEntryLine l
                            WHERE l.JournalEntryId = e.Id) AS Amount
                     FROM dbo.LedgerJournalEntry e
                     JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                    WHERE e.IssuerType = @0 AND e.IssuerId = @1
                    ORDER BY e.RegisteredUtc DESC, e.Number DESC",
                issuerType, issuerId);

            foreach (var r in rows)
            {
                overview.Journal.Add(new JournalRow
                {
                    EntryId = r.Id,
                    Number = LedgerNumberAllocator.Format(r.Prefix, r.Number),
                    Date = r.AccountingDate.Date,
                    Description = r.Description,
                    Counterparty = r.CounterpartyName ?? "",
                    Amount = r.Amount ?? 0m
                });
            }
        }

        /// <summary>Radformen för verifikationsuppslaget. Egen typ — NPoco mappar på kolumnnamn.</summary>
        private class JournalEntryRow
        {
            public int Id { get; set; }
            public int Number { get; set; }
            public string Prefix { get; set; } = "";
            public DateTime AccountingDate { get; set; }
            public string Description { get; set; } = "";
            public string? CounterpartyName { get; set; }

            /// <summary>Null när verifikationen saknar rader — får aldrig bli en krasch i en läsvy.</summary>
            public decimal? Amount { get; set; }
        }

        /// <summary>Radformen för kvittouppslaget. Egen typ — NPoco mappar på kolumnnamn.</summary>
        private class ReceiptNumberRow
        {
            public int PaymentId { get; set; }
            public int ReceiptId { get; set; }
            public int Number { get; set; }
            public string Prefix { get; set; } = "";
        }
    }
}
