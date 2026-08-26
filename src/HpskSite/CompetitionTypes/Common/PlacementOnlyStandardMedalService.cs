using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Standardmedaljer på PLACERING enbart — topp 1/9 silver, topp 1/3 brons per vapengrupp,
    /// utan fasta poängtrösklar. Det är grundregeln; fasta trösklar (Precisions 267/277 m.fl.) är
    /// per-gren-tillägg och finns bara där SHB publicerar en tabell.
    ///
    /// Delad och PARAMETRISERAD, eftersom de två skillnader som faktiskt finns mellan grenarna är
    /// data och inte logik:
    ///  • <see cref="MinimumSeriesCount"/> — hur mycket som måste vara inmatat innan medaljer alls
    ///    delas ut. Duell kräver 6, NationellHelmatch 12 (sin fulla längd).
    ///  • <see cref="PoolAFamily"/> — om AM/AP/AG rankas ihop med öppna A (SPSF-regel). NH gör det,
    ///    Duell inte.
    ///
    /// ⚠️ `DuellStandardMedalService` och `NationellHelmatchStandardMedalService` är fortfarande två
    /// egna, nästan identiska kopior av logiken nedan (148 respektive 150 rader; de skiljer sig på
    /// exakt de två punkterna ovan). De är AVSIKTLIGT orörda här: de fungerar och deras beteende är
    /// dokumenterat, och att slå ihop dem samtidigt som två nya grenar läggs till blandar en
    /// beteenderisk med ett tillägg. **Den här klassen är den att konvergera mot** — nästa gång någon
    /// rör medaljlogiken för Duell eller NH bör de peka hit i stället.
    /// </summary>
    public class PlacementOnlyStandardMedalService
    {
        /// <summary>Minsta antal inmatade serier innan medaljer delas ut.</summary>
        public int MinimumSeriesCount { get; init; } = 6;

        /// <summary>Rankas AM/AP/AG ihop med öppna A-klassen? (SPSF:s A-familjepoolning.)</summary>
        public bool PoolAFamily { get; init; }

        public void CalculateStandardMedals(List<PrecisionShooterResult> shooters, StandardMedalConfig config)
        {
            if (shooters == null || !shooters.Any() || config.SeriesCount < MinimumSeriesCount)
                return;

            foreach (var group in GroupByWeaponGroup(shooters, config.ShouldSplitGroupC))
            {
                ApplyPercentageMedals(group.Value);
            }
        }

        private Dictionary<string, List<PrecisionShooterResult>> GroupByWeaponGroup(
            List<PrecisionShooterResult> shooters, bool shouldSplitGroupC)
        {
            var groups = new Dictionary<string, List<PrecisionShooterResult>>();

            foreach (var shooter in shooters)
            {
                var weaponGroup = ExtractWeaponGroup(shooter.ShootingClass);

                if (PoolAFamily && (weaponGroup == "A_M" || weaponGroup == "A_P" || weaponGroup == "A_G"))
                {
                    // A_Opt hålls utanför — det är en parallell vapengrupp med egen rankning.
                    weaponGroup = "A";
                }

                string groupKey;
                if (shouldSplitGroupC && weaponGroup == "C")
                {
                    var classification = ExtractClassification(shooter.ShootingClass);
                    groupKey = classification != null ? $"C-{classification}" : "C-Öppen";
                }
                else
                {
                    groupKey = weaponGroup;
                }

                if (!groups.ContainsKey(groupKey))
                    groups[groupKey] = new List<PrecisionShooterResult>();

                groups[groupKey].Add(shooter);
            }

            return groups;
        }

        private void ApplyPercentageMedals(List<PrecisionShooterResult> groupShooters)
        {
            if (!groupShooters.Any()) return;

            var sortedShooters = groupShooters
                .OrderByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.TotalXCount)
                .ToList();

            int shooterCount = sortedShooters.Count;
            int silverQuota = shooterCount / 9;
            int bronzeQuota = shooterCount / 3;

            for (int i = 0; i < silverQuota && i < sortedShooters.Count; i++)
            {
                if (sortedShooters[i].StandardMedal != "B")
                    sortedShooters[i].StandardMedal = "S";
            }

            for (int i = 0; i < bronzeQuota && i < sortedShooters.Count; i++)
            {
                if (string.IsNullOrEmpty(sortedShooters[i].StandardMedal))
                    sortedShooters[i].StandardMedal = "B";
            }

            // Lika resultat vid bronsgränsen får samma medalj — annars avgör listordningen, och den
            // är inte en sportslig skillnad.
            if (bronzeQuota > 0 && bronzeQuota < sortedShooters.Count)
            {
                var lastBronze = sortedShooters[bronzeQuota - 1];
                for (int i = bronzeQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastBronze.TotalScore &&
                        sortedShooters[i].TotalXCount == lastBronze.TotalXCount &&
                        string.IsNullOrEmpty(sortedShooters[i].StandardMedal))
                    {
                        sortedShooters[i].StandardMedal = "B";
                    }
                    else break;
                }
            }

            if (silverQuota > 0 && silverQuota < sortedShooters.Count)
            {
                var lastSilver = sortedShooters[silverQuota - 1];
                for (int i = silverQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastSilver.TotalScore &&
                        sortedShooters[i].TotalXCount == lastSilver.TotalXCount)
                    {
                        sortedShooters[i].StandardMedal = "S";
                    }
                    else break;
                }
            }
        }

        private static string ExtractWeaponGroup(string shootingClass)
        {
            var code = ShootingClasses.GetWeaponClassCode(shootingClass);
            // A-undergrupperna släpps igenom även när poolningen är av, så de hamnar i sin EGEN
            // rankningsgrupp i stället för att tyst klumpas ihop till "C" av okänd-fallbacken.
            if (code == "A" || code == "A_Opt" || code == "A_M" || code == "A_P" || code == "A_G"
                || code == "B" || code == "C")
                return code;
            return "C";
        }

        private static string? ExtractClassification(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass)) return null;
            var upper = shootingClass.ToUpper().Trim();
            if (upper.Contains("DAM")) return "Dam";
            if (upper.Contains("JUN")) return "Jun";
            if (upper.Contains("VET Y") || upper.Contains("VETY")) return "Vet Y";
            if (upper.Contains("VET Ä") || upper.Contains("VETÄ")) return "Vet Ä";
            return null;
        }
    }
}
