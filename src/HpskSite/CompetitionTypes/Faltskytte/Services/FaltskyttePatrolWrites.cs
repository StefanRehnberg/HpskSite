using HpskSite.Services.Competitions;
using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Gemensamt för ALLA ställen som ändrar fältets patruller (efteranmälan, byt plats, DNS, avgång, walk-in, skapa/radera patrull, omnumrering,
    /// generatorn och Tillämpa): samma lås per tävling, samma kontroll före en ombyggnad, samma gruppmatchning.
    /// <para><b>Låset</b> heter <see cref="LockName"/> och tas med <c>DatabaseLock</c>. Namnet är en del av kontraktet mellan instanserna — ändra det aldrig
    /// utan att alla anropare ändras samtidigt.</para>
    /// <para><b>Inga spärrar är absoluta</b> (Stefan 2026-10-30): <see cref="ForceNeededMessage"/> ger ett skäl i klartext, och anroparen låter användaren
    /// bekräfta och försöka igen med force. Det som tvingas fram loggas.</para>
    /// </summary>
    public static class FaltskyttePatrolWrites
    {
        public static string LockName(int competitionId) => "falt-startlista:" + competitionId;

        /// <summary>Koderna i en publicerad-grupper-lista ("C,B"); tom = alla.</summary>
        public static HashSet<string> PublishedCodes(string? groups) =>
            (groups ?? "").Split(new[] { ',', '+', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Är en patrull publicerad? Listan kan publiceras per vapengrupp ("C först, B senare"): publicerad-flaggan säger att något är publicerat, <paramref name="groups"/> vilka grupper.
        /// En patrull med blandad etikett ("B+C") räknas som publicerad om någon av dess grupper är det.
        /// </summary>
        public static bool IsPatrolPublished(bool published, string? groups, string? patrolGroup)
        {
            if (!published) return false;
            var set = PublishedCodes(groups);
            if (set.Count == 0) return true;
            var codes = (patrolGroup ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return codes.Any(set.Contains);
        }

        public const string BusyMessage = "Någon annan ändrar startlistan just nu. Försök igen om en stund.";

        /// <summary>
        /// En patrulls vapengrupper är en kodlista ("C", "B+C", "A+R"); tom = vilken grupp som helst. Matchar en patrull en skyttes vapengrupp-kod?
        /// </summary>
        public static bool GroupMatches(string? patrolGroup, string code) =>
            string.IsNullOrEmpty(patrolGroup) || patrolGroup.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(code, StringComparer.OrdinalIgnoreCase);

        /// <summary>SQL-motsvarigheten till <see cref="GroupMatches"/> (utan tom grupp): kolumnen är exakt koden eller innehåller den som ett led i en "+"-lista.</summary>
        public static string GroupMatchSql(string column, string param) =>
            $"({column} = {param} OR {column} LIKE {param} + '+%' OR {column} LIKE '%+' + {param} OR {column} LIKE '%+' + {param} + '+%')";

        /// <summary>
        /// Skäl till att en ombyggnad eller radering bör bekräftas (resultat finns, listan är publicerad, någon patrull har gått ut), eller null.
        /// Anropas inne i skrivningen så att läget inte hinner ändras.
        /// </summary>
        public static string? ForceNeededMessage(IDatabase db, ICompetitionStore competitions, int competitionId)
        {
            var rows = db.ExecuteScalar<int>("SELECT COUNT(*) FROM FaltskytteResultEntry WHERE CompetitionId = @0", competitionId);
            if (rows > 0) return $"Det finns redan resultat ({rows} inmatade rader). Att ändra patrullerna nu kan flytta skyttar som redan har skjutit.";
            if (competitions.GetSaved(competitionId)?.FaltskyttePatrolsPublished == true)
                return "Startlistan är publicerad. Skyttarna kan ha planerat efter tiderna.";
            var started = db.ExecuteScalar<int>("SELECT COUNT(*) FROM FaltskyttePatrol WHERE CompetitionId = @0 AND DepartedAt IS NOT NULL", competitionId);
            if (started > 0) return "Någon patrull har redan gått ut.";
            return null;
        }
    }
}
