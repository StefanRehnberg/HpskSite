using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Vilka vapengrupper som avgör ett mästerskap (S3/S4 i utanför-mästerskapet-arbetet). Lagras som en kommalista i EN kolumn
    /// (<c>Competition.ChampionshipWeaponGroups</c>), tom/NULL = alla grupper (dagens beteende).
    ///
    /// <para>Koderna är de som <see cref="ChampionshipCategory.WeaponGroupFor"/> ger ("A", "A Opt", "B", "C", "R", "M" …) — EN delad
    /// kodlista, så att listan och regeln aldrig kan tala om olika grupper. Skrivningen kanoniseras (distinkta, sorterade Ordinal,
    /// skiftlägesnormaliserade mot kodlistan); okända koder tas bort och loggas inte här (anroparen vet sammanhanget).</para>
    /// </summary>
    public static class ChampionshipWeaponGroups
    {
        /// <summary>Alla koder som finns, i den skrivform <see cref="ChampionshipCategory.WeaponGroupFor"/> ger.</summary>
        public static IReadOnlyList<string> KnownCodes { get; } =
            ShootingClasses.All
                .Select(c => ShootingClasses.WeaponGroupLabel(c.Weapon))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();

        /// <summary>Vapengrupperna i fältskjutning (klassfamiljerna A, A Opt, B, C, R).</summary>
        public static IReadOnlyList<string> FaltCodes { get; } = new[] { "A", "A Opt", "B", "C", "R" };

        /// <summary>
        /// Vilka vapengrupper arrangören får välja mellan för en tävling: fältets egna grupper, plus de som de anmälda klasserna tillhör
        /// och de som redan är sparade (så att ett gammalt val aldrig försvinner ur listan), bara kända koder, ordnade Ordinal.
        /// Alla koder i systemet (precisionens, springskyttets …) erbjuds inte — de hör inte till ett fältmästerskap och skulle förvirra.
        /// </summary>
        public static List<string> OfferedFor(string? discipline, IEnumerable<string?> usedGroups, IEnumerable<string?> storedGroups)
        {
            var known = KnownCodes.ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
            var offered = new HashSet<string>(StringComparer.Ordinal);
            var d = (discipline ?? "").Trim();
            if (d.Equals("Faltskytte", StringComparison.OrdinalIgnoreCase))
                foreach (var c in FaltCodes) if (known.TryGetValue(c, out var k)) offered.Add(k);
            foreach (var g in usedGroups.Concat(storedGroups))
                if (!string.IsNullOrWhiteSpace(g) && known.TryGetValue(g.Trim(), out var code)) offered.Add(code);
            return offered.OrderBy(c => c, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Kanonisk lagringsform: kända koder, distinkta, sorterade Ordinal, kommaseparerade. Tomt (eller bara okända koder) = null =
        /// alla grupper.
        /// </summary>
        public static string? Canonicalize(string? stored)
        {
            var codes = Parse(stored);
            return codes.Count == 0 ? null : string.Join(",", codes);
        }

        /// <summary>De kända koderna i en lagrad lista, kanoniskt ordnade. Tom lista = alla grupper.</summary>
        public static List<string> Parse(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return new List<string>();
            var known = KnownCodes.ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
            return stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => known.TryGetValue(s, out var code) ? code : null)
                .Where(c => c != null)
                .Select(c => c!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
        }
    }
}
