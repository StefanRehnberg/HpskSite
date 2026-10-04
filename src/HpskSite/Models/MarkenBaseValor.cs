namespace HpskSite.Models
{
    /// <summary>
    /// Pistolskyttemärkets brons och silver ur godkända serier (fas A4, beslut 2026-10-02/03).
    ///
    /// <para>En valör är uppfylld ett kalenderår när både del 1 (3 precisionsserier som når valören) och
    /// del 2 (3 tillämpningsserier av valören, ELLER en standardmedalj i fält) är uppfyllda — samma två
    /// delar som guldfodringen, SHB kap 5. Ett märke per år, i turordning (brons före silver).</para>
    ///
    /// <para>⚠️ Guld räknas ALDRIG fram här. Guldmärket är personligt med ett unikt nummer som förbundet
    /// tilldelar, och delas ut på årsmötet — det förblir manuellt.</para>
    ///
    /// <para>Ren funktion: ingen databas, inget "nu". Anroparen samlar underlaget.</para>
    /// </summary>
    public static class MarkenBaseValor
    {
        public const int SeriesRequired = 3;

        /// <summary>One year's evidence. Counts are of VERIFIED series reaching at least the valör.</summary>
        public record YearEvidence(
            int Year,
            int PrecisionAtLeastBrons,
            int PrecisionAtLeastSilver,
            int TillampningAtLeastBrons,
            int TillampningAtLeastSilver,
            bool HasFaltStandardMedal,
            bool GuldfodringFulfilled);

        /// <summary>An existing Pistolskyttemärke base badge (any source, any status).</summary>
        public record HeldBadge(string Level, int Year, string Status);

        /// <summary>A valör to award: level and the year it was earned.</summary>
        public record Award(string Level, int Year);

        /// <summary>The highest valör (ordinal 0–2) a single year's evidence fulfils.</summary>
        public static int YearOrdinal(YearEvidence e)
        {
            bool silver = e.PrecisionAtLeastSilver >= SeriesRequired
                          && (e.TillampningAtLeastSilver >= SeriesRequired || e.HasFaltStandardMedal);
            if (silver) return 2;
            bool brons = e.PrecisionAtLeastBrons >= SeriesRequired
                         && (e.TillampningAtLeastBrons >= SeriesRequired || e.HasFaltStandardMedal);
            return brons ? 1 : 0;
        }

        /// <summary>
        /// The badges to add. Starts from the highest VERIFIED badge the member already holds and only
        /// walks years strictly after it (one step per year), capped at silver.
        ///
        /// <list type="bullet">
        /// <item>A level that already exists in ANY status is never added again — a REJECTED one is how a
        /// functionary's removal sticks, and it also stops the walk: no silver on top of a brons that a
        /// functionary took away.</item>
        /// <item>⚠️ GUARD (Claude, 2026-10-04 — to confirm with Stefan): nothing is derived in a year where
        /// the member fulfils the guldfodring, or any later year. Whoever upholds guld fulfils brons and
        /// silver by definition; without the guard every veteran whose real guldmärke was never
        /// registered here would get brons and silver awarded — and onto the club's order list.</item>
        /// </list>
        /// </summary>
        public static List<Award> Derive(IEnumerable<YearEvidence> years, IEnumerable<HeldBadge> held)
        {
            var heldList = held.ToList();
            var verified = heldList
                .Where(b => b.Status == Marken.StatusVerified && Marken.LevelOrdinal(b.Level) is >= 1 and <= 3)
                .OrderByDescending(b => Marken.LevelOrdinal(b.Level))
                .FirstOrDefault();
            int heldOrd = verified == null ? 0 : Marken.LevelOrdinal(verified.Level);
            int heldYear = verified?.Year ?? int.MinValue;

            var awards = new List<Award>();
            if (heldOrd >= 2) return awards;

            bool guldSeen = false;
            foreach (var e in years.OrderBy(y => y.Year))
            {
                if (e.GuldfodringFulfilled) guldSeen = true;
                if (guldSeen) break;
                if (e.Year <= heldYear) continue;

                int next = heldOrd + 1;
                if (next > 2 || YearOrdinal(e) < next) continue;

                string level = Marken.LevelFromOrdinal(next)!;
                var existing = heldList.FirstOrDefault(b => b.Level == level);
                if (existing != null)
                {
                    if (existing.Status == Marken.StatusRejected) break;   // removed by a functionary
                    heldOrd = next; heldYear = e.Year;                      // already recorded (e.g. Reported)
                    continue;
                }
                awards.Add(new Award(level, e.Year));
                heldOrd = next; heldYear = e.Year;
            }
            return awards;
        }
    }
}
