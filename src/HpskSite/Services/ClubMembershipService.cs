using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Per-club membership records (ClubMembership). A person (hpskMember) has one row
    /// per club they belong to. Follows the IScopeProvider CRUD pattern (see BoardRoleService).
    /// See Documentation/MEMBER_DATABASE.md.
    /// </summary>
    public class ClubMembershipService
    {
        private readonly IScopeProvider _scopeProvider;

        public ClubMembershipService(IScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        public ClubMembership? Get(int memberId, int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE MemberId = @0 AND ClubId = @1", memberId, clubId);
        }

        /// <summary>All memberships for one club (used by fee generation).</summary>
        public List<ClubMembership> GetForClub(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE ClubId = @0", clubId);
        }

        /// <summary>
        /// Klubbens medlemskap för GODKÄNDA medlemmar (<c>cmsMember.isApproved</c>). Avgiften och
        /// medlemsantalet bygger på den här: en ansökan som väntar på godkännande har redan en rad
        /// (synken skapar den när klubben står i egenskaperna) och fakturerades förut som medlem
        /// (genomgången 2026-10-13, A5).
        /// </summary>
        public List<ClubMembership> GetForClubApprovedMembers(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembership>(
                @"SELECT cm.* FROM ClubMembership cm
                  JOIN cmsMember m ON m.nodeId = cm.MemberId
                  WHERE cm.ClubId = @0 AND m.isApproved = 1", clubId);
        }

        /// <summary>
        /// Tar bort raden för en ansökan som avslogs, om den aldrig blev ett medlemskap: inget
        /// medlemsdatum, inget hushåll, ingen bakgrundskontroll och inga anteckningar. En avslagen sökande
        /// har aldrig varit medlem och ska inte stå som "Utträdd" i klubbens historik. En rad som bär
        /// något klubben skrivit rörs inte. Returnerar true när en rad togs bort.
        /// </summary>
        public bool DeleteIfNeverMember(int memberId, int clubId)
        {
            if (memberId <= 0 || clubId <= 0) return false;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Execute(
                @"DELETE FROM ClubMembership
                   WHERE MemberId = @0 AND ClubId = @1 AND MemberSince IS NULL AND NULLIF(HouseholdId, '') IS NULL
                     AND BackgroundCheckApproved = 0 AND BackgroundCheckDate IS NULL AND NULLIF(MemberNotes, '') IS NULL",
                memberId, clubId) > 0;
        }

        /// <summary>All of a person's memberships across clubs.</summary>
        public List<ClubMembership> GetForMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE MemberId = @0", memberId);
        }

        /// <summary>All membership rows in one household within a club (familjeavgift).</summary>
        public List<ClubMembership> GetByHousehold(string householdId, int clubId)
        {
            if (string.IsNullOrWhiteSpace(householdId)) return new List<ClubMembership>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE HouseholdId = @0 AND ClubId = @1", householdId.Trim(), clubId);
        }

        /// <summary>
        /// Skapar RELATIONEN (medlem, klubb) om den saknas. Returnerar true när en rad skapades.
        ///
        /// <para><b>⚠️⚠️ SÄTTER ALDRIG <c>MemberSince</c>.</b> "Medlem sedan" är hur länge personen
        /// varit medlem i DEN HÄR klubben, och det föregår ofta pistol.nu med decennier — ett
        /// registrerings- eller godkännandedatum säger ingenting om det. Uppgiften kan bara komma
        /// från någon som vet (klubben, i medlemsdialogen under Klubbmedlemskap), och den hamnar på
        /// en handling till Polismyndigheten. Ett härlett datum vore en påhittad uppgift.
        /// Stefans beslut 2026-09-09.</para>
        ///
        /// <para><b>⚠️ RÖR ALDRIG en befintlig rad.</b> Metoden är till för att relationen ska
        /// finnas så att klubben HAR någonstans att skriva datumet — inte för att fylla i något.
        /// Fanns raden är den klubbens, med de uppgifter klubben satt.</para>
        ///
        /// <para><b>⚠️ Sväljer sina egna fel.</b> Anroparna är registrering och godkännande; ett
        /// misslyckat radskapande får inte fälla ett godkännande. Utfallet returneras så anroparen
        /// kan logga.</para>
        /// </summary>
        public bool EnsureMembership(int memberId, int clubId)
        {
            if (memberId <= 0 || clubId <= 0) return false;

            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;

                // ⚠️ Villkoret ligger i SQL:en, inte i en läsning följd av en skrivning: två
                // samtidiga anrop (godkännande + en sparning) får inte ge två rader för samma par.
                var affected = db.Execute(
                    @"INSERT INTO ClubMembership (MemberId, ClubId, MembershipStatus, CreatedDate)
                      SELECT @0, @1, @2, @3
                       WHERE NOT EXISTS (SELECT 1 FROM ClubMembership
                                          WHERE MemberId = @0 AND ClubId = @1)",
                    memberId, clubId, "Aktiv", DateTime.UtcNow);

                return affected > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sätter ett AKTIVT medlemskap (allt utom Utträdd/Avliden) till <c>Utträdd</c> med dagens datum som
        /// <c>MemberUntil</c> (om inget datum redan finns). Samma utfall som klubbadmins "Avsluta medlemskap".
        /// Returnerar true när en rad ändrades. Saknas raden händer ingenting.
        /// </summary>
        public bool EndIfActive(int memberId, int clubId)
        {
            if (memberId <= 0 || clubId <= 0) return false;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Execute(
                @"UPDATE ClubMembership
                     SET MembershipStatus = @2, MemberUntil = ISNULL(MemberUntil, @3)
                   WHERE MemberId = @0 AND ClubId = @1 AND MembershipStatus NOT IN (@2, @4)",
                memberId, clubId, "Utträdd", DateTime.Today, "Avliden") > 0;
        }

        /// <summary>
        /// Motsatsen till <see cref="EndIfActive"/>: ett <c>Utträdd</c> medlemskap blir <c>Aktiv</c> igen och
        /// <c>MemberUntil</c> töms. Anropas när klubben LÄGGS TILL i medlemmens klubbar igen. Rör aldrig
        /// <c>Avliden</c>. Returnerar true när en rad ändrades.
        /// </summary>
        public bool ReactivateIfEnded(int memberId, int clubId)
        {
            if (memberId <= 0 || clubId <= 0) return false;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Execute(
                @"UPDATE ClubMembership SET MembershipStatus = @2, MemberUntil = NULL
                   WHERE MemberId = @0 AND ClubId = @1 AND MembershipStatus = @3",
                memberId, clubId, "Aktiv", "Utträdd") > 0;
        }

        /// <summary>
        /// Upsert on (MemberId, ClubId). Returns the saved row. CreatedDate is set on insert.
        /// </summary>
        public ClubMembership Save(ClubMembership m)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var existing = db.FirstOrDefault<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE MemberId = @0 AND ClubId = @1", m.MemberId, m.ClubId);
            if (existing != null)
            {
                m.Id = existing.Id;
                m.CreatedDate = existing.CreatedDate;
                db.Update(m);
            }
            else
            {
                m.CreatedDate = DateTime.UtcNow;
                db.Insert(m);
            }
            return m;
        }

        /// <summary>
        /// Ensure a membership row exists for (memberId, clubId); create a minimal one
        /// (status Aktiv) if missing. Used when a club adds/imports a member.
        /// </summary>
        public ClubMembership EnsureExists(int memberId, int clubId)
        {
            var existing = Get(memberId, clubId);
            if (existing != null) return existing;
            return Save(new ClubMembership { MemberId = memberId, ClubId = clubId, MembershipStatus = "Aktiv" });
        }
    }
}
