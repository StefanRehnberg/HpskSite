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
