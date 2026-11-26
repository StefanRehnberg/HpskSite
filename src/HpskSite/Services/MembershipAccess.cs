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
        {
            if (clubId <= 0) return false;
            // ⚠️ Revisorn FÖRST, som i ekonomins egen upplösning: en revisor som också är klubbadmin får aldrig ändra typ, ålder
            //    eller förbund — det rensar oskickade avgifter och ändrar belopp i det hen granskar (expertgruppen 2026-11-26, K1).
            if (await _fee.IsAuditorAsync(clubId)) return false;
            return Decide(await _auth.IsClubAdminForClub(clubId), await _fee.CanWriteAsync(clubId));
        }

        /// <summary>Beslutet som ren funktion — testas utan Umbraco. Revisorn skriver aldrig, vilken annan roll hen än har.</summary>
        public static bool Decide(bool isClubAdmin, bool isTreasurer, bool isAuditor = false) => !isAuditor && (isClubAdmin || isTreasurer);
    }
}
