namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Stationsupplägget för Magnumfält mot SHB Del E (Magnum-omtaget 2026-11-22): vad bangranskningen och konfiguratorn ska säga om
    /// uppsättningen stationer. Rena funktioner, ingen databas. Det är "bör"-regler och krav som arrangören kan avvika från; svaret är
    /// meningar att visa, aldrig en spärr.
    ///
    /// <para>SHB E.6.1 (s. 135): minst 8 stationer ("bör", "skall" vid SM); station 1 ska ge inskjutning och får inte ha poängmål;
    /// minst två stationer ska ha ringade figurer (poängmål). E.10.5.1 (s. 159): en station är Hands-up, en till tillåts vid fler än 8
    /// stationer, högst 2 figurer. E.6.11.3: särskjutningsstationens figurer ska vara poängmål och högst en träff per figur räknas.
    /// C.5.1 (s. 69): minst 6 stationer ger standardmedalj.</para>
    /// </summary>
    public static class MagnumStationRules
    {
        /// <summary>Vapengruppens nyckel i konfigurationen (M, inte M1–M9).</summary>
        public const string WeaponGroup = "M";
        public const int MinStationsForMedal = 6;
        public const int RecommendedStations = 8;

        /// <param name="Number">Stationens nummer.</param>
        /// <param name="ShootOffOnly">Särskjutningsstation (räknas inte med i stationsantalet).</param>
        /// <param name="HandsUp">Vapenläget är Hands-up.</param>
        /// <param name="Figures">Antal figurer på stationen.</param>
        /// <param name="PoangmalFigures">Antal av dem som är poängmål.</param>
        /// <param name="MaxShotsPerFigure">Stationens max antal träff per figur.</param>
        public record Station(int Number, bool ShootOffOnly, bool HandsUp, int Figures, int PoangmalFigures, int MaxShotsPerFigure);

        public static List<string> Notes(IReadOnlyList<Station> stations)
        {
            var notes = new List<string>();
            var scored = stations.Where(s => !s.ShootOffOnly).OrderBy(s => s.Number).ToList();
            if (scored.Count == 0) return notes;

            if (scored.Count < MinStationsForMedal)
                notes.Add($"Magnum har {scored.Count} stationer. Med färre än {MinStationsForMedal} ger tävlingen ingen standardmedalj (SHB C.5.1).");
            else if (scored.Count < RecommendedStations)
                notes.Add($"Magnum har {scored.Count} stationer. SHB E.6.1 säger minst {RecommendedStations} (\"bör\"; \"skall\" vid SM).");

            if (scored[0].PoangmalFigures > 0)
                notes.Add($"Station {scored[0].Number} har poängmål. Första stationen ska ge inskjutning och får inte ha poängmål (SHB E.6.1).");

            var withPoangmal = scored.Count(s => s.PoangmalFigures > 0);
            if (withPoangmal < 2)
                notes.Add($"Magnum har {withPoangmal} station{(withPoangmal == 1 ? "" : "er")} med poängmål. Minst två stationer ska ha poängmål (SHB E.6.1).");

            var handsUp = scored.Where(s => s.HandsUp).ToList();
            var allowed = scored.Count > RecommendedStations ? 2 : 1;
            if (handsUp.Count == 0)
                notes.Add("Ingen station är Hands-up. En station ska vara Hands-up (SHB E.10.5.1).");
            else if (handsUp.Count > allowed)
                notes.Add($"{handsUp.Count} stationer är Hands-up ({string.Join(", ", handsUp.Select(s => s.Number))}). SHB E.10.5.1 tillåter {allowed}" +
                          (allowed == 1 ? " (en till vid fler än 8 stationer)." : "."));
            foreach (var h in handsUp.Where(h => h.Figures > 2))
                notes.Add($"Station {h.Number} (Hands-up) har {h.Figures} figurer. Högst 2 figurer är tillåtet (SHB E.10.5.1).");

            foreach (var so in stations.Where(s => s.ShootOffOnly).OrderBy(s => s.Number))
            {
                if (so.Figures == 0 || so.PoangmalFigures < so.Figures)
                    notes.Add($"Särskjutningsstation {so.Number}: alla figurer ska vara poängmål (SHB E.6.11.3).");
                if (so.MaxShotsPerFigure != 1)
                    notes.Add($"Särskjutningsstation {so.Number}: högst en träff per figur får räknas (SHB E.6.11.3). Sätt Max träff/fig till 1.");
            }
            return notes;
        }
    }
}
