using HpskSite.Models.Training;

namespace HpskSite.Tests.Services
{
    /// <summary><see cref="TrainingMigrationRules"/> — vilka händelser som blir träningar (fas B3).</summary>
    public class TrainingMigrationRulesTests
    {
        [Theory]
        [InlineData("Träning")]
        [InlineData("träning")]
        [InlineData("Klubbträning")]
        [InlineData("Träning precision")]
        [InlineData("Traning")]
        public void Traning_i_alla_stavningar_migreras_utan_gren(string type)
        {
            var c = TrainingMigrationRules.Classify(type);
            Assert.True(c.Migrate);
            Assert.Null(c.Discipline);
        }

        [Theory]
        [InlineData("Precision", "Precision")]
        [InlineData("precision", "Precision")]
        [InlineData(" Duell ", "Duell")]
        [InlineData("Fältskjutning", "Faltskytte")]
        public void Grennamnen_migreras_med_grenen_satt(string type, string discipline)
        {
            var c = TrainingMigrationRules.Classify(type);
            Assert.True(c.Migrate);
            Assert.Equal(discipline, c.Discipline);
        }

        [Fact]
        public void IPSC_migreras_men_har_ingen_gren_hos_oss()
        {
            var c = TrainingMigrationRules.Classify("IPSC Handgun PCC");
            Assert.True(c.Migrate);
            Assert.Null(c.Discipline);
        }

        [Theory]
        [InlineData("Utbildning")]
        [InlineData("Städning")]
        [InlineData("Möte")]
        [InlineData("Tävling")]
        [InlineData("")]
        [InlineData(null)]
        // En delsträng av ett grennamn är INTE grenen — det här är en utbildning.
        [InlineData("Precisionskurs för nybörjare")]
        [InlineData("Duellkväll med grillning")]
        public void Allt_annat_star_kvar_som_handelse(string? type)
            => Assert.False(TrainingMigrationRules.Classify(type).Migrate);

        [Fact]
        public void Klockslag_tas_ur_start_och_slut_samma_dag()
        {
            var r = TrainingMigrationRules.SplitTimes(new DateTime(2026, 10, 6, 18, 0, 0), new DateTime(2026, 10, 6, 20, 30, 0));
            Assert.Equal(new DateTime(2026, 10, 6), r.Date);
            Assert.Equal("18:00", r.StartTime);
            Assert.Equal("20:30", r.EndTime);
            Assert.False(r.EndDropped);
        }

        [Fact]
        public void Midnatt_ar_inget_klockslag()
        {
            var r = TrainingMigrationRules.SplitTimes(new DateTime(2026, 10, 6), null);
            Assert.Null(r.StartTime);
            Assert.Null(r.EndTime);
        }

        [Fact]
        public void Slut_en_annan_dag_tas_inte_med_men_sags()
        {
            var r = TrainingMigrationRules.SplitTimes(new DateTime(2026, 10, 6, 18, 0, 0), new DateTime(2026, 10, 7, 12, 0, 0));
            Assert.Null(r.EndTime);
            Assert.True(r.EndDropped);
        }

        [Fact]
        public void Slut_fore_start_samma_dag_ignoreras()
        {
            var r = TrainingMigrationRules.SplitTimes(new DateTime(2026, 10, 6, 18, 0, 0), new DateTime(2026, 10, 6, 17, 0, 0));
            Assert.Null(r.EndTime);
            Assert.False(r.EndDropped);
        }

        [Fact]
        public void Ett_osatt_slutdatum_ar_inget_slut()
        {
            var r = TrainingMigrationRules.SplitTimes(new DateTime(2026, 10, 6, 18, 0, 0), DateTime.MinValue);
            Assert.Null(r.EndTime);
            Assert.False(r.EndDropped);
        }
    }
}
