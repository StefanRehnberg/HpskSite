using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Utställare och sandlådor: vem bokföringen skrivs för, och hur en klubb kastar sitt test.
    ///
    /// <para><b>⚠️⚠️ "BÖRJA OM" ÄR EN INSERT, ALDRIG EN DELETE.</b> Verifikationsliggaren raderar
    /// ingenting — det är hela dess syfte. En nollställning som faktiskt raderade rader skulle
    /// kräva att oföränderlighetstriggrarna stängs av, och en kodväg som kan göra det kan också
    /// radera en riktig förenings bokföring. Att skapa en ny sandlåda löser samma behov utan att
    /// någon spärr någonsin rörs.</para>
    ///
    /// <para><b>⚠️ Den gamla sandlådan ÖVERGES, inte raderas.</b> Verifikationerna står kvar och
    /// går att förklara. Att städa bort dem är en operatörsuppgift långt senare, inte något en
    /// knapp i appen gör.</para>
    /// </summary>
    public class LedgerSandboxService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerSandboxService> _logger;

        public LedgerSandboxService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerSandboxService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Utställaren med det id:t, eller null.
        ///
        /// <para><b>⚠️⚠️ VARJE BEHÖRIGHETSKONTROLL MÅSTE GÅ VIA DEN HÄR.</b> Ett utställar-id är
        /// inte längre ett nod-id, så <c>Content.GetById(issuerId)</c> ger <c>null</c> för en
        /// sandlåda. Slår man upp ägaren fel blir svaret "ingen behörighet" i bästa fall — och i
        /// sämsta fall fel förening.</para>
        /// </summary>
        public LedgerIssuer? GetById(int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.SingleOrDefault<LedgerIssuer>(
                "SELECT * FROM dbo.LedgerIssuer WHERE Id = @0", issuerId);
        }

        /// <summary>
        /// Föreningens levande utställare. Skapas om den saknas — en förening som aldrig satt upp
        /// ekonomin har ingen rad ännu, och den ska inte behöva en migrering för att få en.
        ///
        /// <para><b>⚠️ Den levande får <c>Id = OwnerId</c>.</b> Det är samma knep som
        /// migreringen använde och det som gör att rader skrivna före sandlådorna fortfarande
        /// stämmer. Ändra det inte utan att först backfilla oföränderliga tabeller — vilket
        /// kräver att triggrarna stängs av.</para>
        /// </summary>
        public LedgerIssuer EnsureLive(int ownerType, int ownerId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var live = db.FirstOrDefault<LedgerIssuer>(
                @"SELECT * FROM dbo.LedgerIssuer
                   WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = @2",
                ownerType, ownerId, LedgerIssuerKind.Live);

            if (live is not null) return live;

            live = new LedgerIssuer
            {
                Id = ownerId,
                OwnerType = ownerType,
                OwnerId = ownerId,
                Kind = LedgerIssuerKind.Live,
                CreatedUtc = DateTime.UtcNow
            };

            db.Execute(
                @"INSERT INTO dbo.LedgerIssuer (Id, OwnerType, OwnerId, Kind, CreatedUtc)
                  VALUES (@0, @1, @2, @3, @4)",
                live.Id, live.OwnerType, live.OwnerId, live.Kind, live.CreatedUtc);

            return live;
        }

        /// <summary>Föreningens utställare: den levande först, sedan sandlådor, nyast först.</summary>
        public List<LedgerIssuer> ListForOwner(int ownerType, int ownerId, bool includeAbandoned = false)
        {
            using var db = _databaseFactory.CreateDatabase();

            var sql = @"SELECT * FROM dbo.LedgerIssuer
                         WHERE OwnerType = @0 AND OwnerId = @1"
                    + (includeAbandoned ? "" : " AND AbandonedUtc IS NULL")
                    + @" ORDER BY CASE WHEN Kind = 'live' THEN 0 ELSE 1 END, Id DESC";

            return db.Fetch<LedgerIssuer>(sql, ownerType, ownerId);
        }

        /// <summary>
        /// Skapar en ny sandlåda. Finns redan en aktiv <b>överges den</b> — det är så "börja om"
        /// ser ut, och därför behöver kassören aldrig fundera på radering.
        /// </summary>
        public LedgerIssuer CreateSandbox(int ownerType, int ownerId, string? label, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            // ⚠️ En förening har EN aktiv sandlåda i taget. Flera hade betytt att kassören måste
            // hålla reda på vilken hen står i, och det är just den förvirringen sandlådan finns
            // för att undvika.
            var abandoned = db.Execute(
                @"UPDATE dbo.LedgerIssuer SET AbandonedUtc = @3
                   WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = @2 AND AbandonedUtc IS NULL",
                ownerType, ownerId, LedgerIssuerKind.Sandbox, DateTime.UtcNow);

            var id = db.ExecuteScalar<int>("SELECT NEXT VALUE FOR dbo.SQ_LedgerIssuerSandbox");

            var issuer = new LedgerIssuer
            {
                Id = id,
                OwnerType = ownerType,
                OwnerId = ownerId,
                Kind = LedgerIssuerKind.Sandbox,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                CreatedUtc = DateTime.UtcNow,
                CreatedByMemberId = byMemberId
            };

            db.Execute(
                @"INSERT INTO dbo.LedgerIssuer (Id, OwnerType, OwnerId, Kind, Label, CreatedUtc, CreatedByMemberId)
                  VALUES (@0, @1, @2, @3, @4, @5, @6)",
                issuer.Id, issuer.OwnerType, issuer.OwnerId, issuer.Kind, issuer.Label,
                issuer.CreatedUtc, issuer.CreatedByMemberId);

            _logger.LogInformation(
                "Sandlåda {Id} skapad för {Typ}/{Agare}. {Antal} tidigare sandlåda(or) övergavs.",
                issuer.Id, ownerType, ownerId, abandoned);

            return issuer;
        }

        /// <summary>
        /// ⚠️ Den levande utställaren kan ALDRIG överges. Den är föreningens riktiga bokföring,
        /// och att kunna kasta den vore precis det här bygget finns för att göra omöjligt.
        /// </summary>
        public bool Abandon(int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();

            return db.Execute(
                @"UPDATE dbo.LedgerIssuer SET AbandonedUtc = @1
                   WHERE Id = @0 AND Kind = 'sandbox' AND AbandonedUtc IS NULL",
                issuerId, DateTime.UtcNow) > 0;
        }
    }
}
