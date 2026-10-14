using System;
using System.Linq;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>C3-planen S1: postens läsregler och fälttabellen.</summary>
    public class CompetitionInfoTests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("0", false)]
        [InlineData("", false)]
        public void Bool_saknas_ar_falskt(object? raw, bool expected) =>
            Assert.Equal(expected, CompetitionFieldValues.Read(CompetitionFieldShape.Bool, raw));

        [Fact]
        public void Synlig_nar_isActive_saknas()
        {
            Assert.Equal(true, CompetitionFieldValues.Read(CompetitionFieldShape.BoolDefaultTrue, null));
            Assert.Equal(true, CompetitionFieldValues.Read(CompetitionFieldShape.BoolDefaultTrue, ""));
            Assert.Equal(false, CompetitionFieldValues.Read(CompetitionFieldShape.BoolDefaultTrue, 0));
        }

        [Fact]
        public void Datum_MinValue_ar_null()
        {
            Assert.Null(CompetitionFieldValues.Read(CompetitionFieldShape.Date, DateTime.MinValue));
            Assert.Null(CompetitionFieldValues.Read(CompetitionFieldShape.Date, null));
            var d = new DateTime(2026, 10, 25, 9, 0, 0);
            Assert.Equal(d, CompetitionFieldValues.Read(CompetitionFieldShape.Date, d));
        }

        [Theory]
        [InlineData("Kretsmästerskap", "Kretsmästerskap")]
        [InlineData("[\"Kretsmästerskap\"]", "Kretsmästerskap")]
        [InlineData("[]", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Valet_skalas_av(string? raw, string? expected) =>
            Assert.Equal(expected, CompetitionFieldValues.Read(CompetitionFieldShape.Choice, raw));

        [Fact]
        public void Valet_som_array()
        {
            Assert.Equal("Poang", CompetitionFieldValues.Read(CompetitionFieldShape.Choice, new[] { "Poang" }));
        }

        [Fact]
        public void Tom_text_ar_null()
        {
            Assert.Null(CompetitionFieldValues.Read(CompetitionFieldShape.Text, "   "));
            Assert.Equal("120:-", CompetitionFieldValues.Read(CompetitionFieldShape.Text, "120:-"));
        }

        [Fact]
        public void Beloppet_tolkas_som_avgiftsberakningen()
        {
            var c = new CompetitionInfo { RegistrationFee = "150", JuniorRegistrationFee = "0", TeamRegistrationFee = "120:-" };
            Assert.Equal(150m, c.Fee(CompetitionFeeKind.Registration));
            Assert.Equal(0m, c.Fee(CompetitionFeeKind.Junior));      // ifyllt till 0 = gratis, inte tomt
            Assert.Null(c.Fee(CompetitionFeeKind.Stafett));           // inte ifyllt
            Assert.Null(c.Fee(CompetitionFeeKind.Team));              // oläsbart — som RegistrationFeeCalculator
        }

        [Fact]
        public void Tavlingsansvariga_ur_jsonarrayen()
        {
            Assert.Equal(new[] { 12, 34 }, new CompetitionInfo { CompetitionManagers = "[12,34]" }.ManagerIds);
            Assert.Empty(new CompetitionInfo { CompetitionManagers = "trasig" }.ManagerIds);
            Assert.Empty(new CompetitionInfo().ManagerIds);
        }

        [Fact]
        public void Sista_dagen()
        {
            var start = new DateTime(2026, 6, 13, 9, 0, 0);
            Assert.Equal(start.Date, new CompetitionInfo { CompetitionDate = start }.LastDay);
            Assert.Equal(start.Date.AddDays(1), new CompetitionInfo { CompetitionDate = start, CompetitionEndDate = start.AddDays(1) }.LastDay);
            // Ett slutdatum före start är ett skrivfel — startdagen gäller.
            Assert.Equal(start.Date, new CompetitionInfo { CompetitionDate = start, CompetitionEndDate = start.AddDays(-3) }.LastDay);
        }

        [Fact]
        public void Falttabellen_har_unika_alias_och_egenskaper()
        {
            Assert.Equal(CompetitionFieldTable.Fields.Count, CompetitionFieldTable.Fields.Select(f => f.Alias).Distinct().Count());
            Assert.Equal(CompetitionFieldTable.Fields.Count, CompetitionFieldTable.Fields.Select(f => f.Property).Distinct().Count());
            // Varje kolumnnamn är en egenskap på posten — kolumnen och egenskapen kan inte glida isär.
            foreach (var f in CompetitionFieldTable.Fields)
                Assert.NotNull(typeof(CompetitionInfo).GetProperty(f.Property));
            foreach (var f in CompetitionSeriesFieldTable.Fields)
                Assert.NotNull(typeof(CompetitionSeriesInfo).GetProperty(f.Property));
        }

        [Fact]
        public void Varje_falt_gar_att_lasa_och_skriva_genom_tabellen()
        {
            var c = new CompetitionInfo();
            foreach (var f in CompetitionFieldTable.Fields)
            {
                var value = CompetitionFieldValues.Read(f.Shape, f.Shape switch
                {
                    CompetitionFieldShape.Bool or CompetitionFieldShape.BoolDefaultTrue => 1,
                    CompetitionFieldShape.Int => 7,
                    CompetitionFieldShape.Date => new DateTime(2026, 1, 2),
                    _ => "x-" + f.Alias,
                });
                f.Set(c, value);
                Assert.Equal(value, f.Get(c));
            }
        }

        [Fact]
        public void Kopian_tar_bara_definitionen()
        {
            var source = new CompetitionInfo
            {
                Id = 42, Name = "Original", CompetitionName = "KM", Venue = "Banan", ClubId = 2604,
                FaltskyttePatrolsPublished = true, FaltskytteResultsOfficial = true, InvitationFile = "umb://media/x",
                MergeConfig = "[1]", MedalsPerWeaponGroup = true, FaltskytteStationManagers = "{}",
            };
            var copy = CompetitionFieldTable.CopyDefinition(source);
            Assert.Equal("KM", copy.CompetitionName);
            Assert.Equal("Banan", copy.Venue);
            Assert.Equal(2604, copy.ClubId);
            Assert.Equal(0, copy.Id);
            Assert.False(copy.FaltskyttePatrolsPublished);
            Assert.False(copy.FaltskytteResultsOfficial);
            Assert.Null(copy.InvitationFile);
            Assert.Null(copy.MergeConfig);
            Assert.False(copy.MedalsPerWeaponGroup);
            Assert.Null(copy.FaltskytteStationManagers);
        }

        [Fact]
        public void Andrade_falt()
        {
            var a = new CompetitionInfo { Venue = "A", ClubId = 1 };
            var b = a.Clone();
            b.Venue = "B";
            Assert.Equal(new[] { "venue" }, CompetitionFieldTable.Changed(a, b).Select(f => f.Alias));
        }
    }
}
