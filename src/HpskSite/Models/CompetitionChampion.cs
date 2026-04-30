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

        public int TotalScore { get; set; }

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
