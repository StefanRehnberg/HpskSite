using HpskSite.Models.Training;

namespace HpskSite.Tests.Services
{
    /// <summary><see cref="TrainingCourseRules"/> — kursens krav per tillfälle (fas D).</summary>
    public class TrainingCourseRulesTests
    {
        [Theory]
        [InlineData(null, null, false)]
        [InlineData("Mandatory", null, true)]
        [InlineData("Mandatory", "Optional", false)]   // tillfällets avvikelse vinner
        [InlineData(null, "Mandatory", true)]
        [InlineData("Optional", "Mandatory", true)]
        [InlineData("skräp", null, false)]             // ett okänt värde är aldrig ett krav
        public void Narvaro(string? group, string? link, bool mandatory)
            => Assert.Equal(mandatory, TrainingCourseRules.IsMandatory(group, link));

        [Theory]
        [InlineData(null, null, false)]
        [InlineData("Required", null, true)]
        [InlineData("Required", "NotRequired", false)]
        [InlineData(null, "Required", true)]
        public void Anmalan(string? group, string? link, bool required)
            => Assert.Equal(required, TrainingCourseRules.RegistrationRequired(group, link));

        private static readonly DateTime Day = new(2026, 10, 7);

        [Theory]
        [InlineData(17, 0, true)]    // en timme före start
        [InlineData(16, 59, false)]
        [InlineData(20, 59, true)]   // inom en timme efter slut
        [InlineData(21, 1, false)]
        public void Incheckning_kring_tillfallet(int h, int m, bool counts)
            => Assert.Equal(counts, TrainingCourseRules.CheckInCounts(Day.AddHours(h).AddMinutes(m), Day, "18:00", "20:00"));

        private static readonly DateTime Then = new(2026, 11, 18, 15, 0, 0);

        // Magnus 2026-11-20: Emil och Jessica hade avbokat sig själva, avprickningen "Ej här" sparades
        // (Absent) men kursvyn hoppade över avbokade rader — knappen markerades aldrig.
        [Theory]
        [InlineData("Absent", true, "absent")]    // den avbokade raden med registrerad frånvaro SYNS
        [InlineData("Present", true, "present")]
        [InlineData("Excused", true, "excused")]
        [InlineData("Absent", false, "absent")]   // oförändrat för en aktiv rad
        [InlineData("Present", false, "present")]
        [InlineData(null, true, "none")]          // avbokad utan avprickning = ingen rad alls
        [InlineData(null, false, "signedup")]     // aktiv, anmäld, ingen avprickning
        [InlineData("skräp", true, "none")]       // en okänd status är aldrig en närvaro
        public void Cellen_visar_en_registrerad_narvaro_aven_pa_en_avbokad_rad(string? status, bool cancelled, string expected)
            => Assert.Equal(expected, TrainingCourseRules.CellState(status, Then, cancelled ? Then.AddHours(2) : null, registrationRequired: true));

        [Fact]
        public void Utan_anmalningskrav_ar_en_rad_utan_status_ingen_anmalan()
            => Assert.Equal("none", TrainingCourseRules.CellState(null, Then, null, registrationRequired: false));

        [Theory]
        [InlineData("Present", true)]   // kom → aktiv igen
        [InlineData("Excused", true)]   // godtagen förklaring → aktiv igen
        [InlineData("Absent", false)]   // avbokade och kom inte: avbokningen är sann
        [InlineData(null, false)]       // rensa rör inte avbokningen
        [InlineData("skräp", false)]
        public void Vilken_avprickning_ateruppliver_en_avbokad_rad(string? status, bool reopens)
            => Assert.Equal(reopens, TrainingCourseRules.ReopensCancelledRow(status));

        [Fact]
        public void Incheckning_annan_dag_raknas_inte()
            => Assert.False(TrainingCourseRules.CheckInCounts(Day.AddDays(1).AddHours(18), Day, "18:00", "20:00"));

        [Fact]
        public void Utan_sluttid_galler_start_plus_tre_timmar()
        {
            Assert.True(TrainingCourseRules.CheckInCounts(Day.AddHours(21).AddMinutes(30), Day, "18:00", null));
            Assert.False(TrainingCourseRules.CheckInCounts(Day.AddHours(22).AddMinutes(30), Day, "18:00", null));
        }

        [Fact]
        public void Serie_med_skott_summeras_och_X_raknas()
        {
            var r = TrainingCourseRules.ParseSeries(new[] { "X", "10", "9", "8", "x" }, null);
            Assert.Null(r.Error);
            Assert.Equal(47, r.Total);
            Assert.Equal(2, r.XCount);
        }

        [Fact]
        public void Fyra_skott_ar_ingen_serie()
            => Assert.NotNull(TrainingCourseRules.ParseSeries(new[] { "10", "9", "8", "7" }, null).Error);

        [Theory]
        [InlineData("11")]
        [InlineData("-1")]
        [InlineData("a")]
        public void Ogiltigt_skott_vagras(string bad)
            => Assert.NotNull(TrainingCourseRules.ParseSeries(new[] { "10", "9", "8", "7", bad }, null).Error);

        [Theory]
        [InlineData(36, null)]
        [InlineData(0, null)]
        [InlineData(51, "fel")]
        [InlineData(-1, "fel")]
        public void Bara_total(int total, string? error)
            => Assert.Equal(error != null, TrainingCourseRules.ParseSeries(null, total).Error != null);

        [Fact]
        public void Varken_skott_eller_total_vagras()
            => Assert.NotNull(TrainingCourseRules.ParseSeries(null, null).Error);

        [Fact]
        public void Utan_klockslag_galler_hela_dagen()
            => Assert.True(TrainingCourseRules.CheckInCounts(Day.AddHours(7), Day, null, null));
    }
}
