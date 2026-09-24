using HpskSite.Models;
using System.Text;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Fordringsexporten: en SIE-fil med föreningens <b>fordringar</b>, för klubbar vars bokföring
    /// ligger i ett eget program.
    ///
    /// <para><b>⚠️⚠️ DET HÄR ÄR INTE SAMMA SAK SOM <see cref="LedgerSieExportService"/>.</b> Den
    /// exporterar vår journal — allt vi bokfört. Den här exporterar <i>anspråken</i>: vad
    /// föreningen har rätt att få in. Två filer, två ändamål, och de får aldrig slås ihop.</para>
    ///
    /// <para><b>Formen kommer från Fredrik (Varbergs PK, 2026-09-22), och det är hans konstruktion
    /// som gör exporten möjlig alls:</b> vi lämnar <c>1510 debet / 39xx kredit</c> när avgiften
    /// uppstår, och klubbens bankkoppling bokför sedan <c>1930 debet / 1510 kredit</c> när pengarna
    /// kommer. Utan fordringsbenet skulle bankhändelsen skapa intäkten en <b>andra</b> gång — och
    /// det var just den dubbelbokföringen som fick oss att hålla exporten borta från den
    /// bankkopplade klubben.</para>
    ///
    /// <para><b>⚠️⚠️ ANSPRÅKEN TAS PÅ NÄR DE UPPSTOD, aldrig på om de är obetalda.</b> En avgift
    /// som både begärdes och betalades inom perioden måste ändå med: klubbens bankkoppling bokför
    /// <c>1930/1510</c> för den, och saknas fordringsbenet står 1510 negativt för alltid.</para>
    /// </summary>
    public class LedgerReceivableExportService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerReceivableExportService> _logger;

        public LedgerReceivableExportService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerReceivableExportService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>En rad i förhandsvisningen: ett fordrings-/intäktspar.</summary>
        public class Row
        {
            public int ReceivableAccount { get; set; }
            public string ReceivableName { get; set; } = "";
            public int RevenueAccount { get; set; }
            public string RevenueName { get; set; } = "";
            public string What { get; set; } = "";
            public int Count { get; set; }
            public decimal Amount { get; set; }
        }

        public class Result
        {
            public DateTime From { get; set; }
            public DateTime To { get; set; }
            public List<Row> Rows { get; } = new();
            public decimal Total => Rows.Sum(r => r.Amount);

            /// <summary>
            /// Roller som saknar konto. <b>⚠️ Sägs alltid</b> — en fordran utan konto kan inte
            /// exporteras, och att tyst utelämna den gör filen ofullständig utan att någon vet.
            /// </summary>
            public List<string> MissingRoles { get; } = new();

            /// <summary>Bär FILEN mer än ett fordringskonto? Beskriver innehållet, inte valet.</summary>
            public bool IsSplit => Rows.Select(r => r.ReceivableAccount).Distinct().Count() > 1;

            /// <summary>
            /// Har föreningen PEKAT UT minst ett eget fordringskonto per intäktsslag?
            ///
            /// <para><b>⚠️⚠️ SKILD FRÅN <see cref="IsSplit"/>, och skillnaden är inte akademisk.</b>
            /// <c>IsSplit</c> härleds ur raderna, så en period utan avgifter ger <c>false</c> —
            /// och ytan skrev då "Allt på ett fordringskonto" till en klubb som just delat upp
            /// dem. Det är ett påstående om föreningens egen inställning, och det var
            /// motsatsen till sanningen. Uppdelningen är ett VAL och läses ur kontoplanen;
            /// raderna säger bara vad den här filen råkar innehålla.</para>
            /// </summary>
            public bool SplitConfigured { get; set; }
        }

        /// <summary>
        /// Summerar anspråken i perioden, per (fordringskonto, intäktskonto).
        ///
        /// <para>⚠️ Summerat, inte en rad per betalning. En klubb med 400 anmälningar ska inte
        /// importera 400 verifikationer — den vill ha en post per konto och period, precis som
        /// den skulle bokfört den för hand.</para>
        /// </summary>
        public Result Build(int issuerType, int issuerId, DateTime from, DateTime to)
        {
            var result = new Result { From = from.Date, To = to.Date };

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            // Kontoplanens rollmappning. Fordringsrollen är VALFRI; saknas den faller anspråket
            // tillbaka på den allmänna kundfordran — det är vad "valbart" betyder i praktiken.
            var roles = ldb.Fetch<RoleRow>(
                @"SELECT r.RoleKey, r.AccountNumber, a.Name
                    FROM dbo.LedgerAccountRole r
                    LEFT JOIN dbo.LedgerAccount a
                           ON a.IssuerType = r.IssuerType AND a.IssuerId = r.IssuerId
                          AND a.Number = r.AccountNumber
                   WHERE r.IssuerType = @0 AND r.IssuerId = @1",
                issuerType, issuerId);

            var byRole = roles.Where(r => r.AccountNumber > 0)
                              .ToDictionary(r => r.RoleKey, r => r);

            // Valet läses ur kontoplanen, aldrig ur raderna — se SplitConfigured.
            result.SplitConfigured = LedgerAccountRoles.Optional.Any(byRole.ContainsKey);

            // ⚠️ Anspråken tas på CreatedUtc — när avgiften uppstod. Se klassens sammanfattning:
            //    att filtrera på obetalda skulle lämna 1510 negativt för allt som hann betalas.
            var claims = ldb.Fetch<ClaimRow>(
                @"SELECT SourceType, COUNT(*) AS Count_, SUM(Amount) AS Amount
                    FROM dbo.LedgerPayment
                   WHERE IssuerType = @0 AND IssuerId = @1
                     AND VoidedUtc IS NULL
                     AND CreatedUtc >= @2 AND CreatedUtc < @3
                   GROUP BY SourceType",
                issuerType, issuerId, from.Date, to.Date.AddDays(1));

            // ⚠️⚠️ MEDLEMS- OCH KRETSAVGIFTERNA LIGGER INTE I LedgerPayment. De bor i
            //    MembershipFeeCharge, och exporten läste bara betalningstabellen — så den klubb som
            //    exporten FINNS för (stor förening, eget program, bankkoppling) fick en fil utan
            //    sin största intäkt. Bankkopplingen bokförde sedan 1930/1510 för varje inbetald
            //    avgift, och 1510 stod negativt för alltid. Precis det felet klassens egen
            //    sammanfattning beskriver.
            //    Anspråket uppstår när avgiften SKICKAS (en oskickad avgift är ingen fordran —
            //    ingen har fått en räkning), eller när medlemmen väljer sin medlemstyp om det sker
            //    senare, eftersom beloppet först då är känt. Sandlådor har inga avgifter.
            if (issuerId > 0)
                claims.AddRange(FeeClaims(db, issuerType, issuerId, from.Date, to.Date.AddDays(1)));

            foreach (var c in claims)
            {
                if (c.Amount == 0m) continue;

                var revenueRole = RevenueRoleFor(c.SourceType);
                var receivableRole = LedgerAccountRoles.ReceivableFor(revenueRole);

                // Den specifika fordringsrollen när föreningen mappat den, annars den allmänna.
                if (!byRole.TryGetValue(receivableRole, out var recv))
                    byRole.TryGetValue(LedgerAccountRoles.AccountsReceivable, out recv);

                byRole.TryGetValue(revenueRole, out var rev);

                if (recv is null || rev is null)
                {
                    var missing = recv is null
                        ? LedgerAccountRoles.Label(LedgerAccountRoles.AccountsReceivable)
                        : LedgerAccountRoles.Label(revenueRole);

                    if (!result.MissingRoles.Contains(missing)) result.MissingRoles.Add(missing);
                    continue;
                }

                // ⚠️ Slås ihop PER KONTOPAR, inte per källa: har föreningen INTE delat upp
                //    fordringarna ska tävling och medlemskap bli EN rad, annars visar
                //    förhandsvisningen två poster som i filen är samma konto.
                var existing = result.Rows.FirstOrDefault(
                    r => r.ReceivableAccount == recv.AccountNumber
                      && r.RevenueAccount == rev.AccountNumber);

                if (existing is null)
                {
                    result.Rows.Add(new Row
                    {
                        ReceivableAccount = recv.AccountNumber,
                        ReceivableName = recv.Name ?? "",
                        RevenueAccount = rev.AccountNumber,
                        RevenueName = rev.Name ?? "",
                        What = LedgerAccountRoles.Label(revenueRole),
                        Count = c.Count_,
                        Amount = c.Amount
                    });
                }
                else
                {
                    existing.Count += c.Count_;
                    existing.Amount += c.Amount;
                }
            }

            result.Rows.Sort((a, b) => a.ReceivableAccount.CompareTo(b.ReceivableAccount));
            return result;
        }

        /// <summary>Filen. En verifikation per fordringskonto, daterad periodens sista dag.</summary>
        public byte[] BuildFile(Result r, string issuerName, bool sandbox)
        {
            var sb = new StringBuilder();
            void Post(string name, params string?[] f) => sb.AppendLine(SieFormat.Post(name, f));

            Post("FLAGGA", "0");
            Post("PROGRAM", "pistol.nu", "1.0");
            Post("FORMAT", "PC8");
            Post("GEN", SieFormat.Date(DateTime.Today));
            Post("SIETYP", SieFormat.TypeVerifications);

            // ⚠️ Filen SÄGER vad den är. Läses den som en vanlig bokföringsexport dubbelbokförs
            //    intäkterna — och den som öppnar filen om ett år ska inte behöva gissa.
            Post("PROSA", $"Fordringar {SieFormat.Date(r.From)}–{SieFormat.Date(r.To)} "
                        + "från pistol.nu. Motbokas när betalningen syns på kontot.");

            if (sandbox)
            {
                Post("PROSA", "SANDLÅDA – testdata, inte riktig bokföring");
                Post("FNAMN", "SANDLÅDA " + issuerName);
            }
            else Post("FNAMN", issuerName);

            Post("RAR", "0", SieFormat.Date(r.From), SieFormat.Date(r.To));

            foreach (var account in r.Rows.Select(x => x.ReceivableAccount).Distinct())
                Post("KONTO", account.ToString(),
                     r.Rows.First(x => x.ReceivableAccount == account).ReceivableName);

            foreach (var account in r.Rows.Select(x => x.RevenueAccount).Distinct())
                Post("KONTO", account.ToString(),
                     r.Rows.First(x => x.RevenueAccount == account).RevenueName);

            // ⚠️ EN verifikation per fordringskonto. Delar föreningen upp fordringarna blir det
            //    två — och det är hela poängen med uppdelningen: de ska gå att följa var för sig.
            int number = 1;

            foreach (var group in r.Rows.GroupBy(x => x.ReceivableAccount))
            {
                var text = $"Fordringar {SieFormat.Date(r.From)}–{SieFormat.Date(r.To)}";

                sb.AppendLine(SieFormat.Post("VER", "F", number++.ToString(),
                    SieFormat.Date(r.To), text, SieFormat.Date(DateTime.Today)));
                sb.AppendLine("{");

                // Fordran debet, intäkten kredit. ⚠️ Kredit skrivs NEGATIVT i SIE — ett tecknat
                //    belopp per rad, inte två fält. Skrivs den positivt balanserar inte
                //    verifikationen och hela filen avvisas av mottagaren.
                sb.AppendLine("   " + SieFormat.Post("TRANS", group.Key.ToString(), "{}",
                    SieFormat.Amount(group.Sum(x => x.Amount)), SieFormat.Date(r.To), text));

                foreach (var row in group)
                    sb.AppendLine("   " + SieFormat.Post("TRANS", row.RevenueAccount.ToString(), "{}",
                        SieFormat.Amount(-row.Amount), SieFormat.Date(r.To), row.What));

                sb.AppendLine("}");
            }

            return Encoding.GetEncoding(SieFormat.CodePage).GetBytes(sb.ToString());
        }

        /// <summary>Medlemsavgifter (klubb) eller kretsavgifter (krets) som blev fordringar i perioden.</summary>
        private static IEnumerable<ClaimRow> FeeClaims(
            IUmbracoDatabase db, int issuerType, int issuerId, DateTime from, DateTime toExclusive)
        {
            var isRegion = issuerType == (int)DocumentOwnerType.Region;
            var ownerFilter = isRegion ? "c.IssuerType = 1 AND c.RegionId = @0" : "c.IssuerType = 0 AND c.ClubId = @0";

            var row = db.Fetch<ClaimRow>(
                $@"SELECT COUNT(*) AS Count_, ISNULL(SUM(c.Amount), 0) AS Amount
                     FROM dbo.MembershipFeeCharge c
                    WHERE {ownerFilter}
                      AND c.RequestSentDate IS NOT NULL
                      AND c.Amount > 0
                      AND CASE WHEN c.MemberChosenAt > c.RequestSentDate THEN c.MemberChosenAt
                               ELSE c.RequestSentDate END >= @1
                      AND CASE WHEN c.MemberChosenAt > c.RequestSentDate THEN c.MemberChosenAt
                               ELSE c.RequestSentDate END < @2",
                issuerId, from, toExclusive).FirstOrDefault();

            if (row is null || row.Count_ == 0) yield break;

            row.SourceType = isRegion ? LedgerSourceType.RegionFee : LedgerSourceType.MembershipFee;
            yield return row;
        }

        /// <summary>Samma indelning som betalningarnas egen — se <c>LedgerPaymentService</c>.</summary>
        private static string RevenueRoleFor(string sourceType) => sourceType switch
        {
            LedgerSourceType.CompetitionRegistration => LedgerAccountRoles.RevenueParticipationFee,
            LedgerSourceType.TeamFee => LedgerAccountRoles.RevenueParticipationFee,
            LedgerSourceType.CompetitionInvoice => LedgerAccountRoles.RevenueParticipationFee,
            LedgerSourceType.MembershipFee => LedgerAccountRoles.RevenueMembershipFee,
            LedgerSourceType.RegionFee => LedgerAccountRoles.RevenueRegionFee,
            _ => LedgerAccountRoles.RevenueOther
        };

        private class RoleRow
        {
            public string RoleKey { get; set; } = "";
            public int AccountNumber { get; set; }
            public string? Name { get; set; }
        }

        private class ClaimRow
        {
            public string SourceType { get; set; } = "";
            public int Count_ { get; set; }
            public decimal Amount { get; set; }
        }
    }
}
