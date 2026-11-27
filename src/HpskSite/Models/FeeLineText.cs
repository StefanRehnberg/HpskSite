namespace HpskSite.Models
{
    /// <summary>
    /// Radtexterna på en medlemsavgift SÅ SOM BETALAREN LÄSER DEM (mejlet, förhandsvisningen och betalsidan — EN väg).
    /// Raden i databasen bär bara kassörens egen text ("Familj", "SPSF"); den säger inte vad beloppet avser eller vem det gäller.
    /// Här görs den läsbar: grundavgiften för en familj nämner familjemedlemmarna, och ett tillägg/avdrag säger om det är tillägg
    /// eller avdrag och, i en familj, vem av dem det gäller. Rena funktioner — inget läser eller skriver här.
    /// </summary>
    public static class FeeLineText
    {
        /// <param name="lines">Avgiftens rader.</param>
        /// <param name="payerName">Den som betalar (huvudmedlemmen i en familj).</param>
        /// <param name="coveredNames">De andra personerna avgiften täcker (familjemedlemmarna); tom = en ensam medlem.</param>
        /// <param name="subjectName">Namn på en medlem (för tilläggets person); null = okänd.</param>
        public static List<(string Description, decimal Amount)> Describe(
            IEnumerable<MembershipFeeChargeLine> lines, string? payerName, IReadOnlyList<string> coveredNames, Func<int, string?> subjectName)
        {
            var household = new List<string>();
            if (coveredNames.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(payerName)) household.Add(payerName!.Trim());
                foreach (var n in coveredNames)
                    if (!string.IsNullOrWhiteSpace(n) && !household.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase)) household.Add(n.Trim());
            }

            var result = new List<(string, decimal)>();
            foreach (var l in lines)
            {
                var text = l.Description;
                switch (l.Kind)
                {
                    case ClubFeeCalculator.BaseKind:
                        text = household.Count > 1
                            ? $"Grundavgift {l.Description} (för {JoinNames(household)})"
                            : $"Grundavgift {l.Description}";
                        break;
                    case ClubFeeCalculator.PartKind:
                        var who = l.SubjectMemberId is int id ? subjectName(id) : null;
                        text = (l.Amount < 0 ? "Avdrag " : "Tillägg ") + l.Description + (string.IsNullOrWhiteSpace(who) ? "" : $" – {who!.Trim()}");
                        break;
                }
                result.Add((text, l.Amount));
            }
            return result;
        }

        /// <summary>Visas uppdelningen? Fler än en rad, eller en ensam familjerad som nämner vilka som ingår.</summary>
        public static bool WorthShowing(IReadOnlyList<(string Description, decimal Amount)> lines)
            => lines.Count > 1 || (lines.Count == 1 && lines[0].Description.Contains(" (för "));

        /// <summary>"Anna, Bo och Cecilia".</summary>
        public static string JoinNames(IReadOnlyList<string> names)
            => names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " och " + names[^1];
    }
}
