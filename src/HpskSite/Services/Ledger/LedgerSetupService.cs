using HpskSite.Models;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Sätter upp en förening i verifikationsliggaren: kontoplan ur mallen, rollmappning,
    /// räkenskapsår och nummerserier.
    ///
    /// <para><b>Idempotent i båda riktningarna.</b> Körs den två gånger händer ingenting andra
    /// gången; och den <b>rör aldrig</b> ett konto eller en mappning föreningen redan har ändrat.
    /// Mallen är ett startvärde, inte ett kontrakt — en klubb som döpt om 3020 till "Starttavgifter"
    /// eller pekat om en roll ska inte få sin ändring överskriven av en omkörning.</para>
    ///
    /// <para><b>⚠️ Skapar INGEN bokföring.</b> Inga ingående balanser, ingen första verifikation.
    /// En förening som ansluter mitt i ett år behöver sina ingående balanser, och de kommer via
    /// SIE-import — inte härifrån. Utan dem kan en klubb bara ansluta vid ett årsskifte, och det är
    /// den underskattade riktningen i hela SIE-arbetet.</para>
    /// </summary>
    public class LedgerSetupService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerSetupService> _logger;

        public LedgerSetupService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerSetupService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Sätter upp föreningen för ett räkenskapsår. Säker att köra om.
        /// </summary>
        /// <param name="issuerType">Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</param>
        /// <param name="issuerId">Klubbens eller kretsens nod-id.</param>
        /// <param name="year">Räkenskapsåret.</param>
        /// <param name="startDate">
        /// Årets start. Null = 1 januari. <b>Brutet räkenskapsår antas inte bort</b> — en förening
        /// med verksamhetsår juli–juni ska kunna sätta det här.
        /// </param>
        /// <param name="endDate">Årets slut. Null = 31 december.</param>
        /// <param name="shape">
        /// Ur <see cref="LedgerIssuerShape"/>. <b>⚠⚠ OBLIGATORISK, MED FLIT.</b>
        ///
        /// <para>Den här raden avgör om vi är föreningens bokföringsprogram eller bara dess
        /// avgifts- och kontrollsystem — alltså om en evenemangsbetalning ska bli en verifikation
        /// eller inte. Fram till 2026-09-19 var <c>FullLedger</c> ett förval här, och följden hade
        /// blivit att varje förening som någon gång satts upp av en skript- eller migreringskörning
        /// tyst hamnat i bokföringsläge — med krav på räkenskapsår för att kunna ta emot pengar.
        /// Det är inte vårt val att göra åt en förening, och en parameter utan default är det
        /// enda som gör "glömde välja" omöjligt att skilja från "valde".</para>
        /// </param>
        public LedgerSetupResult EnsureIssuer(
            int issuerType,
            int issuerId,
            int year,
            string shape,
            DateTime? startDate = null,
            DateTime? endDate = null)
        {
            // ⚠️ Ett okänt värde får inte tolkas som något — minst av allt som FullLedger.
            // ⚠️ Listan frågas ur LedgerIssuerShape.IsValid, aldrig uppräknad här. En egen
            // upprepning hade behövt rättas på två ställen den dag en form tillkom — och en av
            // dem hade glömts. Formen FeesOnly tillkom 2026-09-21.
            if (!LedgerIssuerShape.IsValid(shape))
            {
                throw new ArgumentException(
                    $"Okänd föreningsform '{shape}'. Välj en av: "
                    + string.Join(", ", LedgerIssuerShape.All) + ".", nameof(shape));
            }

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var result = new LedgerSetupResult();

            // 0. Inställningsraden. Skapas först: kvittot läser momsregistreringen därifrån, och en
            //    förening utan rad skulle falla tillbaka på ett hårdkodat påstående om momsen.
            //    ⚠️ Momsregistrering är AV som förval — den är ett faktum om föreningen, inte något
            //    vi får gissa åt den.
            var hasSettings = ldb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            if (hasSettings == 0)
            {
                ldb.Execute(
                    @"INSERT INTO dbo.LedgerIssuerSettings (IssuerType, IssuerId, IsVatRegistered, Shape)
                      VALUES (@0, @1, 0, @2)",
                    issuerType, issuerId, shape);

                result.SettingsCreated = true;
            }

            // 1. Kontoplanen. Bara konton som saknas läggs till — ett konto föreningen döpt om
            //    eller stängt av rörs inte.
            var existingNumbers = ldb.Fetch<int>(
                "SELECT Number FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId).ToHashSet();

            foreach (var account in LedgerChartTemplate.Accounts)
            {
                if (existingNumbers.Contains(account.Number)) continue;

                ldb.Execute(
                    @"INSERT INTO dbo.LedgerAccount (IssuerType, IssuerId, Number, Name, IsActive, FromTemplate)
                      VALUES (@0, @1, @2, @3, 1, 1)",
                    issuerType, issuerId, account.Number, account.Name);

                result.AccountsAdded++;
            }

            // 2. Rollmappningen. Samma regel: en roll föreningen redan pekat om lämnas i fred.
            var mappedRoles = ldb.Fetch<string>(
                "SELECT RoleKey FROM dbo.LedgerAccountRole WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId).ToHashSet();

            foreach (var role in LedgerAccountRoles.All)
            {
                if (mappedRoles.Contains(role)) continue;

                if (!LedgerChartTemplate.RoleDefaults.TryGetValue(role, out var accountNumber))
                {
                    // Ska vara omöjligt — LedgerChartTemplate.RolesWithoutDefault() ska alltid vara
                    // tom, och sviten kontrollerar det. Loggas hellre än sväljs: en roll utan konto
                    // är en betalning som inte går att bokföra.
                    _logger.LogError(
                        "Kontorollen {Roll} saknar förval i LedgerChartTemplate. Utställare {Typ}/{Id} "
                        + "får ingen mappning för den, och varje post som behöver rollen kommer att fela.",
                        role, issuerType, issuerId);
                    result.RolesWithoutDefault.Add(role);
                    continue;
                }

                ldb.Execute(
                    @"INSERT INTO dbo.LedgerAccountRole (IssuerType, IssuerId, RoleKey, AccountNumber)
                      VALUES (@0, @1, @2, @3)",
                    issuerType, issuerId, role, accountNumber);

                result.RolesMapped++;
            }

            // 3. Räkenskapsåret.
            var fiscalYearId = ldb.ExecuteScalar<int?>(
                @"SELECT Id FROM dbo.LedgerFiscalYear
                   WHERE IssuerType = @0 AND IssuerId = @1 AND Year = @2",
                issuerType, issuerId, year);

            if (fiscalYearId is null)
            {
                ldb.Execute(
                    @"INSERT INTO dbo.LedgerFiscalYear (IssuerType, IssuerId, Year, StartDate, EndDate, Status)
                      VALUES (@0, @1, @2, @3, @4, @5)",
                    issuerType, issuerId, year,
                    startDate ?? new DateTime(year, 1, 1),
                    endDate ?? new DateTime(year, 12, 31),
                    LedgerFiscalYearStatus.Open);

                result.FiscalYearCreated = true;
            }

            // 4. Serierna. Två slag, två syften — kvittonummer är inte verifikationsnummer.
            //    Allokatorn skapar dem vid behov, men att göra det här gör uppsättningen synlig och
            //    ger föreningen något att sätta sitt prefix på innan första posten skrivs.
            foreach (var kind in new[] { LedgerSeriesKind.JournalEntry, LedgerSeriesKind.Receipt })
            {
                var exists = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerNumberSeries
                       WHERE IssuerType = @0 AND IssuerId = @1 AND Year = @2 AND Kind = @3",
                    issuerType, issuerId, year, kind);

                if (exists > 0) continue;

                ldb.Execute(
                    @"INSERT INTO dbo.LedgerNumberSeries (IssuerType, IssuerId, Year, Kind, Prefix, NextNumber)
                      VALUES (@0, @1, @2, @3, @4, 1)",
                    issuerType, issuerId, year, kind,
                    kind == LedgerSeriesKind.Receipt ? "K" : "V");

                result.SeriesCreated++;
            }

            _logger.LogInformation(
                "Verifikationsliggaren: utställare {Typ}/{Id} uppsatt för {Ar} — {Konton} konton, "
                + "{Roller} roller, {Serier} serier.",
                issuerType, issuerId, year,
                result.AccountsAdded, result.RolesMapped, result.SeriesCreated);

            return result;
        }

        /// <summary>
        /// Läser upp föreningens uppsättning för ekonomiytan. Skriver ingenting.
        ///
        /// <para><b>⚠️ Fyller INTE i <see cref="LedgerSetupStatus.CanPost"/> eller
        /// <see cref="LedgerSetupStatus.BlockedReason"/>.</b> Beredskapen att bokföra ägs av
        /// <c>LedgerPostingService.PostingBlockedReason</c>, som är den kontroll betalvägen
        /// faktiskt frågar. En andra bedömning här hade varit fri att säga emot den, och då är
        /// det ytan som ljuger — inte spärren.</para>
        /// </summary>
        public LedgerSetupStatus GetStatus(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var status = new LedgerSetupStatus
            {
                IssuerType = issuerType,
                IssuerId = issuerId,
                RolesTotal = LedgerAccountRoles.All.Length
            };

            var settings = ldb.FirstOrDefault<LedgerIssuerSettings>(
                "SELECT * FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            if (settings is null)
            {
                // Inte uppsatt. Allt nedanför är tomt med flit — vyn ska visa uppsättningen,
                // inte en bokföring utan innehåll.
                return status;
            }

            status.IsSetUp = true;
            status.Shape = settings.Shape ?? "";
            status.IsVatRegistered = settings.IsVatRegistered;
            status.VatNumber = settings.VatNumber;

            status.AccountCount = ldb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            var mapped = ldb.Fetch<string>(
                "SELECT RoleKey FROM dbo.LedgerAccountRole WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId).ToHashSet();

            status.RolesMapped = mapped.Count;

            foreach (var role in LedgerAccountRoles.All)
            {
                if (!mapped.Contains(role)) status.MissingRoles.Add(role);
            }

            status.FiscalYears.AddRange(ldb.Fetch<LedgerFiscalYear>(
                @"SELECT * FROM dbo.LedgerFiscalYear
                   WHERE IssuerType = @0 AND IssuerId = @1
                   ORDER BY Year DESC",
                issuerType, issuerId));

            return status;
        }

        /// <summary>
        /// Momsregistreringen: på med ett momsregistreringsnummer, eller av.
        ///
        /// <para><b>⚠️⚠️ FANNS INTE FÖRRÄN 2026-09-24.</b> Flaggan skrevs som 0 vid uppsättningen
        /// och gick sedan inte att ändra från någon yta, så en momsregistrerad förening fick
        /// <i>"Föreningen är inte momsregistrerad"</i> på varje kvitto — ett falskt påstående på en
        /// utfärdad handling (Michael Henriksson, Åmåls PK).</para>
        ///
        /// <para><b>⚠️ AV VÄGRAS när moms är bokförd i ett år som inte är fastställt.</b> Saldot på
        /// momskontona ska redovisas till Skatteverket; slogs registreringen av skulle momsraderna
        /// och momsytorna försvinna ur sikte medan skulden står kvar. Ett fastställt år är avslutat,
        /// så där är det fritt.</para>
        ///
        /// <para>⚠️ Numret sparas kvar när registreringen slås av. Det står inte på något kvitto då
        /// (kvittot läser flaggan), och slås den på igen behöver kassören inte skriva det på nytt.</para>
        /// </summary>
        public (bool Ok, string? Error) SetVat(int issuerType, int issuerId, bool registered, string? vatNumber)
        {
            string? normalized = null;

            if (registered)
            {
                var (n, err) = LedgerVat.NormalizeVatNumber(vatNumber);
                if (err is not null) return (false, err);
                normalized = n;
            }

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var exists = ldb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1",
                    issuerType, issuerId) > 0;

                if (!exists) return (false, "Sätt upp ekonomin först.");

                if (!registered)
                {
                    var vatAccounts = ldb.Fetch<int>(
                        @"SELECT AccountNumber FROM dbo.LedgerAccountRole
                           WHERE IssuerType = @0 AND IssuerId = @1 AND RoleKey IN (@2, @3)",
                        issuerType, issuerId, LedgerAccountRoles.VatOutgoing, LedgerAccountRoles.VatIncoming);

                    // ⚠️ Både momsbeloppet på källraden OCH rader på momskontona — en SIE-import
                    //    bär momsen som egna rader utan belopp på källraden.
                    var booked = ldb.ExecuteScalar<int>(
                        $@"SELECT COUNT(1)
                             FROM dbo.LedgerJournalEntryLine l
                             JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                             JOIN dbo.LedgerFiscalYear y ON y.Id = e.FiscalYearId
                            WHERE e.IssuerType = @0 AND e.IssuerId = @1
                              AND y.Status <> @2
                              AND ((l.VatAmount IS NOT NULL AND l.VatAmount <> 0)
                                   {(vatAccounts.Count > 0 ? $"OR l.AccountNumber IN ({string.Join(",", vatAccounts)})" : "")})",
                        issuerType, issuerId, LedgerFiscalYearStatus.Established);

                    if (booked > 0)
                        return (false, "Det finns bokförd moms i ett räkenskapsår som inte är avslutat. "
                                     + "Momsen ska redovisas innan registreringen slås av — slå av den "
                                     + "när året är fastställt.");
                }

                ldb.Execute(
                    registered
                        ? "UPDATE dbo.LedgerIssuerSettings SET IsVatRegistered = 1, VatNumber = @2 WHERE IssuerType = @0 AND IssuerId = @1"
                        : "UPDATE dbo.LedgerIssuerSettings SET IsVatRegistered = 0 WHERE IssuerType = @0 AND IssuerId = @1",
                    issuerType, issuerId, (object?)normalized ?? DBNull.Value);

                _logger.LogInformation(
                    "Verifikationsliggaren: utställare {Typ}/{Id} momsregistrering {Lage}.",
                    issuerType, issuerId, registered ? "PÅ" : "AV");

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Momsregistreringen kunde inte sparas för {Typ}/{Id}.", issuerType, issuerId);
                return (false, "Momsinställningen kunde inte sparas. Försök igen.");
            }
        }

        /// <summary>
        /// Byter föreningsform. <b>Egen metod med flit</b> — <see cref="EnsureIssuer"/> skriver
        /// formen bara när inställningsraden skapas, eftersom den aldrig får skriva över något
        /// föreningen valt. Utan den här metoden hade en förening som en gång hamnat i fel form
        /// suttit fast i den, och valet ska gå att ändra åt båda hållen utan att börja om.
        ///
        /// <para><b>⚠️ Byter ingenting annat.</b> Konton, mappningar, räkenskapsår och redan
        /// skrivna verifikationer står kvar. Går föreningen från <c>full</c> till <c>export</c>
        /// slutar nya betalningar bli verifikationer — de gamla raderas inte, för en
        /// verifikationsliggare raderar ingenting.</para>
        /// </summary>
        /// <returns>Sant om formen ändrades. Falskt om den redan var den efterfrågade.</returns>
        public bool SetShape(int issuerType, int issuerId, string shape)
        {
            // ⚠️ Listan frågas ur LedgerIssuerShape.IsValid, aldrig uppräknad här. En egen
            // upprepning hade behövt rättas på två ställen den dag en form tillkom — och en av
            // dem hade glömts. Formen FeesOnly tillkom 2026-09-21.
            if (!LedgerIssuerShape.IsValid(shape))
            {
                throw new ArgumentException(
                    $"Okänd föreningsform '{shape}'. Välj en av: "
                    + string.Join(", ", LedgerIssuerShape.All) + ".", nameof(shape));
            }

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var current = ldb.ExecuteScalar<string>(
                "SELECT Shape FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            if (current == shape) return false;

            var rows = ldb.Execute(
                "UPDATE dbo.LedgerIssuerSettings SET Shape = @2 WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId, shape);

            if (rows > 0)
            {
                _logger.LogInformation(
                    "Verifikationsliggaren: utställare {Typ}/{Id} bytte föreningsform {Fran} → {Till}.",
                    issuerType, issuerId, current ?? "(ingen)", shape);
            }

            return rows > 0;
        }
    }

    /// <summary>Vad uppsättningen faktiskt gjorde. Noll överallt = allt fanns redan.</summary>
    public class LedgerSetupResult
    {
        public int AccountsAdded { get; set; }
        public int RolesMapped { get; set; }
        public int SeriesCreated { get; set; }
        public bool FiscalYearCreated { get; set; }
        public bool SettingsCreated { get; set; }

        /// <summary>Ska alltid vara tom. Se <see cref="LedgerChartTemplate.RolesWithoutDefault"/>.</summary>
        public List<string> RolesWithoutDefault { get; } = new();
    }
}
