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

        /// <summary>
        /// Källtypen på avskrivningens verifikation. <b>Nyckeln hela härledningen vilar på.</b>
        /// <para>⚠️ Samma sträng som förut, flyttad till <see cref="LedgerSourceType"/> där de
        /// andra bor — ett värdebyte hade gjort varje redan bokförd avskrivning osynlig.</para>
        /// </summary>
        public const string SourceType = LedgerSourceType.AssetDepreciation;

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
                        AccumulatedDepreciationAccountNumber = a.AccumulatedDepreciationAccountNumber,
                        AccumulatedDepreciationAccountName = accounts.GetValueOrDefault(a.AccumulatedDepreciationAccountNumber, ""),
                        InUseDate = a.InUseDate,
                        AcquisitionAmount = a.AcquisitionAmount,
                        UsefulLifeYears = a.UsefulLifeYears,
                        ResidualValue = a.ResidualValue,
                        PlannedThisYear = LedgerDepreciation.ForPeriod(a, yearFrom, yearTo),
                        PostedThisYear = posted.GetValueOrDefault(a.Id, 0m),
                        // ⚠️ En utrangerad tillgång är värd NOLL — utrangeringen har bokfört bort
                        //    det som återstod. Planens restvärde här hade visat en pjäs föreningen
                        //    inte längre har som om den fanns kvar.
                        BookValue = a.DisposedDate is DateTime gone && gone.Date <= yearTo.Date
                            ? 0m
                            : LedgerDepreciation.BookValue(a, yearTo),
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
        /// 1229 kredit, så debetsidan är kostnaden. Att summera båda hade gett dubbla beloppet.
        /// ⚠️ Bara <see cref="LedgerSourceType.AssetDepreciation"/> — utrangeringen har debet på
        /// minuskontot och förlustkontot och är ingen avskrivning.</para>
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

            // ⚠️ Inget konto valt ⇒ förslaget, inte ett fel. Ett äldre formulär (eller ett anrop
            //    som inte känner fältet) ska ge BAS-minuskontot, aldrig 0.
            if (asset.AccumulatedDepreciationAccountNumber == 0)
                asset.AccumulatedDepreciationAccountNumber =
                    LedgerDepreciation.AccumulatedAccountFor(asset.AssetAccountNumber);

            if (LedgerAccountClass.Of(asset.AccumulatedDepreciationAccountNumber) != LedgerAccountClass.Assets)
                return (false, "Kontot för ackumulerade avskrivningar måste vara ett balanskonto i klass 1.", 0);

            // ⚠️⚠️ SAMMA KONTO ÄR DIREKTMODELLEN I FÖRKLÄDNAD. Då minskas anskaffningsvärdet med
            //    avskrivningen — precis det Michael rättade oss i (se LedgerAsset).
            if (asset.AccumulatedDepreciationAccountNumber == asset.AssetAccountNumber)
                return (false, "Avskrivningarna ska samlas på ett eget konto, t.ex. 1229 — inte på "
                             + "samma konto som tillgången. Anskaffningsvärdet ska stå kvar.", 0);

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, asset.IssuerId);

                var missing = EnsureAccount(ldb, asset.IssuerType, asset.IssuerId,
                                            asset.AccumulatedDepreciationAccountNumber);
                if (missing is not null) return (false, missing, 0);

                if (asset.Id == 0)
                {
                    ldb.Execute(
                        @"INSERT INTO dbo.LedgerAsset
                            (IssuerType, IssuerId, Name, Note, AssetAccountNumber,
                             DepreciationAccountNumber, AccumulatedDepreciationAccountNumber,
                             InUseDate, AcquisitionAmount,
                             UsefulLifeYears, ResidualValue, CreatedByMemberId, CreatedUtc)
                          VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12)",
                        asset.IssuerType, asset.IssuerId, asset.Name.Trim(), asset.Note,
                        asset.AssetAccountNumber, asset.DepreciationAccountNumber,
                        asset.AccumulatedDepreciationAccountNumber,
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
                        || current.DepreciationAccountNumber != asset.DepreciationAccountNumber
                        || current.AccumulatedDepreciationAccountNumber != asset.AccumulatedDepreciationAccountNumber)
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
                             AcquisitionAmount = @6, UsefulLifeYears = @7, ResidualValue = @8,
                             AccumulatedDepreciationAccountNumber = @11
                       WHERE Id = @0 AND IssuerType = @9 AND IssuerId = @10",
                    asset.Id, asset.Name.Trim(), asset.Note, asset.AssetAccountNumber,
                    asset.DepreciationAccountNumber, asset.InUseDate.Date, asset.AcquisitionAmount,
                    asset.UsefulLifeYears, asset.ResidualValue, asset.IssuerType, asset.IssuerId,
                    asset.AccumulatedDepreciationAccountNumber);

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
        /// Utrangerar en tillgång och <b>bokför</b> det. Raden står kvar i registret.
        ///
        /// <para><b>⚠️⚠️ UTRANGERINGEN BOKFÖRS AV OSS.</b> Med ett minuskonto måste både
        /// anskaffningsvärdet och det ackumulerade bort — belopp kassören annars får räkna fram ur
        /// registret för hand. Fram till 2026-09-24 sattes bara en flagga och kassören ombads
        /// "bokföra resten själv"; en tillgång som lämnat föreningen låg då kvar i balansräkningen
        /// tills någon kom ihåg den.</para>
        ///
        /// <para>Två verifikationer, i ordning: (1) utrangeringsårets återstående avskrivning fram
        /// till dagen, (2) utrangeringen — minuskontot debet, förlusten debet, tillgångskontot
        /// kredit med hela anskaffningsvärdet. <b>Försäljningen</b> är en egen intäkt (t.ex. 1930 /
        /// 3970) som kassören bokför under Bokför; beloppet vet bara hen.</para>
        ///
        /// <para><b>⚠️ Tål att köras om.</b> Stegen är inte en transaktion — varje post går genom
        /// bokföringens egen väg. Men steg 1 bokför bara det som återstår, steg 2 hoppas över om
        /// utrangeringen redan ligger, och flaggan sätts sist. Ett avbrott halvvägs rättas alltså
        /// genom att trycka igen.</para>
        /// </summary>
        public (bool Ok, string? Error) Dispose(
            int issuerType, int issuerId, int assetId, DateTime when, string reason,
            int lossAccountNumber, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "Skriv vad som hände med tillgången — såld, skrotad eller stulen.");

            if (byMemberId <= 0) return (false, "Du måste vara inloggad.");

            when = when.Date;

            LedgerAsset asset;
            decimal remainingThisYear;
            bool disposalPosted;
            (decimal Accumulated, decimal BookValue) amounts;

            // ── Läsfasen. ⚠️ Anslutningen STÄNGS innan något bokförs: bokföringen öppnar sin
            //    egen, och en egen anslutning inuti någon annans är den låsfälla som hängde
            //    prod i 120 s (minnet bridge-inside-someone-elses-scope-deadlocks).
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var found = ldb.Fetch<LedgerAsset>(
                    "SELECT * FROM dbo.LedgerAsset WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    assetId, issuerType, issuerId).FirstOrDefault();

                if (found is null) return (false, "Tillgången finns inte, eller hör till en annan förening.");
                if (found.IsDisposed) return (false, "Tillgången är redan utrangerad.");
                asset = found;

                if (when < asset.InUseDate.Date)
                    return (false, $"Tillgången togs i bruk {asset.InUseDate:yyyy-MM-dd} och kan inte "
                                 + "ha lämnat föreningen före det.");

                // ⚠️ En avskrivning bokförd EFTER utrangeringsdagen gör minuskontot större än
                //    planen fram till dagen — utrangeringen skulle lämna en rest på 1229.
                var laterPosting = ldb.ExecuteScalar<DateTime?>(
                    @"SELECT MAX(AccountingDate) FROM dbo.LedgerJournalEntry
                       WHERE IssuerType = @0 AND IssuerId = @1 AND SourceType = @2 AND SourceId = @3",
                    issuerType, issuerId, SourceType, assetId);

                if (laterPosting is DateTime last && last.Date > when)
                    return (false, $"En avskrivning på tillgången är redan bokförd {last:yyyy-MM-dd}, "
                                 + "efter utrangeringsdagen. Välj ett senare datum, eller rätta "
                                 + "avskrivningen först.");

                var years = ldb.Fetch<LedgerFiscalYear>(
                    @"SELECT * FROM dbo.LedgerFiscalYear
                       WHERE IssuerType = @0 AND IssuerId = @1 AND StartDate <= @2
                       ORDER BY StartDate",
                    issuerType, issuerId, when);

                var year = years.FirstOrDefault(y => y.EndDate.Date >= when);
                if (year is null)
                    return (false, $"Det finns inget räkenskapsår som omfattar {when:yyyy-MM-dd}. "
                                 + "Lägg upp året i ekonomiinställningarna först.");

                var disposedCopy = LedgerDepreciation.CopyDisposedOn(asset, when);

                // ⚠️⚠️ TIDIGARE ÅR MÅSTE VARA BOKFÖRDA. Utrangeringen tar bort PLANENS ackumulerade
                //    belopp från minuskontot; saknas ett års avskrivning där blir 1229 negativt.
                //    År före föreningens första räkenskapsår hos oss antas ligga i den ingående
                //    balansen — dem kan vi inte se.
                foreach (var earlier in years.Where(y => y.EndDate.Date < year.StartDate.Date))
                {
                    var planned = LedgerDepreciation.ForPeriod(disposedCopy, earlier.StartDate, earlier.EndDate);
                    var postedThen = PostedByAsset(ldb, issuerType, issuerId, earlier.StartDate, earlier.EndDate)
                        .GetValueOrDefault(assetId, 0m);

                    if (planned - postedThen > 0.004m)
                        return (false, $"Avskrivningen för {earlier.Year} är inte bokförd på "
                                     + $"{asset.Name} ({planned - postedThen:N2} kr saknas). "
                                     + "Bokför den först, så att utrangeringen går jämnt upp.");
                }

                var plannedThisYear = LedgerDepreciation.ForPeriod(disposedCopy, year.StartDate, when);
                var postedThisYear = PostedByAsset(ldb, issuerType, issuerId, year.StartDate, year.EndDate)
                    .GetValueOrDefault(assetId, 0m);
                remainingThisYear = plannedThisYear - postedThisYear is var r && r > 0.004m ? r : 0m;

                amounts = LedgerDepreciation.Disposal(asset, when);

                disposalPosted = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerJournalEntry
                       WHERE IssuerType = @0 AND IssuerId = @1 AND SourceType = @2 AND SourceId = @3",
                    issuerType, issuerId, LedgerSourceType.AssetDisposal, assetId) > 0;

                if (!disposalPosted && amounts.BookValue > 0m)
                {
                    if (LedgerAccountClass.Of(lossAccountNumber) is < 4 or > 7)
                        return (false, "Välj ett kostnadskonto (klass 4–7) för det värde som återstår.");

                    var missing = EnsureAccount(ldb, issuerType, issuerId, lossAccountNumber);
                    if (missing is not null) return (false, missing);
                }

                var missingAcc = EnsureAccount(ldb, issuerType, issuerId,
                                               asset.AccumulatedDepreciationAccountNumber);
                if (missingAcc is not null) return (false, missingAcc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utrangeringen kunde inte förberedas för tillgång {Id}.", assetId);
                return (false, "Utrangeringen kunde inte sparas.");
            }

            var blocked = _posting.PostingBlockedReason(issuerType, issuerId, when);
            if (blocked is not null) return (false, blocked);

            try
            {
                // (1) Utrangeringsårets återstående avskrivning, daterad utrangeringsdagen.
                if (remainingThisYear > 0m)
                {
                    var dep = _posting.Post(new LedgerPostingRequest
                    {
                        IssuerType = issuerType,
                        IssuerId = issuerId,
                        AccountingDate = when,
                        EventDate = when,
                        Description = $"Avskrivning {asset.Name} fram till utrangering",
                        SourceType = SourceType,
                        SourceId = asset.Id,
                        CreatedByMemberId = byMemberId,
                        Lines = new List<LedgerPostingLine>
                        {
                            new() { AccountNumber = asset.DepreciationAccountNumber, Debit = remainingThisYear },
                            new() { AccountNumber = asset.AccumulatedDepreciationAccountNumber, Credit = remainingThisYear }
                        }
                    });

                    if (!dep.Success) return (false, $"Avskrivningen fram till utrangeringen: {dep.Error}");
                }

                // (2) Utrangeringen.
                if (!disposalPosted)
                {
                    var lines = new List<LedgerPostingLine>();

                    if (amounts.Accumulated > 0m)
                        lines.Add(new() { AccountNumber = asset.AccumulatedDepreciationAccountNumber,
                                          Debit = amounts.Accumulated });

                    if (amounts.BookValue > 0m)
                        lines.Add(new() { AccountNumber = lossAccountNumber, Debit = amounts.BookValue });

                    lines.Add(new() { AccountNumber = asset.AssetAccountNumber,
                                      Credit = asset.AcquisitionAmount });

                    var disp = _posting.Post(new LedgerPostingRequest
                    {
                        IssuerType = issuerType,
                        IssuerId = issuerId,
                        AccountingDate = when,
                        EventDate = when,
                        Description = $"Utrangering {asset.Name}: {reason.Trim()}",
                        SourceType = LedgerSourceType.AssetDisposal,
                        SourceId = asset.Id,
                        CreatedByMemberId = byMemberId,
                        Lines = lines
                    });

                    if (!disp.Success) return (false, $"Utrangeringen: {disp.Error}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utrangeringen kunde inte bokföras för tillgång {Id}.", assetId);
                return (false, "Utrangeringen kunde inte bokföras. Försök igen — det som redan "
                             + "bokförts bokförs inte en gång till.");
            }

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                ldb.Execute(
                    @"UPDATE dbo.LedgerAsset
                         SET DisposedDate = @3, DisposalReason = @4, DisposedByMemberId = @5
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2 AND DisposedDate IS NULL",
                    assetId, issuerType, issuerId, when, reason.Trim(), byMemberId);

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utrangeringen bokfördes men flaggan kunde inte sättas för {Id}.", assetId);
                return (false, "Utrangeringen är bokförd, men registret kunde inte uppdateras. "
                             + "Försök igen — den bokförs inte en gång till.");
            }
        }

        /// <summary>
        /// Ser till att ett konto finns i föreningens kontoplan. Saknas det men finns i mallen
        /// läggs det till; annars ett felmeddelande.
        ///
        /// <para><b>⚠️ Varför här och inte bara i mallen:</b> mallen kopieras in när föreningen
        /// sätts upp, och <c>EnsureIssuer</c> körs inte igen. En förening som satts upp innan
        /// 1229 fanns i mallen hade annars inte kunnat skriva av alls. Bara konton som SAKNAS
        /// läggs till — ett konto föreningen döpt om rörs inte, precis som vid uppsättningen.</para>
        /// </summary>
        private static string? EnsureAccount(LedgerDb ldb, int issuerType, int issuerId, int number)
        {
            var exists = ldb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2",
                issuerType, issuerId, number) > 0;

            if (exists) return null;

            if (LedgerChartTemplate.Find(number) is not { } template)
                return $"Kontot {number} finns inte i föreningens kontoplan. Lägg till det under "
                     + "Inställningar, eller välj ett annat.";

            ldb.Execute(
                @"INSERT INTO dbo.LedgerAccount (IssuerType, IssuerId, Number, Name, IsActive, FromTemplate)
                  VALUES (@0, @1, @2, @3, 1, 1)",
                issuerType, issuerId, template.Number, template.Name);

            return null;
        }

        /// <summary>
        /// Mallens konton för tillgångar (minuskonton och utrangeringens förlust- och vinstkonto)
        /// som föreningen ännu inte har. Formuläret visar dem som val med "läggs till".
        /// </summary>
        public List<LedgerChartTemplate.TemplateAccount> MissingAssetTemplateAccounts(int issuerType, int issuerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var have = ldb.Fetch<int>(
                    "SELECT Number FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1",
                    issuerType, issuerId).ToHashSet();

                return LedgerChartTemplate.Accounts
                    .Where(a => IsAssetSupportAccount(a.Number) && !have.Contains(a.Number))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa kontoplanen för {Typ}/{Id}.", issuerType, issuerId);
                return new();
            }
        }

        /// <summary>Mallens minuskonton i klass 1 (x9) plus utrangeringens förlustkonto.</summary>
        private static bool IsAssetSupportAccount(int number) =>
            (LedgerAccountClass.Of(number) == LedgerAccountClass.Assets && number % 10 == 9)
            || number == LedgerChartTemplate.DisposalLossAccount;

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

            var needing = List(issuerType, issuerId, yearFrom, yearTo).Where(x => x.NeedsPosting).ToList();

            // ⚠️ Minuskontona läggs till ur mallen INNAN något bokförs, och anslutningen stängs
            //    före första Post — en förening uppsatt innan 1229 fanns i mallen har det inte.
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                foreach (var number in needing.Select(v => v.AccumulatedDepreciationAccountNumber).Distinct())
                {
                    var missing = EnsureAccount(ldb, issuerType, issuerId, number);
                    if (missing is not null) problems.Add(missing);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Minuskontona kunde inte kontrolleras för {Typ}/{Id}.", issuerType, issuerId);
                problems.Add("Kontoplanen gick inte att kontrollera.");
            }

            if (problems.Count > 0) return (0, 0m, problems);

            foreach (var v in needing)
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
                            // ⚠️⚠️ MINUSKONTOT, aldrig tillgångskontot — anskaffningsvärdet
                            //    ska stå kvar på 1220 (se LedgerAsset).
                            new() { AccountNumber = v.AccumulatedDepreciationAccountNumber, Credit = v.Remaining }
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
