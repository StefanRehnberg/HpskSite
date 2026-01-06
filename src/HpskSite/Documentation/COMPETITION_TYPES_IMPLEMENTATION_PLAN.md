# Competition Types Implementation Plan

## Objective
Refactor the competition type system to support multiple competition formats while maximizing code independence and reuse through shared utilities.

## Current State
- Only Precision type is fully implemented (28 C# files)
- Duell, Milsnabb, Helmatch exist as type definitions but have no implementations
- FieldShooting and Springskytte exist as type definitions but need new implementations
- Some utilities are scattered throughout Precision code

## Recommended Implementation Phases

## Phase 1: Extract Shared Utilities from Precision (Weeks 1-2)

### 1.1: Create Common/Utilities Folder Structure

Create these new utility files in `CompetitionTypes/Common/Utilities/`:

### 1.2: Create ScoringUtilities.cs

Extract score calculation logic from `PrecisionScoringService`:

```csharp
// CompetitionTypes/Common/Utilities/ScoringUtilities.cs
namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Pure utility functions for scoring calculations.
    /// Can be used by any competition type.
    /// </summary>
    public static class ScoringUtilities
    {
        /// <summary>
        /// Convert shot value to points for precision shooting.
        /// X = 10, 10 = 10, 9-0 = numeric value
        /// </summary>
        public static decimal ShotToPoints(string shot)
        {
            if (string.IsNullOrWhiteSpace(shot))
                return 0;

            var upper = shot.Trim().ToUpper();

            // X counts as 10
            if (upper == "X")
                return 10;

            // Try to parse numeric value
            if (decimal.TryParse(upper, out var value))
                return value >= 0 && value <= 10 ? value : 0;

            return 0;
        }

        /// <summary>
        /// Validate shot value format (0-10 or X)
        /// </summary>
        public static bool IsValidShotValue(string shotValue)
        {
            if (string.IsNullOrWhiteSpace(shotValue))
                return false;

            var upper = shotValue.Trim().ToUpper();

            if (upper == "X")
                return true;

            if (decimal.TryParse(upper, out var value))
                return value >= 0 && value <= 10;

            return false;
        }

        /// <summary>
        /// Calculate total points from a series of shots.
        /// </summary>
        public static decimal CalculateTotal(IEnumerable<string> shots)
        {
            return shots?.Sum(s => ShotToPoints(s)) ?? 0;
        }

        /// <summary>
        /// Count inner tens (X-shots) in a series.
        /// </summary>
        public static int CountInnerTens(IEnumerable<string> shots)
        {
            return shots?.Count(s => IsValidShotValue(s) && s.Trim().ToUpper() == "X") ?? 0;
        }

        /// <summary>
        /// Count all tens (X and 10) in a series.
        /// </summary>
        public static int CountAllTens(IEnumerable<string> shots)
        {
            return shots?.Count(s =>
            {
                var upper = s.Trim().ToUpper();
                return IsValidShotValue(s) && (upper == "X" || upper == "10");
            }) ?? 0;
        }
    }
}
```

### 1.3: Create RankingUtilities.cs

```csharp
// CompetitionTypes/Common/Utilities/RankingUtilities.cs
namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Utility functions for ranking and tie-breaking.
    /// Works with any type that has comparable properties.
    /// </summary>
    public static class RankingUtilities
    {
        /// <summary>
        /// Rank items with tie-breaking rules.
        /// </summary>
        /// <example>
        /// var ranked = RankingUtilities.RankWithTieBreakers(
        ///     participants,
        ///     p => -p.TotalScore,      // Primary: highest score
        ///     p => -p.InnerTensCount,  // Tiebreaker 1: most X's
        ///     p => p.Name               // Tiebreaker 2: alphabetical
        /// );
        /// </example>
        public static List<(T item, int rank)> RankWithTieBreakers<T>(
            IEnumerable<T> items,
            params Func<T, IComparable>[] rankingRules)
        {
            if (!rankingRules.Any())
                throw new ArgumentException("At least one ranking rule required");

            var list = items.ToList();
            if (!list.Any())
                return new List<(T, int)>();

            // Sort by all rules (in order of importance)
            var sorted = list.AsEnumerable();
            foreach (var rule in rankingRules)
            {
                sorted = sorted.OrderBy(rule);
            }

            // Assign ranks with tie support (same score = same rank)
            var ranked = new List<(T, int)>();
            int currentRank = 1;
            IComparable previousValue = null;

            foreach (var item in sorted)
            {
                var currentValue = rankingRules[0](item);
                if (previousValue != null && currentValue.CompareTo(previousValue) != 0)
                {
                    currentRank = ranked.Count + 1;
                }

                ranked.Add((item, currentRank));
                previousValue = currentValue;
            }

            return ranked;
        }

        /// <summary>
        /// Group results by shooting class.
        /// </summary>
        public static Dictionary<string, List<T>> GroupByProperty<T>(
            IEnumerable<T> items,
            Func<T, string> groupSelector)
        {
            return items.GroupBy(groupSelector)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        /// <summary>
        /// Rank within each group independently.
        /// </summary>
        public static Dictionary<string, List<(T item, int rank)>> RankByGroup<T>(
            IEnumerable<T> items,
            Func<T, string> groupSelector,
            params Func<T, IComparable>[] rankingRules)
        {
            var grouped = GroupByProperty(items, groupSelector);
            var result = new Dictionary<string, List<(T, int)>>();

            foreach (var group in grouped)
            {
                result[group.Key] = RankWithTieBreakers(group.Value, rankingRules);
            }

            return result;
        }
    }
}
```

### 1.4: Create ValidationUtilities.cs

```csharp
// CompetitionTypes/Common/Utilities/ValidationUtilities.cs
namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Common validation utilities used across competition types.
    /// </summary>
    public static class ValidationUtilities
    {
        /// <summary>
        /// Validate a list of shots is complete and valid.
        /// </summary>
        public static (bool isValid, List<string> errors) ValidateShotSeries(
            List<string> shots,
            int requiredCount = 10)
        {
            var errors = new List<string>();

            if (shots == null || shots.Count == 0)
                errors.Add($"No shots recorded");
            else if (shots.Count < requiredCount)
                errors.Add($"Only {shots.Count}/{requiredCount} shots recorded");
            else if (shots.Count > requiredCount)
                errors.Add($"Too many shots: {shots.Count} (max {requiredCount})");

            var invalidShots = shots
                .Where(s => !ScoringUtilities.IsValidShotValue(s))
                .ToList();

            if (invalidShots.Any())
                errors.Add($"Invalid shot values: {string.Join(", ", invalidShots)}");

            return (errors.Count == 0, errors);
        }

        /// <summary>
        /// Validate a complete registration.
        /// </summary>
        public static List<string> ValidateRegistration(dynamic registration)
        {
            var errors = new List<string>();

            // Validate required fields
            if (string.IsNullOrWhiteSpace(registration.memberName))
                errors.Add("Member name required");

            if (string.IsNullOrWhiteSpace(registration.shootingClass))
                errors.Add("Shooting class required");

            if (string.IsNullOrWhiteSpace(registration.weaponType))
                errors.Add("Weapon type required");

            return errors;
        }

        /// <summary>
        /// Validate competition dates are reasonable.
        /// </summary>
        public static (bool isValid, string error) ValidateCompetitionDates(
            DateTime startDate,
            DateTime? endDate)
        {
            if (startDate == default)
                return (false, "Start date required");

            if (startDate < DateTime.Now.AddDays(-1))
                return (false, "Start date cannot be in the past");

            if (endDate.HasValue && endDate < startDate)
                return (false, "End date cannot be before start date");

            return (true, "");
        }
    }
}
```

### 1.5: Create ExportUtilities.cs

```csharp
// CompetitionTypes/Common/Utilities/ExportUtilities.cs
namespace HpskSite.CompetitionTypes.Common.Utilities
{
    using System.Text;
    using System.Reflection;

    /// <summary>
    /// Utilities for exporting results to various formats.
    /// </summary>
    public static class ExportUtilities
    {
        /// <summary>
        /// Export items to CSV format.
        /// </summary>
        public static string ExportToCsv<T>(IEnumerable<T> items)
        {
            if (!items.Any())
                return "";

            var sb = new StringBuilder();
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.IgnoreCase);

            // Header row
            sb.AppendLine(string.Join(",", properties.Select(p => EscapeCsv(p.Name))));

            // Data rows
            foreach (var item in items)
            {
                var values = properties.Select(p => EscapeCsv(p.GetValue(item)?.ToString() ?? ""));
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escape CSV field values.
        /// </summary>
        private static string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field))
                return "\"\"";

            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
                return "\"" + field.Replace("\"", "\"\"") + "\"";

            return field;
        }

        /// <summary>
        /// Export to HTML table format.
        /// </summary>
        public static string ExportToHtml<T>(
            IEnumerable<T> items,
            string title = "",
            params Func<T, (string columnName, object value)>[] columns)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<table class='results-table'>");
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine($"<caption>{HtmlEncode(title)}</caption>");

            // Headers
            sb.AppendLine("<thead><tr>");
            foreach (var item in items.FirstOrDefault() != null ? columns : new Func<T, (string, object)>[0])
            {
                sb.AppendLine("<th></th>"); // Simplified for example
            }
            sb.AppendLine("</tr></thead>");

            // Data rows
            sb.AppendLine("<tbody>");
            foreach (var item in items)
            {
                sb.AppendLine("<tr>");
                foreach (var column in columns)
                {
                    var (name, value) = column(item);
                    sb.AppendLine($"<td>{HtmlEncode(value?.ToString() ?? "")}</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");

            return sb.ToString();
        }

        /// <summary>
        /// HTML encode special characters.
        /// </summary>
        private static string HtmlEncode(string text)
        {
            return System.Net.WebUtility.HtmlEncode(text);
        }
    }
}
```

### 1.6: Create DateTimeUtilities.cs

```csharp
// CompetitionTypes/Common/Utilities/DateTimeUtilities.cs
namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Utility functions for date and time formatting.
    /// </summary>
    public static class DateTimeUtilities
    {
        /// <summary>
        /// Format competition date in Swedish locale.
        /// </summary>
        public static string FormatCompetitionDate(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.GetCultureInfo("sv-SE"));
        }

        /// <summary>
        /// Format time in HH:MM format.
        /// </summary>
        public static string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}";
        }

        /// <summary>
        /// Format time from DateTime.
        /// </summary>
        public static string FormatTime(DateTime dateTime)
        {
            return dateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Format competition schedule.
        /// </summary>
        public static string FormatSchedule(DateTime start, DateTime? end)
        {
            var startStr = FormatCompetitionDate(start);
            if (end.HasValue && end.Value.Date != start.Date)
                return $"{startStr} - {FormatCompetitionDate(end.Value)}";
            return startStr;
        }

        /// <summary>
        /// Parse date from Swedish format.
        /// </summary>
        public static bool TryParseCompetitionDate(string dateStr, out DateTime date)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            return DateTime.TryParseExact(dateStr, "yyyy-MM-dd", culture, System.Globalization.DateTimeStyles.None, out date);
        }
    }
}
```

### 1.7: Refactor PrecisionScoringService

Update `PrecisionScoringService` to use shared utilities:

```csharp
// In CompetitionTypes/Precision/Services/PrecisionScoringService.cs
using HpskSite.CompetitionTypes.Common.Utilities;

public class PrecisionScoringService : IScoringService
{
    /// <summary>
    /// Calculate total points for a series of shots.
    /// </summary>
    public decimal CalculateSeriesTotal(List<string> shots)
    {
        return ScoringUtilities.CalculateTotal(shots);
    }

    /// <summary>
    /// Calculate inner tens (X-shots) from a series.
    /// </summary>
    public int CalculateInnerTens(List<string> shots)
    {
        return ScoringUtilities.CountInnerTens(shots);
    }

    /// <summary>
    /// Calculate tens count (10 and X) from a series.
    /// </summary>
    public int CalculateTens(List<string> shots)
    {
        return ScoringUtilities.CountAllTens(shots);
    }

    /// <summary>
    /// Validate if a shot value is valid.
    /// </summary>
    public bool IsValidShotValue(string shotValue)
    {
        return ScoringUtilities.IsValidShotValue(shotValue);
    }

    // Keep any Precision-specific scoring methods that aren't in utilities
    // ...
}
```

### 1.8: Testing Shared Utilities

Create tests for utilities:

```csharp
// CompetitionTypes/Common/Tests/ScoringUtilitiesTests.cs
using Xunit;
using HpskSite.CompetitionTypes.Common.Utilities;

public class ScoringUtilitiesTests
{
    [Fact]
    public void ShotToPoints_WithX_Returns10()
    {
        var result = ScoringUtilities.ShotToPoints("X");
        Assert.Equal(10, result);
    }

    [Fact]
    public void ShotToPoints_With10_Returns10()
    {
        var result = ScoringUtilities.ShotToPoints("10");
        Assert.Equal(10, result);
    }

    [Fact]
    public void ShotToPoints_With5_Returns5()
    {
        var result = ScoringUtilities.ShotToPoints("5");
        Assert.Equal(5, result);
    }

    [Fact]
    public void CalculateTotal_WithValidShots_ReturnsCorrectSum()
    {
        var shots = new List<string> { "X", "10", "9", "8" };
        var result = ScoringUtilities.CalculateTotal(shots);
        Assert.Equal(37, result);
    }

    [Fact]
    public void IsValidShotValue_WithX_ReturnsTrue()
    {
        Assert.True(ScoringUtilities.IsValidShotValue("X"));
    }

    [Fact]
    public void IsValidShotValue_WithInvalidValue_ReturnsFalse()
    {
        Assert.False(ScoringUtilities.IsValidShotValue("11"));
        Assert.False(ScoringUtilities.IsValidShotValue("-1"));
        Assert.False(ScoringUtilities.IsValidShotValue("abc"));
    }
}
```

## Phase 2: Create Duell as Precision Alias (Week 2)

### 2.1: Create Duell Namespace Structure

```
/CompetitionTypes/
  /Duell/
    /Services/
      DuellScoringService.cs
      DuellResultsService.cs
      DuellStartListService.cs
      DuellRegistrationService.cs
      DuellCompetitionEditService.cs
    /Tests/
      DuellScoringTests.cs
```

### 2.2: Implement Duell Services

Since Duell is identical to Precision:

```csharp
// CompetitionTypes/Duell/Services/DuellScoringService.cs
using HpskSite.CompetitionTypes.Precision.Services;

namespace HpskSite.CompetitionTypes.Duell.Services
{
    public class DuellScoringService : PrecisionScoringService
    {
        // Inherits all scoring logic from Precision
        // No additional code needed
    }
}

// CompetitionTypes/Duell/Services/DuellResultsService.cs
using HpskSite.CompetitionTypes.Precision.Services;

namespace HpskSite.CompetitionTypes.Duell.Services
{
    public class DuellResultsService : PrecisionResultsService
    {
        // Inherits all results logic from Precision
        // No additional code needed
    }
}

// ... similar for StartListService, RegistrationService, CompetitionEditService
```

### 2.3: Wire Up in DI Container

Update `Program.cs`:

```csharp
// Register Duell services (using Precision implementations)
services.AddScoped<Duell.Services.DuellScoringService>();
services.AddScoped<Duell.Services.DuellResultsService>();
// ... etc
```

## Phase 3: Implement Milsnabb (Weeks 3-4)

### 3.1: Create Milsnabb Models

```csharp
// CompetitionTypes/Milsnabb/Models/MilsnabbPartResult.cs
namespace HpskSite.CompetitionTypes.Milsnabb.Models
{
    public class MilsnabbPartResult
    {
        public int PartNumber { get; set; }          // 1, 2, 3, or 4
        public string PartName { get; set; }
        public decimal TotalScore { get; set; }
        public List<string> Shots { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    public class MilsnabbCompetitionResult
    {
        public int RegistrationId { get; set; }
        public string MemberName { get; set; }
        public List<MilsnabbPartResult> Parts { get; set; }  // 4 parts
        public decimal TotalScore { get; set; }               // Sum of all parts
        public int Rank { get; set; }
        public string ShootingClass { get; set; }
    }
}
```

### 3.2: Implement Milsnabb Scoring Service

```csharp
// CompetitionTypes/Milsnabb/Services/MilsnabbScoringService.cs
using HpskSite.CompetitionTypes.Common.Interfaces;
using HpskSite.CompetitionTypes.Common.Utilities;

namespace HpskSite.CompetitionTypes.Milsnabb.Services
{
    public class MilsnabbScoringService : IScoringService
    {
        /// <summary>
        /// Calculate score for a single series in Milsnabb.
        /// Milsnabb uses same shot-to-points conversion as Precision.
        /// </summary>
        public decimal CalculateSeriesTotal(List<string> shots)
        {
            return ScoringUtilities.CalculateTotal(shots);
        }

        public int CalculateInnerTens(List<string> shots)
        {
            return ScoringUtilities.CountInnerTens(shots);
        }

        public int CalculateTens(List<string> shots)
        {
            return ScoringUtilities.CountAllTens(shots);
        }

        public bool IsValidShotValue(string shotValue)
        {
            return ScoringUtilities.IsValidShotValue(shotValue);
        }

        /// <summary>
        /// Calculate total for all 4 parts combined.
        /// </summary>
        public decimal CalculateTotalForAllParts(List<List<string>> allPartShots)
        {
            return allPartShots.Sum(partShots => CalculateSeriesTotal(partShots));
        }
    }
}
```

### 3.3: Implement Milsnabb Results Service

```csharp
// CompetitionTypes/Milsnabb/Services/MilsnabbResultsService.cs
using HpskSite.CompetitionTypes.Common.Interfaces;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Milsnabb.Models;

namespace HpskSite.CompetitionTypes.Milsnabb.Services
{
    public class MilsnabbResultsService : IResultsService
    {
        private readonly MilsnabbScoringService _scoringService;

        public MilsnabbResultsService(MilsnabbScoringService? scoringService = null)
        {
            _scoringService = scoringService ?? new MilsnabbScoringService();
        }

        public async Task<List<dynamic>> GenerateCompetitionResults(int competitionId)
        {
            // 1. Get all registrations for competition
            // 2. For each registration:
            //    a. Get results for all 4 parts
            //    b. Calculate score for each part
            //    c. Sum all parts
            // 3. Return results with part breakdown

            var results = new List<dynamic>();

            // TODO: Fetch registrations from data source
            // foreach (var registration in registrations)
            // {
            //     var entry = new MilsnabbCompetitionResult
            //     {
            //         RegistrationId = registration.Id,
            //         MemberName = registration.MemberName,
            //         Parts = new List<MilsnabbPartResult>()
            //     };

            //     for (int partNum = 1; partNum <= 4; partNum++)
            //     {
            //         var partShots = await GetPartShots(registration.Id, partNum);
            //         entry.Parts.Add(new MilsnabbPartResult
            //         {
            //             PartNumber = partNum,
            //             PartName = $"Del {partNum}",
            //             TotalScore = _scoringService.CalculateSeriesTotal(partShots),
            //             Shots = partShots
            //         });
            //     }

            //     entry.TotalScore = entry.Parts.Sum(p => p.TotalScore);
            //     results.Add(entry);
            // }

            // Apply ranking with tie-breaking
            // var ranked = RankingUtilities.RankWithTieBreakers(
            //     results,
            //     r => -(decimal)r.TotalScore,    // Highest total first
            //     r => -r.Parts[0].TotalScore,    // Tiebreaker: Part 1 score
            //     r => (string)r.MemberName       // Tiebreaker: Name
            // );

            return results;
        }

        public Task<List<dynamic>> GetLiveLeaderboard(int competitionId)
        {
            // Similar to GenerateCompetitionResults but for in-progress competition
            throw new NotImplementedException();
        }

        public Task<dynamic> GetParticipantResults(int registrationId)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> ExportResults(int competitionId, string format)
        {
            var results = GenerateCompetitionResults(competitionId).Result;

            return format.ToLower() switch
            {
                "csv" => Task.FromResult(ExportToCsv(results)),
                "html" => Task.FromResult(ExportToHtml(results)),
                _ => Task.FromResult(new byte[0])
            };
        }

        public Task<List<dynamic>> CalculateFinalRanking(int competitionId)
        {
            return GenerateCompetitionResults(competitionId);
        }

        private byte[] ExportToCsv(List<dynamic> results)
        {
            var csv = ExportUtilities.ExportToCsv(results);
            return System.Text.Encoding.UTF8.GetBytes(csv);
        }

        private byte[] ExportToHtml(List<dynamic> results)
        {
            var html = ExportUtilities.ExportToHtml(
                results,
                "Milsnabb Results"
            );
            return System.Text.Encoding.UTF8.GetBytes(html);
        }
    }
}
```

## Phase 4: Implement Helmatch (Weeks 4-5)

### 4.1: Create Similar Structure to Milsnabb

Follow the same pattern as Milsnabb but with:
- 3 parts instead of 4
- Potentially different part names (Precision, Snabbskytte, Fält)
- Otherwise identical implementation structure

## Phase 5: Future Types - FieldShooting and Springskytte (Later)

When implementing FieldShooting and Springskytte:
- Create completely independent implementations
- No inheritance or references to Precision
- Use shared utilities (ScoringUtilities, RankingUtilities, etc.)
- Follow the same 5-service pattern
- Each type fully self-contained

## Implementation Checklist

### Phase 1: Shared Utilities
- [ ] Create ScoringUtilities.cs
- [ ] Create RankingUtilities.cs
- [ ] Create ValidationUtilities.cs
- [ ] Create ExportUtilities.cs
- [ ] Create DateTimeUtilities.cs
- [ ] Create unit tests for utilities
- [ ] Refactor PrecisionScoringService to use utilities
- [ ] Verify Precision still compiles and works
- [ ] Commit changes

### Phase 2: Duell Alias
- [ ] Create Duell folder structure
- [ ] Create service files extending Precision equivalents
- [ ] Register in DI container
- [ ] Verify compiles
- [ ] Commit changes

### Phase 3: Milsnabb
- [ ] Create Milsnabb folder structure
- [ ] Create models (MilsnabbPartResult, MilsnabbCompetitionResult)
- [ ] Implement MilsnabbScoringService
- [ ] Implement MilsnabbResultsService
- [ ] Implement MilsnabbStartListService
- [ ] Implement MilsnabbRegistrationService
- [ ] Implement MilsnabbCompetitionEditService
- [ ] Create unit tests
- [ ] Register in DI container
- [ ] Test with sample data
- [ ] Commit changes

### Phase 4: Helmatch
- [ ] Follow same steps as Milsnabb (3 parts instead of 4)

### Phase 5: Update Documentation
- [ ] Update COMPETITION_TYPES_ARCHITECTURE_GUIDE.md with actual implementations
- [ ] Add code examples from real implementations
- [ ] Update migration guide with lessons learned

## Success Criteria

✅ All shared utilities extracted and tested independently
✅ Duell works as Precision alias
✅ Milsnabb can generate results with 4-part aggregation
✅ Helmatch can generate results with 3-part aggregation
✅ Zero compilation errors or warnings
✅ All existing Precision functionality unchanged
✅ Unit tests cover all core business logic
✅ Code review pass with maintainability focus
✅ Documentation is complete and accurate

## Estimated Effort

- Phase 1: 4-6 days (extraction, testing, refactoring)
- Phase 2: 1-2 days (simple alias pattern)
- Phase 3: 5-7 days (new logic for multi-part results)
- Phase 4: 3-4 days (similar to Milsnabb, faster)
- Phase 5 (Future): 7-10 days per type (fully independent implementations)

**Total for all 5 implemented types: 4-5 weeks**
