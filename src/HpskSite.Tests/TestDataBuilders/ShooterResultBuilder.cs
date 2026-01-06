using HpskSite.CompetitionTypes.Precision.Models;
using System;
using System.Collections.Generic;

namespace HpskSite.Tests.TestDataBuilders
{
    /// <summary>
    /// Fluent builder for creating PrecisionShooterResult test data
    /// </summary>
    public class ShooterResultBuilder
    {
        private int _memberId = 1;
        private string _name = "Test Shooter";
        private string _club = "Test Club";
        private string _shootingClass = "A1";
        private List<PrecisionResultEntry> _results = new List<PrecisionResultEntry>();

        public ShooterResultBuilder WithMemberId(int memberId)
        {
            _memberId = memberId;
            return this;
        }

        public ShooterResultBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        public ShooterResultBuilder WithClub(string club)
        {
            _club = club;
            return this;
        }

        public ShooterResultBuilder WithShootingClass(string shootingClass)
        {
            _shootingClass = shootingClass;
            return this;
        }

        public ShooterResultBuilder WithSeries(params int[] scores)
        {
            _results = new List<PrecisionResultEntry>();
            for (int i = 0; i < scores.Length; i++)
            {
                // Create JSON for shots that add up to the score
                var shots = CreateShotsJson(scores[i], 0);
                _results.Add(new PrecisionResultEntry
                {
                    SeriesNumber = i + 1,
                    Shots = shots,
                    CompetitionId = 0,
                    TeamNumber = 1,
                    Position = 1,
                    MemberId = _memberId,
                    ShootingClass = _shootingClass,
                    EnteredBy = 1
                });
            }
            return this;
        }

        public ShooterResultBuilder WithSeriesAndXCounts(List<(int score, int xCount)> seriesData)
        {
            _results = new List<PrecisionResultEntry>();
            for (int i = 0; i < seriesData.Count; i++)
            {
                var shots = CreateShotsJson(seriesData[i].score, seriesData[i].xCount);
                _results.Add(new PrecisionResultEntry
                {
                    SeriesNumber = i + 1,
                    Shots = shots,
                    CompetitionId = 0,
                    TeamNumber = 1,
                    Position = 1,
                    MemberId = _memberId,
                    ShootingClass = _shootingClass,
                    EnteredBy = 1
                });
            }
            return this;
        }

        /// <summary>
        /// Create a JSON string representing shots that add up to the given score with X count
        /// </summary>
        private string CreateShotsJson(int score, int xCount)
        {
            var shots = new string[5];
            int remaining = score;

            // Add X shots first (X = 10 points)
            for (int i = 0; i < xCount && i < 5; i++)
            {
                shots[i] = "X";
                remaining -= 10;
            }

            // Fill remaining slots to reach target score
            for (int i = xCount; i < 5 && remaining > 0; i++)
            {
                int shotValue = Math.Min(10, remaining);
                shots[i] = shotValue.ToString();
                remaining -= shotValue;
            }

            // Fill any empty slots with "0"
            for (int i = 0; i < 5; i++)
            {
                if (string.IsNullOrEmpty(shots[i]))
                    shots[i] = "0";
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(shots);
        }

        public ShooterResultBuilder WithTotalScore(int totalScore, int seriesCount)
        {
            // Create even distribution of scores across series
            int baseScore = totalScore / seriesCount;
            int remainder = totalScore % seriesCount;

            _results = new List<PrecisionResultEntry>();
            for (int i = 0; i < seriesCount; i++)
            {
                int seriesScore = baseScore + (i < remainder ? 1 : 0);
                var shots = CreateShotsJson(seriesScore, 0);
                _results.Add(new PrecisionResultEntry
                {
                    SeriesNumber = i + 1,
                    Shots = shots,
                    CompetitionId = 0,
                    TeamNumber = 1,
                    Position = 1,
                    MemberId = _memberId,
                    ShootingClass = _shootingClass,
                    EnteredBy = 1
                });
            }
            return this;
        }

        public PrecisionShooterResult Build()
        {
            return new PrecisionShooterResult
            {
                MemberId = _memberId,
                Name = _name,
                Club = _club,
                ShootingClass = _shootingClass,
                Results = _results
            };
        }

        /// <summary>
        /// Resets the builder to default values for reuse
        /// </summary>
        public ShooterResultBuilder Reset()
        {
            _memberId = 1;
            _name = "Test Shooter";
            _club = "Test Club";
            _shootingClass = "A1";
            _results = new List<PrecisionResultEntry>();
            return this;
        }
    }
}
