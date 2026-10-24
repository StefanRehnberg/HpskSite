using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Omgång 3 (Expertgruppen K1, Stefan 2026-10-24): kontaktuppgifter och taket på nya planeringar.
    /// <para>En medlem som någon annan lagt till är <b>inbjuden</b> tills hen själv öppnat planeringen — inloggad eller via
    /// mejllänken. Fram till dess visas inte hens e-post och telefon. Annars kunde vem som helst skapa en planering, lägga
    /// till medlemmar direkt och läsa deras kontaktuppgifter.</para>
    /// </summary>
    public static class PlanningContactRules
    {
        /// <summary>Nya planeringar per medlem och dygn (sajtadmin undantagen). Inbjudningstaket (30) gäller per planering.</summary>
        public const int MaxNewPlanningsPerDay = 10;

        /// <summary>
        /// Inbjuden men inte inne än: en medlem (inte en gäst — gästen har själv svarat på inbjudan med sin adress) som aldrig
        /// öppnat planeringen. Den som kom in genom tävlingens behörighet räknas som inne — ingen annan har lagt till hen.
        /// </summary>
        public static bool IsPendingInvite(PlanningMemberRow p) =>
            p.MemberId > 0 && p.LastSeenAt == null && !p.ViaCompetition && p.AddedBy != p.MemberId;
    }
}
