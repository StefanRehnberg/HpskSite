using System.IO;
using System.Linq;
using HpskSite.Services.Schedule;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// SM i Springskytte 2026 publishes its start lists as PDFs on /sm-springskytte-2026/startlistor
    /// instead of running them in pistol.nu. Without the opt-out below, every entrant who opens Mitt
    /// schema is told "Startlistan är inte publicerad än" for the whole event — a list that is never
    /// coming. The dev database has no SM competition node, so this can't be exercised end-to-end
    /// there; these tests pin the matching and the config path instead.
    /// </summary>
    public class MyScheduleServiceExternalStartListTests
    {
        [Fact]
        public void EmptyConfig_LeavesEveryCompetitionUnchanged()
        {
            // The default. Any regression here silently changes the wording for every competition.
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(null, "sm-springskytte-2026"));
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(new string[0], "sm-springskytte-2026"));
        }

        [Fact]
        public void MatchesConfiguredSegment_IgnoringCaseAndSurroundingWhitespace()
        {
            var configured = new[] { " sm-springskytte-2026 " };

            Assert.True(MyScheduleService.MatchesExternalStartListSegment(configured, "sm-springskytte-2026"));
            Assert.True(MyScheduleService.MatchesExternalStartListSegment(configured, "SM-Springskytte-2026"));
        }

        [Fact]
        public void DoesNotMatchOtherCompetitions()
        {
            var configured = new[] { "sm-springskytte-2026" };

            // A near-miss must not opt out — the warning is correct for these.
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(configured, "sm-springskytte-2027"));
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(configured, "springskytte-2026"));
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(configured, "banfaltet"));
        }

        [Fact]
        public void BlankSegmentsAreIgnored()
        {
            var configured = new[] { "", "   ", "sm-springskytte-2026" };

            Assert.True(MyScheduleService.MatchesExternalStartListSegment(configured, "sm-springskytte-2026"));
            // An empty entry must never match a competition with no segment.
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(configured, ""));
            Assert.False(MyScheduleService.MatchesExternalStartListSegment(configured, null));
        }

        /// <summary>
        /// Proves the shipped appsettings.json actually binds to a string[] at the key the service
        /// reads — a typo in either half fails open and quietly restores the misleading warning.
        /// </summary>
        [Fact]
        public void ShippedAppSettings_BindsTheSmCompetition()
        {
            var appSettings = LocateAppSettings();
            Assert.True(File.Exists(appSettings), $"appsettings.json not found at {appSettings}");

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(appSettings, optional: false)
                .Build();

            var segments = configuration.GetSection(MyScheduleService.ExternalStartListConfigKey).Get<string[]>();

            Assert.NotNull(segments);
            Assert.Contains("sm-springskytte-2026", segments!.Select(s => s.Trim()));
            Assert.True(MyScheduleService.MatchesExternalStartListSegment(segments, "sm-springskytte-2026"));
        }

        private static string LocateAppSettings()
        {
            // Walk up from the test bin folder to the repo's src directory.
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "HpskSite", "appsettings.json");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return Path.Combine("HpskSite", "appsettings.json");
        }
    }
}
