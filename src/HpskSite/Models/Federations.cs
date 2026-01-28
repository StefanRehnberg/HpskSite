namespace HpskSite.Models
{
    using System.ComponentModel;
    using System.Reflection;

    public static class Federations
    {
        public static string GetDescription(this Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = field?.GetCustomAttribute<DescriptionAttribute>();
            return attribute?.Description ?? value.ToString();
        }

        public enum RegionalFederations
        {
            [Description("Blekinge Pistolskyttekrets")]
            Blekinge,

            [Description("Dalarnas Pistolskyttekrets")]
            Dalarna,

            [Description("Gotlands Pistolskyttekrets")]
            Gotland,

            [Description("Gävleborgs Pistolskyttekrets")]
            Gavleborg,

            [Description("Göteborg och Bohusläns Pistol.Krets")]
            Goteborg,

            [Description("Hallands Pistolskyttekrets")]
            Halland,

            [Description("Jämtlands Läns Pistolskyttekrets")]
            Jamtland,

            [Description("Jönköpings Läns Pistolskyttekrets")]
            Jonkoping,

            [Description("Kalmar Läns Norra Pistolskyttekrets")]
            KalmarNorra,

            [Description("Kalmar Läns Södra Pistolskyttekrets")]
            KalmarSodra,

            [Description("Kristianstads Pistolskyttekrets")]
            Kristianstad,

            [Description("Kronobergs Läns Pistolskyttekrets")]
            Kronoberg,

            [Description("Malmöhus Pistolskyttekrets")]
            Malmohus,

            [Description("Norrbottens Pistolskyttekrets")]
            Norrbotten,

            [Description("Skaraborgs Pistolskyttekrets")]
            Skaraborg,

            [Description("Stockholms Pistolskyttekrets")]
            Stockholm,

            [Description("Södermanlands Pistolskyttekrets")]
            Sodermanland,

            [Description("Uppsala Läns Pistolskyttekrets")]
            Uppsala,

            [Description("Värmlands Pistolskyttekrets")]
            Varmland,

            [Description("Västerbottens Läns Pistolskyttekrets")]
            Vasterbotten,

            [Description("Västernorrlands Läns Pistolskyttekrets")]
            Vasternorrland,

            [Description("Västgöta-Dals Pistolskyttekrets")]
            VastgotaDal,

            [Description("Västmanlands Pistolskyttekrets")]
            Vastmanland,

            [Description("Älvsborgs Pistolskyttekrets")]
            Alvsborg,

            [Description("Örebro Läns Pistolskyttekrets")]
            Orebro,

            [Description("Östergötlands Pistolskyttekrets")]
            Ostergotland
        }
    }
}
