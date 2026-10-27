namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Vem bemanningen hör till. <b>Bemanningen ägs av en planering, alltid</b> (Stefan 2026-10-27: dubbelägarskapet är borta); tävlingen tas
    /// fram genom planeringens koppling. Två lägen:
    /// <list type="bullet">
    /// <item><c>(planering, tävling)</c> — en kopplad planering. Raderna bär båda.</item>
    /// <item><c>(planering, null)</c> — en planering utan tävling. Speglingarna till tävlingen körs inte.</item>
    /// </list>
    /// En tävling utan planering har ingen ägare och därmed ingen bemanning (<c>StaffOwnerResolver.ForCompetition</c> ger null); arrangören startar planeringen med ett klick och då tas
    /// äldre tävlingsägda rader in (<c>StaffingPlanningLink.Adopt</c>). Rader med PlanningId NULL ser ingen fråga: de väntar på den adoptionen.
    /// <para>⚠️ <b>Varje fråga går genom <see cref="Where"/></b> och <b>varje insättning</b> sätter <see cref="PlanningId"/> (och <see cref="CompetitionIdOrNull"/> när planeringen är kopplad).</para>
    /// </summary>
    public sealed record StaffOwner(int PlanningId, int? CompetitionId)
    {
        public bool HasCompetition => CompetitionId is > 0;
        /// <summary>Tävlingens id, eller 0 när planeringen inte är kopplad.</summary>
        public int Comp => CompetitionId is > 0 ? CompetitionId.Value : 0;

        public int? PlanningIdOrNull => PlanningId;
        public int? CompetitionIdOrNull => CompetitionId is > 0 ? CompetitionId : null;

        /// <summary>Parametern till <see cref="Where"/>: planeringen.</summary>
        public object P => PlanningId;
        /// <summary>Den andra parametern till <see cref="Filter"/>: tävlingen när <see cref="IncludeFamily"/> gäller, annars -1 (matchar aldrig något).</summary>
        public object FamilyComp => IncludeFamily && CompetitionId is > 0 ? CompetitionId.Value : -1;

        /// <summary>
        /// SQL-villkoret för ägarens rader: planeringens rader. EN parameter, på plats <paramref name="i"/> (P).
        /// <paramref name="alias"/> = tabellens alias med punkt, t.ex. "sa.".
        /// </summary>
        public static string Where(int i, string alias = "") => $"({alias}PlanningId = @{i})";

        /// <summary>
        /// Omgång 3 (Expertgruppen K4): tävlingens samlade bemanning — även rader i familjens delplaneringar som bemannar
        /// samma tävling. Sätts bara av <c>StaffOwnerResolver.ForCompetition</c>, och läses bara genom <see cref="Filter"/>
        /// (tävlingsdagens vyer). Skrivningar går genom <see cref="Where"/> och rör bara den kopplade planeringens rader.
        /// </summary>
        public bool IncludeFamily { get; init; }

        /// <summary>
        /// Läsvillkoret för tävlingens vyer: planeringens rader, och med <see cref="IncludeFamily"/> även familjens delplaneringars rader som bär tävlingen.
        /// ALLTID två parametrar — P på plats i och <see cref="FamilyComp"/> på i+1 — så att anropet ser likadant ut för alla ägare.
        /// </summary>
        public string Filter(int i, string alias = "") =>
            $"({alias}PlanningId = @{i} OR ({alias}PlanningId IS NOT NULL AND {alias}CompetitionId = @{i + 1}))";

        /// <summary>
        /// Om en rad med de här värdena hör till ägaren — samma regel som <see cref="Where"/>, alltså skrivningarnas. En
        /// delplanerings rad ägs av delplaneringen även när tävlingens vy visar den (<see cref="IncludeFamily"/>); den ändras
        /// genom sin egen ägare (radens ägare tas fram per rad).
        /// </summary>
        public bool Owns(int? rowPlanningId, int? rowCompetitionId) => rowPlanningId is > 0 && rowPlanningId == PlanningId;

        /// <summary>Nyckel för minnescacher inom en begäran.</summary>
        public string MemoKey => $"{PlanningId}|{Comp}|{(IncludeFamily ? 1 : 0)}";

        /// <summary>Var självanmälan för den här ägaren finns.</summary>
        public string SignupPath => $"/bemanna?p={PlanningId}";
    }
}
