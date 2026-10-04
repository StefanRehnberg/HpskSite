using HpskSite.Models;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Fas B5: närvaro räknas som aktivitet på ett skyttetillfälle, och på en händelse bara när
    /// närvaron är obligatorisk (Stefans beslut 2026-10-03).
    /// </summary>
    public class ActivityEventRuleTests
    {
        private const string Present = ClubEvents.AttendancePresent;

        [Fact]
        public void Narvaro_pa_en_traning_raknas()
            => Assert.True(MemberActivitySummary.EventCounts(Present, isShootingOccasion: true, isMandatory: false));

        [Fact]
        public void Narvaro_pa_en_stadning_raknas_inte()
            => Assert.False(MemberActivitySummary.EventCounts(Present, isShootingOccasion: false, isMandatory: false));

        [Fact]
        public void Narvaro_pa_en_obligatorisk_stadning_raknas()
            => Assert.True(MemberActivitySummary.EventCounts(Present, isShootingOccasion: false, isMandatory: true));

        [Theory]
        [InlineData(null)]
        [InlineData(ClubEvents.AttendanceAbsent)]
        [InlineData(ClubEvents.AttendanceExcused)]
        public void Ingen_narvaro_raknas_aldrig(string? status)
        {
            Assert.False(MemberActivitySummary.EventCounts(status, true, true));
        }

        [Fact]
        public void En_ClubTraining_ar_alltid_skytte()
            => Assert.True(MemberActivitySummary.IsShootingOccasion(isTraining: true, eventType: "Städning"));

        [Theory]
        [InlineData("Träning", true)]
        [InlineData("Precision", true)]
        [InlineData("Duell", true)]
        [InlineData("Städning", false)]
        [InlineData("Möte", false)]
        [InlineData("Socialt", false)]
        [InlineData(null, false)]
        // En gammal händelse av typen Träning räknas fortfarande — innan migreringen körts är det
        // så alla klubbens träningar ligger.
        public void Gamla_handelser_klassas_som_migreringen_gor(string? type, bool shooting)
            => Assert.Equal(shooting, MemberActivitySummary.IsShootingOccasion(false, type));
    }
}
