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
        /// Produces a combined class name for merged classes. The suffix is driven by the
        /// SOURCE class's category (the class being absorbed into the target) so that e.g.
        /// "C Vet Y → C2 Dam" renders as "C2 Dam+Vet" — target plus the kind of shooters
        /// joining it. Examples:
        ///   C2 + C3 → "C2+3", C2 Dam + C2 → "C2+Dam", C Jun + C1 → "C1+Jun",
        ///   C Vet Y + C2 → "C2+Vet", C Vet Y + C2 Dam → "C2 Dam+Vet",
        ///   Dam C 3 + C 2 → "C2+Dam3", Dam C 3 + Dam C 2 → "C2+3 Dam", A2 + A3 → "A2+3"
        /// </summary>
        public static string GetCombinedClassName(string source, string target)
        {
            var sourceLevel = GetCompetenceLevel(source);
            var targetLevel = GetCompetenceLevel(target);

            // ── Class 2+3 merge for A-family / B / R / C/L open ──────────────────────
            // Pure level merge (no Dam/Vet/Jun marker on either side).
            if (sourceLevel != null && targetLevel != null &&
                !IsDamClass(source) && !IsDamClass(target) &&
                !IsVetYClass(source) && !IsVetAClass(source) &&
                !IsVetYClass(target) && !IsVetAClass(target) &&
                !IsJuniorClass(source) && !IsJuniorClass(target))
            {
                var weaponGroup = GetWeaponGroup(source);
                var low = Math.Min(sourceLevel.Value, targetLevel.Value);
                var high = Math.Max(sourceLevel.Value, targetLevel.Value);
                var prefix = weaponGroup switch
                {
                    "A_Opt" => "A Opt ",
                    "A_M" => "AM",
                    "A_P" => "AP",
                    "A_G" => "AG",
                    _ => weaponGroup
                };
                return $"{prefix}{low}+{high}";
            }

            // ── Both Dam, different levels (e.g. Dam C 3 → Dam C 2) ──────────────────
            if (IsDamClass(source) && IsDamClass(target) &&
                sourceLevel != null && targetLevel != null && sourceLevel != targetLevel)
            {
                var weaponGroup = GetWeaponGroup(source);
                var low = Math.Min(sourceLevel.Value, targetLevel.Value);
                var high = Math.Max(sourceLevel.Value, targetLevel.Value);
                return $"{weaponGroup}{low}+{high} Dam";
            }

            // ── Dam crossing levels into open (e.g. Dam C 3 → C 2) ───────────────────
            // Highlight the source's level so the result class is unambiguous about which
            // Dam-level joined (e.g. "C2+Dam3" means "C2 with Dam C 3 shooters merged in").
            if (IsDamClass(source) && !IsDamClass(target) &&
                sourceLevel != null && targetLevel != null && sourceLevel != targetLevel)
            {
                return $"{target}+Dam{sourceLevel}";
            }
            if (IsDamClass(target) && !IsDamClass(source) &&
                sourceLevel != null && targetLevel != null && sourceLevel != targetLevel)
            {
                return $"{source}+Dam{targetLevel}";
            }

            // ── Suffix from SOURCE category (same-level, or Vet/Jun) ────────────────
            if (IsVetYClass(source) || IsVetAClass(source)) return $"{target}+Vet";
            if (IsJuniorClass(source)) return $"{target}+Jun";
            if (IsDamClass(source) && !IsDamClass(target)) return $"{target}+Dam";

            // ── Reverse direction (target carries the category marker) ──────────────
            if (IsVetYClass(target) || IsVetAClass(target)) return $"{source}+Vet";
            if (IsJuniorClass(target)) return $"{source}+Jun";
            if (IsDamClass(target) && !IsDamClass(source)) return $"{source}+Dam";

            // Fallback
            return $"{source}/{target}";
        }

        /// <summary>
        /// Builds a single combined-class name covering multiple sources merging into one target,
        /// e.g. target=C2 with sources=[C2 Dam, C3 Dam, C Vet Y] → "C2+Dam+Vet". Same-category
        /// duplicates collapse (two Dam-levels both contribute one "Dam" suffix); cross-level
        /// open sources contribute their level number. The per-shooter class column on the
        /// public result list still shows each shooter's original class, so collapsing the
        /// group-level name loses no information.
        /// </summary>
        public static string GetCombinedClassNameMulti(string target, IEnumerable<string> sources)
        {
            var sourceList = sources.Where(s => !string.IsNullOrEmpty(s) && s != target).ToList();
            if (sourceList.Count == 0) return target;
            if (sourceList.Count == 1) return GetCombinedClassName(sourceList[0], target);

            bool hasDam = false, hasJun = false, hasVet = false;
            var openLevels = new SortedSet<int>();

            foreach (var s in sourceList)
            {
                if (IsVetYClass(s) || IsVetAClass(s)) hasVet = true;
                else if (IsJuniorClass(s)) hasJun = true;
                else if (IsDamClass(s)) hasDam = true;
                else
                {
                    var lvl = GetCompetenceLevel(s);
                    if (lvl != null) openLevels.Add(lvl.Value);
                }
            }

            var suffixes = new List<string>();
            foreach (var lvl in openLevels) suffixes.Add(lvl.ToString());
            if (hasDam) suffixes.Add("Dam");
            if (hasJun) suffixes.Add("Jun");
            if (hasVet) suffixes.Add("Vet");

            if (suffixes.Count == 0) return target;
            return $"{target}+{string.Join("+", suffixes)}";
        }

        /// <summary>
        /// Resolve a list of (source, target) merges into a single
        /// "original class name → combined group name" lookup. Multiple merges that share a
        /// target (or chain through one) collapse into one group via union-find — target
        /// always wins (becomes the root). Only the root's name is computed; every member
        /// of the resolved group maps to the same name.
        /// </summary>
        public static Dictionary<string, string> BuildMergeGroupLookup(IEnumerable<ClassMergeAction>? merges)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (merges == null) return lookup;

            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string Find(string c)
            {
                if (!parent.ContainsKey(c)) parent[c] = c;
                while (parent[c] != c)
                {
                    parent[c] = parent[parent[c]]; // path compression
                    c = parent[c];
                }
                return c;
            }

            var mergeList = merges.ToList();
            if (mergeList.Count == 0) return lookup;

            foreach (var m in mergeList)
            {
                if (string.IsNullOrEmpty(m.SourceClass) || string.IsNullOrEmpty(m.TargetClass)) continue;
                Find(m.SourceClass);
                Find(m.TargetClass);
            }
            foreach (var m in mergeList)
            {
                if (string.IsNullOrEmpty(m.SourceClass) || string.IsNullOrEmpty(m.TargetClass)) continue;
                var sourceRoot = Find(m.SourceClass);
                var targetRoot = Find(m.TargetClass);
                if (sourceRoot != targetRoot)
                    parent[sourceRoot] = targetRoot; // target wins
            }

            var byRoot = parent.Keys
                .GroupBy(c => Find(c))
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (root, members) in byRoot)
            {
                if (members.Count <= 1) continue; // singleton — no merge needed
                var sources = members.Where(m => !m.Equals(root, StringComparison.OrdinalIgnoreCase)).ToList();
                var combined = GetCombinedClassNameMulti(root, sources);
                foreach (var member in members)
                    lookup[member] = combined;
            }

            return lookup;
        }

        // ── Rule engine ─────────────────────────────────────────────

        private MergeSuggestion? BuildSuggestion(string className, int count,
            Dictionary<string, int> classCounts, bool allowR23Merge)
        {
            var weaponGroup = GetWeaponGroup(className);

            // Class 1 never merges
            if (IsClass1(className))
                return null;

            // M-classes excluded
            if (weaponGroup == "M")
                return null;

            // ── Weapon groups A, A_Opt, A_M, A_P, A_G, B: class 2 ↔ 3 ──
            // A-family subgroups (AM/AP/AG) merge internally only — never across subgroups
            // and never with the open A class. Same competence-ladder rule as A.
            if (weaponGroup == "A" || weaponGroup == "A_Opt" || weaponGroup == "A_M"
                || weaponGroup == "A_P" || weaponGroup == "A_G" || weaponGroup == "B")
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
        /// For A/A_Opt/B/R weapon groups: class 2 and 3 can merge with each other.
        /// </summary>
        private MergeSuggestion? BuildLevel23Suggestion(string className, int count,
            Dictionary<string, int> classCounts, string weaponGroup)
        {
            var level = GetCompetenceLevel(className);
            if (level != 2 && level != 3) return null;

            var partnerLevel = level == 2 ? 3 : 2;
            var partnerName = FormatLevelName(weaponGroup, partnerLevel);

            if (!classCounts.ContainsKey(partnerName)) return null;

            var weaponGroupLabel = weaponGroup switch
            {
                "A_Opt" => "A Opt",
                "A_M" => "AM",
                "A_P" => "AP",
                "A_G" => "AG",
                _ => weaponGroup
            };
            return new MergeSuggestion
            {
                SourceClass = className,
                SourceCount = count,
                DefaultTarget = partnerName,
                PossibleTargets = new List<string> { partnerName },
                RequiresAdminChoice = false,
                Reason = $"Klass {level} och {partnerLevel} i vapengrupp {weaponGroupLabel} får slås samman"
            };
        }

        /// <summary>
        /// For C/L weapon groups: merge logic from SHB 2026 §D.2.3 (Teknisk specifikation, §4)
        /// with the FR-104 "närmaste kompetensklass" fallback (clarified 2026-05-19):
        ///   Klass 1 (any category) — absolut sammanslagningsförbud (FR-102).
        ///   Dam C 1            → C 1 only (level 1 cannot cross to level 2/3).
        ///   Dam C 2 / Dam C 3  → priority 1 same-level open; fallback nearest-level open or Dam.
        ///   Junior C           → priority 1 open C-class, priority 2 Dam C-class.
        ///   Veteran Y / Ä      → priority 1 open C-class, priority 2 Dam C-class.
        ///   Open C 2 / C 3     → other open level (C2↔C3), then other-level Dam as fallback.
        /// </summary>
        private MergeSuggestion? BuildWeaponGroupCLSuggestion(string className, int count,
            Dictionary<string, int> classCounts, string weaponGroup)
        {
            // ── Dam C 1/2/3 ──
            if (IsDamClass(className))
            {
                var level = GetCompetenceLevel(className);
                if (level == null) return null;

                var primaryTarget = $"{weaponGroup}{level}";
                var primaryExists = classCounts.ContainsKey(primaryTarget);

                // Level 1: only same-level open, no fallback (FR-102 spärr).
                if (level == 1)
                {
                    if (!primaryExists) return null;
                    return new MergeSuggestion
                    {
                        SourceClass = className,
                        SourceCount = count,
                        DefaultTarget = primaryTarget,
                        PossibleTargets = new List<string> { primaryTarget },
                        RequiresAdminChoice = false,
                        Reason = $"Dam slås samman med motsvarande öppen klass ({primaryTarget})"
                    };
                }

                // Level 2 or 3: priority 1 same-level open, fallback to other-level open + Dam.
                var targets = new List<string>();
                if (primaryExists) targets.Add(primaryTarget);

                var otherLevel = level == 2 ? 3 : 2;
                var fallbackOpen = $"{weaponGroup}{otherLevel}";
                var fallbackDam = $"{weaponGroup}{otherLevel} Dam";
                if (classCounts.ContainsKey(fallbackOpen) && !targets.Contains(fallbackOpen))
                    targets.Add(fallbackOpen);
                if (classCounts.ContainsKey(fallbackDam) && !targets.Contains(fallbackDam))
                    targets.Add(fallbackDam);

                if (targets.Count == 0) return null;

                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = targets[0],
                    PossibleTargets = targets,
                    RequiresAdminChoice = targets.Count > 1,
                    Reason = primaryExists
                        ? $"Dam slås samman med motsvarande öppen klass ({primaryTarget})"
                        : $"Motsvarande öppen klass ({primaryTarget}) saknas — välj närmaste klass"
                };
            }

            // ── Junior / Vet Y / Vet Ä: priority 1 open C, priority 2 Dam C ──
            if (IsJuniorClass(className) || IsVetYClass(className) || IsVetAClass(className))
            {
                var openTargets = GetOpenClassTargets(weaponGroup, classCounts);
                var damTargets = GetDamClassTargets(weaponGroup, classCounts);
                var allTargets = openTargets.Concat(damTargets).ToList();
                if (!allTargets.Any()) return null;

                var defaultTarget = openTargets.FirstOrDefault() ?? damTargets.FirstOrDefault();

                var categoryLabel = IsVetAClass(className) ? "Veteran Äldre"
                                  : IsVetYClass(className) ? "Veteran Yngre"
                                  : "Junior";

                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = defaultTarget,
                    PossibleTargets = allTargets,
                    RequiresAdminChoice = true,
                    Reason = $"{categoryLabel} — välj öppen klass (prio 1) eller Dam-klass (prio 2)"
                };
            }

            // ── Open C/L class 2 or 3: FR-104 fallback — nearest competence class ──
            // (Klass 1 already filtered upstream by IsClass1 in BuildSuggestion.)
            var openLevel = GetCompetenceLevel(className);
            if (openLevel == 2 || openLevel == 3)
            {
                var otherLevel = openLevel == 2 ? 3 : 2;
                var targets = new List<string>();
                var partnerOpen = $"{weaponGroup}{otherLevel}";
                var partnerDam = $"{weaponGroup}{otherLevel} Dam";
                if (classCounts.ContainsKey(partnerOpen)) targets.Add(partnerOpen);
                if (classCounts.ContainsKey(partnerDam)) targets.Add(partnerDam);

                if (targets.Count == 0) return null;

                return new MergeSuggestion
                {
                    SourceClass = className,
                    SourceCount = count,
                    DefaultTarget = targets[0],
                    PossibleTargets = targets,
                    RequiresAdminChoice = targets.Count > 1,
                    Reason = $"Klass {openLevel} och {otherLevel} i vapengrupp {weaponGroup} får slås samman"
                };
            }

            return null;
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static List<string> GetOpenClassTargets(string weaponGroup, Dictionary<string, int> classCounts)
        {
            // Return open classes (1, 2, 3) that actually exist in this competition
            var candidates = new[] { $"{weaponGroup}1", $"{weaponGroup}2", $"{weaponGroup}3" };
            return candidates.Where(c => classCounts.ContainsKey(c)).ToList();
        }

        private static List<string> GetDamClassTargets(string weaponGroup, Dictionary<string, int> classCounts)
        {
            // Return Dam classes (level 1/2/3) that actually exist in this competition.
            // Used as priority-2 targets for Junior / Vet Y / Vet Ä per SHB 2026 §D.2.3.
            var candidates = new[] { $"{weaponGroup}1 Dam", $"{weaponGroup}2 Dam", $"{weaponGroup}3 Dam" };
            return candidates.Where(c => classCounts.ContainsKey(c)).ToList();
        }

        private static string GetWeaponGroup(string k)
        {
            // Authoritative lookup — handles names ("A Opt 2") and IDs ("A_opt_2") alike.
            // Returns "" for unknown inputs; callers must not fall back to first-character parsing
            // because that would mis-categorize A_opt classes as plain A.
            return ShootingClasses.GetWeaponClassCode(k);
        }

        /// <summary>
        /// Builds the display name for a given weapon group at a given competence level.
        /// Most groups use a compact form ("A2", "B3", "R2"); A_Opt uses a spaced form ("A Opt 2").
        /// </summary>
        private static string FormatLevelName(string weaponGroup, int level)
        {
            if (weaponGroup == "A_Opt") return $"A Opt {level}";
            // A-family subgroups use the compact display name (AM1/AP1/AG1) — not the
            // underscore-style ID. Mirrors how the registry lists them.
            if (weaponGroup == "A_M") return $"AM{level}";
            if (weaponGroup == "A_P") return $"AP{level}";
            if (weaponGroup == "A_G") return $"AG{level}";
            return $"{weaponGroup}{level}";
        }

        private static int? GetCompetenceLevel(string className)
        {
            // "C2 Dam" → 2, "A3" → 3, "C Vet Y" → null, "L1 Dam" → 1, "A Opt 2" → 2
            if (string.IsNullOrEmpty(className)) return null;

            // Compact form: digit right after the weapon group letter ("A3", "R2", "C1 Dam")
            if (className.Length >= 2 && char.IsDigit(className[1]))
                return className[1] - '0';

            // Spaced form: "A Opt 2" — trailing digit represents the level
            var last = className[className.Length - 1];
            if (char.IsDigit(last)) return last - '0';

            return null;
        }

        private static bool IsClass1(string className)
        {
            // Mirrors the original intent (class 1 never merges) but now uses
            // GetCompetenceLevel so it also catches "A Opt 1".
            return GetCompetenceLevel(className) == 1
                && !className.Contains("Vet")
                && !className.Contains("Jun");
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
            // Pairs like A2↔A3 (or C2↔C3) produce two mirror-image suggestions that describe
            // the same merger. Keep one. When one side is RICHER (more PossibleTargets — e.g.
            // C3 Dam has [C2, C2 Dam] vs C2's [C3 Dam]), drop the poorer side so the admin
            // keeps the full choice.
            var toRemove = new HashSet<MergeSuggestion>();
            foreach (var s in suggestions)
            {
                if (s.DefaultTarget == null || toRemove.Contains(s)) continue;
                var reverse = suggestions.FirstOrDefault(
                    o => o != s && o.SourceClass == s.DefaultTarget && o.DefaultTarget == s.SourceClass);
                if (reverse == null || toRemove.Contains(reverse)) continue;

                if (s.PossibleTargets.Count >= reverse.PossibleTargets.Count)
                    toRemove.Add(reverse);
                else
                    toRemove.Add(s);
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
                { "A1", 31 }, { "A2", 34 }, { "A3", 37 },
                { "A Opt 1", 38 }, { "A Opt 2", 39 }, { "A Opt 3", 40 },
                { "R1", 41 }, { "R2", 42 }, { "R3", 43 },
                { "L1", 50 }, { "L1 Dam", 51 }, { "L2", 52 }, { "L2 Dam", 53 },
                { "L3", 54 }, { "L3 Dam", 55 },
                { "L Vet Y", 56 }, { "L Vet Ä", 57 }, { "L Jun", 58 },
                // A-family subgroups sort at the end so existing positions stay stable.
                // Within the family they're grouped by subgroup then level.
                { "AM1", 60 }, { "AM2", 61 }, { "AM3", 62 },
                { "AP1", 63 }, { "AP2", 64 }, { "AP3", 65 },
                { "AG1", 66 }, { "AG2", 67 }, { "AG3", 68 }
            };
            return order.GetValueOrDefault(className, 999);
        }
    }
}
