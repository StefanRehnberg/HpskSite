namespace HpskSite.Services.StartListCoverage
{
    /// <summary>
    /// The grouping/counting half, shared so two disciplines cannot disagree about what "placed"
    /// means or how the per-weapon-group breakdown is ordered.
    /// </summary>
    internal static class CoverageBuilder
    {
        /// <summary>
        /// <paramref name="KeyClass"/> is what placement is keyed on and is NOT always the class:
        /// the precision family gives every class its own position in a skjutlag (A1 and A_opt_1 are
        /// two separate starts), while a Fältskytte patrol is per WEAPON GROUP — a shooter entered in
        /// C1 and C2 walks the course once. Keying Fältskytte per class would report a phantom
        /// missing start for the second class forever.
        /// <paramref name="ShootingClass"/> stays the real class, because that is what the organiser
        /// needs to read on the row.
        /// </summary>
        internal sealed record Row(int MemberId, string Name, string Club, string ShootingClass, string KeyClass);

        /// <summary>One row actually on the start list / in a patrol, for the mirror report.</summary>
        internal sealed record PlacedRow(int MemberId, string Name, string Club, string ShootingClass, string KeyClass);

        internal static StartListCoverageResult Build(
            List<Row> required, List<PlacedRow> placedRows, bool hasAnyStartList, string unitLabel)
        {
            var placed = new HashSet<string>(
                placedRows.Select(r => CoverageKeys.For(r.MemberId, r.KeyClass)),
                StringComparer.OrdinalIgnoreCase);

            bool IsPlaced(Row r) => placed.Contains(CoverageKeys.For(r.MemberId, r.KeyClass));

            var byWeapon = required
                .GroupBy(r => CoverageKeys.WeaponGroupOf(r.ShootingClass), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CoverageGroup
                {
                    WeaponClass = g.Key,
                    Total = g.Count(),
                    Placed = g.Count(IsPlaced),
                    Missing = g.Where(r => !IsPlaced(r))
                        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => new UnplacedStart
                        {
                            MemberId = r.MemberId,
                            Name = r.Name,
                            Club = r.Club,
                            ShootingClass = r.ShootingClass
                        })
                        .ToList()
                })
                .ToList();

            var requiredKeys = new HashSet<string>(
                required.Select(r => CoverageKeys.For(r.MemberId, r.KeyClass)),
                StringComparer.OrdinalIgnoreCase);

            var orphans = placedRows
                .Where(r => !requiredKeys.Contains(CoverageKeys.For(r.MemberId, r.KeyClass)))
                .GroupBy(r => CoverageKeys.For(r.MemberId, r.KeyClass))
                .Select(g => g.First())
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new UnplacedStart
                {
                    MemberId = r.MemberId,
                    Name = r.Name,
                    Club = r.Club,
                    ShootingClass = r.ShootingClass
                })
                .ToList();

            return new StartListCoverageResult
            {
                Supported = true,
                UnitLabel = unitLabel,
                Total = required.Count,
                Placed = required.Count(IsPlaced),
                ByWeapon = byWeapon,
                OnListWithoutRegistration = orphans,
                HasAnyStartList = hasAnyStartList
            };
        }
    }
}
