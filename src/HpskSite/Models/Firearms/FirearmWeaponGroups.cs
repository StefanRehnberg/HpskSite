using HpskSite.Models;

namespace HpskSite.Models.Firearms
{
    /// <summary>
    /// Vad <c>Firearm.WeaponClass</c> får innehålla, och vad väljarna erbjuder.
    ///
    /// <para><b>Vapengruppens kod PLUS magnumklasserna.</b> För A, B, C, R och L beskriver
    /// gruppkoden vapnet fullständigt (C = kal .22), och siffran efteråt är <b>skyttens
    /// kompetensnivå</b> — C1/C2/C3 är samma pistol, olika skytt. Att lägga den i ett VAPENfält
    /// vore ett kategorifel: nivån ändras när skytten avancerar, vapnet gör det inte.</para>
    ///
    /// <para><b>⚠️ MAGNUM ÄR TVÄRTOM.</b> M1–M9 är inte nivåer utan <b>olika vapen</b> — SA respektive
    /// DA revolver 41-44, 357, fri 9mm, pistol 9mm-455. Gruppkoden <c>M</c> identifierar därför
    /// ingenting: en magnumlicens gäller en bestämd revolver, och M1 och M2 är två skilda vapen.
    /// Utan klasserna i väljaren kunde ett magnumvapen inte beskrivas alls. Rapporterat 2026-09-02.</para>
    ///
    /// <para><b>⚠️ GRUPPEN HÄRLEDS, LAGRAS INTE TVÅ GÅNGER.</b> Allt som behöver veta gruppen ska
    /// gå via <see cref="ShootingClasses.GetWeaponClassCode"/> (eller <c>window.getWeaponClassCode</c>
    /// i klienten), som svarar <c>"M"</c> för <c>"M2"</c> och <c>"C"</c> för <c>"C"</c>. Det är vad
    /// som håller kopplingen till <c>MemberActivityEntry.WeaponGroups</c> hel — jämför aldrig
    /// <c>Firearm.WeaponClass</c> literalt mot en gruppkod, för då slutar magnumvapnen matcha tyst.</para>
    /// </summary>
    public static class FirearmWeaponGroups
    {
        /// <summary>
        /// Väljarens alternativ, i ordning: grupperna först, magnumklasserna sedan.
        ///
        /// <para><c>Value</c> är det som lagras, <c>Label</c> det som visas. Magnumraderna bär sin
        /// beskrivning ("M2 — DA Revolver 41-44 Magnum"), eftersom koden ensam inte säger vilket
        /// vapen det är — och det är hela skälet de finns i listan.</para>
        /// </summary>
        public static IReadOnlyList<FirearmWeaponGroupOption> Options { get; } = Build();

        private static List<FirearmWeaponGroupOption> Build()
        {
            var list = new List<FirearmWeaponGroupOption>();

            foreach (var name in Enum.GetNames<WeaponClass>())
            {
                list.Add(new FirearmWeaponGroupOption(name, name));
            }

            // ⚠️ Ur ShootingClasses, aldrig en handskriven M1..M9-lista. Läggs en magnumklass till
            // i registret ska den dyka upp här av sig själv; en kopia hade tystnat i stället.
            foreach (var sc in ShootingClasses.All.Where(c => c.Weapon == WeaponClass.M))
            {
                var label = string.IsNullOrWhiteSpace(sc.Description)
                    ? sc.Name
                    : $"{sc.Name} — {sc.Description}";
                list.Add(new FirearmWeaponGroupOption(sc.Id, label));
            }

            return list;
        }

        /// <summary>
        /// Är värdet något vapenfältet får bära? Tomt är tillåtet — vapengruppen är frivillig.
        ///
        /// <para>Ersätter det tidigare <c>Enum.TryParse&lt;WeaponClass&gt;</c>, som avvisade
        /// <c>"M2"</c> och därmed gjorde magnumklasserna osparbara även om väljaren erbjöd dem.</para>
        /// </summary>
        public static bool IsValid(string? value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return true;
            return Options.Any(o => string.Equals(o.Value, v, StringComparison.Ordinal));
        }

        /// <summary>
        /// Gruppkoden för ett lagrat värde: <c>"M2"</c> → <c>"M"</c>, <c>"C"</c> → <c>"C"</c>.
        /// Använd den när något ska jämföras mot en vapengrupp, aldrig det lagrade värdet rakt av.
        /// </summary>
        public static string GroupCodeOf(string? value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return "";

            // En gruppkod är sig själv. Kontrolleras FÖRST, eftersom GetWeaponClassCode svarar tomt
            // på en ren gruppkod (den slår upp klasser, inte grupper).
            if (Enum.TryParse<WeaponClass>(v, out var wc)) return wc.ToString();

            var derived = ShootingClasses.GetWeaponClassCode(v);
            return string.IsNullOrEmpty(derived) ? v : derived;
        }
    }

    /// <summary>Ett alternativ i vapengruppväljaren.</summary>
    public readonly record struct FirearmWeaponGroupOption(string Value, string Label);
}
