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
    /// "Vilka deltar i mästerskapet" som regel (S5): utifrån tävlingens inställning (<c>ChampionshipParticipants</c>) och den klubb skytten
    /// TÄVLAR FÖR, ett FÖRSLAG om att skjuta utanför mästerskapet. Ren funktion — inga uppslag, så reglerna går att prova ensamma.
    ///
    /// <para><b>Klubben skytten tävlar för avgör.</b> En skytt som är medlem i flera klubbar väljer vid anmälan vilken hen tävlar för
    /// ("Tävlar för"); anmälningsrutan varnar redan där om valet hamnar utanför mästerskapet. Förslaget gäller alltså den valda klubben —
    /// det tystnar INTE för att skytten också är medlem i en klubb som hade räknats. I stället pekar skälet ut den klubben, så arrangören
    /// ser att ett byte av "Tävlar för" löser det. (Annars hade en skytt kunnat anmäla sig för en klubb utanför kretsen och ändå få
    /// kretsens medalj.)</para>
    ///
    /// <para>Förslaget görs bara när det går att avgöra: okänd klubb, krets eller landsdel ger inget förslag (hellre ett förslag för lite än
    /// en skytt som tyst hamnar utanför på en gissning). Grannkretsar räknas som "inne" bara när arrangören valt <c>own+neighbours</c>
    /// (kretsstyrelsens beslut, SHB C.3.6.4). SM har aldrig förslag.</para>
    /// </summary>
    public static class OutOfCompetitionRules
    {
        /// <summary>
        /// Får en skytt som tävlar för <paramref name="club"/> delta i mästerskapet? True = ja, false = nej (skulle föreslås utanför),
        /// null = går inte att avgöra (okänd krets/landsdel, eller tävlingen har ingen begränsning).
        /// </summary>
        public static bool? Qualifies(ChampionshipScopeKind kind, string? participants, CompetitionOrigin competition, bool isClubOnly, ClubOrigin club)
        {
            var setting = ChampionshipParticipantsSetting.Normalize(participants);
            if (setting == null) return null;                       // alla deltar (dagens beteende)

            switch (kind)
            {
                case ChampionshipScopeKind.Krets:
                {
                    if (string.IsNullOrWhiteSpace(competition.RegionCode) || string.IsNullOrWhiteSpace(club.RegionCode)) return null;
                    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { competition.RegionCode! };
                    if (setting == ChampionshipParticipantsSetting.OwnPlusNeighbours)
                        foreach (var n in RegionAdjacency.NeighboursOf(competition.RegionCode)) allowed.Add(n);
                    return allowed.Contains(club.RegionCode);
                }
                case ChampionshipScopeKind.Landsdel:
                    if (string.IsNullOrWhiteSpace(competition.Area) || string.IsNullOrWhiteSpace(club.Area)) return null;   // landsdel okänd
                    return string.Equals(club.Area, competition.Area, StringComparison.OrdinalIgnoreCase);
                case ChampionshipScopeKind.Klubb:
                    if (isClubOnly || competition.HostClubId <= 0) return null;   // en ren klubbtävling har bara klubbens egna
                    return club.ClubId == competition.HostClubId;
                default:
                    return null;                                                 // SM och allt annat: inga förslag
            }
        }

        public static OutOfCompetitionSuggestion? Suggest(
            ChampionshipScopeKind kind, string? participants, CompetitionOrigin competition, bool isClubOnly,
            int competesForClubId, IReadOnlyCollection<int> shooterClubIds, Func<int, ClubOrigin?> origin)
        {
            if (competesForClubId <= 0) return null;                // vet inte vem skytten tävlar för
            var mine = origin(competesForClubId);
            if (mine == null) return null;
            if (Qualifies(kind, participants, competition, isClubOnly, mine) != false) return null;   // inne, eller går inte att avgöra

            // En annan klubb skytten är medlem i som HADE räknats — nämn den, så ett byte av "Tävlar för" är uppenbart.
            var alternative = shooterClubIds.Where(id => id != competesForClubId).Select(origin).Where(o => o != null).Select(o => o!)
                .FirstOrDefault(o => Qualifies(kind, participants, competition, isClubOnly, o) == true);
            var tail = alternative == null
                ? ""
                : $" Skytten är också medlem i {alternative.ClubName} — välj den som \"Tävlar för\" för att skjuta i mästerskapet.";

            var host = kind switch
            {
                ChampionshipScopeKind.Krets => $"mästerskapet gäller {competition.RegionName ?? competition.RegionCode}",
                ChampionshipScopeKind.Landsdel => $"mästerskapet gäller landsdel {competition.Area}",
                _ => "det är klubbmästerskap i en annan klubb"
            };
            var where = kind switch
            {
                ChampionshipScopeKind.Krets => $" ({mine.RegionName})",
                ChampionshipScopeKind.Landsdel => $" ({mine.RegionName}, landsdel {mine.Area})",
                _ => ""
            };
            var tag = kind switch { ChampionshipScopeKind.Krets => "region", ChampionshipScopeKind.Landsdel => "area", _ => "club" };
            return new OutOfCompetitionSuggestion(tag, $"Skjuter för {mine.ClubName}{where}, men {host}.{tail}");
        }
    }
}
