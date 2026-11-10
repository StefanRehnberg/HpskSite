using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Services.SitePages
{
    /// <summary>Var på sidan en panel ritas. <c>Full</c> = hela bredden; <c>Main</c>/<c>Side</c> = huvudkolumnen (<c>col-lg-8</c>) och sidokolumnen (<c>col-lg-4</c>) på Hem.</summary>
    public enum PanelZone { Full, Main, Side }

    /// <summary>
    /// En systempanel i registret (konvertera-planen v2, K0b). Nyckeln är densamma som blocknyckeln i <see cref="SiteTemplates"/>.
    /// </summary>
    /// <param name="Key">Permanent nyckel = mallens blocknyckel (<c>upcoming-events</c> …).</param>
    /// <param name="Name">Namnet ägaren ser ("Vanligt innehåll").</param>
    /// <param name="Description">Vad panelen visar, på ett språk en ovan person förstår.</param>
    /// <param name="Partial">Delvyn som ritar panelen (tar <see cref="SitePanelContext"/>), eller null när panelen ritas av sidhuvudet (kretsens <c>welcome</c>) eller av en annan panel.</param>
    /// <param name="Zone">Förvald zon.</param>
    /// <param name="PersonalData">Visar personuppgifter (kan aldrig bli öppnare än Inloggade).</param>
    /// <param name="Required">Kan flyttas men aldrig tas bort av klubben när sidan blir en blocksida.</param>
    /// <param name="HostedBy">Panelen ritas inuti en annan panel (snabblänkdokumenten i Snabblänkar).</param>
/// <param name="PublicAllowed">Ägaren får välja "Visa publikt" (Alla, även utloggade) för panelen, ett uttryckligt undantag från "aldrig öppnare än förut" (Stefans beslut, steg 2d). Aldrig en personpanel.</param>
    public sealed record PanelDef(string Key, string Name, string Description, string? Partial, PanelZone Zone = PanelZone.Full,
        bool PersonalData = false, bool Required = false, string? HostedBy = null, bool PublicAllowed = false);

    /// <summary>En panel på en sida, i ordning, med sin zon.</summary>
    public sealed record PanelPlacement(string Key, PanelZone Zone);

    /// <summary>Allt en panel behöver veta om ägaren och tittaren. Ersätter de sju spridda ViewData-uppsättningarna (<c>ligaVD</c>, <c>clubRecVD</c>, <c>regionRecVD</c> …).</summary>
    public sealed record SitePanelContext(string OwnerType, int OwnerId, string OwnerName, IPublishedContent Node, SiteViewer Viewer,
        string RegionCode = "", string PageKey = "home")
    {
        public bool LoggedIn => Viewer.LoggedIn;
        /// <summary>Ägarens administratör enligt vyns egen prövning (klubbadmin respektive kretsadmin; sajtadmin räknas in).</summary>
        public bool OwnerAdmin => Viewer.IsOwnerAdmin;
        public bool IsClub => OwnerType == SiteTemplates.Club;
    }

    /// <summary>
    /// Panelregistret: EN plats för vad varje systempanel heter, vilken delvy som ritar den och i vilken ordning de står på varje
    /// systemsida (standardlistan). Både klubbens och kretsens vyer ritar sina sidor genom <c>_SitePanels</c> ur den här listan;
    /// en omvandlad sida ritas senare ur lagrade block genom SAMMA loop. Testet <c>PanelCatalogTests</c> kräver att registret täcker
    /// exakt mallens blocknycklar (båda hållen).
    /// </summary>
    public static class PanelCatalog
    {
        private const string Dir = "~/Views/Partials/Panels/";

        private static PanelDef D(string key, string name, string description, string? partial, PanelZone zone = PanelZone.Full,
            bool personal = false, bool required = false, string? hostedBy = null, bool publicAllowed = false) =>
            new(key, name, description, partial == null ? null : Dir + partial + ".cshtml", zone, personal, required, hostedBy, publicAllowed);

        public static readonly IReadOnlyList<PanelDef> Club = new[]
        {
            D("about-text", "Om klubben", "Klubbens presentation. Texten skrivs under Klubbens uppgifter och visas här automatiskt.", "_PanelClubAbout", PanelZone.Main, publicAllowed: true),   // presentationstexten visas redan för utloggade på Hem; den ritas på servern (ingen dataslutpunkt att grinda)
            D("login-prompt", "Inloggningsruta", "Rutan som bjuder besökare som inte är inloggade att logga in.", "_PanelClubLoginPrompt", PanelZone.Side, required: true),
            D("contact-card", "Kontakt", "Kontaktperson, e-post och telefon. Hämtas automatiskt ur klubbens uppgifter.", "_PanelClubContact", PanelZone.Side),
            D("quicklink-docs", "Dokument med snabblänk", "Dokument som klubben valt att visa som snabblänk.", "_PanelClubQuickLinkDocs", PanelZone.Side, required: true),
            D("whats-happening", "Det här händer", "Klubbens senaste händelser, tävlingar och träningar. Hämtas automatiskt.", "_PanelClubWhatsHappening"),
            D("upcoming-events", "Kommande händelser", "Klubbens kommande händelser. Hämtas automatiskt.", "_PanelUpcomingEvents", PanelZone.Main),
            D("motions", "Motioner och styrelsens ärenden", "Medlemmarnas motioner och styrelsens ärenden.", "_PanelClubMotions", PanelZone.Side, required: true),
            D("quicklinks", "Snabblänkar", "Genvägar till klubbens andra sidor.", "_PanelClubQuickLinks", PanelZone.Side),
            D("ranges", "Banor", "Klubbens banor med karta och fasta tider. Hämtas automatiskt.", "_PanelClubRanges", publicAllowed: true),
            D("calendar", "Kalender", "Klubbens kalender med händelser och träningar.", "_PanelClubCalendar", required: true, publicAllowed: true),
            D("competitions", "Tävlingar", "Tävlingar som klubben arrangerar.", "_PanelClubCompetitions", required: true, publicAllowed: true),
            D("instructors", "Instruktörer", "Klubbens instruktörer.", "_PanelClubInstructors", personal: true),
            D("guldserie", "Guldserien", "Klubbens ställning i Guldserien.", "_PanelClubGuldserie", personal: true),
            D("member-directory", "Medlemskatalogen", "Klubbens medlemmar. Bara namn och bild; e-post ser bara administratören.", "_PanelClubMemberDirectory", personal: true, required: true),
            D("records", "Klubbrekord", "Klubbens rekord.", "_PanelClubRecords", publicAllowed: true),
            D("document-archive", "Dokumentarkivet", "Klubbens dokument. Varje dokument har sin egen synlighet.", "_PanelClubDocuments", required: true),
        };

        public static readonly IReadOnlyList<PanelDef> Region = new[]
        {
            // Kretsens välkomsttext ritas av sidhuvudet (banderollen), inte av en panel i flikarna.
            D("welcome", "Välkomsthuvud", "Rubrik och välkomsttext högst upp på kretsens sidor.", null),
            D("whats-happening", "Det här händer", "Kretsens senaste händelser, tävlingar och träningar. Hämtas automatiskt.", "_PanelRegionWhatsHappening"),
            D("stats-card", "Kretsen i siffror", "Antal klubbar och kommande tävlingar. Hämtas automatiskt.", "_PanelRegionStats", PanelZone.Main),
            D("motions", "Motioner och styrelsens ärenden", "Kretsårsmötets motioner och kretsstyrelsens ärenden.", "_PanelRegionMotions", PanelZone.Main, required: true),
            D("upcoming-events", "Kommande händelser", "Kretsens kommande händelser. Hämtas automatiskt.", "_PanelUpcomingEvents", PanelZone.Main),
            D("contact-card", "Kontakt", "Kontaktperson, e-post och telefon. Hämtas automatiskt ur kretsens uppgifter.", "_PanelRegionContact", PanelZone.Side),
            D("quicklinks", "Snabblänkar", "Genvägar till kretsens klubbar, tävlingar och rekord.", "_PanelRegionQuickLinks", PanelZone.Side),
            D("quicklink-docs", "Dokument med snabblänk", "Dokument som kretsen valt att visa som snabblänk.", null, PanelZone.Side, required: true, hostedBy: "quicklinks"),
            D("quicklink-records", "Kretsrekord (snabblänk)", "Genvägen till kretsens rekord.", null, PanelZone.Side, hostedBy: "quicklinks"),
            D("champions", "Mästare", "Kretsens regerande mästare.", "_PanelRegionChampions", PanelZone.Side),
            D("about-region", "Om kretsen", "Kretsens presentation. Texten skrivs under Kretsens uppgifter och visas här automatiskt.", "_PanelRegionAbout"),
            D("region-instructors", "Instruktörer", "Kretsens instruktörer.", "_PanelRegionInstructors", personal: true),
            D("competitions", "Tävlingar", "Tävlingar i kretsen.", "_PanelRegionCompetitions", required: true),
            D("calendar", "Kalender", "Kretsens kalender.", "_PanelRegionCalendar", required: true),
            D("records", "Kretsrekord", "Kretsens rekord.", "_PanelRegionRecords", publicAllowed: true),
            D("document-archive", "Dokumentarkivet", "Kretsens dokument. Varje dokument har sin egen synlighet.", "_PanelRegionDocuments", required: true),
            D("board", "Styrelsen", "Kretsens styrelse.", "_PanelRegionBoard", personal: true, required: true),
        };

        public static IReadOnlyList<PanelDef> For(string ownerType) => ownerType == SiteTemplates.Club ? Club : Region;

        public static PanelDef? Find(string ownerType, string key) => For(ownerType).FirstOrDefault(p => p.Key == key);

        private static PanelPlacement[] P(params (string, PanelZone)[] a) => a.Select(x => new PanelPlacement(x.Item1, x.Item2)).ToArray();

        /// <summary>
        /// Standardlistan: panelerna på sidan <paramref name="pageKey"/> i dagens ordning. Klubbens Hem för en utloggad besökare är en egen lista
        /// (Om-text, kontakt, publika dokument, inloggningsruta). Zonen kommer ur registret. Okänd sida = tom.
        /// </summary>
        public static IReadOnlyList<PanelPlacement> Standard(string ownerType, string pageKey, bool loggedIn)
        {
            string[] keys = (ownerType, pageKey, loggedIn) switch
            {
                (SiteTemplates.Club, "home", false) => new[] { "about-text", "contact-card", "quicklink-docs", "login-prompt" },
                (SiteTemplates.Club, "home", true) => new[] { "whats-happening", "upcoming-events", "motions", "contact-card", "quicklinks" },
                (SiteTemplates.Club, "about", _) => new[] { "about-text" },
                (SiteTemplates.Club, "ranges", _) => new[] { "ranges" },
                (SiteTemplates.Club, "calendar", _) => new[] { "calendar" },
                (SiteTemplates.Club, "competitions", _) => new[] { "competitions" },
                (SiteTemplates.Club, "members", _) => new[] { "instructors", "guldserie", "member-directory" },
                (SiteTemplates.Club, "records", _) => new[] { "records" },
                (SiteTemplates.Club, "documents", _) => new[] { "document-archive" },
                (SiteTemplates.Region, "home", _) => new[] { "whats-happening", "stats-card", "motions", "upcoming-events", "contact-card", "quicklinks", "champions" },
                (SiteTemplates.Region, "about", _) => new[] { "about-region", "region-instructors" },
                (SiteTemplates.Region, "competitions", _) => new[] { "competitions" },
                (SiteTemplates.Region, "calendar", _) => new[] { "calendar" },
                (SiteTemplates.Region, "records", _) => new[] { "records" },
                (SiteTemplates.Region, "documents", _) => new[] { "document-archive" },
                (SiteTemplates.Region, "board", _) => new[] { "board" },
                _ => Array.Empty<string>(),
            };
            // Hem-sidornas tvåkolumnslayout: zonen ur registret. På alla andra sidor ritas panelerna i hela bredden efter varandra.
            var home = pageKey == "home";
            return keys.Select(k =>
            {
                var d = Find(ownerType, k) ?? throw new InvalidOperationException($"Panelen {k} saknas i registret");
                return new PanelPlacement(k, home ? d.Zone : PanelZone.Full);
            }).ToArray();
        }

        /// <summary>
        /// Får panelen <paramref name="key"/> ligga på sidan <paramref name="pageKey"/>? Bara de paneler sidans standardlista (inloggad eller utloggad) ritar;
        /// en panel flyttas inte till en annan sida (plan v2 beslut 7: förval "bara sin sida").
        /// </summary>
        public static bool AllowedOn(string ownerType, string pageKey, string key) =>
            Find(ownerType, key) != null
            && Standard(ownerType, pageKey, true).Concat(Standard(ownerType, pageKey, false)).Any(p => p.Key == key);

        /// <summary>Panelerna en klubb får välja på sidan: de tillåtna, utan det mallen bestämt är bara för utloggade (inloggningsruta, Om-text på Hem).</summary>
        public static IReadOnlyList<PanelDef> Selectable(string ownerType, string pageKey) =>
            For(ownerType).Where(d => AllowedOn(ownerType, pageKey, d.Key) && !SiteTemplates.For(ownerType).IsLoggedOutOnly(pageKey, d.Key)).ToList();

        /// <summary>Nycklar som någon standardlista ritar direkt (för testet av täckningen).</summary>
        public static IReadOnlySet<string> ListedKeys(string ownerType)
        {
            var pages = SiteTemplates.For(ownerType).Pages.Where(p => !p.IsAdmin).Select(p => p.Key);
            return pages.SelectMany(p => Standard(ownerType, p, true).Concat(Standard(ownerType, p, false))).Select(x => x.Key).ToHashSet();
        }
    }
}
