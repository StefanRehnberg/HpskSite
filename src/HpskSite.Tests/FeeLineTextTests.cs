using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    public class FeeLineTextTests
    {
        private static MembershipFeeChargeLine L(string kind, string text, decimal amount, int? subject = null)
            => new() { Kind = kind, Description = text, Amount = amount, SubjectMemberId = subject };

        private static string? Name(int id) => id switch { 1 => "Anna Andersson", 2 => "Bo Andersson", 3 => "Cecilia Andersson", _ => null };

        [Fact] public void Familjeavgiften_namnger_familjemedlemmarna_och_varje_tillagg_vem_det_galler()
        {
            var lines = new[]
            {
                L(ClubFeeCalculator.BaseKind, "Familj", 1200m),
                L(ClubFeeCalculator.PartKind, "SPSF", 300m, 1),
                L(ClubFeeCalculator.PartKind, "SPSF", 300m, 2),
            };
            var d = FeeLineText.Describe(lines, "Anna Andersson", new[] { "Bo Andersson", "Cecilia Andersson" }, Name);
            Assert.Equal("Grundavgift Familj (för Anna Andersson, Bo Andersson och Cecilia Andersson)", d[0].Description);
            Assert.Equal("Tillägg SPSF – Anna Andersson", d[1].Description);
            Assert.Equal("Tillägg SPSF – Bo Andersson", d[2].Description);
            Assert.Equal(new[] { 1200m, 300m, 300m }, d.Select(x => x.Amount));
        }

        [Fact] public void En_ensam_medlem_far_ingen_personlista_och_avdrag_heter_avdrag()
        {
            var lines = new[] { L(ClubFeeCalculator.BaseKind, "Senior", 300m), L(ClubFeeCalculator.PartKind, "Annan huvudklubb", -100m) };
            var d = FeeLineText.Describe(lines, "Anna Andersson", Array.Empty<string>(), Name);
            Assert.Equal("Grundavgift Senior", d[0].Description);
            Assert.Equal("Avdrag Annan huvudklubb", d[1].Description);
        }

        [Fact] public void Justeringsraden_och_okanda_rader_lamnas_som_de_ar_och_okand_person_utelamnas()
        {
            var lines = new[] { L(ClubFeeCalculator.AdjustKind, "Avgiften kan inte bli lägre än 0 kr", 50m), L(ClubFeeCalculator.PartKind, "X", 10m, 99) };
            var d = FeeLineText.Describe(lines, "A", Array.Empty<string>(), Name);
            Assert.Equal("Avgiften kan inte bli lägre än 0 kr", d[0].Description);
            Assert.Equal("Tillägg X", d[1].Description);
        }

        [Fact] public void Uppdelningen_visas_for_flera_rader_eller_en_ensam_familjerad()
        {
            Assert.False(FeeLineText.WorthShowing(new List<(string, decimal)> { ("Grundavgift Senior", 300m) }));
            Assert.True(FeeLineText.WorthShowing(new List<(string, decimal)> { ("Grundavgift Familj (för A och B)", 1200m) }));
            Assert.True(FeeLineText.WorthShowing(new List<(string, decimal)> { ("a", 1m), ("b", 2m) }));
        }
    }
}
