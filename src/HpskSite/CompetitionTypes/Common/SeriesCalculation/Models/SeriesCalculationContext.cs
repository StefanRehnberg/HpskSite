namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Models
{
    public class SeriesCalculationContext
    {
        public int SeriesId { get; set; }
        public string SeriesName { get; set; } = "";
        public List<SeriesCompetitionInfo> Competitions { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public Dictionary<int, List<ShooterCompetitionScore>> CompetitionResults { get; set; } = new();
    }

    public class SeriesCompetitionInfo
    {
        public int CompetitionId { get; set; }
        public string Name { get; set; } = "";
        public DateTime Date { get; set; }
    }

    public class ShooterCompetitionScore
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public int ClubId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int TotalScore { get; set; }
        public int XCount { get; set; }

        /// <summary>
        /// Skytten skjöt utanför mästerskapet i den här tävlingen och klassen: resultatet står kvar men skytten tävlar inte, så
        /// hen får ingen placeringspoäng, tar ingen från de tävlande och räknas inte i ett klubblags poäng. Sätts bara av en
        /// poängkälla som känner beteendet (fält); övriga lämnar den falsk och allt räknas som förut.
        /// </summary>
        public bool OutOfCompetition { get; set; }
    }

    public static class ShooterCompetitionScoreExtensions
    {
        /// <summary>De som tävlar: alla utom dem som skjuter utanför mästerskapet. EN definition för varje strategi som rankar.</summary>
        public static IEnumerable<ShooterCompetitionScore> Contenders(this IEnumerable<ShooterCompetitionScore> scores)
            => scores.Where(s => !s.OutOfCompetition);
    }
}
