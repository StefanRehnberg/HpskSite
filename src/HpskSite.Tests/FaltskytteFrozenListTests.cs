using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Results;
using HpskSite.Services.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Utanför mästerskapet, F0 (Stefan 2026-11-04): en publicerad fältlista får inte kunna ändras av att någon
    /// tittar på sidan. Bara en admins omräkning/publicering skriver om den frusna bilden.
    /// </summary>
    public class FaltskytteFrozenListTests
    {
        private const string Snap = "{\"success\":true,\"results\":{}}";

        [Fact]
        public void Official_main_list_with_a_snapshot_is_served_frozen()
            => Assert.True(FaltskytteResultArtifactService.ServeFrozen(false, null, true, Snap));

        [Fact]
        public void Official_main_list_with_an_empty_merge_preview_is_still_frozen()
            => Assert.True(FaltskytteResultArtifactService.ServeFrozen(false, "", true, Snap));

        [Fact]
        public void Preliminary_list_is_computed_live()
            => Assert.False(FaltskytteResultArtifactService.ServeFrozen(false, null, false, Snap));

        [Fact]
        public void Official_list_without_a_snapshot_falls_back_to_live_until_it_is_frozen()
        {
            Assert.False(FaltskytteResultArtifactService.ServeFrozen(false, null, true, null));
            Assert.False(FaltskytteResultArtifactService.ServeFrozen(false, null, true, "  "));
        }

        [Fact]
        public void Sub_competition_is_always_live_by_design()
            => Assert.False(FaltskytteResultArtifactService.ServeFrozen(true, null, true, Snap));

        [Fact]
        public void An_admins_merge_preview_is_live()
            => Assert.False(FaltskytteResultArtifactService.ServeFrozen(false, "[{\"a\":1}]", true, Snap));

        [Fact]
        public void The_snapshot_is_the_exact_response_shape_the_page_reads()
        {
            var services = new ServiceCollection().AddControllers().Services.BuildServiceProvider();
            var json = FaltskytteResultArtifactService.ToResponseJson(new FaltskylteFinalResults { CompetitionId = 7, IsOfficial = true }, services);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            var results = doc.RootElement.GetProperty("results");
            Assert.Equal(7, results.GetProperty("competitionId").GetInt32()); // camelCase som MVC:s Json()
            Assert.True(results.GetProperty("isOfficial").GetBoolean());
        }

        [Fact]
        public void The_store_treats_a_changed_snapshot_as_a_change()
        {
            var a = new CompetitionResultRecord { PublishedSnapshot = "" };
            var b = a.Clone();
            b.PublishedSnapshot = Snap;
            Assert.True(SqlCompetitionResultStore.Differs(a, b));
        }
    }
}
