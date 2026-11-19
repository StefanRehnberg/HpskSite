using HpskSite.Models.Competitions;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Reglerna för <b>Publicera</b> av en dold tävling (kretsutlysningen F1, planen 4.2). Rena funktioner — knappen,
    /// servern och testerna läser samma svar. En dold tävling får publiceras först när det som sidan och kalendern
    /// bygger på finns: namn, gren, datum och en arrangör (klubb, krets eller mästerskap).
    /// </summary>
    public static class CompetitionPublishRules
    {
        /// <summary>Det som saknas innan tävlingen kan publiceras, på svenska ("datum", "gren" …). Tom lista = kan publiceras.</summary>
        public static IReadOnlyList<string> Missing(CompetitionInfo c)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(c.CompetitionName) && string.IsNullOrWhiteSpace(c.Name)) missing.Add("namn");
            if (string.IsNullOrWhiteSpace(c.CompetitionType)) missing.Add("gren");
            if (c.CompetitionDate == null) missing.Add("datum");
            if (c.ClubId <= 0 && string.IsNullOrWhiteSpace(c.RegionalFederation) && string.IsNullOrWhiteSpace(c.CompetitionScope))
                missing.Add("arrangör (klubb, krets eller mästerskapstyp)");
            return missing;
        }

        /// <summary>Förklaringen när Publicera är avstängd, annars null.</summary>
        public static string? BlockedReason(CompetitionInfo c)
        {
            var missing = Missing(c);
            return missing.Count == 0 ? null : "Tävlingen kan inte publiceras än. Det saknas: " + string.Join(", ", missing) + ".";
        }

        /// <summary>
        /// Förklaringen när "Ångra publicering" inte får göras, annars null. En tävling med anmälningar är redan känd
        /// för skyttarna — den får inte försvinna under dem.
        /// </summary>
        public static string? UnpublishBlockedReason(int registrationCount) =>
            registrationCount > 0
                ? $"Publiceringen kan inte ångras — tävlingen har {registrationCount} {(registrationCount == 1 ? "anmälan" : "anmälningar")}."
                : null;
    }
}
