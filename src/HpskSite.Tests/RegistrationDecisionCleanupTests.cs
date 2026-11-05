using HpskSite.Services.Registrations;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// S3: <c>SqlRegistrationStore.Save</c> raderar och sätter in klassraderna på nytt men rör aldrig beslutstabellen, förutom att den
    /// städar beslut vars klassrad försvunnit (klassbyte nollställer flaggan). Här prövas bara parameternumreringen i SQL-bygget;
    /// själva SQL:en är bevisad mot repetitionsdatabasen.
    /// </summary>
    public class RegistrationDecisionCleanupTests
    {
        private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0);

        [Fact]
        public void Kept_classes_are_numbered_from_four_and_travel_as_arguments()
        {
            var c = SqlRegistrationStore.BuildDecisionCleanup(9, Now, 42, 7, new[] { "C1", "A1" });
            Assert.Contains("NOT IN (@4,@5)", c.DeleteSql);
            Assert.Contains("NOT IN (@4,@5)", c.LogSql);
            Assert.Equal(new object[] { 9, Now, 42, 7, "C1", "A1" }, c.Args);
        }

        [Fact]
        public void No_class_left_means_every_decision_goes()
        {
            var c = SqlRegistrationStore.BuildDecisionCleanup(9, Now, 42, 7, Array.Empty<string>());
            Assert.DoesNotContain("NOT IN", c.DeleteSql);
            Assert.Equal(4, c.Args.Length);
        }

        [Fact]
        public void The_log_reads_the_decision_before_the_delete_removes_it()
        {
            var c = SqlRegistrationStore.BuildDecisionCleanup(9, Now, 42, 7, new[] { "C1" });
            Assert.StartsWith("DELETE FROM dbo.CompetitionRegistrationClassDecision", c.DeleteSql);
            Assert.Contains("'class-removed'", c.LogSql);
            Assert.Contains("FROM dbo.CompetitionRegistrationClassDecision", c.LogSql);
        }
    }
}
