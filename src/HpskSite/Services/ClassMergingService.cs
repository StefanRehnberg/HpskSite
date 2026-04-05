using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;

namespace HpskSite.Services
{
    // ── Models ──────────────────────────────────────────────────────

    public class ClassMergeAnalysis
    {
        public List<ClassInfo> Classes { get; set; } = new();
        public List<MergeSuggestion> Suggestions { get; set; } = new();
    }

    public class ClassInfo
    {
        public string ClassName { get; set; } = "";
        public string WeaponGroup { get; set; } = "";
        public int ParticipantCount { get; set; }
        public bool BelowThreshold { get; set; }
        public string MedalImpact { get; set; } = "";
    }

    public class MergeSuggestion
    {
        public string SourceClass { get; set; } = "";
        public int SourceCount { get; set; }
        public string? DefaultTarget { get; set; }
        public List<string> PossibleTargets { get; set; } = new();
        public bool RequiresAdminChoice { get; set; }
        public string Reason { get; set; } = "";
    }

    public class ClassMergeAction
    {
        public string SourceClass { get; set; } = "";
        public string TargetClass { get; set; } = "";
    }

    // ── Service ─────────────────────────────────────────────────────

    public class ClassMergingService
    {
        private const int MergeThreshold = 5;

        /// <summary>
        /// Analyzes class participant counts and generates merge suggestions based on rules.
        /// </summary>
        public ClassMergeAnalysis Analyze(List<PrecisionResultEntry> results, string competitionType)
        {
            // Count distinct members per class (using Name, not ID)
            var classCounts = results
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g => g.Key)
                .GroupBy(k => ShootingClasses.GetById(k.ShootingClass)?.Name ?? k.ShootingClass)
                .ToDictionary(g => g.Key, g => g.Count());

            return AnalyzeFromCounts(classCounts, competitionType);
        }

        /// <summary>
        /// Analyzes class counts from any discipline and generates merge suggestions.
        /// </summary>
        public ClassMergeAnalysis AnalyzeFromCounts(Dictionary<string, int> classCounts, string competitionType)
        {
            var analysis = new ClassMergeAnalysis();

            // Build ClassInfo list
            foreach (var kvp in classCounts.OrderBy(k => GetClassSortOrder(k.Key)))
            {
                var weaponGroup = GetWeaponGroup(k: kvp.Key);
                analysis.Classes.Add(new ClassInfo
                {
                    ClassName = kvp.Key,
                    WeaponGroup = weaponGroup,
                    ParticipantCount = kvp.Value,
                    BelowThreshold = kvp.Value < MergeThreshold,
                    MedalImpact = kvp.Value < MergeThreshold
                        ? GetMedalImpact(kvp.Value, IsJuniorClass(kvp.Key))
                        : ""
                });
            }

            // Generate suggestions for classes below threshold
            var allowR23Merge = competitionType.Equals("Milsnabb", StringComparison.OrdinalIgnoreCase)
                || competitionType.Equals("Faltskytte", StringComparison.OrdinalIgnoreCase)
                || competitionType.Equals("MagnumFalt", StringComparison.OrdinalIgnoreCase);

            foreach (var cls in analysis.Classes.Where(c => c.BelowThreshold))
            {
                var suggestion = BuildSuggestion(cls.ClassName, cls.ParticipantCount, classCounts, allowR23Merge);
                if (suggestion != null)
                    analysis.Suggestions.Add(suggestion);
            }

            // Remove duplicate bidirectional suggestions (e.g. A2→A3 and A3→A2)
            DeduplicateBidirectionalSuggestions(analysis.Suggestions);

            return analysis;
        }

