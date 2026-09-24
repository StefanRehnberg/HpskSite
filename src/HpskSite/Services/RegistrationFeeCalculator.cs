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
            var juniorFee = ReadFeeOrNull(competition, JuniorRegistrationFeeAlias);
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
            var juniorFee = ReadFeeOrNull(competition, JuniorRegistrationFeeAlias);
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

        /// <summary>
        /// ⚠️ <paramref name="juniorFee"/> är NULLBAR med avsikt, och skillnaden mellan null och
        /// noll är hela poängen: <c>null</c> = ingen junioravgift är ifylld, alltså gäller
        /// grundavgiften; <c>0</c> = arrangören har SATT junioravgiften till noll, alltså är
        /// juniorerna gratis.
        ///
        /// Tidigare var parametern <c>decimal</c> och villkoret <c>juniorFee > 0</c>, så en
        /// avgift satt till 0 föll tillbaka på grundavgiften — en junior som skulle vara gratis
        /// fakturerades full avgift, och fältet gick helt enkelt inte att använda för det. Ett
        /// nollbelopp är ett svar, inte ett tomt fält.
        /// </summary>
        private static decimal CalculateTotal(
            IReadOnlyCollection<string> selectedClasses,
            bool isSubCompetition,
            decimal baseFee,
            decimal? juniorFee,
            decimal subCompFee,
            string? subCompFeeMode)
        {
            var config = new FeeConfig(baseFee, juniorFee, subCompFee, subCompFeeMode);

            decimal total = 0;
            if (selectedClasses != null)
            {
                foreach (var cls in selectedClasses)
                    total += FeeForClass(config, cls, isSubCompetition);
            }

            return total + PerRegistrationSurcharge(config, isSubCompetition);
        }

        /// <summary>
        /// Tävlingens avgiftsinställningar, lästa en gång. Finns för att avgiften ska kunna DELAS
        /// per klass (klubben betalar juniorklassen, skytten seniorklassen) utan en andra kopia av
        /// reglerna — delarna måste per konstruktion summera till <see cref="Calculate(IContent, IReadOnlyCollection{string}, bool)"/>.
        /// </summary>
        public readonly record struct FeeConfig(decimal BaseFee, decimal? JuniorFee, decimal SubCompFee, string? SubCompFeeMode)
        {
            public bool SubCompPerClass => !string.Equals(SubCompFeeMode,
                SubCompetitionFeeModePerRegistration, StringComparison.OrdinalIgnoreCase);
        }

        public static FeeConfig ReadConfig(IContent competition) => new(
            ReadFee(competition, RegistrationFeeAlias),
            ReadFeeOrNull(competition, JuniorRegistrationFeeAlias),
            ReadFee(competition, SubCompetitionFeeAlias),
            competition.GetValue<string>(SubCompetitionFeeModeAlias));

        public static FeeConfig ReadConfig(IPublishedContent competition) => new(
            ReadFee(competition, RegistrationFeeAlias),
            ReadFeeOrNull(competition, JuniorRegistrationFeeAlias),
            ReadFee(competition, SubCompetitionFeeAlias),
            competition.Value<string>(SubCompetitionFeeModeAlias));

        /// <summary>En klass avgift: grund- eller junioravgift, plus deltävlingen när den tas per klass.</summary>
        public static decimal FeeForClass(FeeConfig config, string cls, bool isSubCompetition)
        {
            var fee = IsJuniorClass(cls) && config.JuniorFee.HasValue ? config.JuniorFee.Value : config.BaseFee;
            if (isSubCompetition && config.SubCompFee > 0 && config.SubCompPerClass)
                fee += config.SubCompFee;
            return fee;
        }

        /// <summary>Deltävlingsavgiften när den tas EN gång per anmälan — hör inte till någon klass.</summary>
        public static decimal PerRegistrationSurcharge(FeeConfig config, bool isSubCompetition)
            => isSubCompetition && config.SubCompFee > 0 && !config.SubCompPerClass ? config.SubCompFee : 0m;

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

        private static decimal ParseFee(string? raw) => ParseFeeOrNull(raw) ?? 0;

        /// <summary>
        /// Samma tolkning som <see cref="ParseFee"/>, men skiljer "inte ifyllt" (null) från
        /// "ifyllt till 0" (0m). Behövs bara där ett nollbelopp betyder något annat än ett tomt
        /// fält — se <see cref="CalculateTotal"/>.
        /// </summary>
        public static decimal? ReadFeeOrNull(IPublishedContent competition, string alias)
            => competition == null ? null : ParseFeeOrNull(competition.Value<string>(alias));

        public static decimal? ReadFeeOrNull(IContent competition, string alias)
            => competition == null ? null : ParseFeeOrNull(competition.GetValue<string>(alias));

        private static decimal? ParseFeeOrNull(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return v;
            return null;
        }
    }
}
