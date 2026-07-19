using Newtonsoft.Json;
using HpskSite.CompetitionTypes.Springskytte.Models;

namespace HpskSite.CompetitionTypes.Springskytte.Services
{
    /// <summary>
    /// Scoring logic for Springskytte competitions.
    ///
    /// Class C (falling targets): Each miss = 1 point (or 2 for markestagning)
    /// Class A (cardboard/zones): Ring 1-2 = 0, Ring 3 = 1, Ring 4 = 2, outside = 3
    ///
    /// Total time = Sprint time + (Shooting score * Penalty multiplier * 60 seconds)
    /// Each penalty point = 1 minute added to sprint time.
    /// </summary>
    public class SpringskytteScoringService
    {
        /// <summary>
        /// Calculate shooting score from shots JSON based on weapon class.
        /// </summary>
        public int CalculateShootingScore(string shotsJson, string weaponClass)
        {
            var series = DeserializeShots(shotsJson);
            if (!series.Any()) return 0;

            if (weaponClass.Equals("C", StringComparison.OrdinalIgnoreCase))
                return CalculateClassCScore(series);

            if (weaponClass.Equals("A", StringComparison.OrdinalIgnoreCase))
                return CalculateClassAScore(series);

            return 0;
        }

        /// <summary>
        /// Class C: Count misses (Bom). Each miss = 1 penalty point.
        /// </summary>
        private int CalculateClassCScore(List<List<string>> series)
        {
            int misses = 0;
            foreach (var stop in series)
            {
                foreach (var shot in stop)
                {
                    if (shot.Equals("B", StringComparison.OrdinalIgnoreCase) ||
                        shot.Equals("Bom", StringComparison.OrdinalIgnoreCase))
                    {
                        misses++;
                    }
                }
            }
            return misses;
        }

        /// <summary>
        /// Class A: Zone count format — each target is [ring1, ring2, ring3, ring4, bom].
        /// Score = ring3Count * 1 + ring4Count * 2 + bomCount * 3.
        /// </summary>
        private int CalculateClassAScore(List<List<string>> series)
        {
            int totalScore = 0;
            foreach (var target in series)
            {
                // Expected: 5 values = [ring1, ring2, ring3, ring4, bom]
                if (target.Count >= 5)
                {
                    int.TryParse(target[2], out int ring3);
                    int.TryParse(target[3], out int ring4);
                    int.TryParse(target[4], out int bom);
                    totalScore += ring3 * 1 + ring4 * 2 + bom * 3;
                }
            }
            return totalScore;
        }

        /// <summary>
        /// Calculate total time in seconds: sprint time + penalty time.
        /// </summary>
        public decimal? CalculateTotalTime(decimal? sprintTimeSeconds, int shootingScore, int penaltyMultiplier)
        {
            if (sprintTimeSeconds == null) return null;
            return sprintTimeSeconds.Value + (shootingScore * penaltyMultiplier * 60m);
        }

        /// <summary>
        /// Calculate hits per stop for tiebreaker (last stop first).
        /// Works for both Class C (count H/Traff) and Class A (count shots with zone 0).
        /// </summary>
        public List<int> CalculateHitsPerStop(string shotsJson, string weaponClass)
        {
            var series = DeserializeShots(shotsJson);
            var hitsPerStop = new List<int>();

            foreach (var stop in series)
            {
                int hits;
                if (weaponClass.Equals("C", StringComparison.OrdinalIgnoreCase))
                {
                    hits = stop.Count(s =>
                        s.Equals("H", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("Traff", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("Träff", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // Class A zone count format: [ring1, ring2, ring3, ring4, bom]
                    // "Hits" = ring1 + ring2 (zero-penalty shots)
                    if (stop.Count >= 2)
                    {
                        int.TryParse(stop[0], out int r1);
                        int.TryParse(stop[1], out int r2);
                        hits = r1 + r2;
                    }
                    else
                    {
                        hits = 0;
                    }
                }
                hitsPerStop.Add(hits);
            }

            return hitsPerStop;
        }

        /// <summary>
        /// Parse sprint time from "MM:SS" or "H:MM:SS" input string to total seconds.
        /// </summary>
        public decimal? ParseSprintTime(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var parts = input.Trim().Split(':');
            try
            {
                if (parts.Length == 2)
                {
                    // MM:SS
                    int minutes = int.Parse(parts[0]);
                    int seconds = int.Parse(parts[1]);
                    return minutes * 60m + seconds;
                }
                if (parts.Length == 3)
                {
                    // H:MM:SS
                    int hours = int.Parse(parts[0]);
                    int minutes = int.Parse(parts[1]);
                    int seconds = int.Parse(parts[2]);
                    return hours * 3600m + minutes * 60m + seconds;
                }
            }
            catch { }

            // Try parsing as decimal seconds
            if (decimal.TryParse(input, out decimal directSeconds))
                return directSeconds;

            return null;
        }

        /// <summary>
        /// Build a SpringskytteShooterResult from a DB entry + member info.
        /// </summary>
        public SpringskytteShooterResult BuildShooterResult(
            SpringskytteResultEntry entry, string name, string club)
        {
            var shotSeries = DeserializeShots(entry.Shots);

            return new SpringskytteShooterResult
            {
                MemberId = entry.MemberId,
                Name = name,
                Club = club,
                WeaponClass = entry.WeaponClass,
                AgeGenderClass = entry.AgeGenderClass,
                StartOrder = entry.StartOrder,
                StartTime = entry.StartTime,
                SprintTimeSeconds = entry.SprintTimeSeconds,
                ShootingScore = entry.ShootingScore ?? 0,
                PenaltyMultiplier = entry.PenaltyMultiplier,
                TotalTimeSeconds = entry.TotalTimeSeconds,
                ShotSeries = shotSeries,
                StationHands = DeserializeHands(entry.StationHands),
                Status = entry.Status,
                HitsPerStop = CalculateHitsPerStop(entry.Shots, entry.WeaponClass)
            };
        }

        /// <summary>Deserialize the per-station grip array ("1"/"2"); empty on null/blank/invalid.</summary>
        public static List<string> DeserializeHands(string? handsJson)
        {
            if (string.IsNullOrWhiteSpace(handsJson) || handsJson == "[]")
                return new List<string>();
            try
            {
                return JsonConvert.DeserializeObject<List<string>>(handsJson) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static List<List<string>> DeserializeShots(string shotsJson)
        {
            if (string.IsNullOrWhiteSpace(shotsJson) || shotsJson == "[]")
                return new List<List<string>>();

            try
            {
                return JsonConvert.DeserializeObject<List<List<string>>>(shotsJson)
                    ?? new List<List<string>>();
            }
            catch
            {
                return new List<List<string>>();
            }
        }
    }
}
