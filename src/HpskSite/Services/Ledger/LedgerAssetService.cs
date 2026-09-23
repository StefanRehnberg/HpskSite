using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Anläggningsregistret: tillgångarna, årets avskrivningsplan, och bokföringen av den.
    ///
    /// <para><b>⚠️⚠️ "BOKFÖRD" ÄR HÄRLETT, ALDRIG LAGRAT.</b> Årets avskrivning räknas som bokförd
    /// när det finns konteringsrader på tillgångens avskrivningskonto i verifikationer med
    /// <c>SourceType = asset-depreciation</c> och <c>SourceId = tillgångens id</c> inom
    /// räkenskapsåret. Det betyder ingen flagga som kan glömmas, och ingen migrering när något
    /// rättas — samma princip som medlemsavgiftsbryggan och kön "att bokföra".</para>
    ///
    /// <para><b>⚠️ Bokföringen kan köras FLERA gånger under året.</b> Kassören som skriver av
    /// halvårsvis ska kunna göra det; funktionen bokför då bara skillnaden mot vad som redan
    /// ligger. Att kräva "en gång per år" hade gjort en delvis bokförd tillgång omöjlig att
    /// komplettera.</para>
    /// </summary>
    public class LedgerAssetService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;
        private readonly ILogger<LedgerAssetService> _logger;

        /// <summary>Källtypen på avskrivningens verifikation. <b>Nyckeln hela härledningen vilar på.</b></summary>
        public const string SourceType = "asset-depreciation";

        public LedgerAssetService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerPostingService posting,
            ILogger<LedgerAssetService> logger)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
            _logger = logger;
        }

        /// <summary>
        /// Registret för ett räkenskapsår, med plan och utfall per tillgång.
        ///
        /// <para>⚠️ Sorterat på NAMN, aldrig på <c>Id</c> — sandlådans identitet räknar nedåt, så
        /// en id-sortering är omvänd just där.</para>
        /// </summary>
        public List<LedgerAssetView> List(
            int issuerType, int issuerId, DateTime yearFrom, DateTime yearTo)
        {
            var views = new List<LedgerAssetView>();

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var assets = ldb.Fetch<LedgerAsset>(
                    @"SELECT * FROM dbo.LedgerAsset
                       WHERE IssuerType = @0 AND IssuerId = @1
                       ORDER BY Name",
                    issuerType, issuerId);

                if (assets.Count == 0) return views;

                // Kontonamnen i EN fråga — annars blir det ett uppslag per tillgång.
                var accounts = ldb.Fetch<AccountName>(
                    @"SELECT Number, Name FROM dbo.LedgerAccount
                       WHERE IssuerType = @0 AND IssuerId = @1",
                    issuerType, issuerId)
                    .GroupBy(a => a.Number)
                    .ToDictionary(g => g.Key, g => g.First().Name);

                // ⚠️ Utfallet i EN fråga för hela registret. Ett anrop per tillgång är den fälla
                //    som gjorde fakturasidan tolv sekunder lång.
                var posted = PostedByAsset(ldb, issuerType, issuerId, yearFrom, yearTo);

                foreach (var a in assets)
                {
                    views.Add(new LedgerAssetView
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Note = a.Note,
                        AssetAccountNumber = a.AssetAccountNumber,
                        AssetAccountName = accounts.GetValueOrDefault(a.AssetAccountNumber, ""),
                        DepreciationAccountNumber = a.DepreciationAccountNumber,
                        DepreciationAccountName = accounts.GetValueOrDefault(a.DepreciationAccountNumber, ""),
                        InUseDate = a.InUseDate,
                        AcquisitionAmount = a.AcquisitionAmount,
                        UsefulLifeYears = a.UsefulLifeYears,
                        ResidualValue = a.ResidualValue,
                        PlannedThisYear = LedgerDepreciation.ForPeriod(a, yearFrom, yearTo),
                        PostedThisYear = posted.GetValueOrDefault(a.Id, 0m),
                        BookValue = LedgerDepreciation.BookValue(a, yearTo),
                        IsDisposed = a.IsDisposed,
                        DisposedDate = a.DisposedDate,
                        DisposalReason = a.DisposalReason,
                        IsFullyDepreciated = LedgerDepreciation.FullyDepreciatedOn(a) <= yearTo
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anläggningsregistret gick inte att läsa för {Typ}/{Id}.",
                    issuerType, issuerId);
            }

            return views;
        }

        /// <summary>
        /// Vad som bokförts per tillgång under året.
        ///
        /// <para><b>⚠️ Summerar DEBET på avskrivningskontot.</b> Avskrivningen är 7830 debet /
        /// 1220 kredit, så debetsidan är kostnaden. Att summera båda hade gett dubbla beloppet.</para>
        /// </summary>
        private static Dictionary<int, decimal> PostedByAsset(
            LedgerDb ldb, int issuerType, int issuerId, DateTime from, DateTime to)
        {
            var rows = ldb.Fetch<PostedRow>(
                @"SELECT e.SourceId AS AssetId, SUM(l.Debit) AS Amount
                    FROM dbo.LedgerJournalEntry e
                    JOIN dbo.LedgerJournalEntryLine l ON l.JournalEntryId = e.Id
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND e.SourceType = @2 AND e.SourceId IS NOT NULL
                     AND e.AccountingDate >= @3 AND e.AccountingDate <= @4
                     AND l.Debit > 0
                   GROUP BY e.SourceId",
                issuerType, issuerId, SourceType, from.Date, to.Date);

            return rows.Where(r => r.AssetId.HasValue)
                       .ToDictionary(r => r.AssetId!.Value, r => r.Amount);
        }

        public (bool Ok, string? Error, int Id) Save(LedgerAsset asset, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(asset.Name)) return (false, "Ge tillgången ett namn.", 0);
            if (asset.AcquisitionAmount <= 0) return (false, "Anskaffningsvärdet måste vara större än noll.", 0);
            if (asset.UsefulLifeYears is < 1 or > 50) return (false, "Nyttjandeperioden ska vara mellan 1 och 50 år.", 0);
            if (asset.ResidualValue < 0) return (false, "Restvärdet kan inte vara negativt.", 0);
            if (asset.ResidualValue >= asset.AcquisitionAmount)
                return (false, "Restvärdet måste vara lägre än anskaffningsvärdet — annars finns inget att skriva av.", 0);
            if (asset.InUseDate == default) return (false, "Ange när tillgången togs i bruk.", 0);
            if (byMemberId <= 0) return (false, "Du måste vara inloggad.", 0);

            // ⚠️ Kontoklassen prövas. Ett kostnadskonto som tillgångskonto ger en balansräkning
            //    som inte går ihop, och felet upptäcks först i bokslutet.
            if (LedgerAccountClass.Of(asset.AssetAccountNumber) != LedgerAccountClass.Assets)
                return (false, "Tillgångskontot måste vara ett balanskonto i klass 1.", 0);

            if (LedgerAccountClass.Of(asset.DepreciationAccountNumber) is < 4 or > 7)
                return (false, "Avskrivningskontot måste vara ett kostnadskonto (klass 4–7).", 0);

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, asset.IssuerId);

                if (asset.Id == 0)
                {
                    ldb.Execute(
                        @"INSERT INTO dbo.LedgerAsset
                            (IssuerType, IssuerId, Name, Note, AssetAccountNumber,
                             DepreciationAccountNumber, InUseDate, AcquisitionAmount,
                             UsefulLifeYears, ResidualValue, CreatedByMemberId, CreatedUtc)
                          VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11)",
                        asset.IssuerType, asset.IssuerId, asset.Name.Trim(), asset.Note,
                        asset.AssetAccountNumber, asset.DepreciationAccountNumber,
                        asset.InUseDate.Date, asset.AcquisitionAmount, asset.UsefulLifeYears,
                        asset.ResidualValue, byMemberId, DateTime.UtcNow);

                    return (true, null, 0);
                }

                // ⚠️⚠️ BELOPP OCH DATUM LÅSES SÅ SNART NÅGOT BOKFÖRTS. Ändras de i efterhand
                //    stämmer inte längre de verifikationer som redan ligger i liggaren, och
                //    ingenting säger ifrån. Namn och anteckning är alltid fria — en felstavning
                //    ska gå att rätta.
                var postedEver = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerJournalEntry
                       WHERE IssuerType = @0 AND IssuerId = @1
                         AND SourceType = @2 AND SourceId = @3",
                    asset.IssuerType, asset.IssuerId, SourceType, asset.Id);

                if (postedEver > 0)
                {
                    var current = ldb.Fetch<LedgerAsset>(
                        "SELECT * FROM dbo.LedgerAsset WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                        asset.Id, asset.IssuerType, asset.IssuerId).FirstOrDefault();

                    if (current is null) return (false, "Tillgången finns inte.", 0);

                    if (current.AcquisitionAmount != asset.AcquisitionAmount
                        || current.InUseDate.Date != asset.InUseDate.Date
                        || current.UsefulLifeYears != asset.UsefulLifeYears
                        || current.ResidualValue != asset.ResidualValue
                        || current.AssetAccountNumber != asset.AssetAccountNumber
                        || current.DepreciationAccountNumber != asset.DepreciationAccountNumber)
                    {
                        return (false,
                            "Avskrivningar är redan bokförda på tillgången, så belopp, datum, "
                            + "nyttjandeperiod och konton kan inte ändras. Blev något fel: utrangera "
                            + "raden och lägg in en ny, så syns rättelsen.", 0);
                    }

                    ldb.Execute(
                        "UPDATE dbo.LedgerAsset SET Name = @1, Note = @2 WHERE Id = @0",
                        asset.Id, asset.Name.Trim(), asset.Note);

                    return (true, null, asset.Id);
                }

                ldb.Execute(
                    @"UPDATE dbo.LedgerAsset
                         SET Name = @1, Note = @2, AssetAccountNumber = @3,
                             DepreciationAccountNumber = @4, InUseDate = @5,
                             AcquisitionAmount = @6, UsefulLifeYears = @7, ResidualValue = @8
                       WHERE Id = @0 AND IssuerType = @9 AND IssuerId = @10",
                    asset.Id, asset.Name.Trim(), asset.Note, asset.AssetAccountNumber,
                    asset.DepreciationAccountNumber, asset.InUseDate.Date, asset.AcquisitionAmount,
                    asset.UsefulLifeYears, asset.ResidualValue, asset.IssuerType, asset.IssuerId);

                return (true, null, asset.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tillgången kunde inte sparas för {Typ}/{Id}.",
                    asset.IssuerType, asset.IssuerId);

                return (false, "Tillgången kunde inte sparas. Försök igen.", 0);
            }
        }

        /// <summary>
        /// Utrangerar en tillgång. <b>Raden står kvar</b> — registret är den enda plats där
        /// anskaffningsvärdet finns kvar när avskrivningen bokförs direkt mot tillgångskontot.
        /// </summary>
        public (bool Ok, string? Error) Dispose(
            int issuerType, int issuerId, int assetId, DateTime when, string reason, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "Skriv vad som hände med tillgången — såld, skrotad eller stulen.");

            if (byMemberId <= 0) return (false, "Du måste vara inloggad.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var rows = ldb.Execute(
                    @"UPDATE dbo.LedgerAsset
                         SET DisposedDate = @3, DisposalReason = @4, DisposedByMemberId = @5
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2 AND DisposedDate IS NULL",
                    assetId, issuerType, issuerId, when.Date, reason.Trim(), byMemberId);

                return rows == 0
                    ? (false, "Tillgången är redan utrangerad, eller hör till en annan förening.")
                    : (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utrangeringen misslyckades för tillgång {Id}.", assetId);
                return (false, "Utrangeringen kunde inte sparas.");
            }
        }

        /// <summary>
        /// Bokför årets avskrivningar — en verifikation per tillgång.
        ///
        /// <para><b>⚠️⚠️ EN VERIFIKATION PER TILLGÅNG, inte en samlad.</b> Härledningen av
        /// "bokförd" hänger på <c>SourceId</c>, och en samlad post kan bara bära ett id. En
        /// revisor som frågar "vad skrevs av på klubbstugan?" ska dessutom få ett svar.</para>
        ///
        /// <para><b>⚠️ Bokför SKILLNADEN mot vad som redan ligger</b>, så funktionen kan köras
        /// flera gånger under året utan att dubbelbokföra.</para>
        ///
        /// <para><b>⚠️ Bokföringsdatum är årets SISTA dag</b>, inte i dag. En avskrivning hör till
        /// det år den avser; körs bokslutet i mars ska posten ändå ligga i december.</para>
        /// </summary>
        public (int Posted, decimal Amount, List<string> Problems) PostYear(
            int issuerType, int issuerId, DateTime yearFrom, DateTime yearTo, int byMemberId)
        {
            var problems = new List<string>();
            var count = 0;
            var total = 0m;

            var blocked = _posting.PostingBlockedReason(issuerType, issuerId, yearTo);
            if (blocked is not null)
            {
                problems.Add(blocked);
                return (0, 0m, problems);
            }

            foreach (var v in List(issuerType, issuerId, yearFrom, yearTo).Where(x => x.NeedsPosting))
            {
                try
                {
                    // ⚠️ AccountNumber, inte Role. Avskrivningskontot väljs PER TILLGÅNG ur
                    //    föreningens egen kontoplan — en klubbstuga mot 7820, en pistol mot 7830 —
                    //    och det går inte att uttrycka som en roll för hela föreningen.
                    //    Kontoplanen står alltså inte i koden; den står på tillgångens rad.
                    var result = _posting.Post(new LedgerPostingRequest
                    {
                        IssuerType = issuerType,
                        IssuerId = issuerId,
                        AccountingDate = yearTo.Date,
                        EventDate = yearTo.Date,
                        Description = $"Avskrivning {v.Name}",
                        SourceType = SourceType,
                        SourceId = v.Id,
                        CreatedByMemberId = byMemberId,
                        Lines = new List<LedgerPostingLine>
                        {
                            new() { AccountNumber = v.DepreciationAccountNumber, Debit = v.Remaining },
                            new() { AccountNumber = v.AssetAccountNumber, Credit = v.Remaining }
                        }
                    });

                    if (result.Success)
                    {
                        count++;
                        total += v.Remaining;
                    }
                    else
                    {
                        problems.Add($"{v.Name}: {result.Error}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Avskrivningen av {Namn} kunde inte bokföras.", v.Name);
                    problems.Add($"{v.Name}: kunde inte bokföras.");
                }
            }

            return (count, total, problems);
        }

        private class AccountName
        {
            public int Number { get; set; }
            public string Name { get; set; } = "";
        }

        private class PostedRow
        {
            public int? AssetId { get; set; }
            public decimal Amount { get; set; }
        }
    }
}
