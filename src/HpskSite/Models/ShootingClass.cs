namespace HpskSite.Models
{
    public class ShootingClass
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public WeaponClass Weapon { get; set; }

        public ShootingClass(string id, string name, string description, WeaponClass weapon)
        {
            Id = id;
            Name = name;
            Description = description;
            Weapon = weapon;
        }
    }

    public static class ShootingClasses
    {
        public static readonly List<ShootingClass> All = new List<ShootingClass>
        {
            new ShootingClass("A1", "A1", "Vapenklass A för nybörjare", WeaponClass.A),
            new ShootingClass("A2", "A2", "Vapenklass A för Guldmärkesskyttar", WeaponClass.A),
            new ShootingClass("A3", "A3", "Vapenklass A för Riksmästare", WeaponClass.A),
            new ShootingClass("A_opt_1", "A Opt 1", "Vapenklass A optisk för nybörjare", WeaponClass.A_Opt),
            new ShootingClass("A_opt_2", "A Opt 2", "Vapenklass A optisk för Guldmärkesskyttar", WeaponClass.A_Opt),
            new ShootingClass("A_opt_3", "A Opt 3", "Vapenklass A optisk för Riksmästare", WeaponClass.A_Opt),
            // Optional A-family subgroups, only offered when a competition explicitly opts in.
            // Display-grouped as their own weapon classes (AM/AP/AG never merge with each other or
            // with A), but pooled into a single "A family" ranking for percentage-based standard
            // medal calculation per SPSF rules. Level (1-3) follows the same competence ladder as
            // regular A, so existing precisionShooterClass / handicap settings apply unchanged.
            new ShootingClass("A_m_1", "AM1", "Vapenklass AM (militära pistoler, äldre modell) för nybörjare", WeaponClass.A_M),
            new ShootingClass("A_m_2", "AM2", "Vapenklass AM (militära pistoler, äldre modell) för Guldmärkesskyttar", WeaponClass.A_M),
            new ShootingClass("A_m_3", "AM3", "Vapenklass AM (militära pistoler, äldre modell) för Riksmästare", WeaponClass.A_M),
            new ShootingClass("A_p_1", "AP1", "Vapenklass AP (fickmodell, t.ex. Walther PP/PPK) för nybörjare", WeaponClass.A_P),
            new ShootingClass("A_p_2", "AP2", "Vapenklass AP (fickmodell, t.ex. Walther PP/PPK) för Guldmärkesskyttar", WeaponClass.A_P),
            new ShootingClass("A_p_3", "AP3", "Vapenklass AP (fickmodell, t.ex. Walther PP/PPK) för Riksmästare", WeaponClass.A_P),
            new ShootingClass("A_g_1", "AG1", "Vapenklass AG (moderna tjänstepistoler, t.ex. Glock 17/19) för nybörjare", WeaponClass.A_G),
            new ShootingClass("A_g_2", "AG2", "Vapenklass AG (moderna tjänstepistoler, t.ex. Glock 17/19) för Guldmärkesskyttar", WeaponClass.A_G),
            new ShootingClass("A_g_3", "AG3", "Vapenklass AG (moderna tjänstepistoler, t.ex. Glock 17/19) för Riksmästare", WeaponClass.A_G),
            new ShootingClass("B1", "B1", "Vapenklass B för nybörjare", WeaponClass.B),
            new ShootingClass("B2", "B2", "Vapenklass B för Guldmärkesskyttar", WeaponClass.B),
            new ShootingClass("B3", "B3", "Vapenklass B för Riksmästare", WeaponClass.B),
            new ShootingClass("C1", "C1", "Vapenklass C öppen för nybörjare", WeaponClass.C),
            new ShootingClass("C2", "C2", "Vapenklass C öppen för Guldmärkesskyttar", WeaponClass.C),
            new ShootingClass("C3", "C3", "Vapenklass C öppen för Riksmästare", WeaponClass.C),
            new ShootingClass("C_Vet_Y", "C Vet Y", "Vapenklass C Veteran Yngre", WeaponClass.C),
            new ShootingClass("C_Vet_A", "C Vet Ä", "Vapenklass C Veteran Äldre", WeaponClass.C),
            new ShootingClass("C_Jun", "C Jun", "Vapenklass C Juniorer", WeaponClass.C),
            new ShootingClass("C1_Dam", "C1 Dam", "Vapenklass C Dam för nybörjare", WeaponClass.C),
            new ShootingClass("C2_Dam", "C2 Dam", "Vapenklass C Dam för Guldmärkesskyttar", WeaponClass.C),
            new ShootingClass("C3_Dam", "C3 Dam", "Vapenklass C Dam för Riksmästare", WeaponClass.C),
            new ShootingClass("R1", "R1", "Vapenklass R för nybörjare", WeaponClass.R),
            new ShootingClass("R2", "R2", "Vapenklass R för Guldmärkesskyttar", WeaponClass.R),
            new ShootingClass("R3", "R3", "Vapenklass R för Riksmästare", WeaponClass.R),
            new ShootingClass("M1", "M1", "SA Revolver 41-44 Magnum", WeaponClass.M),
            new ShootingClass("M2", "M2", "DA Revolver 41-44 Magnum", WeaponClass.M),
            new ShootingClass("M3", "M3", "SA Revolver 357 Magnum", WeaponClass.M),
            new ShootingClass("M4", "M4", "DA Revolver 357 Magnum", WeaponClass.M),
            new ShootingClass("M5", "M5", "Fri 9mm-455", WeaponClass.M),
            new ShootingClass("M6", "M6", "Pistol 9mm-455", WeaponClass.M),
            new ShootingClass("M7", "M7", "Revolver 357-44", WeaponClass.M),
            new ShootingClass("M8", "M8", "Revolver 38-45", WeaponClass.M),
            new ShootingClass("M9", "M9", "Vapenklass A", WeaponClass.M),
            new ShootingClass("L1", "L1", "Luftpistol för nybörjare", WeaponClass.L),
            new ShootingClass("L2", "L2", "Luftpistol för Guldmärkesskyttar", WeaponClass.L),
            new ShootingClass("L3", "L3", "Luftpistol för Riksmästare", WeaponClass.L),
            new ShootingClass("L_Vet_Y", "L Vet Y", "Luftpistol Veteran Yngre", WeaponClass.L),
            new ShootingClass("L_Vet_A", "L Vet Ä", "Luftpistol Veteran Äldre", WeaponClass.L),
            new ShootingClass("L_Jun", "L Jun", "Luftpistol Juniorer", WeaponClass.L),
            new ShootingClass("L1_Dam", "L1 Dam", "Luftpistol Dam för nybörjare", WeaponClass.L),
            new ShootingClass("L2_Dam", "L2 Dam", "Luftpistol Dam för Guldmärkesskyttar", WeaponClass.L),
            new ShootingClass("L3_Dam", "L3 Dam", "Luftpistol Dam för Riksmästare", WeaponClass.L),
        };

        public static ShootingClass? GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return All.FirstOrDefault(sc => sc.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        public static ShootingClass? GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return All.FirstOrDefault(sc => sc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

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
