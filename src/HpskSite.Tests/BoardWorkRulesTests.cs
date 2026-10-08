using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Ärendekön och motionerna (2026-10-08): reglerna som rena funktioner.
    /// </summary>
    public class BoardWorkRulesTests
    {
        private static BoardMeetingAgendaItem Item(int id, int sort, int? parent = null) =>
            new() { Id = id, SortOrder = sort, ParentItemId = parent, Heading = "P" + id, IsActive = true };

        // ---- Numrering ------------------------------------------------------------------------

        [Fact]
        public void Ordered_numrerar_huvudpunkter_och_underpunkter_med_bokstav()
        {
            var items = new[] { Item(1, 0), Item(2, 1), Item(10, 0, 2), Item(11, 1, 2), Item(3, 2) };
            var o = BoardIssueRules.Ordered(items);
            o.Select(x => x.Label).Should().Equal("§1", "§2", "§2a", "§2b", "§3");
            o.Select(x => x.Item.Id).Should().Equal(1, 2, 10, 11, 3);
            o.Where(x => x.IsSub).Select(x => x.Item.Id).Should().Equal(10, 11);
        }

        [Fact]
        public void Ordered_foljer_SortOrder_inte_insattningsordning()
        {
            var items = new[] { Item(5, 2), Item(6, 0), Item(7, 1), Item(20, 5, 7), Item(21, 1, 7) };
            var o = BoardIssueRules.Ordered(items);
            o.Select(x => x.Item.Id).Should().Equal(6, 7, 21, 20, 5);
            o.Select(x => x.Label).Should().Equal("§1", "§2", "§2a", "§2b", "§3");
        }

        [Fact]
        public void En_underpunkt_vars_huvudpunkt_saknas_blir_huvudpunkt_och_forsvinner_inte()
        {
            // Huvudpunkten 99 är borttagen (finns inte i listan) — ärendet får inte falla ur protokollet.
            var items = new[] { Item(1, 0), Item(30, 0, 99) };
            var o = BoardIssueRules.Ordered(items);
            o.Should().HaveCount(2);
            o.Select(x => x.Item.Id).Should().Contain(30);
            o.Single(x => x.Item.Id == 30).IsSub.Should().BeFalse();
        }

        [Fact]
        public void En_punkt_under_en_underpunkt_skrivs_ut_anda()
        {
            // Bara en nivå finns; en punkt vars förälder är en underpunkt får inte bli osynlig.
            var items = new[] { Item(1, 0), Item(2, 0, 1), Item(3, 0, 2) };
            BoardIssueRules.Ordered(items).Select(x => x.Item.Id).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        }

        [Theory]
        [InlineData(0, "a")]
        [InlineData(1, "b")]
        [InlineData(25, "z")]
        [InlineData(26, "aa")]
        [InlineData(27, "ab")]
        public void Letter(int i, string expected) => BoardIssueRules.Letter(i).Should().Be(expected);

        // ---- Ärendets läge ---------------------------------------------------------------------

        [Fact]
        public void Lage_harleds_ur_motets_status()
        {
            BoardIssueRules.StateOf(null, null).Should().Be(BoardIssueState.Waiting);
            BoardIssueRules.StateOf(null, "Planerat").Should().Be(BoardIssueState.Placed);
            BoardIssueRules.StateOf(null, "VantarJustering").Should().Be(BoardIssueState.Placed);
            BoardIssueRules.StateOf(null, "Justerat").Should().Be(BoardIssueState.Handled);
            BoardIssueRules.StateOf(BoardIssueClosed.Withdrawn, null).Should().Be(BoardIssueState.Withdrawn);
            BoardIssueRules.StateOf(BoardIssueClosed.Rejected, null).Should().Be(BoardIssueState.Rejected);
        }

        [Fact]
        public void Ett_behandlat_arende_star_kvar_som_behandlat_aven_om_det_stangts()
        {
            BoardIssueRules.StateOf(BoardIssueClosed.Withdrawn, "Justerat").Should().Be(BoardIssueState.Handled);
        }

        [Theory]
        [InlineData("Planerat", true, true)]
        [InlineData("Genomfört", true, true)]
        [InlineData("VantarJustering", true, false)]
        [InlineData("Justerat", true, false)]
        [InlineData("Planerat", false, false)]
        public void Placering_bara_pa_olasta_moten(string status, bool active, bool allowed) =>
            (BoardIssueRules.PlaceRefusal(status, active) == null).Should().Be(allowed);

        [Fact]
        public void Okand_sort_blir_beslut() => BoardIssueKinds.Normalize("hittepå").Should().Be(BoardIssueKinds.Decision);

        // ---- Motioner --------------------------------------------------------------------------

        [Fact]
        public void Motionsnummer_skiljer_klubb_och_krets()
        {
            BoardMotionRules.NumberLabel(DocumentOwnerType.Club, 2027, 3).Should().Be("M27-3");
            BoardMotionRules.NumberLabel(DocumentOwnerType.Region, 2026, 12).Should().Be("K26-12");
            BoardMotionRules.NumberLabel(DocumentOwnerType.Club, 2105, 1).Should().Be("M05-1");
        }

        [Fact]
        public void Forslagen_far_att_och_tomma_rader_tas_bort()
        {
            BoardMotionRules.NormalizeProposals(new[] { "  Banan är öppen på söndagar ", "", null, "att styrelsen tar fram ett schema", "ATT något" })
                .Should().Equal("att banan är öppen på söndagar", "att styrelsen tar fram ett schema", "ATT något");
        }

        [Fact]
        public void Forslagen_overlever_en_rundresa_genom_json()
        {
            var list = new List<string> { "att a", "att \"b\" med citattecken" };
            BoardMotionRules.ParseProposals(BoardMotionRules.SerializeProposals(list)).Should().Equal(list);
            BoardMotionRules.ParseProposals(null).Should().BeEmpty();
        }

        [Fact]
        public void Sista_dagen_raknas_med()
        {
            var deadline = new DateTime(2027, 1, 15);
            BoardMotionRules.IsLate(new DateTime(2027, 1, 15, 23, 59, 0), deadline).Should().BeFalse();
            BoardMotionRules.IsLate(new DateTime(2027, 1, 16, 0, 1, 0), deadline).Should().BeTrue();
            BoardMotionRules.IsLate(new DateTime(2030, 1, 1), null).Should().BeFalse();
        }

        [Fact]
        public void Motionens_lage()
        {
            BoardMotionRules.StateOf(false, false, null).Should().Be(BoardMotionState.Received);
            BoardMotionRules.StateOf(false, true, null).Should().Be(BoardMotionState.OpinionReady);
            BoardMotionRules.StateOf(false, true, "Planerat").Should().Be(BoardMotionState.OnAgenda);
            BoardMotionRules.StateOf(false, true, "Justerat").Should().Be(BoardMotionState.Decided);
            BoardMotionRules.StateOf(true, true, null).Should().Be(BoardMotionState.Withdrawn);
            // Ett beslut står kvar även om motionären försökt återkalla efteråt.
            BoardMotionRules.StateOf(true, true, "Justerat").Should().Be(BoardMotionState.Decided);
        }

        [Fact]
        public void Stod_bara_fran_andra_och_bara_fore_beslutet()
        {
            BoardMotionRules.CanSupport(BoardMotionState.Received, isMotioner: false).Should().BeTrue();
            BoardMotionRules.CanSupport(BoardMotionState.OnAgenda, isMotioner: false).Should().BeTrue();
            BoardMotionRules.CanSupport(BoardMotionState.Received, isMotioner: true).Should().BeFalse();
            BoardMotionRules.CanSupport(BoardMotionState.Decided, isMotioner: false).Should().BeFalse();
            BoardMotionRules.CanSupport(BoardMotionState.Withdrawn, isMotioner: false).Should().BeFalse();
        }

        [Fact]
        public void Aterkallelse_fram_till_beslutet()
        {
            BoardMotionRules.CanWithdraw(BoardMotionState.OnAgenda).Should().BeTrue();
            BoardMotionRules.CanWithdraw(BoardMotionState.Decided).Should().BeFalse();
            BoardMotionRules.CanWithdraw(BoardMotionState.Withdrawn).Should().BeFalse();
        }

        [Fact]
        public void Forslag_fran_styrelsen()
        {
            BoardMotionProposals.IsValid("Bifall").Should().BeTrue();
            BoardMotionProposals.IsValid("bifall").Should().BeFalse();
            BoardMotionProposals.Label("Besvarad").Should().Be("Anse motionen besvarad");
            BoardMotionSupportKinds.IsValid("Medmotionar").Should().BeTrue();
            BoardMotionSupportKinds.IsValid("").Should().BeFalse();
        }
    }
}
