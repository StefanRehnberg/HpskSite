using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Föreningens kontoplan — att läsa, lägga till i och döpa om.
    ///
    /// <para><b>⚠️⚠️ FANNS INTE FÖRRÄN 2026-09-22.</b> Kontona såddes ur mallen och gick sedan inte
    /// att röra från någon yta alls. Rapporterat av Stefan under provning: <i>"Jag hittar inte var
    /// man ändrar i kontoplanen, lägger till konton…"</i> — och för den som matar in förra årets
    /// bokföring är det blockerande: en förening har konton vi inte sått.</para>
    ///
    /// <para><b>⚠️ ETT KONTONUMMER ÄNDRAS ALDRIG.</b> Bokförda rader bär numret, och att flytta det
    /// under dem skulle göra historiken obegriplig utan att något felar. Behövs ett annat nummer
    /// läggs ett NYTT konto upp och det gamla stängs — samma logik som liggarens rättelser.</para>
    ///
    /// <para><b>⚠️ ETT KONTO RADERAS ALDRIG, det STÄNGS</b> (<see cref="LedgerAccount.IsActive"/>).
    /// En raderad rad tar med sig förklaringen till varje verifikation som pekar på den.</para>
    /// </summary>
    public class LedgerChartService
    {
        /// <summary>
        /// Lägsta och högsta tillåtna kontonummer. <b>1000–8999</b> — klass 1 och 2 är balans,
        /// 3 och 8 intäkt, 4–7 kostnad. Ett nummer utanför spannet hör inte till någon klass, och
        /// då vet varken rapporten eller bokslutet vad raden är.
        /// </summary>
        public const int MinNumber = 1000;

        /// <inheritdoc cref="MinNumber"/>
        public const int MaxNumber = 8999;

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerChartService> _logger;

        public LedgerChartService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerChartService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Hela kontoplanen, med hur många bokförda rader varje konto bär.
        ///
        /// <para>⚠️ Antalet är inte pynt: det är skillnaden mellan ett konto som går att stänga utan
        /// följder och ett som bär historik.</para>
        /// </summary>
        public List<LedgerChartRow> List(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var accounts = ldb.Fetch<LedgerAccount>(
                @"SELECT * FROM dbo.LedgerAccount
                   WHERE IssuerType = @0 AND IssuerId = @1
                   ORDER BY Number",
                issuerType, issuerId);

            // ⚠️ EN fråga för alla konton. Per konto blir det ~39 tur och retur på en flik som
            //    öppnas varje gång någon tittar på uppsättningen.
            var used = ldb.Fetch<AccountUse>(
                @"SELECT l.AccountNumber AS Number, COUNT(*) AS Rows
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                   GROUP BY l.AccountNumber",
                issuerType, issuerId);

            var roles = ldb.Fetch<LedgerAccountRole>(
                "SELECT * FROM dbo.LedgerAccountRole WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            return accounts.Select(a => new LedgerChartRow
            {
                Number = a.Number,
                Name = a.Name,
                IsActive = a.IsActive,
                FromTemplate = a.FromTemplate,
                EntryLines = used.FirstOrDefault(u => u.Number == a.Number)?.Rows ?? 0,
                // Rollerna som pekar hit. Ett konto de hänger på går inte att stänga.
                Roles = roles.Where(r => r.AccountNumber == a.Number)
                             .Select(r => r.RoleKey)
                             .ToList()
            }).ToList();
        }

        /// <summary>
        /// Lägger till ett konto.
        ///
        /// <para>⚠️ Numret måste vara ledigt. Ett upptaget nummer är inte "samma konto igen" — det
        /// är två olika betydelser på samma rad i varje framtida rapport.</para>
        /// </summary>
        public LedgerChartResult Add(int issuerType, int issuerId, int number, string name)
        {
            name = (name ?? "").Trim();

            if (number < MinNumber || number > MaxNumber)
                return LedgerChartResult.Failed(
                    $"Kontonumret måste ligga mellan {MinNumber} och {MaxNumber}. "
                    + "1000-talen är tillgångar, 2000-talen skulder, 3000-talen intäkter och "
                    + "4000–7000-talen kostnader.");

            if (string.IsNullOrWhiteSpace(name))
                return LedgerChartResult.Failed("Kontot måste ha ett namn.");

            if (name.Length > 120)
                return LedgerChartResult.Failed("Kontonamnet får vara högst 120 tecken.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var taken = ldb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2",
                    issuerType, issuerId, number);

                if (taken > 0)
                    return LedgerChartResult.Failed($"Konto {number} finns redan. Välj ett annat nummer.");

                ldb.Execute(
                    @"INSERT INTO dbo.LedgerAccount (IssuerType, IssuerId, Number, Name, IsActive, FromTemplate)
                      VALUES (@0, @1, @2, @3, 1, 0)",
                    issuerType, issuerId, number, name);

                return new LedgerChartResult { Number = number };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte lägga till konto {Nummer} för {Typ}/{Id}.",
                    number, issuerType, issuerId);

                return LedgerChartResult.Failed("Kontot kunde inte sparas. Försök igen.");
            }
        }

        /// <summary>
        /// Döper om ett konto.
        ///
        /// <para><b>⚠️ Historiken följer INTE med, och det är rätt.</b> Varje bokförd rad bär en
        /// snapshot av kontonamnet som det var när posten skrevs — annars skulle en omdöpning
        /// skriva om vad gamla verifikationer säger. Samma regel som projektnamnet.</para>
        /// </summary>
        public LedgerChartResult Rename(int issuerType, int issuerId, int number, string name)
        {
            name = (name ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name))
                return LedgerChartResult.Failed("Kontot måste ha ett namn.");

            if (name.Length > 120)
                return LedgerChartResult.Failed("Kontonamnet får vara högst 120 tecken.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var rows = ldb.Execute(
                    "UPDATE dbo.LedgerAccount SET Name = @3 WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2",
                    issuerType, issuerId, number, name);

                return rows > 0
                    ? new LedgerChartResult { Number = number }
                    : LedgerChartResult.Failed($"Konto {number} finns inte.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte döpa om konto {Nummer} för {Typ}/{Id}.",
                    number, issuerType, issuerId);

                return LedgerChartResult.Failed("Namnet kunde inte sparas. Försök igen.");
            }
        }

        /// <summary>
        /// Stänger eller öppnar ett konto. <b>Stängt betyder bara "visa mig inte i väljaren"</b> —
        /// rapporterna läser det ändå, och gamla verifikationer pekar fortfarande på det.
        ///
        /// <para><b>⚠️ Ett konto som en kontoroll hänger på går inte att stänga.</b> Rollen är det
        /// som gör att en betalning kan bokföras alls; stängdes kontot skulle bokföringen falla
        /// först när någon faktiskt betalade — alltså långt från den här ytan, och tyst.</para>
        /// </summary>
        public LedgerChartResult SetActive(int issuerType, int issuerId, int number, bool active)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                if (!active)
                {
                    var roles = ldb.Fetch<string>(
                        @"SELECT RoleKey FROM dbo.LedgerAccountRole
                           WHERE IssuerType = @0 AND IssuerId = @1 AND AccountNumber = @2",
                        issuerType, issuerId, number);

                    if (roles.Count > 0)
                        return LedgerChartResult.Failed(
                            $"Konto {number} används av "
                            + string.Join(", ", roles.Select(LedgerAccountRoles.Label))
                            + ". Peka om den först, annars går betalningar inte att bokföra.");
                }

                var rows = ldb.Execute(
                    "UPDATE dbo.LedgerAccount SET IsActive = @3 WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2",
                    issuerType, issuerId, number, active);

                return rows > 0
                    ? new LedgerChartResult { Number = number }
                    : LedgerChartResult.Failed($"Konto {number} finns inte.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte ändra konto {Nummer} för {Typ}/{Id}.",
                    number, issuerType, issuerId);

                return LedgerChartResult.Failed("Ändringen kunde inte sparas. Försök igen.");
            }
        }

        /// <summary>
        /// Pekar om en kontoroll till ett annat konto.
        ///
        /// <para>⚠️ Målet måste finnas och vara öppet. En roll som pekar på ett stängt konto är
        /// samma sak som ingen roll alls — och det upptäcks först när en betalning ska bokföras.</para>
        /// </summary>
        public LedgerChartResult SetRole(int issuerType, int issuerId, string roleKey, int number)
        {
            if (!LedgerAccountRoles.All.Contains(roleKey))
                return LedgerChartResult.Failed("Okänd roll.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var open = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerAccount
                       WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2 AND IsActive = 1",
                    issuerType, issuerId, number);

                if (open == 0)
                    return LedgerChartResult.Failed($"Konto {number} finns inte eller är stängt.");

                var rows = ldb.Execute(
                    @"UPDATE dbo.LedgerAccountRole SET AccountNumber = @3
                       WHERE IssuerType = @0 AND IssuerId = @1 AND RoleKey = @2",
                    issuerType, issuerId, roleKey, number);

                if (rows == 0)
                {
                    ldb.Execute(
                        @"INSERT INTO dbo.LedgerAccountRole (IssuerType, IssuerId, RoleKey, AccountNumber)
                          VALUES (@0, @1, @2, @3)",
                        issuerType, issuerId, roleKey, number);
                }

                return new LedgerChartResult { Number = number };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte peka om rollen {Roll} för {Typ}/{Id}.",
                    roleKey, issuerType, issuerId);

                return LedgerChartResult.Failed("Kopplingen kunde inte sparas. Försök igen.");
            }
        }

        private class AccountUse
        {
            public int Number { get; set; }
            public int Rows { get; set; }
        }
    }

    /// <summary>En rad i kontoplanen, som ytan behöver den.</summary>
    public class LedgerChartRow
    {
        public int Number { get; set; }

        public string Name { get; set; } = "";

        public bool IsActive { get; set; }

        /// <summary>Kom ur mallen, inte från föreningen själv. Bara upplysning.</summary>
        public bool FromTemplate { get; set; }

        /// <summary>Antal bokförda rader som pekar hit. &gt; 0 = kontot bär historik.</summary>
        public int EntryLines { get; set; }

        /// <summary>Kontoroller som pekar hit. Är listan icke-tom går kontot inte att stänga.</summary>
        public List<string> Roles { get; set; } = new();

        /// <summary>1 och 2 är balanskonton — tillgångar och skulder.</summary>
        public bool IsBalance => Number < 3000;

        /// <summary>Klass 3 och 8. Samma gräns som överallt annars i liggaren.</summary>
        public bool IsIncome => (Number >= 3000 && Number <= 3999) || (Number >= 8000 && Number <= 8999);
    }

    public class LedgerChartResult
    {
        public bool Success => Error is null;

        public string? Error { get; set; }

        public int Number { get; set; }

        public static LedgerChartResult Failed(string error) => new() { Error = error };
    }
}