        /// <summary>
        /// Produces a combined class name for merged classes.
        /// Examples: C2 + C3 → "C2+3", C Vet Ä + C Vet Y → "C Vet",
        /// C2 Dam + C2 → "C2+Dam", C Jun + C1 → "C1+Jun", A2 + A3 → "A2+3"
        /// </summary>
        public static string GetCombinedClassName(string source, string target)
        {
            // Vet Ä + Vet Y → drop the age suffix → "C Vet" / "L Vet"
            if (IsVetAClass(source) && IsVetYClass(target) ||
                IsVetYClass(source) && IsVetAClass(target))
            {
                var weaponGroup = GetWeaponGroup(source);
                return $"{weaponGroup} Vet";
            }

            // Class 2+3 merge (A2+A3, B2+B3, R2+R3) → "A2+3"
            var sourceLevel = GetCompetenceLevel(source);
            var targetLevel = GetCompetenceLevel(target);
            if (sourceLevel != null && targetLevel != null &&
                !IsDamClass(source) && !IsDamClass(target) &&
                !IsVetYClass(source) && !IsVetAClass(source) &&
                !IsJuniorClass(source) && !IsJuniorClass(target))
            {
                var weaponGroup = GetWeaponGroup(source);
                var low = Math.Min(sourceLevel.Value, targetLevel.Value);
                var high = Math.Max(sourceLevel.Value, targetLevel.Value);
                return $"{weaponGroup}{low}+{high}";
            }

            // Dam merging into open class → "C2+Dam"
            if (IsDamClass(source) && !IsDamClass(target))
                return $"{target}+Dam";
            if (IsDamClass(target) && !IsDamClass(source))
                return $"{source}+Dam";

            // Jun merging into open class → "C1+Jun"
            if (IsJuniorClass(source) && !IsJuniorClass(target))
                return $"{target}+Jun";
            if (IsJuniorClass(target) && !IsJuniorClass(source))
                return $"{source}+Jun";

            // Vet Y merging into open class → "C2+Vet"
            if ((IsVetYClass(source) || IsVetAClass(source)) && !IsVetYClass(target) && !IsVetAClass(target))
                return $"{target}+Vet";
            if ((IsVetYClass(target) || IsVetAClass(target)) && !IsVetYClass(source) && !IsVetAClass(source))
                return $"{source}+Vet";

            // Fallback: "Source/Target"
            return $"{source}/{target}";
        }

        // ── Rule engine ─────────────────────────────────────────────

        private MergeSuggestion? BuildSuggestion(string className, int count,
            Dictionary<string, int> classCounts, bool allowR23Merge)
        {
            var weaponGroup = GetWeaponGroup(className);

            // Class 1 never merges
            if (IsClass1(className))
                return null;

            // A Opt never merges
            if (className == "A Opt")
                return null;

            // M-classes excluded
            if (weaponGroup == "M")
                return null;

            // ── Weapon groups A, B: class 2 ↔ 3 ──
            if (weaponGroup == "A" || weaponGroup == "B")
                return BuildLevel23Suggestion(className, count, classCounts, weaponGroup);

            // ── Weapon group R: class 2 ↔ 3 (Milsnabb, Fältskytte, MagnumFält) ──
            if (weaponGroup == "R" && allowR23Merge)
                return BuildLevel23Suggestion(className, count, classCounts, weaponGroup);

            // ── Weapon groups C and L: special rules ──
            if (weaponGroup == "C" || weaponGroup == "L")
                return BuildWeaponGroupCLSuggestion(className, count, classCounts, weaponGroup);

            return null;
        }

        /// <summary>
        /// For A/B/R weapon groups: class 2 and 3 can merge with each other.
        /// </summary>
        private MergeSuggestion? BuildLevel23Suggestion(string className, int count,
            Dictionary<string, int> classCounts, string weaponGroup)
        {
            var level = GetCompetenceLevel(className);
            if (level != 2 && level != 3) return null;

            var partnerLevel = level == 2 ? 3 : 2;
            var partnerName = $"{weaponGroup}{partnerLevel}";

            if (!classCounts.ContainsKey(partnerName)) return null;

            return new MergeSuggestion
            {
                SourceClass = className,
                SourceCount = count,
                DefaultTarget = partnerName,
                PossibleTargets = new List<string> { partnerName },
                RequiresAdminChoice = false,
                Reason = $"Klass {level} och {partnerLevel} i vapengrupp {weaponGroup} får slås samman"
            };
        }

        /// <summary>
        /// For C/L weapon groups: Dam→Open, Vet Ä→Vet Y (step 1), Vet Y→Open (step 2), Jun→Open (admin choice).
        /// </summary>
        private MergeSuggestion? BuildWeaponGroupCLSuggestion(string className, int count,
            Dictionary<string, int> classCounts, string weaponGroup)
        {
            // ── Dam classes: merge into corresponding open class ──
            if (IsDamClass(className))
            {
                var level = GetCompetenceLevel(className);
                if (level == null) return null;
                var target = $"{weaponGroup}{level}";
                if (!classCounts.ContainsKey(target)) return null;

                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = target,
                    PossibleTargets = new List<string> { target },
                    RequiresAdminChoice = false,
                    Reason = $"Dam slås samman med motsvarande öppen klass ({target})"
                };
            }

