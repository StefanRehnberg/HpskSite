using System.Globalization;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    public static class RegistrationFeeCalculator
    {
        public const string RegistrationFeeAlias = "registrationFee";
        public const string JuniorRegistrationFeeAlias = "juniorRegistrationFee";
        public const string SubCompetitionFeeAlias = "subCompetitionFee";
        public const string SubCompetitionFeeModeAlias = "subCompetitionFeeMode";

        public const string SubCompetitionFeeModePerClass = "perClass";
        public const string SubCompetitionFeeModePerRegistration = "perRegistration";

        public static decimal Calculate(
            IPublishedContent competition,
            IReadOnlyCollection<string> selectedClasses,
            bool isSubCompetition)
        {
            if (competition == null) return 0;
            var baseFee = ReadFee(competition, RegistrationFeeAlias);
            var juniorFee = ReadFee(competition, JuniorRegistrationFeeAlias);
            var subCompFee = ReadFee(competition, SubCompetitionFeeAlias);
            var mode = competition.Value<string>(SubCompetitionFeeModeAlias);
            return CalculateTotal(selectedClasses, isSubCompetition, baseFee, juniorFee, subCompFee, mode);
        }

        public static decimal Calculate(
            IContent competition,
            IReadOnlyCollection<string> selectedClasses,
            bool isSubCompetition)
        {
            if (competition == null) return 0;
            var baseFee = ReadFee(competition, RegistrationFeeAlias);
            var juniorFee = ReadFee(competition, JuniorRegistrationFeeAlias);
            var subCompFee = ReadFee(competition, SubCompetitionFeeAlias);
            var mode = competition.GetValue<string>(SubCompetitionFeeModeAlias);
            return CalculateTotal(selectedClasses, isSubCompetition, baseFee, juniorFee, subCompFee, mode);
        }

        /// <summary>
        /// Returns just the deltävling (sub-competition) portion of the fee.
        /// Zero if the registration isn't opted into the sub-competition or no fee is configured.
        /// </summary>
        public static decimal CalculateSubCompetitionPortion(
            IPublishedContent competition,
            IReadOnlyCollection<string> selectedClasses,
            bool isSubCompetition)
        {
            if (!isSubCompetition || competition == null) return 0;
            var subCompFee = ReadFee(competition, SubCompetitionFeeAlias);
            if (subCompFee <= 0) return 0;
            var mode = competition.Value<string>(SubCompetitionFeeModeAlias);
            return ComputeSubCompPortion(selectedClasses, subCompFee, mode);
        }

        public static decimal CalculateSubCompetitionPortion(
            IContent competition,
            IReadOnlyCollection<string> selectedClasses,
            bool isSubCompetition)
        {
            if (!isSubCompetition || competition == null) return 0;
            var subCompFee = ReadFee(competition, SubCompetitionFeeAlias);
            if (subCompFee <= 0) return 0;
            var mode = competition.GetValue<string>(SubCompetitionFeeModeAlias);
            return ComputeSubCompPortion(selectedClasses, subCompFee, mode);
        }

        private static decimal ComputeSubCompPortion(
            IReadOnlyCollection<string> selectedClasses,
            decimal subCompFee,
            string? mode)
        {
            var applyPerClass = !string.Equals(mode,
                SubCompetitionFeeModePerRegistration, StringComparison.OrdinalIgnoreCase);
            if (!applyPerClass) return subCompFee;
            var count = selectedClasses?.Count ?? 0;
            if (count <= 0) count = 1;
            return subCompFee * count;
        }

        public static bool IsJuniorClass(string classIdOrName)
        {
            if (string.IsNullOrWhiteSpace(classIdOrName)) return false;

            // Standard disciplines: "C_Jun", "L_Jun"
            if (classIdOrName.Contains("_Jun", StringComparison.OrdinalIgnoreCase)) return true;

            // Springskytte composite class: "A-D jun", "A-D 15", "C-H 18", etc.
            // Age/gender part is after the '-'; sub-21 brackets (15, 18, jun) all count as junior.
            var dashIdx = classIdOrName.IndexOf('-');
            var agePart = dashIdx >= 0 ? classIdOrName.Substring(dashIdx + 1) : classIdOrName;

            if (agePart.Contains("jun", StringComparison.OrdinalIgnoreCase)) return true;
            if (agePart.Contains("15", StringComparison.Ordinal)) return true;
            if (agePart.Contains("18", StringComparison.Ordinal)) return true;

            return false;
        }

        private static decimal CalculateTotal(
            IReadOnlyCollection<string> selectedClasses,
            bool isSubCompetition,
            decimal baseFee,
            decimal juniorFee,
            decimal subCompFee,
            string? subCompFeeMode)
        {
            var applyPerClass = !string.Equals(subCompFeeMode,
                SubCompetitionFeeModePerRegistration, StringComparison.OrdinalIgnoreCase);

            decimal total = 0;
            if (selectedClasses != null)
            {
                foreach (var cls in selectedClasses)
                {
                    var feeForClass = IsJuniorClass(cls) && juniorFee > 0 ? juniorFee : baseFee;
                    if (isSubCompetition && subCompFee > 0 && applyPerClass)
                        feeForClass += subCompFee;
                    total += feeForClass;
                }
            }

            if (isSubCompetition && subCompFee > 0 && !applyPerClass)
                total += subCompFee;

            return total;
        }

        private static decimal ReadFee(IPublishedContent competition, string alias)
        {
            var raw = competition.Value<string>(alias);
            return ParseFee(raw);
        }

        private static decimal ReadFee(IContent competition, string alias)
        {
            var raw = competition.GetValue<string>(alias);
            return ParseFee(raw);
        }

        private static decimal ParseFee(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return v;
            return 0;
        }
    }
}
