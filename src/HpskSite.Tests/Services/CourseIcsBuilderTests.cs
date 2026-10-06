using FluentAssertions;
using HpskSite.Services.Training;
using Xunit;

namespace HpskSite.Tests.Services
{
    public class CourseIcsBuilderTests
    {
        private static TrainingCourseService.Occasion Occ(int id, string date, string? start, bool past = false,
            bool cancelled = false, bool mandatory = false, string? note = null, string? end = null) => new()
        {
            TrainingId = id, Date = date, StartTime = start, EndTime = end, Name = "Nybörjarträning",
            Note = note, IsPast = past, IsCancelled = cancelled, MandatoryForCourse = mandatory, IsMandatory = mandatory
        };

        [Fact]
        public void Skriver_bara_kommande_ej_installda_med_starttid()
        {
            var (ics, n) = CourseIcsBuilder.Build("Nybörjare 2027", 57, new[]
            {
                Occ(1, "2026-10-08", "18:30"),
                Occ(2, "2026-10-01", "18:30", past: true),
                Occ(3, "2026-10-09", "18:30", cancelled: true),
                Occ(4, "2026-10-10", null),
            }, "https://pistol.nu");
            n.Should().Be(1);
            ics.Should().Contain("UID:course-57-training-1@pistol.nu");
            ics.Should().NotContain("training-2@").And.NotContain("training-3@").And.NotContain("training-4@");
        }

        [Fact]
        public void Paminner_dagen_innan_och_obligatoriskt_dessutom_tva_timmar_fore()
        {
            var (plain, _) = CourseIcsBuilder.Build("K", 1, new[] { Occ(1, "2026-10-08", "18:30") }, "https://x");
            plain.Should().Contain("TRIGGER:-P1D").And.NotContain("TRIGGER:-PT2H");
            var (mand, _) = CourseIcsBuilder.Build("K", 1, new[] { Occ(1, "2026-10-08", "18:30", mandatory: true) }, "https://x");
            mand.Should().Contain("TRIGGER:-P1D").And.Contain("TRIGGER:-PT2H").And.Contain("Obligatoriskt för kursen.");
        }

        [Fact]
        public void Kursens_namn_pa_tillfallet_och_sluttid()
        {
            var (ics, _) = CourseIcsBuilder.Build("K", 1, new[] { Occ(1, "2026-10-08", "18:30", note: "Teori 1", end: "21:00") }, "https://x");
            ics.Should().Contain("SUMMARY:Teori 1 — K");
            ics.Should().Contain("DTSTART:20261008T183000").And.Contain("DTEND:20261008T210000");
        }

        [Fact]
        public void Utan_sluttid_blir_tillfallet_tva_timmar()
        {
            var (ics, _) = CourseIcsBuilder.Build("K", 1, new[] { Occ(1, "2026-10-08", "18:30") }, "https://x");
            ics.Should().Contain("DTEND:20261008T203000");
        }
    }
}
