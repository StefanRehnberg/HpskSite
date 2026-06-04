namespace HpskSite.Models
{
    /// <summary>
    /// Domain constants and rule helpers for Märken (marksmanship proficiency badges, SHB kap 5).
    /// Phase 1 covers <b>Pistolskyttemärket</b> only — base valörer (Brons/Silver/Guld), the yearly
    /// Guldfodringar (two-part upholding), and the derived årtalsmärke ladder.
    ///
    /// See <c>Documentation/MARKEN_SYSTEM.md</c> for the full spec and the chapter-5 requirement tables.
    /// </summary>
    public static class Marken
    {
        // ── Badge families (only Pistolskytte is built in Phase 1; the rest are reserved) ──
        public const string FamilyPistolskytte = "Pistolskytte";
        public const string FamilyLuftpistol = "Luftpistol"; // mirrors MarkenFamilies.Luftpistol (for discipline classification)
        public const string FamilyMastar = "Mastar";         // Mästarmärket (5.2) — bespoke, year-count → valör
        public const string FamilyStormastar = "Stormastar"; // Stormästarmärket (5.3) — career inteckningspoäng

        public static string FamilyDisplayName(string? family) => family switch
        {
            FamilyPistolskytte => "Pistolskyttemärket",
            FamilyMastar => "Mästarmärket",
            FamilyStormastar => "Stormästarmärket",
            _ => family ?? ""
        };

        // ── Base valörer ──
        public const string LevelBrons = "Brons";
        public const string LevelSilver = "Silver";
        public const string LevelGuld = "Guld";

        /// <summary>Sort rank of a base valör (1..3); 0 if not a base valör.</summary>
        public static int LevelOrdinal(string? level) => level switch
        {
            LevelBrons => 1,
            LevelSilver => 2,
            LevelGuld => 3,
            _ => 0
        };

        /// <summary>Base valör name for an ordinal (1..3), or null.</summary>
        public static string? LevelFromOrdinal(int ord) => ord switch
        {
            1 => LevelBrons,
            2 => LevelSilver,
            3 => LevelGuld,
            _ => null
        };

        /// <summary>
        /// SHB progression rule (5.4.2 and the equivalent clause in the other märke families):
        /// "Endast ett märke kan under året erövras … och märke av högre grad endast av den som
        /// förut innehar märke av närmast lägre grad." At most one valör per year, and a higher
        /// grade only once the next-lower is held.
        ///
        /// Input: each year's highest qualified valör ordinal (0=none, 1=Brons, 2=Silver, 3=Guld).
        /// Walks years chronologically, stepping up exactly one grade in any year that qualifies for
        /// the next grade. Returns the held ordinal, the year that level was reached, and the Guld
        /// fulfilment years that feed the årtalsmärke ladder (the year Guld is first reached, then
        /// each later year Guld requirements were re-met).
        /// </summary>
        public static (int HeldOrdinal, int HeldYear, List<int> GuldYears) ApplyValorProgression(
            IEnumerable<(int Year, int QualifiedOrdinal)> perYear)
        {
            int held = 0, heldYear = 0;
            var guldYears = new List<int>();
            foreach (var (year, ord) in perYear.OrderBy(p => p.Year))
            {
                if (held >= 3)
                {
                    if (ord >= 3) guldYears.Add(year); // re-met Guld → årtalsmärke ("ånyo")
                }
                else
                {
                    int next = held + 1;
                    if (ord >= next)
                    {
                        held = next;
                        heldYear = year;
                        if (held == 3) guldYears.Add(year); // year Guld first reached
                    }
                }
            }
            return (held, heldYear, guldYears);
        }

        // ── Sources ──
        public const string SourceSelfReported = "SelfReported";
        public const string SourceOnSite = "OnSite";
        public const string SourceAdmin = "Admin";
        public const string SourceTrappa = "Skyttetrappan"; // base valör materialized from Skyttetrappan completion
        public const string SourceAuto = "Auto";            // valör auto-derived from competition results

        // ── Award / qualification status ──
        public const string StatusReported = "Reported"; // "Ej verifierad"
        public const string StatusVerified = "Verified";
        public const string StatusRejected = "Rejected";

        // ── Märke series submissions (validated single-series evidence) ──
        public const string SeriesStatusPending = "Pending";   // awaiting a functionary's validation
        // Verified / Rejected reuse StatusVerified / StatusRejected.

        public const string SeriesTypePrecision = "Precision"; // a "Guldserie" — 5-shot precision series
        public const string SeriesTypeSpeed = "Speed";         // a "Snabbserie" — tillämpning, hits-in-time per valör

        public static string SeriesTypeDisplay(string? t) => t switch
        {
            SeriesTypePrecision => "Guldserie",
            SeriesTypeSpeed => "Snabbserie",
            _ => t ?? ""
        };

        // Snabbserie targets. B100/C30 = tillämpning (Pistolskyttemärket, hits-in-time).
        // Snabbpistol = snabbpistoltavla 25 m, scored 0–50 (Elit/Mästar).
        public const string SpeedTargetB100 = "B100_50m";
        public const string SpeedTargetC30 = "C30_25m";
        public const string SpeedTargetSnabbpistol = "Snabbpistol_25m";

        public static string SpeedTargetDisplay(string? t) => t switch
        {
            SpeedTargetB100 => "B100, 50 m",
            SpeedTargetC30 => "1/6 C30, 25 m",
            SpeedTargetSnabbpistol => "Snabbpistoltavla 25 m",
            _ => t ?? ""
        };

        public static bool IsValidSpeedTarget(string? t) => t is SpeedTargetB100 or SpeedTargetC30 or SpeedTargetSnabbpistol;

        // ── Series discipline (what a series physically is — drives which badges it feeds) ──
        // A precision series feeds Pistolskytte Guldfodring AND Elit precision; a tillämpning series
        // feeds Pistolskytte's speed part; a snabbpistol series feeds Elit's speed part; an air series
        // feeds Luftpistol. One entry, credited to every badge whose per-series threshold it meets.
        public const string DisciplinePrecision = "Precision";
        public const string DisciplineTillampning = "Tillampning";
        public const string DisciplineSnabbpistol = "Snabbpistol";
        public const string DisciplineAir = "Air";

        public static string SeriesDiscipline(string? badgeFamily, string? seriesType, string? target)
        {
            if (badgeFamily == FamilyLuftpistol) return DisciplineAir;
            if (seriesType == SeriesTypeSpeed)
                return target == SpeedTargetSnabbpistol ? DisciplineSnabbpistol : DisciplineTillampning;
            return DisciplinePrecision;
        }

        public static string DisciplineDisplay(string? d) => d switch
        {
            DisciplinePrecision => "Precisionsserie",
            DisciplineTillampning => "Snabbserie (tillämpning)",
            DisciplineSnabbpistol => "Snabbpistolserie",
            DisciplineAir => "Luftpistolserie",
            _ => ""
        };

        /// <summary>The snabbserie requirement text for a claimed valör (SHB 5.1.1.1 pt 2), for display.</summary>
        public static string SpeedRequirementText(string? level) => level switch
        {
            LevelBrons => "5 träff, 60 s",
            LevelSilver => "6 träff, 40 s",
            LevelGuld => "6 träff (vapengrupp A & R 17 s, B & C 15 s)",
            _ => ""
        };

        // ── Guldfodring part sources ──
        public const string PartSourceTrainingScore = "TrainingScore";
        public const string PartSourceCompetition = "Competition";
        public const string PartSourceStandardMedal = "StandardMedal";
        public const string PartSourceManualAttest = "ManualAttest";

        public static string PartSourceDisplay(string? src) => src switch
        {
            PartSourceTrainingScore => "Träningsserier",
            PartSourceCompetition => "Tävling",
            PartSourceStandardMedal => "Standardmedalj i fält",
            PartSourceManualAttest => "Intygad på plats",
            _ => src ?? ""
        };

        public static string StatusDisplay(string? status) => status switch
        {
            StatusReported => "Ej verifierad",
            StatusVerified => "Verifierad",
            StatusRejected => "Avvisad",
            _ => status ?? ""
        };

        public static string SourceDisplay(string? source) => source switch
        {
            SourceSelfReported => "Egenrapporterad",
            SourceOnSite => "pistol.nu",
            SourceAdmin => "Admin",
            SourceTrappa => "Skyttetrappan",
            SourceAuto => "Tävlingsresultat",
            _ => source ?? ""
        };

        // ── Guldfodring precision thresholds (SHB 5.1.1.1, pistoltavla 25 m, 5 skott, 5 min) ──
        // Per series, per vapengrupp. R has no row in the precision table — we treat it as C
        // (conservative, slightly strict). ⚠️ Confirm the R mapping with SPSF; a functionary signs
        // off anyway, so an over-strict default never produces a wrong award.

        private static int GuldPerSeries(string group) => group switch
        {
            "A" => 43,
            "B" => 45,
            "C" => 46,
            "R" => 46,
            _ => 46
        };

        private static int SilverPerSeries(string group) => group switch
        {
            "A" => 38,
            "B" => 39,
            "C" => 40,
            "R" => 40,
            _ => 40
        };

        /// <summary>Series required for a Guldfodring precision part (SHB 5.1.1.1 pt 1: 3 precisionsserier).</summary>
        public const int GuldfodringPrecisionSeriesRequired = 3;

        /// <summary>
        /// Snabbserier required for a Guldfodring speed part (SHB 5.1.1.1 pt 2: 3 tillämpningsserier,
        /// each 6 träff within the valör's time) — OR a single held Standardmedalj i fältskjutning.
        /// </summary>
        public const int GuldfodringSpeedSeriesRequired = 3;

        /// <summary>The Guld per-series requirement for a weapon group (before age concessions), for display.</summary>
        public static int GuldPerSeriesBase(string group) => GuldPerSeries(group);

        /// <summary>
        /// Weapon group ("A"/"B"/"C"/"R") from a shooting class id ("A1", "C2", "A_Opt", "R3").
        /// Returns null for air pistol ("P") and anything else — those don't count toward
        /// Pistolskyttemärket (air pistol has its own märke, 5.11, Phase 2).
        /// </summary>
        public static string? WeaponGroup(string? shootingClass)
        {
            if (string.IsNullOrWhiteSpace(shootingClass)) return null;
            var c = char.ToUpperInvariant(shootingClass.Trim()[0]);
            return c switch
            {
                'A' => "A",
                'B' => "B",
                'C' => "C",
                'R' => "R",
                _ => null // 'P' (air) and unknown → not a Pistolskytte weapon group
            };
        }

        /// <summary>
        /// Per-series points required for the precision part of a Guldfodring, applying the
        /// age concessions (SHB 5.1.1.1 + 5.1.2.2), using the calendar-year rule
        /// (D.2.8: ålder = tävlingsår − födelseår):
        ///   • turned 65 the previous year (ageThisYear ≥ 66) → Silver requirements (the 65+ inteckning rule)
        ///   • turned 55 the previous year (ageThisYear ≥ 56) → Guld − 1 / serie
        ///   • otherwise → Guld
        /// When birthYear is unknown (0), no concession is applied (full Guld requirement — fail safe).
        /// </summary>
        public static int PrecisionThreshold(string weaponGroup, int year, int birthYear)
        {
            int ageThisYear = birthYear > 0 ? year - birthYear : 0;
            if (birthYear > 0 && ageThisYear >= 66)
                return SilverPerSeries(weaponGroup);
            if (birthYear > 0 && ageThisYear >= 56)
                return GuldPerSeries(weaponGroup) - 1;
            return GuldPerSeries(weaponGroup);
        }

        // ── Age from Swedish personnummer ─────────────────────────────
        /// <summary>
        /// Birth year parsed from a personnummer, or 0 if it can't be read.
        /// Accepts 12-digit (YYYYMMDD-NNNN / YYYYMMDDNNNN), 10-digit (YYMMDD-NNNN / YYMMDD+NNNN /
        /// YYMMDDNNNN), with or without separators/spaces. The '+' separator means age ≥ 100.
        /// </summary>
        public static int BirthYearFromPersonNumber(string? personNumber, int currentYear)
        {
            if (string.IsNullOrWhiteSpace(personNumber)) return 0;

            bool plusSeparator = personNumber.Contains('+');
            var digits = new string(personNumber.Where(char.IsDigit).ToArray());

            if (digits.Length == 12 || digits.Length == 13)
            {
                if (int.TryParse(digits.Substring(0, 4), out var y4) && y4 >= 1900 && y4 <= currentYear)
                    return y4;
                return 0;
            }

            if (digits.Length >= 10)
            {
                if (!int.TryParse(digits.Substring(0, 2), out var yy)) return 0;
                // Pick the century so the resulting age is in [0, 99] for '-', shifted back 100 for '+'.
                int year = 1900 + yy;
                if (year > currentYear) year -= 100;          // e.g. "05" in 2026 → 2005, not 1905→ handled below
                // Standard rule: candidate is the most recent year ≤ currentYear matching YY.
                int candidate2000 = 2000 + yy;
                if (candidate2000 <= currentYear) year = candidate2000;
                else year = 1900 + yy;
                if (plusSeparator) year -= 100;
                if (year < 1900 || year > currentYear) return 0;
                return year;
            }

            return 0;
        }

        // ── Årtalsmärke ladder (SHB 5.1.2.2) ──────────────────────────
        // Each step = 3 more fulfilled Guldfodring-years ("oavsett om i följd eller ej").
        // Index 0 = no årtalsmärke yet. The display name for a fulfilled-year count is derived.

        public static readonly string[] ArtalsmarkeLadder =
        {
            /* 0  */ "",
            /* 3  */ "Lägre årtalsmärket med en stjärna",
            /* 6  */ "Lägre årtalsmärket med två stjärnor",
            /* 9  */ "Lägre årtalsmärket med tre stjärnor",
            /* 12 */ "Högre årtalsmärke i brons",
            /* 15 */ "Högre årtalsmärke i silver",
            /* 18 */ "Högre årtalsmärke i guld",
            /* 21 */ "Högre årtalsmärke i guld med en stjärna",
            /* 24 */ "Högre årtalsmärke i guld med två stjärnor",
            /* 27 */ "Högre årtalsmärke i guld med tre stjärnor",
            /* 30 */ "Högre årtalsmärke med krans",
            /* 33 */ "Högre årtalsmärke med krans och en stjärna",
            /* 36 */ "Högre årtalsmärke med krans och två stjärnor",
            /* 39 */ "Högre årtalsmärke med krans och tre stjärnor",
            /* 42 */ "Högre årtalsmärke med emaljerad krans",
            /* 45 */ "Högre årtalsmärke med emaljerad krans och en stjärna",
            /* 48 */ "Högre årtalsmärke med emaljerad krans och två stjärnor",
            /* 51 */ "Högre årtalsmärke med emaljerad krans och tre stjärnor"
        };

        /// <summary>Years required per ladder step (3 per step).</summary>
        public const int YearsPerArtalsmarkeStep = 3;

        /// <summary>
        /// Current årtalsmärke step index for a given count of fulfilled Guldfodring-years
        /// (0 = none yet, capped at the top of the ladder).
        /// </summary>
        public static int ArtalsmarkeStepIndex(int fulfilledYears)
        {
            if (fulfilledYears < YearsPerArtalsmarkeStep) return 0;
            int idx = fulfilledYears / YearsPerArtalsmarkeStep;
            return Math.Min(idx, ArtalsmarkeLadder.Length - 1);
        }

        public static string ArtalsmarkeName(int fulfilledYears) =>
            ArtalsmarkeLadder[ArtalsmarkeStepIndex(fulfilledYears)];

        /// <summary>Fulfilled-year count at which the next ladder step is reached (or 0 if maxed out).</summary>
        public static int YearsToNextArtalsmarke(int fulfilledYears)
        {
            int nextStep = ArtalsmarkeStepIndex(fulfilledYears) + 1;
            if (nextStep >= ArtalsmarkeLadder.Length) return 0;
            return nextStep * YearsPerArtalsmarkeStep;
        }

        // ── Mästarmärket (5.2) ────────────────────────────────────────
        // Year-count → valör, same time-norm structure as the lägre årtalsmärke: brons/silver/guld at
        // 3/6/9 qualifying years (not necessarily consecutive), then guld med ★/★★/★★★ each +5 years.
        // A "qualifying year" (route 1, SHB 5.2 alt. 1) = a standardmedalj i SILVER i fältskjutning AND
        // a standardmedalj i SILVER i precisionsskjutning the same year. Route 2 (årliga kompetensprov)
        // is not auto-evaluated — surfaced as a note (see SHB 5.2 alt. 2).
        public const string MastarRoute2Note =
            "Alternativ 2 (årliga kompetensprov i precision/serier/fält) finns också — se SHB kap 5.2.";

        /// <summary>Base valör (Brons/Silver/Guld) for a Mästarmärke qualifying-year count, or null below 3.</summary>
        public static string? MastarLevel(int qualifyingYears) =>
            qualifyingYears >= 9 ? LevelGuld
            : qualifyingYears >= 6 ? LevelSilver
            : qualifyingYears >= 3 ? LevelBrons
            : null;

        /// <summary>Stars on the guld märke (0–3): +1 per 5 qualifying years beyond the 9 that earn guld.</summary>
        public static int MastarGuldStars(int qualifyingYears) =>
            qualifyingYears < 9 ? 0 : Math.Min(3, (qualifyingYears - 9) / 5);

        /// <summary>Full valör name incl. stars, e.g. "Guld med två stjärnor".</summary>
        public static string MastarLevelDisplay(int qualifyingYears)
        {
            var lvl = MastarLevel(qualifyingYears);
            if (lvl == null) return "";
            int stars = MastarGuldStars(qualifyingYears);
            if (lvl != LevelGuld || stars == 0) return lvl;
            return stars switch
            {
                1 => "Guld med en stjärna",
                2 => "Guld med två stjärnor",
                _ => "Guld med tre stjärnor"
            };
        }

        /// <summary>Qualifying-year count at which the next valör/star step is reached (0 = maxed).</summary>
        public static int MastarYearsToNext(int qualifyingYears)
        {
            if (qualifyingYears < 3) return 3;
            if (qualifyingYears < 6) return 6;
            if (qualifyingYears < 9) return 9;
            if (qualifyingYears < 24) return ((qualifyingYears - 9) / 5 + 1) * 5 + 9; // 14, 19, 24
            return 0;
        }

        // ── Stormästarmärket (5.3) ────────────────────────────────────
        // Career inteckningspoäng; 30 p → eligible (club nominates to SPSF with a meritförteckning).
        // Tabell 2 (1972→): points by championship level × deltagarantal × placering. Each string lists
        // points for place 1, 2, 3, … reading left→right. Pre-1972 Tabell 1 and Rikstävlingen (dropped
        // 1972) are not modelled — they don't apply to current shooters.
        public const string SmScopeKrets = "Krets";
        public const string SmScopeLandsdel = "Landsdel";
        public const string SmScopeSvenskt = "Svenskt";
        public const int StormastarEligibleAt = 30;

        public static string StormastarScopeDisplay(string? scope) => scope switch
        {
            SmScopeKrets => "Kretsmästerskap",
            SmScopeLandsdel => "Landsdelsmästerskap",
            SmScopeSvenskt => "Svenskt mästerskap",
            _ => scope ?? ""
        };

        // [scope][participant-band index] = points-per-place string. Bands: 8–20,21–50,51–100,101–150,151–200,201+.
        private static readonly Dictionary<string, string[]> _stormastarTable = new()
        {
            // KM 201+ left blank in SHB — carried from 151–200 ("4321").
            [SmScopeKrets]    = new[] { "21",  "321",  "321",   "321",    "4321",    "4321"     },
            [SmScopeLandsdel] = new[] { "1",   "21",   "321",   "4321",   "54321",   "654321"   },
            [SmScopeSvenskt]  = new[] { "321", "4321", "54321", "654321", "7654321", "87654321" }
        };

        private static int StormastarBandIndex(int participants) =>
            participants < 8 ? -1
            : participants <= 20 ? 0
            : participants <= 50 ? 1
            : participants <= 100 ? 2
            : participants <= 150 ? 3
            : participants <= 200 ? 4
            : 5;

        /// <summary>Inteckningspoäng for one championship result (Tabell 2). 0 if it doesn't score.</summary>
        public static int StormastarPoints(string? scope, int participants, int place)
        {
            if (place < 1 || scope == null || !_stormastarTable.TryGetValue(scope, out var bands)) return 0;
            int bi = StormastarBandIndex(participants);
            if (bi < 0) return 0;
            string s = bands[bi];
            if (place > s.Length) return 0;
            return s[place - 1] - '0'; // leftmost digit = points for place 1
        }
    }
}
