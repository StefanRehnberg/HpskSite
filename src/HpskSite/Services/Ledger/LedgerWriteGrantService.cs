using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// "Vilka får arbeta med ekonomin?" — rätten som kassören, ordföranden eller en administratör
    /// kan ge någon annan. Se <see cref="LedgerWriteGrant"/> för varför den finns.
    ///
    /// <para><b>⚠️ VEM som får ge avgörs INTE här</b> utan i <see cref="LedgerAccessService"/>
    /// (<c>CanManageWriteGrants</c>). Den här tjänsten lagrar och svarar; att gå förbi grinden
    /// genom att anropa den direkt från en endpoint utan kontroll är samma fel som
    /// <c>AuthorizeWriteAsync</c> finns för att förhindra.</para>
    /// </summary>
    public class LedgerWriteGrantService
    {
        /// <summary>Längsta tillåtna giltighet. Längre än så är inte "hjälp för en tid".</summary>
        public const int MaxMonths = 24;

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerWriteGrantService> _logger;

        public LedgerWriteGrantService(IUmbracoDatabaseFactory databaseFactory, ILogger<LedgerWriteGrantService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Har medlemmen en gällande rätt i föreningen? <b>Fel = nej</b> — en trasig fråga får
        /// aldrig öppna bokföringen.
        /// </summary>
        public bool HasActive(int ownerType, int ownerId, int memberId)
        {
            if (memberId <= 0 || ownerId <= 0) return false;

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerWriteGrant
                       WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2
                         AND RevokedUtc IS NULL AND EndsDate >= @3",
                    ownerType, ownerId, memberId, DateTime.Today) > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte pröva ekonomirätten för medlem {Id}.", memberId);
                return false;
            }
        }

        /// <summary>Föreningens lista — gällande först, sedan avslutade och utgångna.</summary>
        public List<LedgerWriteGrant> ListForOwner(int ownerType, int ownerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.Fetch<LedgerWriteGrant>(
                        "SELECT * FROM dbo.LedgerWriteGrant WHERE OwnerType = @0 AND OwnerId = @1",
                        ownerType, ownerId)
                    .OrderByDescending(g => g.IsActive).ThenByDescending(g => g.GrantedUtc)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa ekonomirätterna för {Typ}/{Id}.", ownerType, ownerId);
                return new();
            }
        }

        /// <summary>
        /// Ger rätten. <b>En gällande rätt per person</b>: finns en redan förlängs den inte tyst —
        /// kassören ska se ett beslut, inte en rad som bytt datum.
        /// </summary>
        public (bool Ok, string? Error, int Id) Grant(
            int ownerType, int ownerId, int memberId, string memberName,
            DateTime endsDate, string? reason,
            int byMemberId, string byName, string byRole)
        {
            if (memberId <= 0) return (false, "Välj vem som ska få arbeta med ekonomin.", 0);
            if (byMemberId <= 0) return (false, "Du måste vara inloggad.", 0);
            if (endsDate.Date < DateTime.Today) return (false, "Slutdatumet har redan passerat.", 0);
            if (endsDate.Date > DateTime.Today.AddMonths(MaxMonths))
                return (false, $"Rätten kan gälla högst {MaxMonths / 12} år framåt. Förnya den när den löper ut.", 0);

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var existing = db.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerWriteGrant
                       WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2
                         AND RevokedUtc IS NULL AND EndsDate >= @3",
                    ownerType, ownerId, memberId, DateTime.Today);

                if (existing > 0)
                    return (false, $"{memberName} har redan rätten. Avsluta den först om slutdatumet ska ändras.", 0);

                var id = db.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerWriteGrant
                        (OwnerType, OwnerId, MemberId, MemberName, GrantedByMemberId, GrantedByName,
                         GrantedByRole, GrantedUtc, EndsDate, Reason)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9);
                      SELECT CAST(SCOPE_IDENTITY() AS int);",
                    ownerType, ownerId, memberId, memberName, byMemberId, byName, byRole,
                    DateTime.UtcNow, endsDate.Date,
                    string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());

                _logger.LogInformation(
                    "Ekonomi: {Typ}/{Id} — {Av} ({Roll}) gav {Vem} rätt att arbeta med ekonomin t.o.m. {Slut:yyyy-MM-dd}.",
                    ownerType, ownerId, byName, byRole, memberName, endsDate);

                return (true, null, id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ekonomirätten kunde inte sparas för {Typ}/{Id}.", ownerType, ownerId);
                return (false, "Rätten kunde inte sparas. Försök igen.", 0);
            }
        }

        /// <summary>Avslutar en rätt. Raden står kvar — historiken är vad revisorn granskar.</summary>
        public (bool Ok, string? Error, LedgerWriteGrant? Grant) Revoke(int ownerType, int ownerId, int grantId, int byMemberId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var rows = db.Execute(
                    @"UPDATE dbo.LedgerWriteGrant SET RevokedUtc = @3, RevokedByMemberId = @4
                       WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND RevokedUtc IS NULL",
                    grantId, ownerType, ownerId, DateTime.UtcNow, byMemberId);

                if (rows == 0) return (false, "Rätten är redan avslutad, eller hör till en annan förening.", null);

                var g = db.FirstOrDefault<LedgerWriteGrant>("SELECT * FROM dbo.LedgerWriteGrant WHERE Id = @0", grantId);
                return (true, null, g);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ekonomirätten {Id} kunde inte avslutas.", grantId);
                return (false, "Rätten kunde inte avslutas. Försök igen.", null);
            }
        }
    }
}
