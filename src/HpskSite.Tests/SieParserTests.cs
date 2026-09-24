using System;
using System.Linq;
using System.Text;
using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// SIE-importens läsare (P10.2).
    ///
    /// <para><b>⚠️ Två fel är tysta och dyra:</b> fel kodsida (kontoplanen får "F”reningskonto"
    /// och ingen ser det förrän rapporten skrivs ut), och en rad som läses två gånger (#RTRANS
    /// följs av en identisk #TRANS — tar vi båda är verifikationen fel utan att den slutar balansera,
    /// eftersom även motraden kan vara dubbel).</para>
    /// </summary>
    public class SieParserTests
    {
        static SieParserTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        private const string Sample =
            "#FLAGGA 0\n#FORMAT PC8\n#SIETYP 4\n#PROGRAM \"SpeedLedger\" 1.0\n#FNAMN \"Varbergs PK\"\n#ORGNR 849600-1234\n" +
            "#RAR 0 20260101 20261231\n#RAR -1 20250101 20251231\n" +
            "#KONTO 1930 \"Företagskonto\"\n#KONTO 2060 \"Eget kapital\"\n#KONTO 3010 \"Medlemsavgifter\"\n" +
            "#KTYP 1930 T\n" +
            "#IB 0 1930 48312.50\n#IB 0 2060 -48312.50\n#IB -1 1930 1000.00\n" +
            "#UB 0 1930 50000.00\n" +
            "#VER A 1 20260115 \"Medlemsavgift \\\"Anna\\\"\" 20260116\n{\n" +
            "   #TRANS 1930 {} 500.00\n   #TRANS 3010 {\"1\" \"10\"} -500.00 20260115 \"Anna A\"\n}\n" +
            "#VER A 2 20260120 \"Rättad\"\n{\n" +
            "   #BTRANS 1930 {} 100.00\n   #RTRANS 1930 {} 200.00\n   #TRANS 1930 {} 200.00\n   #TRANS 3010 {} -200.00\n}\n";

        private static SieFile ParseText(string text, Encoding enc) => SieParser.Parse(enc.GetBytes(text));

        [Fact]
        public void Huvudet_kontona_och_aret_lases()
        {
            var f = ParseText(Sample, Encoding.GetEncoding(437));
            f.Errors.Should().BeEmpty();
            f.Program.Should().Be("SpeedLedger");
            f.CompanyName.Should().Be("Varbergs PK");
            f.OrgNumber.Should().Be("849600-1234");
            f.YearStart.Should().Be(new DateTime(2026, 1, 1));
            f.YearEnd.Should().Be(new DateTime(2026, 12, 31));
            f.Accounts[1930].Should().Be("Företagskonto");
        }

        [Fact]
        public void Bara_innevarande_ars_IB_tas_in()
        {
            var f = ParseText(Sample, Encoding.GetEncoding(437));
            f.OpeningBalances.Should().HaveCount(2);
            f.OpeningBalances[1930].Should().Be(48312.50m);
            f.OpeningBalances.Values.Sum().Should().Be(0m);
        }

        [Fact]
        public void Verifikationer_med_citat_objektlista_och_radtext()
        {
            var f = ParseText(Sample, Encoding.GetEncoding(437));
            f.Vouchers.Should().HaveCount(2);
            var v = f.Vouchers[0];
            v.Series.Should().Be("A");
            v.Number.Should().Be(1);
            v.Date.Should().Be(new DateTime(2026, 1, 15));
            v.Text.Should().Be("Medlemsavgift \"Anna\"");
            v.Transactions.Should().HaveCount(2);
            v.Transactions[1].Account.Should().Be(3010);
            v.Transactions[1].Amount.Should().Be(-500m);
            v.Transactions[1].Text.Should().Be("Anna A");
            v.Imbalance.Should().Be(0m);
            v.Total.Should().Be(500m);
        }

        [Fact]
        public void RTRANS_och_BTRANS_lases_INTE_som_rader()
        {
            var v = ParseText(Sample, Encoding.GetEncoding(437)).Vouchers[1];
            v.Transactions.Should().HaveCount(2);
            v.Total.Should().Be(200m);
            v.Imbalance.Should().Be(0m);
        }

        [Fact]
        public void Det_som_inte_tas_in_sags()
        {
            var f = ParseText(Sample, Encoding.GetEncoding(437));
            f.Notes.Should().Contain(n => n.StartsWith("#RTRANS"));
            f.Notes.Should().Contain(n => n.StartsWith("#BTRANS"));
            f.Notes.Should().Contain(n => n.StartsWith("#UB"));
        }

        [Theory]
        [InlineData(437)]
        [InlineData(1252)]
        [InlineData(65001)]
        public void Svenska_bokstaver_overlever_alla_tre_kodningarna(int codePage)
        {
            var f = ParseText(Sample, Encoding.GetEncoding(codePage));
            f.Accounts[1930].Should().Be("Företagskonto");
            f.Vouchers[1].Text.Should().Be("Rättad");
        }

        [Fact]
        public void En_oavslutad_verifikation_ar_ett_fel()
        {
            var f = ParseText("#VER A 1 20260115 \"x\"\n{\n#TRANS 1930 {} 1.00\n", Encoding.ASCII);
            f.Errors.Should().Contain(e => e.Contains("slutar mitt i"));
        }

        [Fact]
        public void En_trasig_rad_namnges_med_radnummer()
        {
            var f = ParseText("#VER A x 20260115\n{\n}\n#KONTO 1930 \"öppet citat\n", Encoding.UTF8);
            f.Errors.Should().Contain(e => e.StartsWith("Rad 1:"));
            f.Errors.Should().Contain(e => e.StartsWith("Rad 4:"));
        }

        [Fact]
        public void Komma_som_decimaltecken_godtas()
        {
            SieParser.TryAmount("-1234,50", out var v).Should().BeTrue();
            v.Should().Be(-1234.50m);
        }
    }
}
