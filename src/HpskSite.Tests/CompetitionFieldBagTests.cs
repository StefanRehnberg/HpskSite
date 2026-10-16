using System;
using System.Collections.Generic;
using System.Text.Json;
using HpskSite.Models.Competitions;
using HpskSite.Services.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>C3-planen S5b: EN regel för fältpåsen, och kopian tar bara definitionen.</summary>
    public class CompetitionFieldBagTests
    {
        /// <summary>Fältpåsen som formulären skickar: en JSON-ordbok, alltså JsonElement-värden.</summary>
        private static Dictionary<string, object> Bag(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        [Fact]
        public void Formularnamnet_far_egenskapens_alias()
        {
            // Annonsens sparning skrev förut den råa nyckeln, så inställningen gick aldrig att slå på där.
            var c = new CompetitionInfo();
            CompetitionFieldBag.Apply(c, Bag("{\"allowDualCClass\": true}"));
            Assert.True(c.AllowDualCClassRegistration);
        }

        [Fact]
        public void Belopp_lagras_som_text_och_negativt_ar_inget()
        {
            var c = new CompetitionInfo();
            CompetitionFieldBag.Apply(c, Bag("{\"registrationFee\": \"150\", \"teamRegistrationFee\": 600, \"juniorRegistrationFee\": \"-5\"}"));
            Assert.Equal("150", c.RegistrationFee);
            Assert.Equal("600", c.TeamRegistrationFee);
            Assert.Null(c.JuniorRegistrationFee);
        }

        [Fact]
        public void Klasserna_blir_en_JSON_array()
        {
            var c = new CompetitionInfo();
            CompetitionFieldBag.Apply(c, Bag("{\"shootingClassIds\": \"C1,C2, C3\"}"));
            Assert.Equal("[\"C1\",\"C2\",\"C3\"]", c.ShootingClassIds);
        }

        [Fact]
        public void Typerna_foljer_tabellen()
        {
            var c = new CompetitionInfo { IsVisible = true };
            CompetitionFieldBag.Apply(c, Bag(
                "{\"competitionDate\": \"2026-11-07T09:30\", \"maxParticipants\": \"40\", \"clubId\": \"2604\", " +
                "\"showLiveResults\": true, \"isActive\": \"false\", \"rangeId\": \"\", \"isExternal\": \"true\"}"));
            Assert.Equal(new DateTime(2026, 11, 7, 9, 30, 0), c.CompetitionDate);
            Assert.Equal(40, c.MaxParticipants);
            Assert.Equal(2604, c.ClubId);
            Assert.True(c.ShowLiveResults);
            Assert.False(c.IsVisible);
            Assert.Equal(0, c.RangeId);
            Assert.True(c.IsExternal);
        }

        [Fact]
        public void Tavlingsansvariga_ar_en_array_av_heltal()
        {
            var c = new CompetitionInfo();
            CompetitionFieldBag.Apply(c, Bag("{\"competitionManagers\": [8315, 5513]}"));
            Assert.Equal("[8315,5513]", c.CompetitionManagers);
        }

        [Fact]
        public void Tillstand_och_okanda_nycklar_skrivs_aldrig_men_namnges()
        {
            var c = new CompetitionInfo { MergeConfig = "orörd", FaltskytteResultsOfficial = false };
            var r = CompetitionFieldBag.Apply(c, Bag(
                "{\"mergeConfig\": \"[]\", \"faltskytteResultsOfficial\": true, \"teamResultSeriesCount\": 3, \"venue\": \"Banan\"}"));
            Assert.Equal("orörd", c.MergeConfig);
            Assert.False(c.FaltskytteResultsOfficial);
            Assert.Equal("Banan", c.Venue);
            Assert.Equal(new[] { "mergeConfig", "faltskytteResultsOfficial", "teamResultSeriesCount" }, r.Ignored);
        }

        [Fact]
        public void Anroparens_egna_nycklar_hoppas_over()
        {
            var c = new CompetitionInfo { CompetitionType = "Precision" };
            var r = CompetitionFieldBag.Apply(c, Bag("{\"competitionType\": \"Springskytte\"}"), new HashSet<string> { "competitionType" });
            Assert.Equal("Precision", c.CompetitionType);
            Assert.Empty(r.Ignored);
        }

        [Fact]
        public void Kopian_tar_bara_definitionen_och_flyttar_datumen()
        {
            var source = new CompetitionInfo
            {
                Id = 7, Name = "KM", CompetitionName = "KM", CompetitionDate = new DateTime(2026, 5, 1), RegistrationCloseDate = new DateTime(2026, 4, 20),
                Venue = "Banan", StationConfig = "{}", CompetitionManagers = "[1]",
                FaltskyttePatrolsPublished = true, FaltskytteResultsOfficial = true, FaltskytteStationManagers = "{}",
                MergeConfig = "[x]", MedalsPerWeaponGroup = true, InvitationFile = "umb://media/1", ResultListFile = "umb://media/2",
            };

            var copy = new CompetitionInfo();
            CompetitionFieldTable.CopyDefinitionInto(source, copy, shiftYears: 1);
            Assert.Equal(new DateTime(2027, 5, 1), copy.CompetitionDate);
            Assert.Equal(new DateTime(2027, 4, 20), copy.RegistrationCloseDate);
            Assert.Equal("Banan", copy.Venue);
            Assert.Equal("{}", copy.StationConfig);
            Assert.Equal("[1]", copy.CompetitionManagers);
            Assert.False(copy.FaltskyttePatrolsPublished);
            Assert.False(copy.FaltskytteResultsOfficial);
            Assert.Null(copy.FaltskytteStationManagers);
            Assert.Null(copy.MergeConfig);
            Assert.False(copy.MedalsPerWeaponGroup);
            Assert.Null(copy.InvitationFile);
            Assert.Null(copy.ResultListFile);
            Assert.Equal(0, copy.Id);

            var undated = new CompetitionInfo();
            CompetitionFieldTable.CopyDefinitionInto(source, undated, shiftYears: null);
            Assert.Null(undated.CompetitionDate);
            Assert.Null(undated.RegistrationCloseDate);
        }
    }
}
