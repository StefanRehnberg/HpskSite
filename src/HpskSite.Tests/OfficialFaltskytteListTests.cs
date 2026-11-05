using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// F0c: lagkort, Min sida, serier och särskjutningskortet läser den officiella fältlistans frusna bild genom
    /// <see cref="OfficialFaltskytteList"/>. Bilden måste gå att läsa tillbaka med samma värden som skrevs.
    /// </summary>
    public class OfficialFaltskytteListTests
    {
        private static IServiceProvider Services() =>
            new ServiceCollection().AddControllers().Services.BuildServiceProvider();

        private static FaltskylteFinalResults Sample() => new()
        {
            CompetitionId = 42,
            IsOfficial = true,
            ScoringMode = "Poang",
            ClassGroups =
            {
                new FaltskytteClassGroup
                {
                    ClassName = "C2",
                    Shooters =
                    {
                        new FaltskytteShooterResult
                        {
                            MemberId = 7, Name = "A", ShootingClass = "C2",
                            Stations =
                            {
                                new FaltskytteStationResult { StationNumber = 1, Hits = 5, Figures = 3, TiebreakerScore = 12 },
                                new FaltskytteStationResult { StationNumber = 2, Hits = 4, Figures = 2 }
                            }
                        }
                    }
                }
            },
            MedalCategoryTies =
            {
                new FaltskytteMedalCategoryTies { CategoryName = "C" }
            }
        };

        [Fact]
        public void The_frozen_picture_reads_back_with_the_values_that_were_written()
        {
            var json = FaltskytteResultArtifactService.ToResponseJson(Sample(), Services());
            var back = OfficialFaltskytteList.Parse(json, Services());
            Assert.NotNull(back);
            Assert.Equal(42, back!.CompetitionId);
            Assert.Equal("Poang", back.ScoringMode);
            Assert.Equal("C", back.MedalCategoryTies.Single().CategoryName);
            Assert.Equal(12, back.ClassGroups.Single().Shooters.Single().Stations[0].TiebreakerScore);
        }

        [Fact]
        public void The_rows_carry_member_class_station_and_scores_for_the_team_count()
        {
            var rows = OfficialFaltskytteList.RowsOf(Sample());
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => { Assert.Equal(7, r.MemberId); Assert.Equal("C2", r.ShootingClass); Assert.Equal(42, r.CompetitionId); });
            Assert.Equal(9, rows.Sum(r => r.Hits));
            Assert.Equal(5, rows.Sum(r => r.Figures));
            Assert.Equal(12, rows.Sum(r => r.TiebreakerScore ?? 0));
        }

        [Fact]
        public void A_snapshot_without_results_is_not_a_list()
            => Assert.Null(OfficialFaltskytteList.Parse("{\"success\":true}", Services()));
    }
}
