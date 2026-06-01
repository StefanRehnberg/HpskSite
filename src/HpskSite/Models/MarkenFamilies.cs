namespace HpskSite.Models
{
    /// <summary>How a badge family is earned.</summary>
    public enum MarkenPattern
    {
        /// <summary>Pistolskyttemärket — two-part yearly Guldfodring (handled by its own analyzer).</summary>
        Pistolskytte,
        /// <summary>Earned at N different competitions/year meeting a per-valör point/hit threshold.</summary>
        CompetitionAchievement,
        /// <summary>Earned by N validated single series meeting a per-series threshold (range proof).</summary>
        SeriesProof
    }

    /// <summary>
    /// Static definition of a Märke family (Phase 2). Pure data + small evaluators so adding a family
    /// is a table entry, not new code. Pistolskyttemärket (Phase 1) keeps its bespoke analyzer; it's
    /// represented here only for its display name + årtalsmärke ladder.
    ///
    /// Requirement tables transcribed from SHB 2026 kap 5 (see Documentation/MARKEN_SYSTEM.md App. A).
    /// </summary>
    public class MarkenFamilyDef
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public MarkenPattern Pattern { get; init; }

        /// <summary>Discipline whose hosted result table to harvest (CompetitionAchievement only).</summary>
        public string? Discipline { get; init; }
        /// <summary>Hosted result table name (CompetitionAchievement only).</summary>
        public string? ResultTable { get; init; }
        /// <summary>Whether the result rows are per-station hits (Fält) vs per-series points (precision-shape).</summary>
        public bool HitBased { get; init; }

        /// <summary>
        /// SHB: the märke prov is only valid at krets-/landsdels-/riks-/nationella tävlingar (not club
        /// competitions). When true, hosted comps only count if their competitionScope is krets+
        /// (Kretsmästerskap / Landsdelsmästerskap / Svenskt Mästerskap); club comps must be
        /// self-reported (functionary confirms the level). False for Nationell helmatch (any level + träning).
        /// </summary>
        public bool RequiresKretsScope { get; init; }

        /// <summary>Competitions required (3 for most; 2 for Nationell helmatch). CompetitionAchievement only.</summary>
        public int CompetitionsRequired { get; init; } = 3;
        /// <summary>Series required (SeriesProof only) — e.g. 5 for Luftpistol/Elit.</summary>
        public int SeriesRequired { get; init; }
        /// <summary>For Elit: a second set of series (snabb) also required at the same valör.</summary>
        public bool RequiresSpeedSeriesToo { get; init; }

        /// <summary>Advisory prerequisite valör in <see cref="Marken.FamilyPistolskytte"/> (or null).</summary>
        public string? PrereqPistolskytteLevel { get; init; }
        public string? PrereqText { get; init; }

        /// <summary>Årtalsmärke ladder (index 0 = none). Step cadence = <see cref="ArtalsStepYears"/>.</summary>
        public string[] ArtalsLadder { get; init; } = { "" };
        public int ArtalsStepYears { get; init; } = 3;

        // ── Requirement tables ──
        // CompetitionAchievement: CompLevels[weaponGroup][dim] = {Brons, Silver, Guld}. dim = series
        // count (Precision 6/7/10) or station count (Fält 6..10); 0 = dimension-independent (Milsnabb,
        // NatHelmatch). SeriesProof: SeriesThreshold = {Brons, Silver, Guld} points per series.
        public Dictionary<string, Dictionary<int, int[]>>? CompLevels { get; init; }
        public int[]? SeriesThreshold { get; init; }

        /// <summary>Highest valör a single competition result reaches, or null.</summary>
        public string? LevelForCompetition(string weaponGroup, int dim, int total)
        {
            if (CompLevels == null) return null;
            if (!CompLevels.TryGetValue(weaponGroup, out var byDim)) return null;
            if (!byDim.TryGetValue(dim, out var lv) && !byDim.TryGetValue(0, out lv)) return null;
            if (total >= lv[2]) return Marken.LevelGuld;
            if (total >= lv[1]) return Marken.LevelSilver;
            if (total >= lv[0]) return Marken.LevelBrons;
            return null;
        }

        /// <summary>Highest valör a single series reaches (SeriesProof), or null.</summary>
        public string? LevelForSeries(int seriesTotal)
        {
            if (SeriesThreshold == null) return null;
            if (seriesTotal >= SeriesThreshold[2]) return Marken.LevelGuld;
            if (seriesTotal >= SeriesThreshold[1]) return Marken.LevelSilver;
            if (seriesTotal >= SeriesThreshold[0]) return Marken.LevelBrons;
            return null;
        }
    }

    public static class MarkenFamilies
    {
        // ── Family keys (Pistolskytte lives in Marken.FamilyPistolskytte) ──
        public const string Precision = "Precision";
        public const string Falt = "Falt";
        public const string Milsnabb = "Milsnabb";
        public const string NationellHelmatch = "NationellHelmatch";
        public const string Luftpistol = "Luftpistol";
        public const string Elit = "Elit";

        private static int[] L(int b, int s, int g) => new[] { b, s, g };

        private static readonly Dictionary<string, MarkenFamilyDef> _all = new()
        {
            // ── Precisionsskyttemärket (5.8) — 3 comps, point totals by group × series count ──
            [Precision] = new MarkenFamilyDef
            {
                Key = Precision,
                DisplayName = "Precisionsskyttemärket",
                Pattern = MarkenPattern.CompetitionAchievement,
                Discipline = "Precision",
                ResultTable = "PrecisionResultEntry",
                CompetitionsRequired = 3,
                RequiresKretsScope = true,
                PrereqPistolskytteLevel = Marken.LevelBrons,
                PrereqText = "Kräver pistolskyttemärke i brons föregående kalenderår.",
                ArtalsStepYears = 3,
                ArtalsLadder = new[]
                {
                    "",
                    "Precisionsskyttemärket i guld med en stjärna",
                    "Precisionsskyttemärket i guld med två stjärnor",
                    "Precisionsskyttemärket i guld med tre stjärnor"
                },
                CompLevels = new()
                {
                    ["A"] = new() { [6] = L(194, 231, 262), [7] = L(226, 269, 305), [10] = L(322, 383, 434) },
                    ["B"] = new() { [6] = L(200, 237, 274), [7] = L(233, 276, 319), [10] = L(332, 393, 454) },
                    ["C"] = new() { [6] = L(206, 243, 280), [7] = L(240, 283, 326), [10] = L(342, 403, 464) }
                }
            },

            // ── Militär Snabbmatchmärket (5.10) — 3 comps, point totals by group (dim-independent) ──
            [Milsnabb] = new MarkenFamilyDef
            {
                Key = Milsnabb,
                DisplayName = "Militär Snabbmatchmärket",
                Pattern = MarkenPattern.CompetitionAchievement,
                Discipline = "Milsnabb",
                ResultTable = "MilsnabbResultEntry",
                CompetitionsRequired = 3,
                RequiresKretsScope = true,
                PrereqPistolskytteLevel = Marken.LevelBrons,
                PrereqText = "Kräver pistolskyttemärke i brons föregående kalenderår.",
                ArtalsStepYears = 3,
                ArtalsLadder = new[]
                {
                    "",
                    "Militär Snabbmatchmärket i guld med en stjärna",
                    "Militär Snabbmatchmärket i guld med två stjärnor",
                    "Militär Snabbmatchmärket i guld med tre stjärnor"
                },
                CompLevels = new()
                {
                    ["A"] = new() { [0] = L(377, 454, 515) },
                    ["B"] = new() { [0] = L(391, 472, 543) },
                    ["C"] = new() { [0] = L(404, 481, 550) },
                    ["R"] = new() { [0] = L(388, 467, 532) }
                }
            },

            // ── Märke i Nationell helmatch (5.9) — TWO occasions, point totals by group ──
            [NationellHelmatch] = new MarkenFamilyDef
            {
                Key = NationellHelmatch,
                DisplayName = "Märke i Nationell helmatch",
                Pattern = MarkenPattern.CompetitionAchievement,
                Discipline = "NationellHelmatch",
                ResultTable = "NationellHelmatchResultEntry",
                CompetitionsRequired = 2,
                PrereqPistolskytteLevel = Marken.LevelBrons,
                PrereqText = "Kräver pistolskyttemärke i brons föregående kalenderår.",
                ArtalsStepYears = 3,
                ArtalsLadder = new[]
                {
                    "",
                    "Märke Nationell helmatch i guld med en stjärna",
                    "Märke Nationell helmatch i guld med två stjärnor",
                    "Märke Nationell helmatch i guld med tre stjärnor"
                },
                CompLevels = new()
                {
                    ["A"] = new() { [0] = L(365, 435, 500) },
                    ["B"] = new() { [0] = L(380, 450, 520) },
                    ["C"] = new() { [0] = L(390, 460, 530) }
                }
            },

            // ── Luftpistolmärket (5.11) — 5 series, per-series points (group-independent) ──
            // NOTE: luftpistol årtalsmärke advances ONE step per re-fulfilled guld-year (not per 3).
            [Luftpistol] = new MarkenFamilyDef
            {
                Key = Luftpistol,
                DisplayName = "Luftpistolmärket",
                Pattern = MarkenPattern.SeriesProof,
                SeriesRequired = 5,
                ArtalsStepYears = 1,
                ArtalsLadder = new[]
                {
                    "",
                    "Årtalsmärke luftpistol",
                    "Årtalsmärke luftpistol med en stjärna",
                    "Årtalsmärke luftpistol med två stjärnor",
                    "Årtalsmärke luftpistol med tre stjärnor",
                    "Årtalsmärke luftpistol med krans",
                    "Årtalsmärke luftpistol med krans och en stjärna",
                    "Årtalsmärke luftpistol med krans och två stjärnor",
                    "Årtalsmärke luftpistol med krans och tre stjärnor"
                },
                SeriesThreshold = L(66, 76, 88)
            },

            // ── Elitmärket (5.4) — 5 precision + 5 snabb series, per-series points; needs Guldmärke ──
            [Elit] = new MarkenFamilyDef
            {
                Key = Elit,
                DisplayName = "Elitmärket",
                Pattern = MarkenPattern.SeriesProof,
                SeriesRequired = 5,
                RequiresSpeedSeriesToo = true,
                PrereqPistolskytteLevel = Marken.LevelGuld,
                PrereqText = "Kräver pistolskyttemärke i guld.",
                ArtalsStepYears = 3,
                ArtalsLadder = new[]
                {
                    "",
                    "Elitmärket i guld med kvistar",
                    "Elitmärket i guld med kvistar och en stjärna",
                    "Elitmärket i guld med kvistar och två stjärnor",
                    "Elitmärket i guld med kvistar och tre stjärnor"
                },
                SeriesThreshold = L(45, 48, 49)
            },

            // ── Fältskyttemärket (5.7) — 3 comps, hit counts by group × station count ──
            // Thresholds verified from the SHB tables screenshot (Documentation/FaltskytteMarketTables.png).
            // dim = station count (6/7/8/9/10). The "% av maximal poängsumma" alternative (poäng-mode
            // / non-standard station counts) is not yet modelled — hit-count by station count only.
            [Falt] = new MarkenFamilyDef
            {
                Key = Falt,
                DisplayName = "Fältskyttemärket",
                Pattern = MarkenPattern.CompetitionAchievement,
                Discipline = "Faltskytte",
                ResultTable = "FaltskytteResultEntry",
                HitBased = true,
                CompetitionsRequired = 3,
                RequiresKretsScope = true,
                PrereqPistolskytteLevel = Marken.LevelBrons,
                PrereqText = "Kräver pistolskyttemärke i brons föregående kalenderår samt fyllda 15 år.",
                ArtalsStepYears = 3,
                ArtalsLadder = new[]
                {
                    "",
                    "Fältskyttemärket i guld med en stjärna",
                    "Fältskyttemärket i guld med två stjärnor",
                    "Fältskyttemärket i guld med tre stjärnor",
                    "Fältskyttemärket i guld med kvistar",
                    "Fältskyttemärket i guld med kvistar och en stjärna",
                    "Fältskyttemärket i guld med kvistar och två stjärnor",
                    "Fältskyttemärket i guld med kvistar och tre stjärnor"
                },
                CompLevels = new()
                {
                    ["A"] = new() { [6] = L(19, 23, 27), [7] = L(22, 27, 31), [8] = L(25, 31, 36), [9] = L(29, 36, 41), [10] = L(32, 40, 46) },
                    ["B"] = new() { [6] = L(22, 27, 31), [7] = L(25, 31, 36), [8] = L(29, 36, 41), [9] = L(32, 40, 46), [10] = L(36, 45, 51) },
                    ["C"] = new() { [6] = L(22, 27, 31), [7] = L(25, 31, 36), [8] = L(29, 36, 41), [9] = L(32, 40, 46), [10] = L(36, 45, 51) },
                    ["R"] = new() { [6] = L(20, 25, 29), [7] = L(23, 29, 34), [8] = L(27, 34, 38), [9] = L(30, 38, 44), [10] = L(34, 42, 49) }
                }
            }
        };

        public static MarkenFamilyDef? Get(string? key) =>
            key != null && _all.TryGetValue(key, out var d) ? d : null;

        public static IReadOnlyCollection<MarkenFamilyDef> All => _all.Values;

        public static IEnumerable<MarkenFamilyDef> CompetitionFamilies =>
            _all.Values.Where(f => f.Pattern == MarkenPattern.CompetitionAchievement);

        public static IEnumerable<MarkenFamilyDef> SeriesProofFamilies =>
            _all.Values.Where(f => f.Pattern == MarkenPattern.SeriesProof);

        /// <summary>Family display name, falling back to the Pistolskytte name / raw key.</summary>
        public static string DisplayName(string? key) =>
            key == Marken.FamilyPistolskytte ? Marken.FamilyDisplayName(key)
            : Get(key)?.DisplayName ?? key ?? "";

        /// <summary>Årtalsmärke step name for a guld-fulfilled-year count, using the family's ladder + cadence.</summary>
        public static (string Name, int NextAtYears) Artalsmarke(string? family, int fulfilledYears)
        {
            // Pistolskytte keeps its 17-step ladder in Marken.
            if (family == Marken.FamilyPistolskytte)
                return (Marken.ArtalsmarkeName(fulfilledYears), Marken.YearsToNextArtalsmarke(fulfilledYears));

            var def = Get(family);
            if (def == null || def.ArtalsLadder.Length <= 1) return ("", 0);
            int step = fulfilledYears / def.ArtalsStepYears;
            step = Math.Min(step, def.ArtalsLadder.Length - 1);
            int nextStep = step + 1;
            int nextAt = nextStep < def.ArtalsLadder.Length ? nextStep * def.ArtalsStepYears : 0;
            return (def.ArtalsLadder[step], nextAt);
        }
    }
}
