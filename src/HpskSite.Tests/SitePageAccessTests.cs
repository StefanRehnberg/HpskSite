using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna steg 1: de rena synlighetsreglerna (plan v4, avsnitt 4). Ingen databas, ingen app.</summary>
    public class SitePageAccessTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer LoggedIn = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Board = new(true, true, true, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);
        // En medlem som INTE är inloggad kan inte finnas; ett flaggat men utloggat värde ska ändå aldrig ge åtkomst.
        private static readonly SiteViewer LoggedOutButFlagged = new(false, true, true, true);

        [Theory]
        [InlineData(SiteVisibility.Alla, true, true, true, true, true)]
        [InlineData(SiteVisibility.Inloggade, false, true, true, true, true)]
        [InlineData(SiteVisibility.Klubbmedlemmar, false, false, true, true, true)]
        [InlineData(SiteVisibility.Styrelse, false, false, false, true, true)]
        [InlineData(SiteVisibility.Dold, false, false, false, false, true)]
        [InlineData(SiteVisibility.Utloggad, true, false, false, false, false)]
        public void CanSee_följer_modellen(SiteVisibility v, bool anon, bool loggedIn, bool member, bool board, bool admin)
        {
            Assert.Equal(anon, SiteAccess.CanSee(v, Anon));
            Assert.Equal(loggedIn, SiteAccess.CanSee(v, LoggedIn));
            Assert.Equal(member, SiteAccess.CanSee(v, Member));
            Assert.Equal(board, SiteAccess.CanSee(v, Board));
            Assert.Equal(admin, SiteAccess.CanSee(v, Admin));
        }

        [Theory]
        [InlineData(SiteVisibility.Inloggade)]
        [InlineData(SiteVisibility.Klubbmedlemmar)]
        [InlineData(SiteVisibility.Styrelse)]
        [InlineData(SiteVisibility.Dold)]
        public void Utloggad_ger_aldrig_åtkomst_till_något_som_kräver_inloggning(SiteVisibility v) =>
            Assert.False(SiteAccess.CanSee(v, LoggedOutButFlagged));

        [Fact]
        public void Okänd_synlighet_är_aldrig_synlig() =>
            Assert.False(SiteAccess.CanSee((SiteVisibility)99, Admin));

        [Fact]
        public void Admin_ser_inte_utloggade_block()
        {
            // Inloggningsrutan hör till den som är utloggad; en inloggad admin ser den inte.
            Assert.False(SiteAccess.CanSee(SiteVisibility.Utloggad, Admin));
        }

        [Theory]
        [InlineData(SiteVisibility.Alla, SiteVisibility.Inloggade, SiteVisibility.Inloggade)]
        [InlineData(SiteVisibility.Styrelse, SiteVisibility.Inloggade, SiteVisibility.Styrelse)]
        [InlineData(SiteVisibility.Klubbmedlemmar, SiteVisibility.Dold, SiteVisibility.Dold)]
        [InlineData(SiteVisibility.Alla, SiteVisibility.Alla, SiteVisibility.Alla)]
        [InlineData(SiteVisibility.Utloggad, SiteVisibility.Alla, SiteVisibility.Utloggad)]
        public void Striktaste_vinner(SiteVisibility a, SiteVisibility b, SiteVisibility expected)
        {
            Assert.Equal(expected, SiteAccess.Strictest(a, b));
            Assert.Equal(expected, SiteAccess.Strictest(b, a));
        }

        [Fact]
        public void Personuppgiftstaket_gör_Alla_till_Inloggade_men_rör_inget_annat()
        {
            Assert.Equal(SiteVisibility.Inloggade, SiteAccess.CapPersonal(SiteVisibility.Alla));
            Assert.Equal(SiteVisibility.Styrelse, SiteAccess.CapPersonal(SiteVisibility.Styrelse));
            Assert.Equal(SiteVisibility.Dold, SiteAccess.CapPersonal(SiteVisibility.Dold));
        }

        [Fact]
        public void Utloggad_kan_inte_väljas_av_en_klubb()
        {
            Assert.False(SiteAccess.IsSelectable(SiteVisibility.Utloggad));
            Assert.False(SiteAccess.IsSelectable((SiteVisibility)99));
            Assert.True(SiteAccess.IsSelectable(SiteVisibility.Alla));
            Assert.True(SiteAccess.IsSelectable(SiteVisibility.Dold));
        }
    }
}
