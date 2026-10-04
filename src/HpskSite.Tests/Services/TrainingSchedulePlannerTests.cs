using HpskSite.Services.Training;

namespace HpskSite.Tests.Services
{
    /// <summary><see cref="TrainingSchedulePlanner"/> — schema → tillfällen (fas B1).</summary>
    public class TrainingSchedulePlannerTests
    {
        private static readonly DateTime Mon = new(2026, 10, 5);   // måndag

        [Fact]
        public void TisdagarOchTorsdagar_TvaVeckor()
        {
            var o = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(13), new[] { 2, 4 });
            Assert.Equal(new[] { 6, 8, 13, 15 }, o.Select(x => x.Date.Day));
        }

        [Fact]
        public void Sondag_Ar7()
        {
            Assert.Equal(7, TrainingSchedulePlanner.IsoWeekday(new DateTime(2026, 10, 11)));
            var o = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(6), new[] { 7 });
            Assert.Single(o);
            Assert.Equal(11, o[0].Date.Day);
        }

        [Fact]
        public void Uppehall_HoppasOver()
        {
            var o = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(13), new[] { 2 },
                breaks: new[] { new TrainingSchedulePlanner.DateRange(Mon.AddDays(7), Mon.AddDays(9)) });
            Assert.Equal(new[] { 6 }, o.Select(x => x.Date.Day));
        }

        [Fact]
        public void Skjutledare_Roterar()
        {
            var o = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(27), new[] { 2 }, rotation: new[] { 10, 20, 30 });
            Assert.Equal(new int?[] { 10, 20, 30, 10 }, o.Select(x => x.SkjutledareMemberId));
        }

        [Fact]
        public void UtanRotation_FarAllaStandard()
        {
            var o = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(13), new[] { 2 }, defaultSkjutledare: 42);
            Assert.All(o, x => Assert.Equal(42, x.SkjutledareMemberId));
        }

        [Fact]
        public void Idempotent_BefintligaDatumHoppasOver_UtanAttRotationenForskjuts()
        {
            var first = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(13), new[] { 2 }, rotation: new[] { 10, 20, 30 });
            var again = TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(27), new[] { 2 }, rotation: new[] { 10, 20, 30 },
                existingDates: first.Select(x => x.Date));
            Assert.Equal(new[] { 20, 27 }, again.Select(x => x.Date.Day));
            Assert.Equal(new int?[] { 30, 10 }, again.Select(x => x.SkjutledareMemberId));
        }

        [Fact]
        public void BakvantPeriod_EllerIngaDagar_GerIngenting()
        {
            Assert.Empty(TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(-1), new[] { 2 }));
            Assert.Empty(TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(30), Array.Empty<int>()));
            Assert.Empty(TrainingSchedulePlanner.Expand(Mon, Mon.AddDays(30), new[] { 0, 8 }));
        }

        [Fact]
        public void Taket_StopparEnFelskrivenPeriod()
        {
            var o = TrainingSchedulePlanner.Expand(Mon, Mon.AddYears(40), new[] { 1, 2, 3, 4, 5, 6, 7 });
            Assert.Equal(TrainingSchedulePlanner.MaxOccasions, o.Count);
        }

        [Fact]
        public void ParseWeekdays_TalarOmOlasligt()
        {
            Assert.Equal(new[] { 2, 4 }, TrainingSchedulePlanner.ParseWeekdays("2, 4,x,9,2"));
            Assert.Empty(TrainingSchedulePlanner.ParseWeekdays(null));
        }
    }
}
