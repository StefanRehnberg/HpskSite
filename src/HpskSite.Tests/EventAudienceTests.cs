using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Vem som får anmäla sig till en klubb- eller kretshändelse.
    ///
    /// <para><b>⚠️ DEN BÄRANDE REGELN: RIKTNINGEN ÄR ENKELRIKTAD MOT DET SMALASTE.</b> Ett värde
    /// som inte går att läsa — en tom egenskap, en felstavning, en halv migrering — får aldrig
    /// ÖPPNA en anmälan. Faller den här riktningen står en händelse tyst öppen för hela landet,
    /// och ingenting på skärmen säger det.</para>
    /// </summary>
    public class EventAudienceTests
    {
        // ── Normaliseringen ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Oppen")]          // felstavat
        [InlineData("open ")]          // rätt ord, men med skräp — trimmas och godtas
        [InlineData("AllaMedlemmar")]  // svenskt, alltså okänt
        [InlineData("Everyone")]
        [InlineData("1")]
        public void Okant_eller_tomt_varde_blir_alltid_det_smalaste(string? raw)
        {
            var v = EventAudience.Normalise(raw);
            // "open " trimmas till ett giltigt värde; allt annat i listan ska bli Club.
            if ((raw ?? "").Trim().Equals("open", System.StringComparison.OrdinalIgnoreCase))
                v.Should().Be(EventAudience.Open);
            else
                v.Should().Be(EventAudience.Club, "ett olasbart varde far ALDRIG oppna en anmalan");
        }

        [Theory]
        [InlineData("Club")]
        [InlineData("Region")]
        [InlineData("AllMembers")]
        [InlineData("Open")]
        public void Kanda_varden_overlever_normaliseringen(string raw)
            => EventAudience.Normalise(raw).Should().Be(raw);

        [Theory]
        [InlineData("club")]
        [InlineData("REGION")]
        [InlineData("allmembers")]
        [InlineData("oPeN")]
        public void Skiftlage_spelar_ingen_roll_men_svaret_ar_kanoniskt(string raw)
            => EventAudience.All.Should().Contain(EventAudience.Normalise(raw));

        // ── ⚠️ Inloggningskravet ────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Bara EN nivå släpper in någon utan konto. Vore `RequiresLogin` falskt för en till
        /// skulle den anonyma anmälningsvägen öppnas på händelser vars arrangör valt en nivå som
        /// förutsätter medlemskap — och det syns ingenstans förrän en främling står på listan.
        /// </summary>
        [Fact]
        public void Bara_Open_slapper_in_utan_konto()
        {
            EventAudience.RequiresLogin(EventAudience.Club).Should().BeTrue();
            EventAudience.RequiresLogin(EventAudience.Region).Should().BeTrue();
            EventAudience.RequiresLogin(EventAudience.AllMembers).Should().BeTrue();
            EventAudience.RequiresLogin(EventAudience.Open).Should().BeFalse();

            EventAudience.AllowsAnonymous(EventAudience.Open).Should().BeTrue();
            EventAudience.AllowsAnonymous(EventAudience.AllMembers).Should().BeFalse();
        }

        /// <summary>⚠️ Och ett trasigt värde kräver inloggning — samma enkelriktning.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Oppen")]
        public void Ett_trasigt_varde_slapper_aldrig_in_nagon_utan_konto(string? raw)
        {
            EventAudience.RequiresLogin(raw).Should().BeTrue();
            EventAudience.AllowsAnonymous(raw).Should().BeFalse();
        }

        // ── Bredd-ordningen ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ordningen i <see cref="EventAudience.All"/> är BÄRANDE — den styr både rullgardinen och
        /// kapningen av deltagarlistans synlighet. Kastas den om följer listan med.
        /// </summary>
        [Fact]
        public void Ordningen_gar_fran_smalast_till_bredast()
            => EventAudience.All.Should().Equal(
                EventAudience.Club, EventAudience.Region,
                EventAudience.AllMembers, EventAudience.Open);

        [Fact]
        public void Bredare_niva_ar_minst_lika_bred_som_smalare()
        {
            EventAudience.IsAtLeastAsWideAs(EventAudience.Open, EventAudience.Club).Should().BeTrue();
            EventAudience.IsAtLeastAsWideAs(EventAudience.AllMembers, EventAudience.Region).Should().BeTrue();
            EventAudience.IsAtLeastAsWideAs(EventAudience.Region, EventAudience.Region).Should().BeTrue();

            EventAudience.IsAtLeastAsWideAs(EventAudience.Club, EventAudience.Region).Should().BeFalse();
            EventAudience.IsAtLeastAsWideAs(EventAudience.Region, EventAudience.AllMembers).Should().BeFalse();
        }

        /// <summary>
        /// ⚠️ Det här predikatet avgör om deltagarlistan kapas vid kretsen OCH om dörrlistan
        /// släpper varje medlem. Ett trasigt värde måste därför räknas som smalast här också.
        /// </summary>
        [Fact]
        public void Ett_trasigt_varde_ar_aldrig_bredare_an_klubben()
        {
            EventAudience.IsAtLeastAsWideAs("Oppen", EventAudience.Region).Should().BeFalse();
            EventAudience.IsAtLeastAsWideAs(null, EventAudience.AllMembers).Should().BeFalse();
            EventAudience.IsAtLeastAsWideAs("", EventAudience.Region).Should().BeFalse();
        }

        // ── Beskeden ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Meddelandet låg i två kopior som båda sa "medlemmar i {klubben}" — vilket blev direkt
        /// osant i samma stund nivån gick att ändra. Ett besked som beskriver en regel som inte
        /// gäller skickar arrangören att leta efter fel sak.
        /// </summary>
        [Fact]
        public void Beskedet_beskriver_den_valda_nivan_och_inte_klubben()
        {
            EventAudience.Phrase(EventAudience.Club, "Ankeborg", false).Should().Contain("Ankeborg");
            EventAudience.Phrase(EventAudience.Region, "Ankeborg", false).Should().Contain("kretsens");
            EventAudience.Phrase(EventAudience.AllMembers, "Ankeborg", false)
                .Should().Contain("alla medlemmar").And.NotContain("Ankeborg");
            EventAudience.Phrase(EventAudience.Open, "Ankeborg", false)
                .Should().Contain("utan konto").And.NotContain("Ankeborg");
        }

        /// <summary>En KRETShändelse pa standardnivan galler hela kretsen, inte en klubb.</summary>
        [Fact]
        public void Kretshandelse_pa_standardnivan_namner_kretsen()
            => EventAudience.Phrase(EventAudience.Club, "Ankelands krets", true)
                .Should().Contain("kretsens klubbar");

        [Fact]
        public void Varje_niva_har_en_egen_lasbar_etikett()
        {
            var labels = EventAudience.All.Select(EventAudience.Display).ToList();
            labels.Should().OnlyHaveUniqueItems();
            labels.Should().NotContain(string.Empty);
        }
    }
}
