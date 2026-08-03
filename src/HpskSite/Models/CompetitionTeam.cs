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
        public bool IsRelay { get; set; }
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
            ["A"] = new[] { "A1", "A2", "A3" },
            ["A Opt"] = new[] { "A_opt_1", "A_opt_2", "A_opt_3" },
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
        //   Herrar: Junior & Senior t.o.m. 64 år (klasser Jun, 21, 35, 50, 60), 3 skyttar, MIXED gender
        //   Damer:  Junior & Senior t.o.m. 64 år (klasser Jun, 21, 35, 50, 60), 2 skyttar, women only
        //   Veteran: fr.o.m. 65 år (klasser 65, 70), 2 skyttar, mixed gender
        // Boundary: "Gränsen går mellan klass 60 och klass 65"
        // Note: Junior team class only exists for Stafett, not Lagtävling
        // Note: "Äldre löpare får ingå i yngre lag" only applies to Stafett
        //
        // GENDER RULE (2026-08-03) — an H-lag ("Herrar") accepts shooters of BOTH genders; only a
        // D-lag ("Damer") is restricted. That restriction is expressed ONLY as the whitelist of
        // individual classes below: a shooter registered in "A-H 21" is not in A-Damer's list and
        // is therefore refused. There is deliberately NO gender flag on this record — the earlier
        // `GenderRestriction` field was never read, and reviving it is how a Dam gets locked out of
        // a Herr team again. Do not add one; extend the class lists instead.
        private static readonly string[] SpringskytteDamClassesA =
            { "A-D 15", "A-D 18", "A-D jun", "A-D 21", "A-D 35", "A-D 50", "A-D 60" };
        private static readonly string[] SpringskytteDamClassesC =
            { "C-D 15", "C-D 18", "C-D jun", "C-D 21", "C-D 35", "C-D 50", "C-D 60" };

        private static readonly Dictionary<string, SpringskytteTeamClassDef> SpringskytteTeamClassMap = new()
        {
            ["A-Herrar"] = new(new[] { "A-H 15", "A-H 18", "A-H jun", "A-H 21", "A-H 35", "A-H 50", "A-H 60" },
                               AlsoEligibleClasses: SpringskytteDamClassesA),
            ["A-Damer"] = new(SpringskytteDamClassesA),
            ["A-Veteran"] = new(new[] { "A-H 65", "A-H 70", "A-D 65", "A-D 70" }),
            ["C-Herrar"] = new(new[] { "C-H 15", "C-H 18", "C-H jun", "C-H 21", "C-H 35", "C-H 50", "C-H 60" },
                               AlsoEligibleClasses: SpringskytteDamClassesC),
            ["C-Damer"] = new(SpringskytteDamClassesC),
            ["C-Veteran"] = new(new[] { "C-H 65", "C-H 70", "C-D 65", "C-D 70" }),
        };

        /// <param name="IndividualClasses">
        /// The classes that DEFINE the team class — a competition offers the team class when it runs
        /// at least one of these. Keeping the Dam classes out of this list is what stops a
        /// Dam-classes-only competition from offering an (empty) Herrlag.
        /// </param>
        /// <param name="AlsoEligibleClasses">
        /// Extra classes whose shooters may JOIN the team without the class making the team class
        /// available. This is how a Dam runs in a Herrlag.
        /// </param>
        private record SpringskytteTeamClassDef(string[] IndividualClasses, string[]? AlsoEligibleClasses = null)
        {
            public IEnumerable<string> AllEligibleClasses =>
                AlsoEligibleClasses == null ? IndividualClasses : IndividualClasses.Concat(AlsoEligibleClasses);
        }

        // Stafett (relay) team class definitions per SHB 2026 §3 Stafettävling
        // Always weapon class C. Members do NOT need to be individually registered — so unlike
        // lagtävling there is no registration class to derive gender from, and `GenderRestriction`
        // is the ONLY gate. It is enforced in CompetitionTeamService (create + roster edit).
        // "Stafett Senior Herr" is MIXED (both genders may run); only the Dam relay is restricted.
        private static readonly Dictionary<string, StafettTeamClassDef> StafettTeamClassMap = new()
        {
            ["Stafett Junior"] = new(2, 0, null, "Mixad, 15-20 år"),
            ["Stafett Senior Herr"] = new(3, 0, null, "Mixad, 21+ år"),
            ["Stafett Senior Dam"] = new(2, 0, "F", "Damer, 21+ år"),
            ["Stafett Veteran"] = new(2, 0, null, "Mixad, 50+ år"),
        };

        private record StafettTeamClassDef(int CoreMembers, int MaxSpares, string? GenderRestriction, string Description);

        /// <summary>True when the team class is a stafett (relay) class.</summary>
        public static bool IsStafettClass(string teamClass) =>
            StafettTeamClassMap.ContainsKey(teamClass);

        /// <summary>
        /// Weapon group of a Springskytte LAG class — "A-Herrar" → "A". Returns "" for anything that
        /// isn't of that shape (stafett classes, standard-discipline classes). Same "A-"/"C-" prefix
        /// convention CalculateTeamResultsAsync relies on, so don't rename the team classes.
        /// </summary>
        public static string GetSpringskytteWeaponGroup(string teamClass) =>
            teamClass != null && teamClass.Length > 1 && teamClass[1] == '-'
                ? teamClass.Substring(0, 1)
                : "";

        /// <summary>
        /// Gender restriction for a stafett class: "F" = damer only, null = mixed (both genders).
        /// Returns null for anything that isn't a stafett class.
        /// </summary>
        public static string? GetStafettGenderRestriction(string teamClass) =>
            StafettTeamClassMap.TryGetValue(teamClass, out var def) ? def.GenderRestriction : null;

        /// <summary>
        /// Gets all stafett (relay) team classes with display metadata.
        /// Always returns all 4 classes (no filtering — always weapon class C).
        /// </summary>
        public static List<StafettTeamClassInfo> GetStafettTeamClasses()
        {
            return StafettTeamClassMap.Select(kvp => new StafettTeamClassInfo
            {
                TeamClass = kvp.Key,
                CoreMembers = kvp.Value.CoreMembers,
                MaxSpares = kvp.Value.MaxSpares,
                GenderRestriction = kvp.Value.GenderRestriction,
                Description = kvp.Value.Description
            }).ToList();
        }

        /// <summary>
        /// Gets team size for a stafett class. Returns null if not a stafett class.
        /// </summary>
        public static (int coreMembers, int maxSpares)? GetStafettTeamSize(string teamClass)
        {
            return StafettTeamClassMap.TryGetValue(teamClass, out var def)
                ? (def.CoreMembers, def.MaxSpares)
                : null;
        }

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
                    // Only include if at least one DEFINING class from this team class is in the
                    // competition — AlsoEligibleClasses (the Dam classes on a Herrlag) must not make
                    // the team class appear on a competition that runs no classes of its own.
                    if (def.IndividualClasses.Any(ic => competitionClassIds.Contains(ic)))
                    {
                        var (core, spare) = GetTeamSize(teamClass);
                        result.Add(new TeamClassInfo
                        {
                            TeamClass = teamClass,
                            CoreMembers = core,
                            MaxSpares = spare,
                            // ...but who may JOIN spans the full eligible set, so a Dam registered
                            // in A-D 21 shows as selectable for A-Herrar.
                            CompatibleClasses = def.AllEligibleClasses
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
            // Check stafett classes first (they have their own sizes)
            var stafettSize = GetStafettTeamSize(teamClass);
            if (stafettSize.HasValue)
                return stafettSize.Value;

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
                    ? def.AllEligibleClasses.ToArray()
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

    public class StafettTeamClassInfo
    {
        public string TeamClass { get; set; } = "";
        public int CoreMembers { get; set; }
        public int MaxSpares { get; set; }
        public string? GenderRestriction { get; set; }
        public string Description { get; set; } = "";
    }
}
