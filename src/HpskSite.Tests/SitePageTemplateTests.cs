using System.Text;
using System.Text.RegularExpressions;
using HpskSite.Services.Addresses;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna steg 1 (plan v4, avsnitt 2.2–2.5): mallregistret. Det som en besökare ser med brytaren PÅ ska vara
    /// exakt det dagens vy visar för samma roll: ny synlighet ≤ gammal. Förväntningarna är skrivna ur dagens vykod
    /// (Club.cshtml:102-298, ClubNavigation.cshtml, RegionalNavigation.cshtml, _RegionPublicContent.cshtml), inte ur mallen.
    /// </summary>
    public class SitePageTemplateTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer LoggedIn = new(true, false, false, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);

        private static string[] Labels(IEnumerable<SitePageDef> pages) => pages.Select(p => p.Label).ToArray();
        private static string[] Keys(IEnumerable<SiteBlockDef> blocks) => blocks.Select(b => b.Key).ToArray();

        // ── Klubben ────────────────────────────────────────────────────────────

        [Fact]
        public void Klubb_utloggad_har_ingen_meny_och_exakt_dagens_startsida()
        {
            var t = SiteTemplates.ClubTemplate;
            Assert.Empty(t.MenuFor(Anon));
            // Club.cshtml:102-181: Om-text, kontaktkort, publika snabblänkdokument, inloggningsruta. Inget mer.
            Assert.Equal(new[] { "about-text", "login-prompt", "contact-card", "quicklink-docs" },
                Keys(t.BlocksFor(t.PageByKey("home")!, Anon)));
            foreach (var p in t.Pages.Where(p => !p.IsHome))
                Assert.Empty(t.BlocksFor(p, Anon));
        }

        [Fact]
        public void Klubb_inloggad_har_dagens_åtta_flikar_utan_Admin()
        {
            var t = SiteTemplates.ClubTemplate;
            Assert.Equal(new[] { "Hem", "Om Klubben", "Banor", "Kalender", "Tävlingar", "Medlemmar", "Rekord", "Dokument" },
                Labels(t.MenuFor(LoggedIn)));
        }

        [Fact]
        public void Klubb_admin_har_dagens_nio_flikar_med_Admin_sist()
        {
            var t = SiteTemplates.ClubTemplate;
            Assert.Equal(new[] { "Hem", "Om Klubben", "Banor", "Kalender", "Tävlingar", "Medlemmar", "Rekord", "Dokument", "Admin" },
                Labels(t.MenuFor(Admin)));
        }

        [Fact]
        public void Klubb_inloggad_Hem_visar_inte_Om_texten_eller_inloggningsrutan()
        {
            // Club.cshtml:192-296: inloggad Hem har Det här händer, Kommande, motioner, kontakt, snabblänkar — inte Om-texten
            // (den ligger på Om Klubben) och inte inloggningsrutan (bara för utloggade).
            var t = SiteTemplates.ClubTemplate;
            Assert.Equal(new[] { "contact-card", "quicklink-docs", "whats-happening", "upcoming-events", "motions" },
                Keys(t.BlocksFor(t.PageByKey("home")!, LoggedIn)));
        }

        [Fact]
        public void Admin_sidan_bär_bara_adminpanelen_och_syns_bara_för_ägarens_admin()
        {
            var t = SiteTemplates.ClubTemplate;
            var admin = t.PageByKey("admin")!;
            Assert.True(admin.IsAdmin);
            Assert.False(t.PageVisible(admin, Anon));
            Assert.False(t.PageVisible(admin, LoggedIn));
            Assert.True(t.PageVisible(admin, Admin));
            Assert.Null(t.PageBySlug("")); // Hem och Admin har inget slugg att slå upp
        }

        // ── Kretsen ────────────────────────────────────────────────────────────

        [Fact]
        public void Krets_utloggad_har_dagens_fyra_publika_poster()
        {
            var t = SiteTemplates.RegionTemplate;
            Assert.Equal(new[] { "Hem", "Om", "Tävlingar", "Kalender" }, Labels(t.MenuFor(Anon)));
        }

        [Fact]
        public void Krets_utloggad_Om_visar_texten_men_inte_instruktörerna()
        {
            // RegionalPage.cshtml:162-183: aboutRegion för alla, instruktörerna bara inloggad.
            var t = SiteTemplates.RegionTemplate;
            Assert.Equal(new[] { "about-region" }, Keys(t.BlocksFor(t.PageByKey("about")!, Anon)));
            Assert.Equal(new[] { "about-region", "region-instructors" }, Keys(t.BlocksFor(t.PageByKey("about")!, LoggedIn)));
        }

        [Fact]
        public void Krets_utloggad_Hem_saknar_motionskort_mästare_och_Kretsrekord()
        {
            var t = SiteTemplates.RegionTemplate;
            var anon = Keys(t.BlocksFor(t.PageByKey("home")!, Anon));
            Assert.DoesNotContain("motions", anon);
            Assert.DoesNotContain("champions", anon);
            Assert.DoesNotContain("quicklink-records", anon);
            Assert.Contains("contact-card", anon);          // _RegionPublicContent.cshtml:113-152, Alla
            Assert.Contains("upcoming-events", anon);       // :94-106, Alla
            Assert.Contains("quicklink-docs", anon);        // :198, publika
            var loggedIn = Keys(t.BlocksFor(t.PageByKey("home")!, LoggedIn));
            Assert.Contains("motions", loggedIn);
            Assert.Contains("champions", loggedIn);
            Assert.DoesNotContain("quicklink-records", loggedIn);   // Kretsrekord-genvägen är borttagen (finns i menyn)
        }

        [Fact]
        public void Krets_inloggad_har_dagens_sju_poster_och_admin_åtta()
        {
            var t = SiteTemplates.RegionTemplate;
            Assert.Equal(new[] { "Hem", "Om", "Tävlingar", "Kalender", "Rekord", "Dokument", "Styrelsen" }, Labels(t.MenuFor(LoggedIn)));
            Assert.Equal(new[] { "Hem", "Om", "Tävlingar", "Kalender", "Rekord", "Dokument", "Styrelsen", "Administration" }, Labels(t.MenuFor(Admin)));
        }

        // ── Regler som gäller båda ─────────────────────────────────────────────

        [Fact]
        public void Inget_personuppgiftsblock_är_publikt()
        {
            foreach (var t in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
                foreach (var b in t.Pages.SelectMany(p => p.Blocks).Where(b => b.PersonalData))
                    Assert.NotEqual(SiteVisibility.Alla, SiteTemplate.EffectiveVisibility(b));
        }

        [Fact]
        public void Taket_gäller_även_om_mallen_felaktigt_satte_Alla_på_ett_personblock()
        {
            var bad = new SiteBlockDef("member-directory", SiteVisibility.Alla, PersonalData: true);
            Assert.Equal(SiteVisibility.Inloggade, SiteTemplate.EffectiveVisibility(bad));
        }

        [Fact]
        public void Nycklar_och_sluggar_är_entydiga_och_låsta_till_ett_enkelt_format()
        {
            foreach (var t in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
            {
                Assert.Equal(t.Pages.Count, t.Pages.Select(p => p.Key).Distinct().Count());
                var slugs = t.Pages.Where(p => p.Slug.Length > 0).Select(p => p.Slug).ToList();
                Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                foreach (var s in slugs) Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), s);
                foreach (var p in t.Pages)
                    Assert.Equal(p.Blocks.Count, p.Blocks.Select(b => b.Key).Distinct().Count());
                Assert.Single(t.Pages, p => p.IsHome);
                Assert.Single(t.Pages, p => p.IsAdmin);
            }
        }

        [Fact]
        public void Ingen_mallslugg_är_ett_reserverat_segment_under_klubb_eller_krets()
        {
            // CompetitionPagesController äger tavlingar/handelser/serier och klubbkatalogen heter klubbar; år är tävlingsår.
            foreach (var t in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
                foreach (var slug in SiteTemplates.Slugs(t.OwnerType))
                    Assert.False(PageAddressRules.IsReservedOwnerChildSegment(slug), $"{t.OwnerType}: sluggen '{slug}' är reserverad");
            // Särskilt: tävlingssidan får inte heta tavlingar.
            Assert.NotNull(SiteTemplates.ClubTemplate.PageBySlug("vara-tavlingar"));
            Assert.Null(SiteTemplates.ClubTemplate.PageBySlug("tavlingar"));
        }

        [Fact]
        public void Slugguppslaget_är_skiftlägesokänsligt_och_känner_inte_Hem()
        {
            var t = SiteTemplates.ClubTemplate;
            Assert.Equal("banor", t.PageBySlug("BANOR")?.Slug);
            Assert.Null(t.PageBySlug("okand"));
            Assert.Null(t.PageBySlug(""));
        }

        // ── Ögonblicksbild ─────────────────────────────────────────────────────

        /// <summary>
        /// Mallen är kod och slår igenom hos ALLA påslagna klubbar samtidigt. Därför faller det här testet när registret
        /// ändras utan att ögonblicksbilden uppdaterats. Uppdatera bilden bara efter ett uttryckligt beslut, och bara med
        /// golden master (ny synlighet ≤ gammal) som underlag; höj <c>Version</c> i samma commit.
        /// </summary>
        [Fact]
        public void Mallens_ögonblicksbild_är_godkänd()
        {
            var actual = Canonical(SiteTemplates.ClubTemplate) + Canonical(SiteTemplates.RegionTemplate);
            var path = SnapshotPath();
            // Uppdatera ÖGONBLICKSBILDEN medvetet: HPSK_UPDATE_SNAPSHOT=1 dotnet test --filter Mallens_ögonblicksbild
            if (Environment.GetEnvironmentVariable("HPSK_UPDATE_SNAPSHOT") == "1")
                File.WriteAllText(path, actual, new UTF8Encoding(false));
            var approved = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "(saknas)";
            Assert.True(approved == actual.Replace("\r\n", "\n"),
                "Mallregistret har ändrats. Om det är avsiktligt: höj Version, kontrollera golden master (ny synlighet ≤ gammal) "
                + "och uppdatera ögonblicksbilden med HPSK_UPDATE_SNAPSHOT=1.\n--- NY ÖGONBLICKSBILD ---\n" + actual);
        }

        private static string SnapshotPath([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.Combine(Path.GetDirectoryName(thisFile)!, "SitePageTemplateSnapshot.txt");

        private static string Canonical(SiteTemplate t)
        {
            var sb = new StringBuilder();
            sb.Append($"{t.OwnerType} v{t.Version} menyKräverInloggning={t.MenuRequiresLogin}\n");
            foreach (var p in t.Pages)
            {
                sb.Append($"  {p.Key} /{p.Slug} \"{p.Label}\" #{p.Order}{(p.IsAdmin ? " ADMIN" : "")}\n");
                foreach (var b in p.Blocks)
                    sb.Append($"    {b.Key} {b.Visibility}{(b.PersonalData ? " personuppgifter" : "")}{(b.Retired ? " utgången" : "")}\n");
            }
            return sb.ToString();
        }
    }
}
