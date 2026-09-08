using HpskSite.CompetitionTypes.Common;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    /// <summary>
    /// Vilken vapengrupp en finalstartlista gäller — EN plats, delad av controllern, den
    /// publika tävlingssidan och inmatningsytorna.
    ///
    /// Sedan 2026-09-08 finns en finalstartlista per vapengrupp: en vapengrupps final är EN
    /// skjutsession med eget datum, egen starttid och egen publicering. På SSM 2026 skjuts C
    /// med final på lördagen, A på söndag förmiddag och B på söndag eftermiddag.
    ///
    /// ⚠️ Gruppen ligger i <c>configurationData</c> (<see cref="StartListSettings.WeaponGroup"/>)
    /// och inte som en doctype-egenskap, med flit: <c>SetValue</c> på en saknad egenskap är en
    /// TYST no-op, och en uppdelning som tappar sin gruppmärkning slår ihop alla finaler igen
    /// utan att något säger till. configurationData läses ändå av varje yta som rör listan.
    ///
    /// ⚠️ Läs den ALDRIG ur nodens namn. Namnet sätts av generatorn för att ge ett läsbart
    /// URL-segment ("finalstartlista-c"), men ett namn kan ändras i backoffice och är då inte
    /// längre sant — samma fälla som [[clubservice-name-is-clubname-property]].
    /// </summary>
    public static class FinalsWeaponGroup
    {
        /// <summary>
        /// Gruppen ur en finalstartlistas <c>configurationData</c>, eller <c>""</c>.
        ///
        /// Ordningen är inte godtycklig:
        /// 1. <c>Settings.WeaponGroup</c> — vad generatorn stämplade.
        /// 2. Annars HÄRLEDS gruppen ur skyttarnas klasser. Listor genererade före
        ///    2026-09 saknar stämpeln, och en sådan lista som bara innehåller C-skyttar
        ///    blir därmed C-listan utan att någon migrering behöver köras.
        /// 3. <c>""</c> — listan spänner över flera vapengrupper, alltså en äldre
        ///    heltävlingslista. Ytan ska säga det i klartext, inte visa den som någon
        ///    grupps lista.
        /// </summary>
        public static string FromConfigurationData(string? configurationData)
        {
            if (string.IsNullOrWhiteSpace(configurationData)) return "";
            try
            {
                var cfg = JsonConvert.DeserializeObject<StartListConfiguration>(configurationData);
                return FromConfiguration(cfg);
            }
            catch
            {
                // En trasig lista får inte ta ner sidan som visar den. Tom grupp betyder
                // "vet inte", vilket ytan redan hanterar.
                return "";
            }
        }

        /// <summary>Samma regel för en redan avserialiserad konfiguration.</summary>
        public static string FromConfiguration(StartListConfiguration? config)
        {
            var stamped = config?.Settings?.WeaponGroup;
            if (!string.IsNullOrWhiteSpace(stamped)) return stamped.Trim();

            var classes = (config?.Teams ?? new List<StartListTeam>())
                .SelectMany(t => t.Shooters ?? new List<StartListShooter>())
                .Select(sh => sh.WeaponClass);
            return ChampionshipCategory.WeaponGroupForClasses(classes);
        }

        /// <summary>
        /// Etiketten som visas för en lista. En heltävlingslista får en egen text i stället
        /// för en tom rubrik — "Final" utan grupp läses som att gruppen saknas.
        /// </summary>
        public static string Label(string? weaponGroup) =>
            string.IsNullOrWhiteSpace(weaponGroup) ? "hela tävlingen" : $"vapengrupp {weaponGroup.Trim()}";
    }
}
