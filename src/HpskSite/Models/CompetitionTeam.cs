using NPoco;

namespace HpskSite.Models
{
    [TableName("CompetitionTeam")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionTeamDto
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string TeamName { get; set; } = "";
        public string TeamClass { get; set; } = "";
        public int ClubId { get; set; }
        public int CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    [TableName("CompetitionTeamMember")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionTeamMemberDto
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public int MemberId { get; set; }
        public bool IsSpare { get; set; }
        public DateTime JoinedAt { get; set; }
    }

    public static class TeamClassHelper
    {
        // Standard competition team class mappings
        private static readonly Dictionary<string, string[]> StandardTeamClassMap = new()
        {
            ["A"] = new[] { "A1", "A2", "A3", "A_opt" },
            ["B"] = new[] { "B1", "B2", "B3" },
            ["C Öppen"] = new[] { "C1", "C2", "C3" },
            ["C Vet"] = new[] { "C_Vet_Y", "C_Vet_A" },
            ["C Jun"] = new[] { "C_Jun" },
            ["C Dam"] = new[] { "C1_Dam", "C2_Dam", "C3_Dam" },
            ["R"] = new[] { "R1", "R2", "R3" },
            ["M"] = new[] { "M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9" },
            ["L Öppen"] = new[] { "L1", "L2", "L3" },
            ["L Vet"] = new[] { "L_Vet_Y", "L_Vet_A" },
            ["L Jun"] = new[] { "L_Jun" },
            ["L Dam"] = new[] { "L1_Dam", "L2_Dam", "L3_Dam" },
        };

        // Springskytte team class definitions per SHB 2026 rules (Lagtävling):
        //   Herrar: Junior & Senior t.o.m. 64 år (klasser Jun, 21, 35, 50, 60), 3 skyttar, men only
        //   Damer:  Junior & Senior t.o.m. 64 år (klasser Jun, 21, 35, 50, 60), 2 skyttar, women only
        //   Veteran: fr.o.m. 65 år (klasser 65, 70), 2 skyttar, mixed gender
        // Boundary: "Gränsen går mellan klass 60 och klass 65"
        // Note: Junior team class only exists for Stafett, not Lagtävling
        // Note: "Äldre löpare får ingå i yngre lag" only applies to Stafett
        private static readonly Dictionary<string, SpringskytteTeamClassDef> SpringskytteTeamClassMap = new()
        {
            ["A-Herrar"] = new(new[] { "A-H 15", "A-H 18", "A-H jun", "A-H 21", "A-H 35", "A-H 50", "A-H 60" }, "M"),
            ["A-Damer"] = new(new[] { "A-D 15", "A-D 18", "A-D jun", "A-D 21", "A-D 35", "A-D 50", "A-D 60" }, "F"),
            ["A-Veteran"] = new(new[] { "A-H 65", "A-H 70", "A-D 65", "A-D 70" }, null),
            ["C-Herrar"] = new(new[] { "C-H 15", "C-H 18", "C-H jun", "C-H 21", "C-H 35", "C-H 50", "C-H 60" }, "M"),
            ["C-Damer"] = new(new[] { "C-D 15", "C-D 18", "C-D jun", "C-D 21", "C-D 35", "C-D 50", "C-D 60" }, "F"),
            ["C-Veteran"] = new(new[] { "C-H 65", "C-H 70", "C-D 65", "C-D 70" }, null),
        };

        private record SpringskytteTeamClassDef(string[] IndividualClasses, string? GenderRestriction);

        /// <summary>
        /// Gets available team classes based on which individual classes exist in the competition.
        /// </summary>
        public static List<TeamClassInfo> GetTeamClasses(string[] competitionClassIds, bool isSpringskytte)
        {
            var result = new List<TeamClassInfo>();

            if (isSpringskytte)
            {
                foreach (var (teamClass, def) in SpringskytteTeamClassMap)
                {
                    // Only include if at least one individual class from this team class is in the competition
                    if (def.IndividualClasses.Any(ic => competitionClassIds.Contains(ic)))
                    {
                        var (core, spare) = GetTeamSize(teamClass);
                        result.Add(new TeamClassInfo
                        {
                            TeamClass = teamClass,
                            CoreMembers = core,
                            MaxSpares = spare,
                            CompatibleClasses = def.IndividualClasses
                                .Where(ic => competitionClassIds.Contains(ic))
                                .ToArray()
                        });
                    }
                }
            }
            else
            {
                foreach (var (teamClass, individualClasses) in StandardTeamClassMap)
                {
                    if (individualClasses.Any(ic => competitionClassIds.Contains(ic)))
                    {
                        var (core, spare) = GetTeamSize(teamClass);
                        result.Add(new TeamClassInfo
                        {
                            TeamClass = teamClass,
                            CoreMembers = core,
                            MaxSpares = spare,
                            CompatibleClasses = individualClasses
                                .Where(ic => competitionClassIds.Contains(ic))
                                .ToArray()
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Returns (coreMembers, maxSpares) for a team class.
        /// Vet/Dam/Jun = 2+1, all others = 3+1.
        /// </summary>
        public static (int coreMembers, int maxSpares) GetTeamSize(string teamClass)
        {
            if (IsVeteranClass(teamClass) || IsJuniorClass(teamClass) || IsLadiesClass(teamClass))
                return (2, 1);
            return (3, 1);
        }

        /// <summary>
        /// Gets individual class IDs that map to a team class.
        /// </summary>
        public static string[] GetCompatibleIndividualClasses(string teamClass, bool isSpringskytte)
        {
            if (isSpringskytte)
            {
                return SpringskytteTeamClassMap.TryGetValue(teamClass, out var def)
                    ? def.IndividualClasses
                    : Array.Empty<string>();
            }

            return StandardTeamClassMap.TryGetValue(teamClass, out var classes)
                ? classes
                : Array.Empty<string>();
        }

        public static bool IsVeteranClass(string cls) =>
            cls.Contains("Vet", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Veteran", StringComparison.OrdinalIgnoreCase);

        public static bool IsJuniorClass(string cls) =>
            cls.Contains("Jun", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Junior", StringComparison.OrdinalIgnoreCase);

        public static bool IsLadiesClass(string cls) =>
            cls.Contains("Dam", StringComparison.OrdinalIgnoreCase);
    }

    public class TeamClassInfo
    {
        public string TeamClass { get; set; } = "";
        public int CoreMembers { get; set; }
        public int MaxSpares { get; set; }
        public string[] CompatibleClasses { get; set; } = Array.Empty<string>();
    }
}
