using HpskSite.Models.CompetitionCalls;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Datumregeln (kretsutlysningen F4, N6) — <b>en varning, ingen spärr</b> (Stefan 2026-11-20: "det är vuxna människor, vi måste lita på
    /// att de förstår vad de gör"). Är en tävlings utlysning fastställd av kretsen (<c>LockedByKrets</c>) och någon utom kretsen
    /// (<see cref="CompetitionCallAccess.CanManageCallsForRegion"/>, sajtadmin inräknad) ändrar datumet, svarar servern först med
    /// varningen (hårdare om anmälningar finns) och sparar bara när anropet bär bekräftelsen <c>confirmDateChange</c>. Efter sparningen
    /// skrivs en händelserad och kretsen får en notis — synlighet i stället för spärr.
    ///
    /// <para>Skrivvägar som frågar den idag: redigeringen (<c>CompetitionEditController.SaveCompetition</c>, som både Precision- och
    /// Springskytte-modalen använder). Serieomgångar, ändringsguiden och flytt frågar inte ännu — där finns ingen varning och ingen notis,
    /// men heller ingen spärr.</para>
    /// <para>En tävling utan utlysning, eller med en som inte är fastställd, berörs inte alls.</para>
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

        public readonly record struct Check(CompetitionCall? Call, string? Warning);

        /// <summary>
        /// Varningen när den som sparar ändrar dagen från <paramref name="currentDate"/> till <paramref name="newDate"/> på en fastställd
        /// utlysning (annars <c>Warning</c> null). Bara DAGEN jämförs. <paramref name="registrations"/> avgör hur hård varningen är.
        /// <c>Call</c> är utlysningen när varning gäller — används efteråt för notisen.
        /// </summary>
        public async Task<Check> CheckAsync(int competitionId, DateTime? currentDate, DateTime? newDate, Func<int> registrations)
        {
            if (currentDate?.Date == newDate?.Date) return new(null, null);
            var call = _calls.ForCompetition(competitionId);
            if (call == null || !call.LockedByKrets) return new(null, null);
            var isKrets = await _access.CanManageCallsForRegion(call.RegionId);
            if (isKrets) return new(null, null);
            return new(call, CompetitionCallRules.DateChangeWarning(true, false, registrations()));
        }
    }
}
