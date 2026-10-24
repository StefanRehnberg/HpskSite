namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Vem bemanningen hör till (P2b, 2026-10-23). Bemanningen ägs av en <b>planering</b>; tävlingen tas
    /// fram genom planeringens koppling. Tre lägen:
    /// <list type="bullet">
    /// <item><c>(planering, tävling)</c> — en kopplad planering. Raderna bär båda.</item>
    /// <item><c>(planering, null)</c> — en planering utan tävling. Speglingarna till tävlingen körs inte.</item>
    /// <item><c>(0, tävling)</c> — en tävling som ännu inte har en planering. Raderna har PlanningId NULL och
    ///   läses som förut. De flyttas in i planeringen när den startas eller kopplas
    ///   (<c>StaffingPlanningLink.Adopt</c>).</item>
    /// </list>
    /// <para>⚠️ <b>Varje fråga går genom <see cref="Where"/></b> — "radens planering, eller tävlingens rader
    /// som ännu saknar planering". En fråga direkt på CompetitionId missar planeringens rader efter en
    /// losskoppling, och en direkt på PlanningId missar tävlingens äldre rader.</para>
    /// <para>⚠️ <b>Varje insättning går genom <see cref="Stamp"/>-värdena</b> (<see cref="PlanningIdOrNull"/>,
    /// <see cref="CompetitionIdOrNull"/>). En insättning som bara sätter CompetitionId lämnar en rad som en
    /// okopplad planering aldrig ser.</para>
    /// </summary>
    public sealed record StaffOwner(int PlanningId, int? CompetitionId)
    {
        /// <summary>En tävling utan planering.</summary>
        public static StaffOwner ForCompetitionOnly(int competitionId) => new(0, competitionId > 0 ? competitionId : null);

        public bool HasPlanning => PlanningId > 0;
        public bool HasCompetition => CompetitionId is > 0;
        /// <summary>Tävlingens id, eller 0 när planeringen inte är kopplad.</summary>
        public int Comp => CompetitionId is > 0 ? CompetitionId.Value : 0;

        public int? PlanningIdOrNull => PlanningId > 0 ? PlanningId : null;
        public int? CompetitionIdOrNull => CompetitionId is > 0 ? CompetitionId : null;

        /// <summary>Parametrarna till <see cref="Where"/>. -1 matchar aldrig något.</summary>
        public object P => PlanningId > 0 ? PlanningId : -1;
        public object C => CompetitionId is > 0 ? CompetitionId.Value : -1;

        /// <summary>
        /// SQL-villkoret för ägarens rader, med parametrarna på plats <paramref name="i"/> (P) och i+1 (C).
        /// <paramref name="alias"/> = tabellens alias med punkt, t.ex. "sa.".
        /// </summary>
        public static string Where(int i, string alias = "") =>
            $"({alias}PlanningId = @{i} OR ({alias}PlanningId IS NULL AND {alias}CompetitionId = @{i + 1}))";

        /// <summary>
        /// Omgång 3 (Expertgruppen K4): tävlingens samlade bemanning — även rader i familjens delplaneringar som bemannar
        /// samma tävling. Sätts bara av <c>StaffOwnerResolver.ForCompetition</c>, och läses bara genom <see cref="Filter"/>
        /// (tävlingsdagens vyer). Skrivningar går genom <see cref="Where"/> och rör bara den kopplade planeringens rader.
        /// </summary>
        public bool IncludeFamily { get; init; }

        /// <summary>
        /// Läsvillkoret för tävlingens vyer: med <see cref="IncludeFamily"/> alla rader som bär tävlingen, annars
        /// <see cref="Where"/>. Samma parametrar (P på plats i, C på i+1).
        /// </summary>
        public string Filter(int i, string alias = "") =>
            IncludeFamily && HasCompetition
                ? $"({alias}PlanningId = @{i} OR {alias}CompetitionId = @{i + 1})"
                : Where(i, alias);

        /// <summary>
        /// Om en rad med de här värdena hör till ägaren — samma regel som <see cref="Where"/>, alltså skrivningarnas. En
        /// delplanerings rad ägs av delplaneringen även när tävlingens vy visar den (<see cref="IncludeFamily"/>); den ändras
        /// genom sin egen ägare (radens ägare tas fram per rad).
        /// </summary>
        public bool Owns(int? rowPlanningId, int? rowCompetitionId) =>
            rowPlanningId is > 0 ? rowPlanningId == PlanningId
                                 : HasCompetition && rowCompetitionId == CompetitionId;

        /// <summary>Nyckel för minnescacher inom en begäran.</summary>
        public string MemoKey => $"{PlanningId}|{Comp}|{(IncludeFamily ? 1 : 0)}";

        /// <summary>Var självanmälan för den här ägaren finns.</summary>
        public string SignupPath => HasPlanning ? $"/bemanna?p={PlanningId}" : $"/bemanna?c={Comp}";
    }
}
