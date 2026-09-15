using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Klubb- och kretsmästare. Manually entered per year and class. Reuses
    /// <see cref="RecordClassRegistry"/> for class-vs-discipline-vs-type validity.
    /// </summary>
    [TableName("CompetitionChampions")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionChampion
    {
        public int Id { get; set; }

        /// <summary>'Club' or 'Region'.</summary>
        public string Level { get; set; } = "";

        /// <summary>clubId as string for Club, regionCode for Region.</summary>
        public string ScopeId { get; set; } = "";

        /// <summary>Championship year (e.g. 2026).</summary>
        public int Year { get; set; }

        public string Discipline { get; set; } = "";

        /// <summary>'Individual' or 'Team'.</summary>
        public string ChampionType { get; set; } = "";

        /// <summary>Class code from RecordClassRegistry.</summary>
        public string ClassCode { get; set; } = "";

        /// <summary>
        /// Resultatet i grenens egen enhet: poäng för seriegrenarna, TRÄFF för fältskytte.
        /// Enheten står i <see cref="RecordClassRegistry.GetScoreUnit"/> och måste följa med
        /// talet till varje yta — se <see cref="RecordClassRegistry.FormatScore"/>.
        /// </summary>
        public int TotalScore { get; set; }

        /// <summary>
        /// Grenens andra tal, när den har ett: fältskyttets FIGURER. Null för seriegrenarna.
        /// Etiketten kommer ur <see cref="RecordClassRegistry.GetSecondaryLabel"/>.
        /// </summary>
        public int? SecondaryScore { get; set; }

        public string? CompetitionName { get; set; }

        public DateTime? CompetitionDate { get; set; }

        public int? HolderMemberId { get; set; }

        public string HolderName { get; set; } = "";

        public string? TeamName { get; set; }

        public string? TeamMembersJson { get; set; }

        public string? Notes { get; set; }

        public int EnteredByMemberId { get; set; }

        public DateTime EnteredAt { get; set; }
    }
}