            // ── Vet Ä: merge into Vet Y (step 1) ──
            if (IsVetAClass(className))
            {
                var vetYName = className.Replace("Vet Ä", "Vet Y");
                // Vet Y might not exist in this competition, but we still suggest it
                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = vetYName,
                    PossibleTargets = new List<string> { vetYName },
                    RequiresAdminChoice = false,
                    Reason = $"Veteran Äldre slås samman med Veteran Yngre"
                };
            }

            // ── Vet Y: if combined with Vet Ä still < 5, merge into open class (step 2) ──
            if (IsVetYClass(className))
            {
                var vetAName = className.Replace("Vet Y", "Vet Ä");
                var vetACount = classCounts.GetValueOrDefault(vetAName, 0);
                var combinedCount = count + vetACount;

                // Only suggest further merge if combined Vet Y + Vet Ä is still < 5
                if (combinedCount >= MergeThreshold) return null;

                var openTargets = GetOpenClassTargets(weaponGroup, classCounts);
                if (!openTargets.Any()) return null;

                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = null,
                    PossibleTargets = openTargets,
                    RequiresAdminChoice = true,
                    Reason = $"Veteran ({combinedCount} skyttar inkl. Vet Ä) — välj vilken öppen klass att slå samman med"
                };
            }

            // ── Jun: merge into open class (admin choice) ──
            if (IsJuniorClass(className))
            {
                var openTargets = GetOpenClassTargets(weaponGroup, classCounts);
                if (!openTargets.Any()) return null;

                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = null,
                    PossibleTargets = openTargets,
                    RequiresAdminChoice = true,
                    Reason = "Junior — välj vilken öppen klass att slå samman med"
                };
            }

            // ── Open C/L class 2 or 3: cannot merge with each other in C/L ──
            // (Rules say class 2+3 merging only allowed in A and B)
            return null;
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static List<string> GetOpenClassTargets(string weaponGroup, Dictionary<string, int> classCounts)
        {
            // Return open classes (1, 2, 3) that actually exist in this competition
            var candidates = new[] { $"{weaponGroup}1", $"{weaponGroup}2", $"{weaponGroup}3" };
            return candidates.Where(c => classCounts.ContainsKey(c)).ToList();
        }

        private static string GetWeaponGroup(string k)
        {
            // Use ShootingClasses model when possible
            var sc = ShootingClasses.GetByName(k);
            if (sc != null) return sc.Weapon.ToString();

            // Fallback: extract first letter
            if (string.IsNullOrEmpty(k)) return "";
            return k.Substring(0, 1);
        }

        private static int? GetCompetenceLevel(string className)
        {
            // "C2 Dam" → 2, "A3" → 3, "C Vet Y" → null, "L1 Dam" → 1
            // Look for a digit right after the weapon group letter
            if (className.Length < 2) return null;
            if (char.IsDigit(className[1]))
                return className[1] - '0';
            return null;
        }

        private static bool IsClass1(string className)
        {
            return className.Length >= 2 && className[1] == '1' && !className.Contains("Vet") && !className.Contains("Jun");
        }

        private static bool IsDamClass(string className) => className.Contains("Dam");

        private static bool IsVetYClass(string className) => className.Contains("Vet Y");

        private static bool IsVetAClass(string className) => className.Contains("Vet Ä");

        private static bool IsJuniorClass(string className) => className.Contains("Jun");

        private static string GetMedalImpact(int count, bool isJunior)
        {
            if (isJunior) return "Alltid medaljer till topp 3";
            return count switch
            {
                4 => "Guld + Silver",
                3 => "Enbart Guld",
                _ => "Inga medaljer"
            };
        }

        private static void DeduplicateBidirectionalSuggestions(List<MergeSuggestion> suggestions)
        {
            // For A2↔A3: if both have suggestions pointing at each other, keep only one
            var toRemove = new List<MergeSuggestion>();
            foreach (var s in suggestions)
            {
                if (s.DefaultTarget == null) continue;
                var reverse = suggestions.FirstOrDefault(
                    o => o != s && o.SourceClass == s.DefaultTarget && o.DefaultTarget == s.SourceClass);
                if (reverse != null && !toRemove.Contains(s))
                    toRemove.Add(reverse);
            }
            foreach (var r in toRemove)
                suggestions.Remove(r);
        }

        private static int GetClassSortOrder(string className)
        {
            var order = new Dictionary<string, int>
            {
                { "C1", 1 }, { "C1 Dam", 2 }, { "C1 Jun", 3 },
                { "C2", 4 }, { "C2 Dam", 5 }, { "C2 Jun", 6 },
                { "C3", 7 }, { "C3 Dam", 8 }, { "C3 Jun", 9 },
                { "C Vet Y", 10 }, { "C Vet Ä", 11 }, { "C Jun", 12 },
                { "B1", 16 }, { "B2", 19 }, { "B3", 22 },
                { "A1", 31 }, { "A2", 34 }, { "A3", 37 }, { "A Opt", 40 },
                { "R1", 41 }, { "R2", 42 }, { "R3", 43 },
                { "L1", 50 }, { "L1 Dam", 51 }, { "L2", 52 }, { "L2 Dam", 53 },
                { "L3", 54 }, { "L3 Dam", 55 },
                { "L Vet Y", 56 }, { "L Vet Ä", 57 }, { "L Jun", 58 }
            };
            return order.GetValueOrDefault(className, 999);
        }
    }
}
