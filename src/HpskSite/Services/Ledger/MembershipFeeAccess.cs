using HpskSite.Models;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Vem som får SKRIVA klubbens medlemsavgift (kategorier, tillägg, skapa, skicka, markera betald).
    ///
    /// <para><b>⚠️⚠️ EN upplösning, ingen egen.</b> Avgiften är klubbens pengar, och <c>/ekonomi</c> har redan
    /// en regel för vem som får skriva i dem: kassören, den som kassören gett rätten (<c>LedgerWriteGrant</c>),
    /// sajtadmin, och administratören i en klubb som ännu saknar kassör. Styrelsen läser, revisorn skriver
    /// aldrig. Förut gatades avgiftens skrivande ändpunkter på <c>IsClubAdminForClub</c>, så en revisor som
    /// också var klubbadmin kunde markera avgifter betalda — och därmed bokföra — i det hen granskar.
    /// Sidans <c>feesReadOnly</c> och varje skrivande ändpunkt frågar därför HÄR.</para>
    /// </summary>
    public class MembershipFeeAccess
    {
        private readonly LedgerAccessService _ledger;

        public MembershipFeeAccess(LedgerAccessService ledger) => _ledger = ledger;

        /// <summary>Får den inloggade skriva klubbens avgift?</summary>
        public async Task<bool> CanWriteAsync(int clubId)
            => clubId > 0 && Decide((await _ledger.ResolveAsync(DocumentOwnerType.Club, clubId)).Access);

        /// <summary>Själva beslutet som ren funktion — testas utan Umbraco.</summary>
        public static bool Decide(LedgerAccess access) => access == LedgerAccess.Write;
    }
}
