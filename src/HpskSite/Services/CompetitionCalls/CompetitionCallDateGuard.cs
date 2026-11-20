using HpskSite.Models.CompetitionCalls;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Datumregeln (kretsutlysningen F4, N6): är en tävlings utlysning <b>fastställd av kretsen</b> (<c>LockedByKrets</c>) får klubben inte
    /// ändra tävlingens datum — bara kretsen (<see cref="CompetitionCallAccess.CanManageCallsForRegion"/>), sajtadmin inräknad. Klubben
    /// får <i>föreslå</i> ett annat datum (<c>CompetitionCallClubController.ProposeDate</c>).
    ///
    /// <para><b>⚠️ En SERVERREGEL, inte en dold knapp.</b> Varje skrivväg för tävlingens datum måste fråga den här klassen innan den sparar;
    /// A/B-provet är att en klubbadmin som skickar ett nytt <c>competitionDate</c> rakt mot slutpunkten får vägran. Skrivvägar som kallar
    /// <see cref="RefusalAsync"/> idag: redigeringen (<c>CompetitionEditController.SaveCompetition</c>). En ny skrivväg för datum (serieomgångar,
    /// ändringsguiden, flytt) ska anropa den — annars går regeln att runda.</para>
    /// <para>En tävling utan utlysning, eller med en utlysning som inte är fastställd, berörs inte alls.</para>
    /// </summary>
    public sealed class CompetitionCallDateGuard
    {
        private readonly CompetitionCallService _calls;
        private readonly CompetitionCallAccess _access;

        public CompetitionCallDateGuard(CompetitionCallService calls, CompetitionCallAccess access)
        {
            _calls = calls;
            _access = access;
        }

        /// <summary>
        /// Förklaringen när den som sparar inte får ändra datumet från <paramref name="currentDate"/> till <paramref name="newDate"/>,
        /// annars null. Bara DAGEN jämförs (klockslaget är arrangörens). Ett oförändrat datum är alltid tillåtet.
        /// </summary>
        public async Task<string?> RefusalAsync(int competitionId, DateTime? currentDate, DateTime? newDate)
        {
            if (currentDate?.Date == newDate?.Date) return null;
            var call = _calls.ForCompetition(competitionId);
            if (call == null || !call.LockedByKrets) return null;
            var isKrets = await _access.CanManageCallsForRegion(call.RegionId);
            return CompetitionCallRules.DateChangeRefusal(call.LockedByKrets, isKrets);
        }
    }
}
