using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>Resultatet av en prövning: null = godkänt, annars meddelandet till kassören.</summary>
    public static class ClubFeeSettingsRules
    {
        public const int MaxLabel = 100;
        public const int MaxOptionName = 100;

        /// <summary>Prövar ett tillägg/avdrag innan det sparas. Returnerar null när det går bra.</summary>
        public static string? ValidatePart(
            string? label, decimal amount, string? condition, int? optionId,
            IReadOnlyCollection<string> typeKeys,
            IReadOnlySet<int> clubOptionIds,
            IReadOnlyCollection<string> validTypeKeys)
        {
            if (string.IsNullOrWhiteSpace(label)) return "Ange vad tillägget eller avdraget heter.";
            if (label.Trim().Length > MaxLabel) return $"Namnet får vara högst {MaxLabel} tecken.";
            if (amount == 0m) return "Beloppet får inte vara 0 kr. Skriv ett plusbelopp för ett tillägg eller ett minusbelopp för ett avdrag.";
            if (decimal.Round(amount, 2) != amount) return "Beloppet får ha högst två decimaler.";
            if (Math.Abs(amount) > 1_000_000m) return "Beloppet är orimligt stort.";

            switch (condition)
            {
                case MembershipFeePartCondition.Always:
                case MembershipFeePartCondition.PrimaryClub:
                case MembershipFeePartCondition.NotPrimaryClub:
                    if (optionId.HasValue) return "Ett tillval kan bara anges när villkoret är \"Har tillvalet\".";
                    break;
                case MembershipFeePartCondition.Option:
                    if (optionId is not int oid || !clubOptionIds.Contains(oid))
                        return "Välj ett av klubbens tillval.";
                    break;
                default:
                    return "Okänt villkor.";
            }

            foreach (var k in typeKeys)
                if (!validTypeKeys.Any(v => MembershipTypeKeys.Same(v, k)))
                    return "Okänd medlemstyp i urvalet.";
            return null;
        }

        /// <summary>Namnet på ett tillval. Unikt per klubb (skiftlägesokänsligt).</summary>
        public static string? ValidateOptionName(string? name, IEnumerable<string> otherNames)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Ange tillvalets namn.";
            if (name.Trim().Length > MaxOptionName) return $"Namnet får vara högst {MaxOptionName} tecken.";
            if (otherNames.Any(n => string.Equals(n?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return "Det finns redan ett tillval med det namnet.";
            return null;
        }

        /// <summary>Urvalet av medlemstyper normaliserat till nycklarnas kanoniska stavning, utan dubbletter.</summary>
        public static List<string> NormalizeTypeKeys(IEnumerable<string>? keys, IReadOnlyCollection<string> validTypeKeys)
        {
            var result = new List<string>();
            foreach (var k in keys ?? Array.Empty<string>())
            {
                var v = validTypeKeys.FirstOrDefault(x => MembershipTypeKeys.Same(x, k));
                if (v is not null && !result.Contains(v)) result.Add(v);
            }
            return result;
        }
    }
}
