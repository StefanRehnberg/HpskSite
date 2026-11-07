using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2b: vilka systemsidor som kan få egen text före/efter, och hur öppna den får vara (ren kod).</summary>
    public class SiteSlotRulesTests
    {
        [Fact]
        public void Admin_och_okända_sidor_kan_inte_få_slots_övriga_kan()
        {
            foreach (var t in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
            {
                Assert.False(t.CanHaveSlots("admin"));
                Assert.False(t.CanHaveSlots("nope"));
                Assert.Null(t.MostOpenLevel("admin"));
                foreach (var p in t.Pages.Where(p => !p.IsAdmin)) Assert.True(t.CanHaveSlots(p.Key), p.Key);
            }
        }

        [Fact]
        public void En_slot_får_aldrig_vara_öppnare_än_systemsidans_öppnaste_block()
        {
            var club = SiteTemplates.ClubTemplate;
            // Hem har publika block (Alla), Rekord och Dokument ligger bakom inloggning.
            Assert.Equal(SiteVisibility.Alla, club.MostOpenLevel("home"));
            Assert.Equal(SiteVisibility.Inloggade, club.MostOpenLevel("records"));
            Assert.Equal(SiteVisibility.Inloggade, club.MostOpenLevel("documents"));
            Assert.Equal(SiteVisibility.Inloggade, club.MostOpenLevel("calendar"));
        }

        [Fact]
        public void Personblock_är_aldrig_Alla_så_Medlemmar_ger_Inloggade_som_öppnaste_nivå()
        {
            // Medlemssidan bär bara personblock (capp: Inloggade) — en slot där kan inte vara öppnare än så.
            Assert.Equal(SiteVisibility.Inloggade, SiteTemplates.ClubTemplate.MostOpenLevel("members"));
        }

        [Fact]
        public void Kretsens_publika_sidor_tillåter_Alla_och_Rekord_Dokument_Styrelsen_kräver_inloggning()
        {
            var r = SiteTemplates.RegionTemplate;
            foreach (var key in new[] { "home", "about", "competitions", "calendar" }) Assert.Equal(SiteVisibility.Alla, r.MostOpenLevel(key));
            foreach (var key in new[] { "records", "documents", "board" }) Assert.Equal(SiteVisibility.Inloggade, r.MostOpenLevel(key));
        }
    }
}
