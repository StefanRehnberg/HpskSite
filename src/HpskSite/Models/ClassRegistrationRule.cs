using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models
{
    /// <summary>
    /// Vilka klasser EN skytt får stå i på EN tävling. Enda platsen regeln bor — anmälan,
    /// registreringsbordet, redigera anmälan och startlistans klassbyte/tillägg frågar alla här.
    ///
    /// <para>Regeln (samma som anmälningsformuläret alltid haft i klienten):</para>
    /// <list type="bullet">
    /// <item>Samma klass får aldrig förekomma två gånger — det vore samma start två gånger.</item>
    /// <item>En klass per vapengrupp. A, A Opt, AM, AP, AG, B, R och M är var sin grupp.</item>
    /// <item>C och L: en klass, eller — när tävlingen har <see cref="PropertyAlias"/> påslagen —
    /// högst två ur OLIKA kategorier (öppen, dam, veteran, junior).</item>
    /// </list>
    ///
    /// <para>⚠️ Fram till 2026-09-25 fanns regeln BARA i klienten. Servern tog emot vad som helst,
    /// registreringsbordets bockrutor lät C2 och C Vet Y stå ihop oavsett inställning, och
    /// startlistans klassbyte kunde ge samma skytt samma klass i två skjutlag (Michael Henriksson,
    /// Åmåls PK). Den dåvarande servermetoden <c>FindWeaponClassConflicts</c> anropades av ingen.</para>
    ///
    /// <para>Klassen får anges som id ("C_Vet_Y") ELLER visningsnamn ("C Vet Y") — startlistan och
    /// resultatraderna bär namnet, anmälan id:t. En okänd klass räknas bara mot sig själv.</para>
    /// </summary>
    public static class ClassRegistrationRule
    {
        /// <summary>Tävlingens egenskap. Formulärfältet heter <c>allowDualCClass</c>.</summary>
        public const string PropertyAlias = "allowDualCClassRegistration";

        /// <summary>Kategori inom C/L. Veteran yngre och äldre är SAMMA kategori.</summary>
        public static string? Category(string? classIdOrName)
        {
            var sc = Resolve(classIdOrName);
            if (sc == null) return null;
            if (sc.Weapon != WeaponClass.C && sc.Weapon != WeaponClass.L) return null;
            var id = sc.Id;
            if (id.Contains("_Vet_", StringComparison.OrdinalIgnoreCase)) return "veteran";
            if (id.EndsWith("_Dam", StringComparison.OrdinalIgnoreCase)) return "dam";
            if (id.EndsWith("_Jun", StringComparison.OrdinalIgnoreCase)) return "junior";
            return "öppen";
        }

        /// <summary>
        /// Null när klasserna går ihop, annars ett meddelande på svenska som namnger klasserna.
        /// </summary>
        public static string? Conflict(IEnumerable<string?> classes, bool allowDualC)
        {
            var list = classes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();

            var dup = list.GroupBy(ShootingClasses.NormalizeKey).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
                return $"Skytten står redan i klass {ShootingClasses.ToCanonicalName(dup.First())}. "
                     + "Samma klass kan bara förekomma en gång per skytt.";

            foreach (var group in list
                         .Select(c => (Raw: c, Weapon: ShootingClasses.GetWeaponClass(c)))
                         .Where(x => x.Weapon.HasValue)
                         .GroupBy(x => x.Weapon!.Value))
            {
                var names = group.Select(x => ShootingClasses.ToCanonicalName(x.Raw)).ToList();
                if (names.Count < 2) continue;
                var joined = string.Join(" och ", names);
                var weapon = WeaponLabel(group.Key);

                if (group.Key != WeaponClass.C && group.Key != WeaponClass.L)
                    return $"En skytt kan bara stå i en klass per vapengrupp ({joined} är båda {weapon}).";

                if (!allowDualC)
                    return $"Tävlingen tillåter bara en {weapon}-klass per skytt ({joined}). "
                         + "Arrangören kan tillåta två i tävlingens inställningar (Tillåt dubbel C-klassregistrering).";

                if (names.Count > 2)
                    return $"Högst två {weapon}-klasser per skytt ({joined}).";

                var cats = group.Select(x => Category(x.Raw)).ToList();
                if (cats[0] == cats[1])
                    return $"Två {weapon}-klasser måste vara ur olika kategorier (öppen, dam, veteran, junior) — "
                         + $"{joined} är båda {cats[0]}.";
            }

            return null;
        }

        private static ShootingClass? Resolve(string? idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return null;
            var k = idOrName.Trim();
            return ShootingClasses.GetById(k) ?? ShootingClasses.GetByName(k);
        }

        private static string WeaponLabel(WeaponClass w) => w switch
        {
            WeaponClass.A_Opt => "A Opt",
            WeaponClass.A_M => "AM",
            WeaponClass.A_P => "AP",
            WeaponClass.A_G => "AG",
            _ => w.ToString()
        };
    }
}
