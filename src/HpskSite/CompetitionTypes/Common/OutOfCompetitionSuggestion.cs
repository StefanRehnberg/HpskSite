using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>Var en klubb hör hemma: kretsens kod (enum-namnet, t.ex. "Halland") och landsdelen ("Syd", "Vast", "Ost", "Nord").</summary>
    public sealed record ClubOrigin(int ClubId, string ClubName, string RegionCode, string RegionName, string? Area);

    /// <summary>Det tävlingen själv hör hemma i: värdklubben (om en) och värdkretsen/landsdelen.</summary>
    public sealed record CompetitionOrigin(int HostClubId, string? RegionCode, string? RegionName, string? Area);

    /// <summary>Varför en skytt FÖRESLÅS skjuta utanför mästerskapet. Lagras aldrig — räknas om ur anmälan och tävlingens inställning.</summary>
    public sealed record OutOfCompetitionSuggestion(string Kind, string Reason);

    /// <summary>
    /// "Vilka deltar i mästerskapet" som regel (S5): utifrån tävlingens inställning (<c>ChampionshipParticipants</c>) och var skytten
    /// tävlar för, ett FÖRSLAG om att skjuta utanför mästerskapet. Ren funktion — inga uppslag, så reglerna går att prova ensamma.
    ///
    /// <para>Förslaget görs bara när det går att avgöra. <b>Okänd krets, okänd klubb eller okänd landsdel ger inget förslag</b>
    /// (hellre ett förslag för lite än en skytt som tyst hamnar utanför på en gissning). Det tystnar också om NÅGON av skyttens
    /// klubbar hör hemma i mästerskapets krets/landsdel/klubb — skytten kan själv välja vilken klubb hen tävlar för.</para>
    ///
    /// <para>Grannkretsar räknas som "inne" bara när arrangören valt <c>own+neighbours</c> (kretsstyrelsens beslut, SHB C.3.6.4);
    /// annars är de utanför. SM har aldrig förslag.</para>
    /// </summary>
    public static class OutOfCompetitionRules
    {
        public static OutOfCompetitionSuggestion? Suggest(
            ChampionshipScopeKind kind, string? participants, CompetitionOrigin competition, bool isClubOnly,
            int competesForClubId, IReadOnlyCollection<int> shooterClubIds, Func<int, ClubOrigin?> origin)
        {
            var setting = ChampionshipParticipantsSetting.Normalize(participants);
            if (setting == null) return null;                       // alla deltar (dagens beteende)
            if (competesForClubId <= 0) return null;                // vet inte vem skytten tävlar för
            var mine = origin(competesForClubId);
            if (mine == null) return null;


            var clubs = shooterClubIds.Append(competesForClubId).Distinct().Select(origin).Where(o => o != null).Select(o => o!).ToList();

            if (kind == ChampionshipScopeKind.Krets)
            {
                if (string.IsNullOrWhiteSpace(competition.RegionCode) || string.IsNullOrWhiteSpace(mine.RegionCode)) return null;
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { competition.RegionCode! };
                if (setting == ChampionshipParticipantsSetting.OwnPlusNeighbours)
                    foreach (var n in RegionAdjacency.NeighboursOf(competition.RegionCode)) allowed.Add(n);
                if (clubs.Any(c => allowed.Contains(c.RegionCode))) return null;
                return new OutOfCompetitionSuggestion("region",
                    $"Skjuter för {mine.ClubName} ({mine.RegionName}), men mästerskapet gäller {competition.RegionName ?? competition.RegionCode}.");
            }

            if (kind == ChampionshipScopeKind.Landsdel)
            {
                if (string.IsNullOrWhiteSpace(competition.Area) || string.IsNullOrWhiteSpace(mine.Area)) return null;   // landsdel okänd
                if (clubs.Any(c => string.Equals(c.Area, competition.Area, StringComparison.OrdinalIgnoreCase))) return null;
                return new OutOfCompetitionSuggestion("area",
                    $"Skjuter för {mine.ClubName} ({mine.RegionName}, landsdel {mine.Area}), men mästerskapet gäller landsdel {competition.Area}.");
            }

            if (kind == ChampionshipScopeKind.Klubb)
            {
                if (isClubOnly || competition.HostClubId <= 0) return null;   // en ren klubbtävling har bara klubbens egna
                if (clubs.Any(c => c.ClubId == competition.HostClubId)) return null;
                return new OutOfCompetitionSuggestion("club",
                    $"Skjuter för {mine.ClubName}, men det är klubbmästerskap i en annan klubb.");
            }

            return null;   // SM och allt annat: inga förslag
        }
    }
}
