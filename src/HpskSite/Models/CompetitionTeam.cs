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
        // ── Standard (icke-Springskytte) lagklasser ─────────────────────────────────────────
        //
        // ⚠️ TVÅ uppsättningar per lagklass, av samma skäl som Springskytte har det:
        // `IndividualClasses` är de klasser som DEFINIERAR lagklassen (tävlingen erbjuder den
        // när minst en av dem körs, och minst en lagmedlem måste vara anmäld i en av dem),
        // `AlsoEligibleClasses` är de som bara får GÅ MED.
        //
        // Det öppna vapengruppslaget lånar in ur sin egen vapengrupps övriga klasser. Regeln
        // (SHB, lagtävling): "en skytt från en annan C-klass får ingå i ett lag i Vapengrupp C
        // om minst en deltagare i laget deltar i den aktuella klassen, skytten inte ingår i
        // något annat lag [i vapengruppen], skjuter under samma förutsättningar och uppfyller
        // kraven för klassen där laget ingår."
        //
        // Innan uppdelningen fanns pekade "C Öppen" BARA på C1/C2/C3, så en skytt som
        // individuellt tävlar i C1 Dam eller C Vet Y kunde inte ingå i klubbens öppna C-lag —
        // och eftersom öppna lag kräver exakt tre ordinarie föll HELA laget, inte bara
        // skytten. Hittades med skarp SSM 2026-data (JPK och Lunds PK, 2026-09-07).
        //
        // ⚠️ Lägg ALDRIG in låneklasserna i `IndividualClasses` — då börjar en tävling som
        // bara kör damklasser erbjuda ett tomt "C Öppen"-lag.
        //
        // Klasslagen (Dam/Vet/Jun) lånar INTE: de är klasspecifika, och att låna in en
        // öppen-C-skytt i ett damlag är inte samma sak som det omvända.
        private static readonly string[] CBorrowClasses =
            { "C1_Dam", "C2_Dam", "C3_Dam", "C_Vet_Y", "C_Vet_A", "C_Jun" };
        private static readonly string[] LBorrowClasses =
            { "L1_Dam", "L2_Dam", "L3_Dam", "L_Vet_Y", "L_Vet_A", "L_Jun" };

        private static readonly Dictionary<string, StandardTeamClassDef> StandardTeamClassMap = new()
        {
            ["A"] = new(new[] { "A1", "A2", "A3" }),
            ["A Opt"] = new(new[] { "A_opt_1", "A_opt_2", "A_opt_3" }),
            ["B"] = new(new[] { "B1", "B2", "B3" }),
            ["C Öppen"] = new(new[] { "C1", "C2", "C3" }, AlsoEligibleClasses: CBorrowClasses),
            ["C Vet"] = new(new[] { "C_Vet_Y", "C_Vet_A" }),
            ["C Jun"] = new(new[] { "C_Jun" }),
            ["C Dam"] = new(new[] { "C1_Dam", "C2_Dam", "C3_Dam" }),
            ["R"] = new(new[] { "R1", "R2", "R3" }),
            ["M"] = new(new[] { "M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9" }),
            ["L Öppen"] = new(new[] { "L1", "L2", "L3" }, AlsoEligibleClasses: LBorrowClasses),
            ["L Vet"] = new(new[] { "L_Vet_Y", "L_Vet_A" }),
            ["L Jun"] = new(new[] { "L_Jun" }),
            ["L Dam"] = new(new[] { "L1_Dam", "L2_Dam", "L3_Dam" }),
        };

        private record StandardTeamClassDef(string[] IndividualClasses, string[]? AlsoEligibleClasses = null)
        {
            public string[] AllEligibleClasses =>
                AlsoEligibleClasses == null
                    ? IndividualClasses
                    : IndividualClasses.Concat(AlsoEligibleClasses).ToArray();

            /// <summary>True när lagklassen kan låna in skyttar ur andra klasser.</summary>
            public bool Borrows => AlsoEligibleClasses is { Length: > 0 };
        }

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
                foreach (var (teamClass, def) in StandardTeamClassMap)
                {
                    // Tillgänglig när tävlingen kör minst en DEFINIERANDE klass. Låneklasserna
                    // får inte göra lagklassen synlig på en tävling som inte kör någon egen.
                    if (def.IndividualClasses.Any(ic => competitionClassIds.Contains(ic)))
                    {
                        var (core, spare) = GetTeamSize(teamClass);
                        result.Add(new TeamClassInfo
                        {
                            TeamClass = teamClass,
                            CoreMembers = core,
                            MaxSpares = spare,
                            // ...men vem som får VÄLJAS spänner över hela lånemängden, så en
                            // dam anmäld i C1 Dam syns som valbar för C Öppen.
                            CompatibleClasses = def.AllEligibleClasses
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
                return SpringskytteTeamClassMap.TryGetValue(teamClass, out var sdef)
                    ? sdef.AllEligibleClasses.ToArray()
                    : Array.Empty<string>();
            }

            return StandardTeamClassMap.TryGetValue(teamClass, out var def)
                ? def.AllEligibleClasses
                : Array.Empty<string>();
        }

        /// <summary>
        /// De klasser som DEFINIERAR lagklassen — delmängden av
        /// <see cref="GetCompatibleIndividualClasses"/> som INTE är inlånade.
        ///
        /// Används för lagtävlingens villkor "minst en deltagare i laget deltar i den aktuella
        /// klassen": ett "C Öppen"-lag av tre damklassanmälda skyttar är inte ett öppet C-lag.
        /// För en lagklass som inte lånar är den identisk med den kompatibla mängden.
        /// </summary>
        public static string[] GetDefiningIndividualClasses(string teamClass, bool isSpringskytte)
        {
            if (isSpringskytte)
            {
                return SpringskytteTeamClassMap.TryGetValue(teamClass, out var sdef)
                    ? sdef.IndividualClasses
                    : Array.Empty<string>();
            }

            return StandardTeamClassMap.TryGetValue(teamClass, out var def)
                ? def.IndividualClasses
                : Array.Empty<string>();
        }

        /// <summary>True när lagklassen får låna in skyttar ur andra klasser i samma vapengrupp.</summary>
        public static bool BorrowsFromOtherClasses(string teamClass, bool isSpringskytte)
        {
            if (isSpringskytte)
                return SpringskytteTeamClassMap.TryGetValue(teamClass, out var sdef)
                       && sdef.AlsoEligibleClasses is { Length: > 0 };

            return StandardTeamClassMap.TryGetValue(teamClass, out var def) && def.Borrows;
        }

        /// <summary>
        /// Vapengruppen en STANDARD-lagklass hör till, härledd ur dess definierande klasser —
        /// "C Öppen", "C Vet", "C Jun" och "C Dam" ger alla <c>"C"</c>; "A Opt" ger
        /// <c>"A_Opt"</c>. Tom sträng för okänd lagklass (och för Springskytte, som har
        /// <see cref="GetSpringskytteWeaponGroup"/>).
        ///
        /// Finns för lagtävlingens villkor "skytten ingår inte i något annat lag": spärren
        /// gäller inom VAPENGRUPPEN, så att vara med i klubbens B- och A-lag samtidigt är helt
        /// i sin ordning — det är dubbelräkning inom samma vapengrupp regeln stoppar.
        /// </summary>
        public static string GetStandardWeaponFamily(string teamClass)
        {
            if (teamClass == null) return "";
            if (!StandardTeamClassMap.TryGetValue(teamClass, out var def) || def.IndividualClasses.Length == 0)
                return "";
            return ShootingClasses.GetWeaponClassCode(def.IndividualClasses[0]);
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
