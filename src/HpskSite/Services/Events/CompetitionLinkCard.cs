using HpskSite.Services.RegistrationGate;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// En kopplad tävlings rad på händelsesidan: namn, datum, läge och EN huvudknapp som följer
    /// tävlingens läge — Anmäl dig medan anmälan är öppen, sedan Startlista, sist Resultat. Samma
    /// regler som tävlingssidan använder (anmälningsfönstret, startlistegrinden, resultatnoden).
    /// </summary>
    public sealed class CompetitionLinkCard
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public DateTime? Date { get; init; }
        public string Url { get; init; } = "";
        public string Status { get; init; } = "";
        public bool StatusPositive { get; init; }
        public string? PrimaryLabel { get; init; }
        public string? PrimaryUrl { get; init; }
        public string? PrimaryIcon { get; init; }

        /// <param name="result">Tävlingens resultatlista ur <c>ICompetitionResultStore</c>, eller null.</param>
        /// <param name="startLists">Startlistornas lagring — kortet frågar efter den officiella listan.</param>
        public static CompetitionLinkCard Describe(IPublishedContent comp, DateTime now, HpskSite.Models.Results.CompetitionResultRecord? result,
            HpskSite.Services.StartLists.IStartListStore startLists)
        {
            var url = comp.Url();
            var date = comp.Value<DateTime>("competitionDate");

            // Resultat: resultatnoden "Resultat" med isOfficial (precisionsfamiljen, spring), eller
            // fältskyttets flagga på tävlingen.
            var resultsOfficial = (result?.IsOfficial ?? false) || comp.Value<bool>("faltskytteResultsOfficial");
            if (resultsOfficial && result != null)
                return Card(comp, url, date, "Resultat publicerade", true, "Resultat", url.TrimEnd('/') + "/resultat/", "bi-trophy");

            if (StartListRegistrationGate.HasPublishedStartList(comp, startLists))
            {
                var type = comp.Value<string>("competitionType") ?? "";
                string? listUrl = null;
                if (type.StartsWith("Springskytte", StringComparison.OrdinalIgnoreCase))
                    listUrl = $"/startlista/{comp.Id}";
                else
                {
                    // Adressen byggs ur tävlingens, som överallt annars — inte ur listans nod.
                    var official = startLists.ListPublished(comp.Id, HpskSite.Models.StartLists.StartListKind.Qualifying)
                        .FirstOrDefault(r => r.IsOfficial);
                    listUrl = official == null ? null : url.TrimEnd('/') + "/startlista/";
                }
                // Fältskyttets patrullista visas på tävlingssidan — då leder knappen dit.
                return Card(comp, url, date, "Startlista publicerad", true, "Startlista", listUrl ?? url, "bi-list-ol");
            }

            var isActive = comp.Value<bool>("isActive");
            var opens = comp.Value<DateTime>("registrationOpenDate");
            var closes = comp.Value<DateTime>("registrationCloseDate");
            if (isActive && opens != DateTime.MinValue && now < opens)
                return Card(comp, url, date, "Anmälan öppnar " + opens.ToString("d MMM", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")), false, null, null, null);
            // Samma jämförelse som tävlingssidan (now <= registrationCloseDate), så de två ytorna inte
            // säger olika saker om samma anmälan.
            if (isActive && now >= opens && closes != DateTime.MinValue && now <= closes
                && !StartListRegistrationGate.IsClosed(comp, startLists))
                return Card(comp, url, date, "Anmälan öppen", true, "Anmäl dig", url, "bi-pencil-square");
            if (date != DateTime.MinValue && date.Date < now.Date)
                return Card(comp, url, date, "Genomförd", false, null, null, null);
            return Card(comp, url, date, "Anmälan stängd", false, null, null, null);
        }

        private static CompetitionLinkCard Card(IPublishedContent comp, string url, DateTime date, string status, bool positive,
            string? label, string? primaryUrl, string? icon) => new()
        {
            Id = comp.Id,
            Name = comp.Name ?? "",
            Date = date == DateTime.MinValue ? null : date,
            Url = url,
            Status = status,
            StatusPositive = positive,
            PrimaryLabel = label,
            PrimaryUrl = primaryUrl,
            PrimaryIcon = icon
        };
    }
}
