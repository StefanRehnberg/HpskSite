using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Reads and writes <see cref="ClubDpaAcceptance"/> rows — a club's electronic
    /// acceptance of the Personuppgiftsbiträdesavtal (DPA). Acceptance is per club,
    /// per contract version. A club is "current" when it has accepted
    /// <see cref="DpaInfo.Version"/>.
    /// </summary>
    public class DpaAcceptanceService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<DpaAcceptanceService> _logger;

        public DpaAcceptanceService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<DpaAcceptanceService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        public record DpaStatus(
            bool Accepted,            // true when the club has accepted the CURRENT version
            string CurrentVersion,
            string? AcceptedVersion,  // the latest version the club has accepted (any)
            DateTime? AcceptedDate,
            string? AcceptedByName);

        /// <summary>
        /// Returns the club's acceptance status against the current contract version.
        /// Defensive: any DB error (e.g. table not yet created) is treated as "not accepted"
        /// so the gate never hard-fails the club admin panel.
        /// </summary>
        public async Task<DpaStatus> GetStatusForClubAsync(int clubId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var rows = await db.FetchAsync<ClubDpaAcceptance>(
                    "WHERE ClubId = @0 ORDER BY AcceptedDate DESC", clubId);

                var latest = rows.FirstOrDefault();
                var current = rows.FirstOrDefault(r => r.DpaVersion == DpaInfo.Version);

                if (current != null)
                    return new DpaStatus(true, DpaInfo.Version, current.DpaVersion,
                        current.AcceptedDate, current.AcceptedByName);

                return new DpaStatus(false, DpaInfo.Version, latest?.DpaVersion,
                    latest?.AcceptedDate, latest?.AcceptedByName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read ClubDpaAcceptance for club {ClubId} — treating as not accepted. " +
                    "Has create-club-dpa-acceptance-table.sql been run?", clubId);
                return new DpaStatus(false, DpaInfo.Version, null, null, null);
            }
        }

        /// <summary>
        /// Records (or refreshes) the club's acceptance of the current contract version.
        /// Idempotent on (ClubId, DpaInfo.Version): a repeat accept just updates the
        /// timestamp / acceptor on the existing row rather than creating duplicates.
        /// </summary>
        public async Task RecordAcceptanceAsync(int clubId, int memberId, string? memberName, string? ipAddress)
        {
            using var db = _databaseFactory.CreateDatabase();

            var existing = await db.SingleOrDefaultAsync<ClubDpaAcceptance>(
                "WHERE ClubId = @0 AND DpaVersion = @1", clubId, DpaInfo.Version);

            if (existing != null)
            {
                existing.AcceptedByMemberId = memberId;
                existing.AcceptedByName = memberName;
                existing.AcceptedDate = DateTime.Now;
                existing.IpAddress = ipAddress;
                await db.UpdateAsync(existing);
            }
            else
            {
                await db.InsertAsync(new ClubDpaAcceptance
                {
                    ClubId = clubId,
                    DpaVersion = DpaInfo.Version,
                    AcceptedByMemberId = memberId,
                    AcceptedByName = memberName,
                    AcceptedDate = DateTime.Now,
                    IpAddress = ipAddress
                });
            }

            _logger.LogInformation(
                "Club {ClubId} accepted DPA version {Version} (by member {MemberId}).",
                clubId, DpaInfo.Version, memberId);
        }
    }
}
