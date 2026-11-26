using HpskSite.Services.Ledger;

namespace HpskSite.Services
{
    /// <summary>
    /// Vem får hantera en medlems TYP, STATUS och TILLHÖRIGHET (medlemstyp, medlemskapets status, förbund, familj, klubbar)?
    /// Klubbadmin ELLER kassören (Stefan 2026-11-26): det är medlemsuppgifter, inte pengar. Avgifterna (belopp, tillägg och
    /// avdrag, tillvalens priser, betald, mejl) förblir kassörens och prövas fortfarande med <see cref="MembershipFeeAccess"/>.
    /// Prövas alltid mot klubben i anropet, aldrig mot ett id från klienten.
    /// </summary>
    public class MembershipAccess
    {
        private readonly AdminAuthorizationService _auth;
        private readonly MembershipFeeAccess _fee;

        public MembershipAccess(AdminAuthorizationService auth, MembershipFeeAccess fee)
        {
            _auth = auth;
            _fee = fee;
        }

        public async Task<bool> CanManageMemberAsync(int clubId)
            => clubId > 0 && (await _auth.IsClubAdminForClub(clubId) || await _fee.CanWriteAsync(clubId));

        /// <summary>Beslutet som ren funktion — testas utan Umbraco.</summary>
        public static bool Decide(bool isClubAdmin, bool isTreasurer) => isClubAdmin || isTreasurer;
    }
}
