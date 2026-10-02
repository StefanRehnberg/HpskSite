namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// SHB:s regler för fältstationer, på servern — för kretsens bangranskning (fas 3).
    ///
    /// <para><b>⚠️⚠️ EN SPEGEL AV KONFIGURATORNS JAVASCRIPT, och de två MÅSTE säga samma sak.</b>
    /// Källan är <c>_FaltskytteConfiguratorScript.cshtml</c>: <c>SHB_MAX_DISTANCES</c>,
    /// <c>faltShbBucket</c>, <c>faltLookupSizeGroup</c> (stödhand ett steg upp, mörker ett steg ner) och
    /// <c>faltSuggestShootingTime</c> (Normal: girig fördelning av 6 skott på de svåraste figurerna;
    /// Poäng: minst ett skott per figur, överskottet på de enklaste; tillägg för 45° vapenläge, omriktning
    /// och mörker ×1,30). Ändras en regel där måste den ändras här — annars säger granskarens checklista
    /// något annat än banläggarens skärm om samma station. <c>ShbFieldRulesTests</c> pinnar talen.</para>
    ///
    /// <para>Rena funktioner, ingen databas.</para>
    /// </summary>
    public static class ShbFieldRules
    {
        /// <summary>SHB:s maxavstånd (m) per storleksgrupp och vapengruppshink. Grupp 15 = ej grupperad (ingen rad).</summary>
        private static readonly Dictionary<int, (int AR, int B, int C, int? M)> MaxDistances = new()
        {
            [0] = (80, 90, 100, null),
            [1] = (70, 80, 90, 180),
            [2] = (60, 65, 75, 150),
            [3] = (50, 55, 65, 130),
            [4] = (45, 50, 55, 110),
            [5] = (40, 45, 50, 100),
            [6] = (35, 40, 45, 90),
            [7] = (30, 35, 40, 80),
            [8] = (27, 32, 36, 68),
            [9] = (24, 27, 30, 60),
            [10] = (20, 22, 25, 50),
            [11] = (16, 18, 20, 40),
            [12] = (14, 16, 18, 36),
            [13] = (12, 14, 16, 32),
            [14] = (7, 8, 9, 17),
        };

        private static readonly Dictionary<string, double> MaxTimePerShot = new() { ["AR"] = 2.0, ["B"] = 1.75, ["C"] = 1.5, ["M"] = 20 };
        public const int Weapon45Supplement = 2;
        public const int OmriktningSupplement = 2;
        public const double MorkerMultiplier = 1.30;
        public const int StationShots = 6;
        /// <summary>SHB E.7.1.1: skjutavstånd över 180 meter är inte tillåtet (magnum).</summary>
        public const int MagnumMaxDistance = 180;
        public const string SupportHandAllowed = "Stödhand tillåten";

        /// <summary>Vapengrupp → SHB-diagrammets hink (AR / B / C / M). Hela A-familjen och R delar diagram 1.</summary>
        public static string Bucket(string? weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass)) return "AR";
            if (weaponClass == "B") return "B";
            if (weaponClass == "C") return "C";
            if (weaponClass == "M" || (weaponClass.Length > 1 && weaponClass[0] == 'M' && char.IsDigit(weaponClass[1]))) return "M";
            return "AR";
        }

        public static bool IsMagnum(string? weaponClass) => Bucket(weaponClass) == "M";

        /// <summary>Maxavstånd för en storleksgrupp och vapengrupp; null när gruppen saknar SHB-rad.</summary>
        public static int? MaxDistance(int? sizeGroup, string? weaponClass)
        {
            if (sizeGroup == null || !MaxDistances.TryGetValue(sizeGroup.Value, out var row)) return null;
            return Bucket(weaponClass) switch { "B" => row.B, "C" => row.C, "M" => row.M, _ => row.AR };
        }

        /// <summary>
        /// Stödhand lättar ett steg (SHB D.10.6.1), mörker skärper ett steg; de tar ut varandra.
        /// Begränsat till tabellen (1–14).
        /// </summary>
        public static int LookupSizeGroup(int sizeGroup, bool supportHandAllowed, bool morker)
        {
            var step = (supportHandAllowed ? -1 : 0) + (morker ? 1 : 0);
            return step == 0 ? sizeGroup : Math.Min(14, Math.Max(1, sizeGroup + step));
        }

        public record Figure(int? SizeGroup, int TargetsPerFigure);
        public record TargetGroup(int? Distance, IReadOnlyList<Figure> Figures);
        public record Station(int ShootingTimeSec, string? SupportHand, string? WeaponStartPosition,
            int MinShotsPerFigure, int MaxShotsPerFigure, IReadOnlyList<TargetGroup> Groups);

        /// <summary>Maxavståndet för en målgrupp = den snävaste figuren; null när ingen figur har storleksgrupp.</summary>
        public static int? GroupMaxDistance(TargetGroup g, string? weaponClass, Station s, bool morker)
        {
            var eased = s.SupportHand == SupportHandAllowed;
            int? max = null;
            foreach (var f in g.Figures)
            {
                if (f.SizeGroup is null or 0) continue;
                var m = MaxDistance(LookupSizeGroup(f.SizeGroup.Value, eased, morker), weaponClass);
                if (m != null) max = max == null ? m : Math.Min(max.Value, m.Value);
            }
            if (max != null && IsMagnum(weaponClass)) max = Math.Min(max.Value, MagnumMaxDistance);
            return max;
        }

        /// <summary>
        /// SHB:s minsta skjuttid för stationen (sekunder), samma beräkning som konfiguratorns förslag.
        /// Null när ingen figur har en storleksgrupp — då finns inget SHB-minimum att jämföra med.
        /// </summary>
        public static int? MinimumShootingTime(Station s, string? weaponClass, bool morker, bool poangMode)
        {
            if (s.Groups.Count == 0) return null;
            var maxTime = MaxTimePerShot[Bucket(weaponClass)];
            var eased = s.SupportHand == SupportHandAllowed;
            var stationMax = s.MaxShotsPerFigure > 0 ? s.MaxShotsPerFigure : 6;
            var stationMin = Math.Max(0, s.MinShotsPerFigure);
            var figures = new List<(double PerShot, int Min, int Max)>();
            foreach (var g in s.Groups)
            {
                var groupMax = GroupMaxDistance(g, weaponClass, s, morker);
                foreach (var f in g.Figures)
                {
                    if (f.SizeGroup is null or 0) continue;
                    var maxD = MaxDistance(LookupSizeGroup(f.SizeGroup.Value, eased, morker), weaponClass);
                    if (maxD is null or 0) continue;
                    var d = g.Distance ?? groupMax ?? maxD.Value;
                    var targets = f.TargetsPerFigure > 0 ? f.TargetsPerFigure : 1;
                    figures.Add(((double)d / maxD.Value * maxTime, targets * stationMin, targets * stationMax));
                }
            }
            if (figures.Count == 0) return null;

            var counts = figures.Select(f => poangMode ? Math.Min(Math.Max(1, f.Min), f.Max) : Math.Min(Math.Max(0, f.Min), f.Max)).ToArray();
            var remaining = StationShots - counts.Sum();
            // Poäng: överskottet på de enklaste. Normal: på de svåraste (värsta fördelning).
            var order = Enumerable.Range(0, figures.Count)
                .OrderBy(i => poangMode ? figures[i].PerShot : -figures[i].PerShot).ToList();
            foreach (var i in order)
            {
                if (remaining <= 0) break;
                var take = Math.Min(figures[i].Max - counts[i], remaining);
                if (take > 0) { counts[i] += take; remaining -= take; }
            }
            var sum = (int)Math.Ceiling(figures.Select((f, i) => counts[i] * f.PerShot).Sum());

            // ⚠ 45°-tillägget gäller inte magnum (E.7.1.2) — samma undantag som konfiguratorn.
            if (s.WeaponStartPosition == "45 grader" && !IsMagnum(weaponClass)) sum += Weapon45Supplement;
            sum += Math.Max(0, s.Groups.Count - 1) * OmriktningSupplement;
            if (morker) sum = (int)Math.Ceiling(sum * MorkerMultiplier);
            return sum;
        }
    }
}
