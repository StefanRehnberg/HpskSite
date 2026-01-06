using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for ValidationUtilities
    /// Tests input validation and error checking
    /// </summary>
    public class ValidationUtilitiesTests
    {
        // ============ ValidateShotSeries Tests ============

        [Fact]
        public void ValidateShotSeries_WithCompleteSeries_ReturnsValid()
        {
            var shots = new List<string> { "X", "10", "9", "8", "7", "6", "5", "4", "3", "2" };
            var (isValid, errors) = ValidationUtilities.ValidateShotSeries(shots, 10);

            Assert.True(isValid);
            Assert.Empty(errors);
        }

        [Fact]
        public void ValidateShotSeries_WithIncompleteSeries_ReturnsInvalid()
        {
            var shots = new List<string> { "X", "10", "9" };
            var (isValid, errors) = ValidationUtilities.ValidateShotSeries(shots, 10);

            Assert.False(isValid);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("Incomplete"));
        }

        [Fact]
        public void ValidateShotSeries_WithInvalidShots_ReturnsError()
        {
            var shots = new List<string> { "X", "10", "invalid", "8", "7", "6", "5", "4", "3", "2" };
            var (isValid, errors) = ValidationUtilities.ValidateShotSeries(shots, 10);

            Assert.False(isValid);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("Invalid"));
        }

        [Fact]
        public void ValidateShotSeries_WithEmptyList_ReturnsInvalid()
        {
            var shots = new List<string>();
            var (isValid, errors) = ValidationUtilities.ValidateShotSeries(shots, 10);

            Assert.False(isValid);
            Assert.NotEmpty(errors);
        }

        [Fact]
        public void ValidateShotSeries_WithNull_ReturnsInvalid()
        {
            var (isValid, errors) = ValidationUtilities.ValidateShotSeries(null, 10);

            Assert.False(isValid);
            Assert.NotEmpty(errors);
        }

        [Fact]
        public void ValidateShotSeries_WithTooManyShots_ReturnsError()
        {
            var shots = new List<string> { "X", "10", "9", "8", "7", "6", "5", "4", "3", "2", "1", "0" };
            var (isValid, errors) = ValidationUtilities.ValidateShotSeries(shots, 10);

            Assert.False(isValid);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("Too many"));
        }

        // ============ ValidateRegistration Tests ============

        [Fact]
        public void ValidateRegistration_WithCompleteData_ReturnsNoErrors()
        {
            dynamic registration = new
            {
                memberName = "John Doe",
                shootingClass = "A",
                weaponType = "Pistol",
                memberId = 123,
                competitionId = 456
            };

            var errors = ValidationUtilities.ValidateRegistration(registration);

            Assert.Empty(errors);
        }

        [Fact]
        public void ValidateRegistration_WithMissingName_ReturnsError()
        {
            dynamic registration = new
            {
                memberName = "",
                shootingClass = "A",
                weaponType = "Pistol",
                memberId = 123,
                competitionId = 456
            };

            var errors = ValidationUtilities.ValidateRegistration(registration);

            Assert.NotEmpty(errors);
            Assert.True(((IEnumerable<string>)errors).Any(e => e.Contains("name")));
        }

        [Fact]
        public void ValidateRegistration_WithMissingClass_ReturnsError()
        {
            dynamic registration = new
            {
                memberName = "John Doe",
                shootingClass = "",
                weaponType = "Pistol",
                memberId = 123,
                competitionId = 456
            };

            var errors = ValidationUtilities.ValidateRegistration(registration);

            Assert.NotEmpty(errors);
            Assert.True(((IEnumerable<string>)errors).Any(e => e.Contains("class")));
        }

        [Fact]
        public void ValidateRegistration_WithNull_ReturnsError()
        {
            var errors = ValidationUtilities.ValidateRegistration(null);

            Assert.NotEmpty(errors);
        }

        // ============ ValidateCompetitionDates Tests ============

        [Fact]
        public void ValidateCompetitionDates_WithValidDate_ReturnsValid()
        {
            var startDate = DateTime.Now.AddDays(1);
            var (isValid, error) = ValidationUtilities.ValidateCompetitionDates(startDate, null);

            Assert.True(isValid);
            Assert.Empty(error);
        }

        [Fact]
        public void ValidateCompetitionDates_WithDefaultDate_ReturnsInvalid()
        {
            var startDate = default(DateTime);
            var (isValid, error) = ValidationUtilities.ValidateCompetitionDates(startDate, null);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        [Fact]
        public void ValidateCompetitionDates_WithEndBeforeStart_ReturnsInvalid()
        {
            var startDate = DateTime.Now.AddDays(5);
            var endDate = DateTime.Now.AddDays(1);

            var (isValid, error) = ValidationUtilities.ValidateCompetitionDates(startDate, endDate);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        [Fact]
        public void ValidateCompetitionDates_WithValidRange_ReturnsValid()
        {
            var startDate = DateTime.Now.AddDays(1);
            var endDate = DateTime.Now.AddDays(3);

            var (isValid, error) = ValidationUtilities.ValidateCompetitionDates(startDate, endDate);

            Assert.True(isValid);
            Assert.Empty(error);
        }

        // ============ IsValidEmail Tests ============

        [Theory]
        [InlineData("user@example.com")]
        [InlineData("test.name@domain.co.uk")]
        [InlineData("user+tag@example.com")]
        public void IsValidEmail_WithValidEmail_ReturnsTrue(string email)
        {
            var result = ValidationUtilities.IsValidEmail(email);
            Assert.True(result);
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("missing@domain")]
        [InlineData("@nodomain.com")]
        [InlineData("")]
        [InlineData(null)]
        public void IsValidEmail_WithInvalidEmail_ReturnsFalse(string email)
        {
            var result = ValidationUtilities.IsValidEmail(email);
            Assert.False(result);
        }

        // ============ IsValidPhoneNumber Tests ============

        [Theory]
        [InlineData("0701234567")]
        [InlineData("+46701234567")]
        [InlineData("070-123-4567")]
        public void IsValidPhoneNumber_WithValidPhone_ReturnsTrue(string phone)
        {
            var result = ValidationUtilities.IsValidPhoneNumber(phone);
            Assert.True(result);
        }

        [Theory]
        [InlineData("123")]
        [InlineData("")]
        [InlineData(null)]
        public void IsValidPhoneNumber_WithInvalidPhone_ReturnsFalse(string phone)
        {
            var result = ValidationUtilities.IsValidPhoneNumber(phone);
            Assert.False(result);
        }

        // ============ ValidateParticipantCount Tests ============

        [Fact]
        public void ValidateParticipantCount_WithValidCount_ReturnsValid()
        {
            var (isValid, error) = ValidationUtilities.ValidateParticipantCount(50, 2, 1000);

            Assert.True(isValid);
            Assert.Empty(error);
        }

        [Fact]
        public void ValidateParticipantCount_WithBelowMinimum_ReturnsInvalid()
        {
            var (isValid, error) = ValidationUtilities.ValidateParticipantCount(1, 2, 1000);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        [Fact]
        public void ValidateParticipantCount_WithAboveMaximum_ReturnsInvalid()
        {
            var (isValid, error) = ValidationUtilities.ValidateParticipantCount(1001, 2, 1000);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        // ============ ValidateNoDuplicates Tests ============

        [Fact]
        public void ValidateNoDuplicates_WithNoDuplicates_ReturnsValid()
        {
            var registrations = new List<(int id, string name)>
            {
                (1, "Alice"),
                (2, "Bob"),
                (3, "Charlie")
            };

            var (isValid, duplicates) = ValidationUtilities.ValidateNoDuplicates(
                registrations,
                item => item.id
            );

            Assert.True(isValid);
            Assert.Empty(duplicates);
        }

        [Fact]
        public void ValidateNoDuplicates_WithDuplicates_ReturnsDuplicateIds()
        {
            var registrations = new List<(int id, string name)>
            {
                (1, "Alice"),
                (2, "Bob"),
                (1, "Alice2")
            };

            var (isValid, duplicates) = ValidationUtilities.ValidateNoDuplicates(
                registrations,
                item => item.id
            );

            Assert.False(isValid);
            Assert.Single(duplicates);
            Assert.Contains(1, duplicates);
        }

        // ============ ValidateRequired Tests ============

        [Fact]
        public void ValidateRequired_WithValue_ReturnsValid()
        {
            var (isValid, error) = ValidationUtilities.ValidateRequired("John Doe", "Name");

            Assert.True(isValid);
            Assert.Empty(error);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        public void ValidateRequired_WithoutValue_ReturnsInvalid(string value)
        {
            var (isValid, error) = ValidationUtilities.ValidateRequired(value, "Name");

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        // ============ ValidateRange Tests ============

        [Fact]
        public void ValidateRange_WithValueInRange_ReturnsValid()
        {
            var (isValid, error) = ValidationUtilities.ValidateRange(5, "Score", 0, 10);

            Assert.True(isValid);
            Assert.Empty(error);
        }

        [Fact]
        public void ValidateRange_WithValueBelowRange_ReturnsInvalid()
        {
            var (isValid, error) = ValidationUtilities.ValidateRange(-1, "Score", 0, 10);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        [Fact]
        public void ValidateRange_WithValueAboveRange_ReturnsInvalid()
        {
            var (isValid, error) = ValidationUtilities.ValidateRange(11, "Score", 0, 10);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        // ============ ValidateRange (decimal) Tests ============

        [Fact]
        public void ValidateRangeDecimal_WithValueInRange_ReturnsValid()
        {
            var (isValid, error) = ValidationUtilities.ValidateRange(5.5m, "Score", 0m, 10m);

            Assert.True(isValid);
            Assert.Empty(error);
        }

        [Fact]
        public void ValidateRangeDecimal_WithValueOutOfRange_ReturnsInvalid()
        {
            var (isValid, error) = ValidationUtilities.ValidateRange(10.5m, "Score", 0m, 10m);

            Assert.False(isValid);
            Assert.NotEmpty(error);
        }

        // ============ IsValidShootingClass Tests ============

        [Fact]
        public void IsValidShootingClass_WithAllowedClass_ReturnsTrue()
        {
            var result = ValidationUtilities.IsValidShootingClass("A", "A", "B", "C");

            Assert.True(result);
        }

        [Fact]
        public void IsValidShootingClass_WithDisallowedClass_ReturnsFalse()
        {
            var result = ValidationUtilities.IsValidShootingClass("D", "A", "B", "C");

            Assert.False(result);
        }

        [Fact]
        public void IsValidShootingClass_WithNoRestrictions_ReturnsTrue()
        {
            var result = ValidationUtilities.IsValidShootingClass("AnyClass");

            Assert.True(result);
        }

        // ============ IsValidWeaponType Tests ============

        [Fact]
        public void IsValidWeaponType_WithAllowedType_ReturnsTrue()
        {
            var result = ValidationUtilities.IsValidWeaponType("Pistol", "Pistol", "Rifle");

            Assert.True(result);
        }

        [Fact]
        public void IsValidWeaponType_WithDisallowedType_ReturnsFalse()
        {
            var result = ValidationUtilities.IsValidWeaponType("Shotgun", "Pistol", "Rifle");

            Assert.False(result);
        }
    }
}
