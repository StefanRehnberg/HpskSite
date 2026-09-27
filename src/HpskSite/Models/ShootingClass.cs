namespace HpskSite.Models
{
    /// <summary>
    /// ⚠️⚠️ THE ONE PLACE SHOOTING CLASSES ARE DEFINED. Every list, dropdown, badge, rule and
    /// grouping reads this registry — server side through <see cref="ShootingClasses"/>, client side
    /// through <c>_ShootingClassesBootstrap.cshtml</c> (window.HpskShootingClasses /
    /// getShootingClassName / getShootingClassCategory / getShootingClassLevel).
    ///
    /// Never hardcode a list of class ids or names, never parse an id ("_Dam", "_Vet_", charAt(0)),
    /// and never show an Id to a person — show <see cref="Name"/>. Id and Name are identical for
    /// C1/C2/C3 and differ for every class with a suffix, so a surface that shows or compares the
    /// Id looks right in testing and breaks only the dam, veteran, junior and optic classes.
    /// <c>ShootingClassRegistryGuardTests</c> fails the build on a new hardcoded class literal.
    /// </summary>
    public class ShootingClass
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public WeaponClass Weapon { get; set; }

        /// <summary>Open, Dam, Veteran yngre/äldre or Junior. Only C and L have anything but Open.</summary>
        public ClassCategory Category { get; set; }

        /// <summary>
        /// Competence level 1–3 (nybörjare / guldmärkesskytt / riksmästare), or null when the class
        /// has no level: the veteran and junior classes, and ⚠️ M1–M9, which are different WEAPONS,
        /// not competence levels.
        /// </summary>
        public int? Level { get; set; }

        public ShootingClass(string id, string name, string description, WeaponClass weapon,
            ClassCategory category = ClassCategory.Open, int? level = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Weapon = weapon;
            Category = category;
            Level = level;
        }

        /// <summary>True for Veteran yngre and Veteran äldre — one category where registration rules are concerned.</summary>
        public bool IsVeteran => Category is ClassCategory.VeteranYounger or ClassCategory.VeteranOlder;
    }

    /// <summary>Category within a weapon group. Only C and L use anything but <see cref="Open"/>.</summary>
    public enum ClassCategory
    {
        Open,
        Dam,
        VeteranYounger,
        VeteranOlder,
        Junior
    }

    public static class ShootingClasses
    {
        private const ClassCategory Open = ClassCategory.Open;
        private const ClassCategory Dam = ClassCategory.Dam;
        private const ClassCategory VetY = ClassCategory.VeteranYounger;
        private const ClassCategory VetA = ClassCategory.VeteranOlder;
        private const ClassCategory Jun = ClassCategory.Junior;

        public static readonly List<ShootingClass> All = new List<ShootingClass>
        {
            new ShootingClass("A1", "A1", "Vapenklass A för nybörjare", WeaponClass.A, Open, 1),
            new ShootingClass("A2", "A2", "Vapenklass A för Guldmärkesskyttar", WeaponClass.A, Open, 2),
            new ShootingClass("A3", "A3", "Vapenklass A för Riksmästare", WeaponClass.A, Open, 3),
            new ShootingClass("A_opt_1", "A Opt 1", "Vapenklass A optisk för nybörjare", WeaponClass.A_Opt, Open, 1),
            new ShootingClass("A_opt_2", "A Opt 2", "Vapenklass A optisk för Guldmärkesskyttar", WeaponClass.A_Opt, Open, 2),
            new ShootingClass("A_opt_3", "A Opt 3", "Vapenklass A optisk för Riksmästare", WeaponClass.A_Opt, Open, 3),
            // Optional A-family subgroups, only offered when a competition explicitly opts in.
            // Display-grouped as their own weapon classes (AM/AP/AG never merge with each other or
            // with A), but pooled into a single "A family" ranking for percentage-based standard
            // medal calculation per SPSF rules. Level (1-3) follows the same competence ladder as
            // regular A, so existing precisionShooterClass / handicap settings apply unchanged.
            new ShootingClass("A_m_1", "AM1", "Vapenklass AM (militära pistoler, äldre modell) för nybörjare", WeaponClass.A_M, Open, 1),
            new ShootingClass("A_m_2", "AM2", "Vapenklass AM (militära pistoler, äldre modell) för Guldmärkesskyttar", WeaponClass.A_M, Open, 2),
            new ShootingClass("A_m_3", "AM3", "Vapenklass AM (militära pistoler, äldre modell) för Riksmästare", WeaponClass.A_M, Open, 3),
            new ShootingClass("A_p_1", "AP1", "Vapenklass AP (fickmodell, t.ex. Walther PP/PPK) för nybörjare", WeaponClass.A_P, Open, 1),
            new ShootingClass("A_p_2", "AP2", "Vapenklass AP (fickmodell, t.ex. Walther PP/PPK) för Guldmärkesskyttar", WeaponClass.A_P, Open, 2),
            new ShootingClass("A_p_3", "AP3", "Vapenklass AP (fickmodell, t.ex. Walther PP/PPK) för Riksmästare", WeaponClass.A_P, Open, 3),
            new ShootingClass("A_g_1", "AG1", "Vapenklass AG (moderna tjänstepistoler, t.ex. Glock 17/19) för nybörjare", WeaponClass.A_G, Open, 1),
            new ShootingClass("A_g_2", "AG2", "Vapenklass AG (moderna tjänstepistoler, t.ex. Glock 17/19) för Guldmärkesskyttar", WeaponClass.A_G, Open, 2),
            new ShootingClass("A_g_3", "AG3", "Vapenklass AG (moderna tjänstepistoler, t.ex. Glock 17/19) för Riksmästare", WeaponClass.A_G, Open, 3),
            new ShootingClass("B1", "B1", "Vapenklass B för nybörjare", WeaponClass.B, Open, 1),
            new ShootingClass("B2", "B2", "Vapenklass B för Guldmärkesskyttar", WeaponClass.B, Open, 2),
            new ShootingClass("B3", "B3", "Vapenklass B för Riksmästare", WeaponClass.B, Open, 3),
            new ShootingClass("C1", "C1", "Vapenklass C öppen för nybörjare", WeaponClass.C, Open, 1),
            new ShootingClass("C2", "C2", "Vapenklass C öppen för Guldmärkesskyttar", WeaponClass.C, Open, 2),
            new ShootingClass("C3", "C3", "Vapenklass C öppen för Riksmästare", WeaponClass.C, Open, 3),
            new ShootingClass("C_Vet_Y", "C Vet Y", "Vapenklass C Veteran Yngre", WeaponClass.C, VetY),
            new ShootingClass("C_Vet_A", "C Vet Ä", "Vapenklass C Veteran Äldre", WeaponClass.C, VetA),
            new ShootingClass("C_Jun", "C Jun", "Vapenklass C Juniorer", WeaponClass.C, Jun),
            new ShootingClass("C1_Dam", "C1 Dam", "Vapenklass C Dam för nybörjare", WeaponClass.C, Dam, 1),
            new ShootingClass("C2_Dam", "C2 Dam", "Vapenklass C Dam för Guldmärkesskyttar", WeaponClass.C, Dam, 2),
            new ShootingClass("C3_Dam", "C3 Dam", "Vapenklass C Dam för Riksmästare", WeaponClass.C, Dam, 3),
            new ShootingClass("R1", "R1", "Vapenklass R för nybörjare", WeaponClass.R, Open, 1),
            new ShootingClass("R2", "R2", "Vapenklass R för Guldmärkesskyttar", WeaponClass.R, Open, 2),
            new ShootingClass("R3", "R3", "Vapenklass R för Riksmästare", WeaponClass.R, Open, 3),
            // ⚠️ M1–M9 are different weapons, not competence levels — Level stays null.
            new ShootingClass("M1", "M1", "SA Revolver 41-44 Magnum", WeaponClass.M),
            new ShootingClass("M2", "M2", "DA Revolver 41-44 Magnum", WeaponClass.M),
            new ShootingClass("M3", "M3", "SA Revolver 357 Magnum", WeaponClass.M),
            new ShootingClass("M4", "M4", "DA Revolver 357 Magnum", WeaponClass.M),
            new ShootingClass("M5", "M5", "Fri 9mm-455", WeaponClass.M),
            new ShootingClass("M6", "M6", "Pistol 9mm-455", WeaponClass.M),
            new ShootingClass("M7", "M7", "Revolver 357-44", WeaponClass.M),
            new ShootingClass("M8", "M8", "Revolver 38-45", WeaponClass.M),
            new ShootingClass("M9", "M9", "Vapenklass A", WeaponClass.M),
            new ShootingClass("L1", "L1", "Luftpistol för nybörjare", WeaponClass.L, Open, 1),
            new ShootingClass("L2", "L2", "Luftpistol för Guldmärkesskyttar", WeaponClass.L, Open, 2),
            new ShootingClass("L3", "L3", "Luftpistol för Riksmästare", WeaponClass.L, Open, 3),
            new ShootingClass("L_Vet_Y", "L Vet Y", "Luftpistol Veteran Yngre", WeaponClass.L, VetY),
            new ShootingClass("L_Vet_A", "L Vet Ä", "Luftpistol Veteran Äldre", WeaponClass.L, VetA),
            new ShootingClass("L_Jun", "L Jun", "Luftpistol Juniorer", WeaponClass.L, Jun),
            new ShootingClass("L1_Dam", "L1 Dam", "Luftpistol Dam för nybörjare", WeaponClass.L, Dam, 1),
            new ShootingClass("L2_Dam", "L2 Dam", "Luftpistol Dam för Guldmärkesskyttar", WeaponClass.L, Dam, 2),
            new ShootingClass("L3_Dam", "L3 Dam", "Luftpistol Dam för Riksmästare", WeaponClass.L, Dam, 3),
        };

        /// <summary>Id or Name → the class, or null. The one resolver every helper below uses.</summary>
        public static ShootingClass? Resolve(string? idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return null;
            var key = idOrName.Trim();
            return GetById(key) ?? GetByName(key);
        }

        /// <summary>The classes of one weapon group, in registry order.</summary>
        public static IEnumerable<ShootingClass> ForWeapon(WeaponClass weapon) =>
            All.Where(sc => sc.Weapon == weapon);

        /// <summary>
        /// The classes of one weapon group in the given categories — grouped in the order the
        /// categories are passed, registry order within each.
        /// </summary>
        public static IEnumerable<ShootingClass> For(WeaponClass weapon, params ClassCategory[] categories) =>
            categories.Distinct().SelectMany(c => All.Where(sc => sc.Weapon == weapon && sc.Category == c));

        /// <summary>Category of a class given as Id or Name; null when unknown.</summary>
        public static ClassCategory? GetCategory(string? idOrName) => Resolve(idOrName)?.Category;

        /// <summary>Competence level 1–3 of a class given as Id or Name; null when it has none or is unknown.</summary>
        public static int? GetLevel(string? idOrName) => Resolve(idOrName)?.Level;

        /// <summary>
        /// Human label of a weapon group — "A", "A Opt", "AM", "AP", "AG", "B", "C", "R", "M", "L".
        /// ⚠️ The enum names (A_Opt, A_M…) are code, not labels; never show them.
        /// </summary>
        public static string WeaponGroupLabel(WeaponClass weapon) => weapon switch
        {
            WeaponClass.A_Opt => "A Opt",
            WeaponClass.A_M => "AM",
            WeaponClass.A_P => "AP",
            WeaponClass.A_G => "AG",
            _ => weapon.ToString()
        };

        /// <summary>
        /// Label of a weapon-group CODE as stored ("A_Opt" → "A Opt"), including a mixed patrol's
        /// combined code ("A+A_Opt" → "A+A Opt"). Unknown parts are returned unchanged.
        /// </summary>
        public static string WeaponGroupsText(string? codes)
        {
            if (string.IsNullOrWhiteSpace(codes)) return "";
            return string.Join("+", codes.Split('+').Select(part =>
            {
                var p = part.Trim();
                return Enum.TryParse<WeaponClass>(p, ignoreCase: true, out var w) ? WeaponGroupLabel(w) : p;
            }));
        }

        /// <summary>Swedish label of a category as it appears in class names: "Dam", "Vet Y", "Vet Ä", "Jun", or "" for open.</summary>
        public static string CategoryLabel(ClassCategory category) => category switch
        {
            ClassCategory.Dam => "Dam",
            ClassCategory.VeteranYounger => "Vet Y",
            ClassCategory.VeteranOlder => "Vet Ä",
            ClassCategory.Junior => "Jun",
            _ => ""
        };

        /// <summary>
        /// The C/L sub-category of a class — "Dam", "Jun", "Vet Y", "Vet Ä" — or null for an open
        /// class. Returns false (and null) when the registry does not know the string, so a caller
        /// holding a combined group name ("C2+Dam") can apply its own fallback.
        /// </summary>
        public static bool TryGetSubCategoryLabel(string? idOrName, out string? label)
        {
            var known = Resolve(idOrName);
            if (known == null) { label = null; return false; }
            label = known.Category == ClassCategory.Open ? null : CategoryLabel(known.Category);
            return true;
        }

        public static ShootingClass? GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return All.FirstOrDefault(sc => sc.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static ShootingClass? GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return All.FirstOrDefault(sc => sc.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Display name for a class given as Id or Name — what a PERSON should read. Unknown input
        /// (Springskytte's "C-H 50", a merged "C2+Dam") is returned trimmed and unchanged.
        /// Same thing as <see cref="ToCanonicalName"/>; this name says why you are calling it.
        /// </summary>
        public static string DisplayName(string? idOrName) => ToCanonicalName(idOrName);

        public static List<ShootingClass> GetActive()
        {
            // For now, all classes are active. Could add IsActive property later if needed.
            return All.ToList();
        }

        /// <summary>
        /// Authoritative lookup of a shooting class's weapon group.
        /// Accepts either the Id ("A_opt_1") or the display Name ("A Opt 1").
        /// Returns null when the input is unknown — callers must not fall back to string parsing.
        /// </summary>
        public static WeaponClass? GetWeaponClass(string? shootingClassIdOrName)
        {
            if (string.IsNullOrWhiteSpace(shootingClassIdOrName)) return null;
            return (GetById(shootingClassIdOrName) ?? GetByName(shootingClassIdOrName))?.Weapon;
        }

        /// <summary>
        /// Returns the weapon-class code as a string (e.g., "A", "B", "C", "A_Opt").
        /// Use this instead of <c>id.Substring(0, 1)</c> / <c>id[0]</c> / <c>id.StartsWith("A")</c>
        /// so A_opt classes are correctly categorized as their own weapon group.
        /// Returns the empty string when the input is unknown.
        /// </summary>
        public static string GetWeaponClassCode(string? shootingClassIdOrName)
        {
            var weapon = GetWeaponClass(shootingClassIdOrName);
            return weapon?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// The canonical form a shooting class is STORED in on a result row: the display
        /// <see cref="ShootingClass.Name"/> ("C Vet Y"), never the Id ("C_Vet_Y").
        ///
        /// ⚠️ Id and Name are the same string for C1/C2/C3 and differ for every class with a
        /// suffix — C_Vet_Y/"C Vet Y", C_Vet_A/"C Vet Ä", A_opt_1/"A Opt 1". A surface that
        /// stores the Id therefore looks correct in testing and only splits the veteran, dam,
        /// junior and optic classes. That is the 2026-08-25 klubbmästerskap bug: the finals
        /// entry screen took the class straight from the finals start list JSON (Id form) while
        /// the qualifying screen took it from GetShootersForResultsEntry (Name form), so
        /// grouping by (MemberId, ShootingClass) put a veteran's grundserier and finalserier in
        /// two rows that both DISPLAYED "C Vet Y".
        ///
        /// Call this on every class string crossing into or out of a result row. Unknown input
        /// is returned trimmed and unchanged — never dropped, so a class we do not recognise
        /// still groups with itself.
        /// </summary>
        public static string ToCanonicalName(string? shootingClassIdOrName)
        {
            if (string.IsNullOrWhiteSpace(shootingClassIdOrName)) return string.Empty;
            var key = shootingClassIdOrName.Trim();
            return (GetById(key) ?? GetByName(key))?.Name ?? key;
        }

        /// <summary>
        /// Case-insensitive grouping/lookup key that folds Id and Name onto the same value.
        /// Use wherever a class string is a dictionary key or a GroupBy key.
        /// </summary>
        public static string NormalizeKey(string? shootingClassIdOrName) =>
            ToCanonicalName(shootingClassIdOrName).ToLowerInvariant();
    }

    public enum WeaponClass
    {
        /// <summary>
        /// Tjänstevapen
        /// </summary>
        A,
        /// <summary>
        /// Tjänstevapen med optiskt riktmedel
        /// </summary>
        A_Opt,
        /// <summary>
        /// AM: Militära pistoler av äldre modell (m/07, m/40, P08)
        /// — A-family subgroup, separate display class, pooled with A for medal calc.
        /// </summary>
        A_M,
        /// <summary>
        /// AP: Pistoler av fickmodell (Walther PP, PPK)
        /// — A-family subgroup, separate display class, pooled with A for medal calc.
        /// </summary>
        A_P,
        /// <summary>
        /// AG: Moderna tjänstepistoler med fasta riktmedel (Glock 17, 19)
        /// — A-family subgroup, separate display class, pooled with A for medal calc.
        /// </summary>
        A_G,
        /// <summary>
        /// Kal. 32-45
        /// </summary>
        B,
        /// <summary>
        /// Kal. 22
        /// </summary>
        C,
        /// <summary>
        /// Revolver
        /// </summary>
        R,
        /// <summary>
        /// Magnum
        /// </summary>
        M,
        /// <summary>
        /// Luftpistol
        /// </summary>
        L
    }

}
