namespace HpskSite.Models
{
    /// <summary>
    /// Presentation of the class picker: the order of the weapon-group sections and a short
    /// explanation under the heading. Holds NO classes — the classes in each section come from
    /// <see cref="ShootingClasses.ForWeapon"/>, so a class added to the registry appears in the
    /// picker without touching any view.
    /// </summary>
    public static class ShootingClassPickerLayout
    {
        public static readonly WeaponClass[] SectionOrder =
        {
            WeaponClass.A, WeaponClass.A_Opt, WeaponClass.A_M, WeaponClass.A_P, WeaponClass.A_G,
            WeaponClass.B, WeaponClass.C, WeaponClass.R, WeaponClass.L, WeaponClass.M
        };

        public static string Subtitle(WeaponClass weapon) => weapon switch
        {
            WeaponClass.A_M => "militära pistoler av äldre modell",
            WeaponClass.A_P => "pistoler av fickmodell",
            WeaponClass.A_G => "moderna tjänstepistoler",
            _ => ""
        };

        /// <summary>What the weapon group is, in a few words — for optgroup labels ("C — Kal. 22").</summary>
        public static string WeaponDescription(WeaponClass weapon) => weapon switch
        {
            WeaponClass.A => "Tjänstevapen",
            WeaponClass.A_Opt => "Optisk",
            WeaponClass.A_M => "Militära pistoler, äldre modell",
            WeaponClass.A_P => "Fickmodell",
            WeaponClass.A_G => "Moderna tjänstepistoler",
            WeaponClass.B => "Kal. 32-45",
            WeaponClass.C => "Kal. 22",
            WeaponClass.R => "Revolver",
            WeaponClass.M => "Magnum",
            WeaponClass.L => "Luftpistol",
            _ => ""
        };

        /// <summary>Weapon groups shot in fältskytte, in the order the entry form lists them.</summary>
        public static readonly WeaponClass[] FaltskytteOrder =
        {
            WeaponClass.C, WeaponClass.A, WeaponClass.A_Opt, WeaponClass.A_M, WeaponClass.A_P,
            WeaponClass.A_G, WeaponClass.B, WeaponClass.R, WeaponClass.M
        };
    }
}
