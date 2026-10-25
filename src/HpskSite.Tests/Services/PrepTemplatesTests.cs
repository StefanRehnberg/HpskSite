using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Omgång 3 (K10): mallarna per nivå enligt SHB C.3.5 och C.4, Milsnabb och flera grenar.</summary>
    public class PrepTemplatesTests
    {
        private static string[] Titles(System.Collections.Generic.List<PrepArea> t) => t.SelectMany(a => a.Items.Select(i => i.Title)).ToArray();

        [Fact]
        public void SM_soks_inte_och_har_SM_ansvarig()
        {
            var t = PrepTemplates.For("Precision", "sm");
            Titles(t).Should().Contain("Kontakta förbundets SM-ansvarige");
            Titles(t).Should().NotContain(x => x.StartsWith("Ansökan"));
            t.Single(a => a.Area == "Förbundet").Items.Should().Contain(i => i.Title.StartsWith("Förbundet fastställer SM-datumet"));
        }

        [Fact]
        public void Landsdelsmasterskap_soks_via_kretsen_fore_1_oktober_och_inbjudan_godkanns()
        {
            var t = PrepTemplates.For("Faltskytte", "ldm");
            var f = t.Single(a => a.Area == "Förbundet").Items;
            var ans = f.Single(i => i.Title == "Ansökan via kretsen till förbundet");
            ans.DueFromAnchor!(new DateTime(2028, 8, 12)).Should().Be(new DateTime(2027, 9, 1));
            f.Should().Contain(i => i.Title == "Förslag till inbjudan till förbundet för godkännande" && i.ToForbundet);
            f.Should().Contain(i => i.Title == "Fältets underlag till förbundet" && i.DaysBeforeComp == 84);
            Titles(t).Should().NotContain("Kontakta förbundets SM-ansvarige");
            Titles(t).Should().Contain(x => x.StartsWith("Utse tävlingsjury"));
        }

        [Fact]
        public void Nationell_tavling_inbjudan_for_kannedom_och_falt_till_kretsstyrelsen()
        {
            var t = PrepTemplates.For("Faltskytte", "nationell");
            var f = t.Single(a => a.Area == "Förbundet").Items;
            f.Should().Contain(i => i.Title == "Ansökan via kretsen till förbundet");
            f.Should().Contain(i => i.Title == "Inbjudan till förbundet för kännedom");
            f.Should().NotContain(i => i.Title.Contains("godkännande"));
            t.Single(a => a.Area == "Kretsen").Items.Should().Contain(i => i.Title.StartsWith("Målförutsättningar"));
            Titles(t).Should().Contain(x => x.StartsWith("Boka vapenkontrollant"));
        }

        [Fact]
        public void Kretstavling_soks_hos_kretsstyrelsen_klubbtavling_soks_inte()
        {
            Titles(PrepTemplates.For("Precision", "krets")).Should().Contain("Ansökan om kretstävling till kretsstyrelsen");
            var klubb = Titles(PrepTemplates.For("Precision", "klubb"));
            klubb.Should().NotContain(x => x.Contains("Ansökan") || x.Contains("sanktion"));
            klubb.Should().NotContain(x => x.StartsWith("Boka vapenkontrollant"));
        }

        [Fact]
        public void Okand_storlek_ar_klubb()
        {
            PrepTemplates.NormalizeSize("Mästerskap").Should().Be("klubb");
            PrepTemplates.NormalizeSize(" SM ").Should().Be("sm");
        }

        [Fact]
        public void Milsnabb_har_vandningen()
        {
            Titles(PrepTemplates.For("Milsnabb", "klubb")).Should().Contain(x => x.StartsWith("Vändanordning"));
        }

        [Fact]
        public void Flera_grenar_gemensamma_omraden_en_gang_grenens_med_namn()
        {
            var t = PrepTemplates.ForMany(new[] { "Precision", "Faltskytte" }, "sm");
            t.Count(a => a.Area == "Inbjudan & anmälan").Should().Be(1);
            t.Count(a => a.Area == "Säkerhet & sjukvård").Should().Be(1);
            t.Count(a => a.Area == "Förbundet").Should().Be(1);
            t.Should().Contain(a => a.Area == "Materiel – Fält").And.Contain(a => a.Area == "Materiel – Precision");
            // Fältets eget förbundskrav följer med in i det gemensamma området.
            t.Single(a => a.Area == "Förbundet").Items.Should().Contain(i => i.Title == "Fältets underlag till förbundet");
            t.Single(a => a.Area == "Inbjudan & anmälan").Items.Select(i => i.Title).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void Mallens_ord_ar_arrangorens_och_varje_uppgift_finns_en_gang()
        {
            foreach (var d in new[] { "Precision", "Faltskytte", "Springskytte", "Milsnabb" })
                foreach (var size in PrepTemplates.Sizes)
                {
                    var t = PrepTemplates.For(d, size);
                    t.Select(a => a.Area).Should().NotContain(a => a.Contains("Sanktion") || a == "Bankonfiguration" || a == "Stationsbygge" || a.StartsWith("Kansli"));
                    Titles(t).Should().NotContain(x => x.Contains("domare"), $"{d}/{size}");
                    Titles(t).Should().OnlyHaveUniqueItems($"{d}/{size}");
                    t.Should().Contain(a => a.Area == "Säkerhet & sjukvård").And.Contain(a => a.Area == "Funktionärer").And.Contain(a => a.Area == "Prisutdelning");
                    (t.Any(a => a.Area == "Ekonomi") && t.Any(a => a.Area == "Parkering")).Should().Be(size != "klubb", $"{d}/{size}");
                }
            // Juryn bara där SHB kräver den (C.4.1.5).
            Titles(PrepTemplates.For("Precision", "krets")).Should().NotContain(x => x.Contains("jury"));
            Titles(PrepTemplates.For("Precision", "nationell")).Should().Contain(x => x.StartsWith("Utse tävlingsjury —"));
            PrepTemplates.For("Faltskytte", "klubb").Should().Contain(a => a.Area == PrepTemplates.BuildCourseArea);
        }

        [Fact]
        public void En_gren_far_inga_grennamn()
        {
            PrepTemplates.ForMany(new[] { "Faltskytte" }, "krets").Should().NotContain(a => a.Area.Contains(" – "));
        }
    }
}
