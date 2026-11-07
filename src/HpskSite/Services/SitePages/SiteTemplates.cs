namespace HpskSite.Services.SitePages
{
    /// <summary>Ett block i en mallsida. <see cref="Key"/> är en permanent identitet: den döps aldrig om och återanvänds aldrig.</summary>
    /// <param name="Key">Permanent nyckel (t.ex. <c>about-text</c>). Slugg och etikett är attribut, inte nycklar.</param>
    /// <param name="Visibility">Dagens synlighet för blocket, bevarad (ny ≤ gammal).</param>
    /// <param name="PersonalData">Bär personuppgifter: kan aldrig bli <see cref="SiteVisibility.Alla"/> (<see cref="SiteAccess.CapPersonal"/>).</param>
    /// <param name="Retired">Utgången blocktyp: renderas inte men nyckeln finns kvar.</param>
    public sealed record SiteBlockDef(string Key, SiteVisibility Visibility, bool PersonalData = false, bool Retired = false);

    /// <summary>
    /// En sida i mallen. <see cref="Slug"/> är en offentlig adress och därför fast. Hem har tomt slugg och
    /// ingen <c>PageAddress</c>-rad (roten är Umbraco-nodens adress). <see cref="IsAdmin"/> = systemposten Admin.
    /// </summary>
    public sealed record SitePageDef(
        string Key, string Slug, string Label, int Order, IReadOnlyList<SiteBlockDef> Blocks, bool IsAdmin = false)
    {
        public bool IsHome => Slug.Length == 0 && !IsAdmin;
    }

    /// <summary>Mallen för en ägartyp. <see cref="Version"/> höjs vid varje ändring av registret (loggas vid aktivering).</summary>
    public sealed record SiteTemplate(
        string OwnerType, int Version, bool MenuRequiresLogin, IReadOnlyList<SitePageDef> Pages)
    {
        /// <summary>Sidan bakom ett slugg under ägaren. Hem (tomt slugg) och Admin har inget slugg att slå upp: null.</summary>
        public SitePageDef? PageBySlug(string slug) =>
            string.IsNullOrEmpty(slug)
                ? null
                : Pages.FirstOrDefault(p => !p.IsAdmin && p.Slug.Length > 0 && string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

        public SitePageDef? PageByKey(string key) =>
            Pages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

        /// <summary>Sidan syns i menyn för <paramref name="who"/> om minst ett av dess block syns, Admin bara för ägarens admin.</summary>
        public bool PageVisible(SitePageDef page, SiteViewer who)
        {
            if (page.IsAdmin) return who.LoggedIn && who.IsOwnerAdmin;
            return page.Blocks.Any(b => !b.Retired && SiteAccess.CanSee(EffectiveVisibility(b), who));
        }

        /// <summary>Blockets synlighet efter taket för personuppgiftsblock.</summary>
        public static SiteVisibility EffectiveVisibility(SiteBlockDef b) =>
            b.PersonalData ? SiteAccess.CapPersonal(b.Visibility) : b.Visibility;

        /// <summary>Menyn för <paramref name="who"/>. Klubbens utloggade besökare har ingen meny (dagens sida), kretsens har fyra.</summary>
        public IReadOnlyList<SitePageDef> MenuFor(SiteViewer who)
        {
            if (MenuRequiresLogin && !who.LoggedIn) return Array.Empty<SitePageDef>();
            return Pages.Where(p => PageVisible(p, who)).OrderBy(p => p.Order).ToList();
        }

        /// <summary>Blocken på <paramref name="page"/> som <paramref name="who"/> får se, i mallens ordning.</summary>
        public IReadOnlyList<SiteBlockDef> BlocksFor(SitePageDef page, SiteViewer who) =>
            page.Blocks.Where(b => !b.Retired && SiteAccess.CanSee(EffectiveVisibility(b), who)).ToList();
    }

    /// <summary>
    /// Mallregistret: systemsidorna och standardmenyn för klubbar och kretsar, definierade i kod (plan v4, 2.2).
    /// Reglerna: sid- och blocknycklar är permanenta; sluggarna är offentliga adresser och fasta; ändras något
    /// här faller <c>SitePageTemplateTests.Mallens_ögonblicksbild_är_godkänd</c> tills ögonblicksbilden uppdaterats
    /// med ett uttryckligt beslut (ett block som höjs mot Alla bryter regeln "ny ≤ gammal").
    /// </summary>
    public static class SiteTemplates
    {
        public const string Club = "Club";
        public const string Region = "Region";

        private const SiteVisibility All = SiteVisibility.Alla;
        private const SiteVisibility Auth = SiteVisibility.Inloggade;
        private const SiteVisibility Out = SiteVisibility.Utloggad;

        /// <summary>
        /// Klubben: dagens nio flikar. Utloggad ser bara startsidan (Om-text, kontaktkort, publika snabblänkdokument,
        /// inloggningsruta); alla andra flikar kräver bara inloggning (Club.cshtml:102-181, ClubNavigation.cshtml).
        /// </summary>
        public static readonly SiteTemplate ClubTemplate = new(Club, Version: 1, MenuRequiresLogin: true, new[]
        {
            new SitePageDef("home", "", "Hem", 10, new[]
            {
                new SiteBlockDef("about-text", Out),            // utloggad vy; syns inte på inloggad Hem idag
                new SiteBlockDef("login-prompt", Out),
                new SiteBlockDef("contact-card", All),          // inloggad Hem visar det bara när något är ifyllt
                new SiteBlockDef("quicklink-docs", All),        // publika snabblänkdokument
                new SiteBlockDef("whats-happening", Auth),
                new SiteBlockDef("upcoming-events", Auth),      // inkl. händelsetypen Nyhet
                new SiteBlockDef("motions", Auth),
                new SiteBlockDef("quicklinks", Auth),
            }),
            new SitePageDef("about", "om-klubben", "Om Klubben", 20, new[] { new SiteBlockDef("about-text", Auth) }),
            new SitePageDef("ranges", "banor", "Banor", 30, new[] { new SiteBlockDef("ranges", Auth) }),
            new SitePageDef("calendar", "kalender", "Kalender", 40, new[] { new SiteBlockDef("calendar", Auth) }),
            // Etiketten är dagens "Tävlingar"; sluggen undviker det reserverade "tavlingar" (CompetitionPagesController).
            new SitePageDef("competitions", "vara-tavlingar", "Tävlingar", 50, new[] { new SiteBlockDef("competitions", Auth) }),
            new SitePageDef("members", "medlemmar", "Medlemmar", 60, new[]
            {
                new SiteBlockDef("instructors", Auth, PersonalData: true),
                new SiteBlockDef("guldserie", Auth, PersonalData: true),
                new SiteBlockDef("member-directory", Auth, PersonalData: true), // bara namn och bild; e-post bara för admin
            }),
            new SitePageDef("records", "rekord", "Rekord", 70, new[] { new SiteBlockDef("records", Auth) }),
            // Dokumentens egen nivå (Public/Authenticated/ClubMembers/ClubAdmins) gäller per dokument.
            new SitePageDef("documents", "dokument", "Dokument", 80, new[] { new SiteBlockDef("document-archive", Auth) }),
            new SitePageDef("admin", "", "Admin", 90, new[] { new SiteBlockDef("admin-panel", SiteVisibility.Dold) }, IsAdmin: true),
        });

        /// <summary>
        /// Kretsen: dagens åtta poster. Hem, Om, Tävlingar och Kalender är publika; Rekord, Dokument och Styrelsen
        /// kräver inloggning (RegionalNavigation.cshtml, RegionalPage.cshtml, _RegionPublicContent.cshtml).
        /// </summary>
        public static readonly SiteTemplate RegionTemplate = new(Region, Version: 1, MenuRequiresLogin: false, new[]
        {
            new SitePageDef("home", "", "Hem", 10, new[]
            {
                new SiteBlockDef("welcome", All),
                new SiteBlockDef("whats-happening", All),
                new SiteBlockDef("stats-card", All),
                new SiteBlockDef("upcoming-events", All),
                new SiteBlockDef("contact-card", All),
                new SiteBlockDef("quicklink-docs", All),        // kretsens snabblänkdokument
                new SiteBlockDef("quicklinks", All),
                new SiteBlockDef("quicklink-records", Auth),    // "Kretsrekord" är bara för inloggade
                new SiteBlockDef("motions", Auth),
                new SiteBlockDef("champions", Auth),
            }),
            new SitePageDef("about", "om", "Om", 20, new[]
            {
                new SiteBlockDef("about-region", All),
                new SiteBlockDef("region-instructors", Auth, PersonalData: true),
            }),
            new SitePageDef("competitions", "vara-tavlingar", "Tävlingar", 30, new[] { new SiteBlockDef("competitions", All) }),
            new SitePageDef("calendar", "kalender", "Kalender", 40, new[] { new SiteBlockDef("calendar", All) }),
            new SitePageDef("records", "rekord", "Rekord", 50, new[] { new SiteBlockDef("records", Auth) }),
            new SitePageDef("documents", "dokument", "Dokument", 60, new[] { new SiteBlockDef("document-archive", Auth) }),
            new SitePageDef("board", "styrelsen", "Styrelsen", 70, new[] { new SiteBlockDef("board", Auth, PersonalData: true) }),
            new SitePageDef("admin", "", "Administration", 80, new[] { new SiteBlockDef("admin-panel", SiteVisibility.Dold) }, IsAdmin: true),
        });

        public static SiteTemplate For(string ownerType) => ownerType switch
        {
            Club => ClubTemplate,
            Region => RegionTemplate,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Okänd ägartyp"),
        };

        /// <summary>
        /// Alla mallsluggar för ägartypen (utan Hem och Admin). Används av spärren mot klubbnamn som skuggar kretsens
        /// sidor och av findern.
        /// </summary>
        public static IReadOnlyList<string> Slugs(string ownerType) =>
            For(ownerType).Pages.Where(p => !p.IsAdmin && p.Slug.Length > 0).Select(p => p.Slug).ToList();
    }
}
