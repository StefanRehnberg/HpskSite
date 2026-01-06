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
            return All.FirstOrDefault(sc => sc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public static List<ShootingClass> GetActive()
        {
            // For now, all classes are active. Could add IsActive property later if needed.
            return All.ToList();
        }
    }

    public enum WeaponClass
    {
        /// <summary>
        /// Tjänstevapen
        /// </summary>
        A,
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
