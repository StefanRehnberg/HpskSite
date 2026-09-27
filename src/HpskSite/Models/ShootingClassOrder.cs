namespace HpskSite.Models
{
    /// <summary>
    /// Sort order for shooting classes — and for result-list GROUPS named after them ("C2+Dam",
    /// "A2+3") — computed from the registry: weapon group, then level, then category.
    ///
    /// ⚠️ This replaces five hand-written "class → number" tables that had drifted apart: the
    /// result list knew neither C Jun, L, M nor AM/AP/AG (they sorted last, alphabetically), the
    /// merge dialog put AM/AP/AG after L, and so on. The surfaces genuinely order weapon groups
    /// differently (a result list starts with C, series standings with A), so the weapon order is
    /// a parameter — but no surface lists classes any more.
    /// </summary>
    public static class ShootingClassOrder
    {
        /// <summary>Result lists, finals and the merge dialog: C first, then B, A, the A family, R, L, M.</summary>
        public static readonly WeaponClass[] ResultList =
        {
            WeaponClass.C, WeaponClass.B, WeaponClass.A, WeaponClass.A_Opt,
            WeaponClass.A_M, WeaponClass.A_P, WeaponClass.A_G, WeaponClass.R, WeaponClass.L, WeaponClass.M
        };

        /// <summary>Fältskytte result lists: C, L, B, the A family, R, M.</summary>
        public static readonly WeaponClass[] Faltskytte =
        {
            WeaponClass.C, WeaponClass.L, WeaponClass.B, WeaponClass.A, WeaponClass.A_Opt,
            WeaponClass.A_M, WeaponClass.A_P, WeaponClass.A_G, WeaponClass.R, WeaponClass.M
        };

        /// <summary>Series standings: A first (A family together), then B, C, R, M, L.</summary>
        public static readonly WeaponClass[] SeriesStandings =
        {
            WeaponClass.A, WeaponClass.A_Opt, WeaponClass.A_M, WeaponClass.A_P, WeaponClass.A_G,
            WeaponClass.B, WeaponClass.C, WeaponClass.R, WeaponClass.M, WeaponClass.L
        };

        public const int Unknown = 9999;

        /// <summary>
        /// Sort key. Within a weapon group: levels 1→3 (or 3→1 when <paramref name="levelDescending"/>),
        /// the dam class right after the open class of the same level, then the classes without a
        /// level — Vet Y, Vet Ä, Jun — and magnum by its number. Unknown strings get <see cref="Unknown"/>.
        /// A merged group sorts with the class its name starts with ("C2+Dam" with C2), just after it.
        /// </summary>
        public static int Key(string? classOrGroup, WeaponClass[] weaponOrder, bool levelDescending = false)
        {
            if (string.IsNullOrWhiteSpace(classOrGroup)) return Unknown;
            var text = classOrGroup.Trim();

            var sc = ShootingClasses.Resolve(text);
            var merged = false;

            // A weapon group or championship category rather than a class — "C", "A Opt",
            // "C Dam", "C Vet Y". Sorts at the head of its weapon group, before the classes.
            if (sc == null && TryParseCategoryGroup(text, out var groupWeapon, out var groupCategory))
            {
                var gi = Array.IndexOf(weaponOrder, groupWeapon);
                if (gi < 0) gi = weaponOrder.Length;
                return gi * 1000 + CategorySlot(groupCategory) * 2;
            }

            if (sc == null)
            {
                // "C2+Dam" → C2, "A2+3" → A2; failing that, the longest registry name the group starts with.
                var head = text.Split('+')[0].Trim();
                sc = ShootingClasses.Resolve(head)
                     ?? ShootingClasses.All
                         .Where(c => text.StartsWith(c.Name, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(c => c.Name.Length)
                         .FirstOrDefault();
                merged = sc != null;
            }
            if (sc == null) return Unknown;

            var weaponIndex = Array.IndexOf(weaponOrder, sc.Weapon);
            if (weaponIndex < 0) weaponIndex = weaponOrder.Length;

            int levelSlot;
            if (sc.Weapon == WeaponClass.M)
                levelSlot = int.TryParse(sc.Id.Substring(1), out var m) ? m : 9; // M1–M9 by number
            else if (sc.Level is int level)
                levelSlot = levelDescending ? 4 - level : level;
            else
                levelSlot = 4; // classes without a level after the levelled ones

            return weaponIndex * 1000 + levelSlot * 20 + CategorySlot(sc.Category) * 2 + (merged ? 1 : 0);
        }

        private static int CategorySlot(ClassCategory category) => category switch
        {
            ClassCategory.Open => 0,
            ClassCategory.Dam => 1,
            ClassCategory.VeteranYounger => 2,
            ClassCategory.VeteranOlder => 3,
            ClassCategory.Junior => 4,
            _ => 5
        };

        /// <summary>"C" / "A Opt" / "C Dam" / "C Vet Ä" → weapon group + category, from the registry's labels.</summary>
        private static bool TryParseCategoryGroup(string text, out WeaponClass weapon, out ClassCategory category)
        {
            weapon = default;
            category = ClassCategory.Open;
            foreach (var w in Enum.GetValues<WeaponClass>().OrderByDescending(w => ShootingClasses.WeaponGroupLabel(w).Length))
            {
                var label = ShootingClasses.WeaponGroupLabel(w);
                if (!text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = text.Substring(label.Length).Trim();
                if (rest.Length == 0) { weapon = w; return true; }
                foreach (var c in Enum.GetValues<ClassCategory>())
                {
                    if (c != ClassCategory.Open
                        && string.Equals(rest, ShootingClasses.CategoryLabel(c), StringComparison.OrdinalIgnoreCase))
                    {
                        weapon = w;
                        category = c;
                        return true;
                    }
                }
                return false;
            }
            return false;
        }
    }
}
