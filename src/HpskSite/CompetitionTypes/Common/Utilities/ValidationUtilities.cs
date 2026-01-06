namespace HpskSite.CompetitionTypes.Common.Utilities
{
    using Microsoft.CSharp.RuntimeBinder;

    /// <summary>
    /// Common validation utilities used across competition types.
    /// Provides standard validation rules and error messages.
    /// </summary>
    public static class ValidationUtilities
    {
        /// <summary>
        /// Validate a list of shots is complete and valid.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <param name="requiredCount">Expected number of shots (default 10)</param>
        /// <returns>Tuple with validation result and list of errors</returns>
        public static (bool isValid, List<string> errors) ValidateShotSeries(
            List<string> shots,
            int requiredCount = 10)
        {
            var errors = new List<string>();

            if (shots == null)
            {
                errors.Add("Shot list is null");
                return (false, errors);
            }

            if (shots.Count == 0)
            {
                errors.Add($"No shots recorded. Expected {requiredCount} shots.");
            }
            else if (shots.Count < requiredCount)
            {
                errors.Add($"Incomplete series: {shots.Count}/{requiredCount} shots recorded");
            }
            else if (shots.Count > requiredCount)
            {
                errors.Add($"Too many shots: {shots.Count} shots recorded (max {requiredCount})");
            }

            var invalidShots = shots
                .Where((s, idx) => !ScoringUtilities.IsValidShotValue(s))
                .ToList();

            if (invalidShots.Any())
            {
                errors.Add($"Invalid shot values found: {string.Join(", ", invalidShots)}");
            }

            return (errors.Count == 0, errors);
        }

        /// <summary>
        /// Validate a registration has required fields.
        /// </summary>
        /// <param name="registration">Registration object as dynamic</param>
        /// <returns>List of validation errors (empty if valid)</returns>
        public static List<string> ValidateRegistration(dynamic registration)
        {
            var errors = new List<string>();

            // Validate required fields
            if (registration == null)
            {
                errors.Add("Registration is null");
                return errors;
            }

            // Get the object's properties dynamically
            try
            {
                // Use reflection to get properties from the dynamic object's actual type
                var registrationObj = (object)registration;
                var props = registrationObj.GetType().GetProperties();
                var propDict = props.ToDictionary(p => p.Name, p => p.GetValue(registrationObj));

                // Check memberName
                if (!propDict.ContainsKey("memberName") || string.IsNullOrWhiteSpace(propDict["memberName"]?.ToString()))
                    errors.Add("Member name is required");

                // Check shootingClass
                if (!propDict.ContainsKey("shootingClass") || string.IsNullOrWhiteSpace(propDict["shootingClass"]?.ToString()))
                    errors.Add("Shooting class is required");

                // Check weaponType
                if (!propDict.ContainsKey("weaponType") || string.IsNullOrWhiteSpace(propDict["weaponType"]?.ToString()))
                    errors.Add("Weapon type is required");

                // Check memberId
                if (!propDict.ContainsKey("memberId") || (propDict["memberId"] is int id && id <= 0))
                    errors.Add("Valid member ID is required");

                // Check competitionId
                if (!propDict.ContainsKey("competitionId") || (propDict["competitionId"] is int cid && cid <= 0))
                    errors.Add("Valid competition ID is required");
            }
            catch (Exception)
            {
                errors.Add("Registration object validation failed");
            }

            return errors;
        }

        /// <summary>
        /// Validate competition dates are reasonable.
        /// </summary>
        /// <param name="startDate">Competition start date</param>
        /// <param name="endDate">Optional competition end date</param>
        /// <returns>Tuple with validation result and error message if invalid</returns>
        public static (bool isValid, string error) ValidateCompetitionDates(
            DateTime startDate,
            DateTime? endDate)
        {
            if (startDate == default)
                return (false, "Start date is required");

            // Allow dates in the past for historical competitions
            // But warn if more than 1 year in past
            if (startDate < DateTime.Now.AddYears(-1))
                return (false, "Start date appears to be too far in the past (more than 1 year)");

            if (endDate.HasValue)
            {
                if (endDate == default)
                    return (false, "End date has invalid value");

                if (endDate < startDate)
                    return (false, "End date cannot be before start date");

                // End date should be same day or later
                var daysDifference = (endDate.Value.Date - startDate.Date).TotalDays;
                if (daysDifference > 30)
                    return (false, "Competition span cannot exceed 30 days");
            }

            return (true, "");
        }

        /// <summary>
        /// Validate email address format.
        /// </summary>
        /// <param name="email">Email address to validate</param>
        /// <returns>True if email appears valid, false otherwise</returns>
        public static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                // Must have a valid address and contain a dot after @ for domain
                // This ensures emails like "missing@domain" (no TLD) are rejected
                return addr.Address == email.Trim() && addr.Host.Contains(".");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validate phone number format (basic check).
        /// </summary>
        /// <param name="phone">Phone number to validate</param>
        /// <returns>True if phone appears valid, false otherwise</returns>
        public static bool IsValidPhoneNumber(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return false;

            var cleaned = System.Text.RegularExpressions.Regex.Replace(phone, @"[^\d+\-\s()]", "");
            return cleaned.Length >= 7; // At least 7 digits
        }

        /// <summary>
        /// Validate participant count is reasonable for a competition.
        /// </summary>
        /// <param name="registrationCount">Number of registered participants</param>
        /// <param name="minRequired">Minimum required participants (default 2)</param>
        /// <param name="maxAllowed">Maximum allowed participants (default 1000)</param>
        /// <returns>Tuple with validation result and error message if invalid</returns>
        public static (bool isValid, string error) ValidateParticipantCount(
            int registrationCount,
            int minRequired = 2,
            int maxAllowed = 1000)
        {
            if (registrationCount < minRequired)
                return (false, $"Minimum {minRequired} participants required");

            if (registrationCount > maxAllowed)
                return (false, $"Too many participants ({registrationCount}). Maximum {maxAllowed} allowed.");

            return (true, "");
        }

        /// <summary>
        /// Validate all required registrations are present for a competition.
        /// </summary>
        /// <param name="registrations">List of registrations</param>
        /// <returns>Tuple with validation result and list of errors</returns>
        public static (bool isValid, List<string> errors) ValidateCompleteRegistrations(
            IEnumerable<dynamic> registrations)
        {
            var errors = new List<string>();

            if (registrations == null)
            {
                errors.Add("Registrations list is null");
                return (false, errors);
            }

            var regList = registrations.ToList();
            if (!regList.Any())
            {
                errors.Add("No registrations found");
                return (false, errors);
            }

            // Check each registration
            foreach (var reg in regList)
            {
                var regErrors = ValidateRegistration(reg);
                if (regErrors.Any())
                {
                    errors.AddRange(regErrors);
                }
            }

            return (errors.Count == 0, errors);
        }

        /// <summary>
        /// Validate shooting class value is recognized.
        /// </summary>
        /// <param name="shootingClass">Shooting class name</param>
        /// <param name="allowedClasses">List of valid class names</param>
        /// <returns>True if class is valid, false otherwise</returns>
        public static bool IsValidShootingClass(
            string shootingClass,
            params string[] allowedClasses)
        {
            if (string.IsNullOrWhiteSpace(shootingClass))
                return false;

            if (allowedClasses == null || allowedClasses.Length == 0)
                return !string.IsNullOrWhiteSpace(shootingClass); // No restrictions

            return allowedClasses.Any(c => c.Equals(shootingClass, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate weapon type value.
        /// </summary>
        /// <param name="weaponType">Weapon type name</param>
        /// <param name="allowedTypes">List of valid weapon types</param>
        /// <returns>True if weapon type is valid, false otherwise</returns>
        public static bool IsValidWeaponType(
            string weaponType,
            params string[] allowedTypes)
        {
            if (string.IsNullOrWhiteSpace(weaponType))
                return false;

            if (allowedTypes == null || allowedTypes.Length == 0)
                return !string.IsNullOrWhiteSpace(weaponType); // No restrictions

            return allowedTypes.Any(t => t.Equals(weaponType, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate that no duplicate registrations exist.
        /// </summary>
        /// <param name="registrations">List of registrations</param>
        /// <param name="memberIdSelector">Function to get member ID from registration</param>
        /// <returns>Tuple with validation result and list of duplicate member IDs</returns>
        public static (bool isValid, List<int> duplicateIds) ValidateNoDuplicates<T>(
            IEnumerable<T> registrations,
            Func<T, int> memberIdSelector)
        {
            var seenIds = new HashSet<int>();
            var duplicates = new List<int>();

            foreach (var reg in registrations ?? Enumerable.Empty<T>())
            {
                var id = memberIdSelector(reg);
                if (seenIds.Contains(id))
                {
                    if (!duplicates.Contains(id))
                        duplicates.Add(id);
                }
                seenIds.Add(id);
            }

            return (duplicates.Count == 0, duplicates);
        }

        /// <summary>
        /// Validate string is not null or empty.
        /// </summary>
        /// <param name="value">String to validate</param>
        /// <param name="fieldName">Name of field (for error message)</param>
        /// <returns>Tuple with validation result and error message if invalid</returns>
        public static (bool isValid, string error) ValidateRequired(
            string value,
            string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
                return (false, $"{fieldName} is required");

            return (true, "");
        }

        /// <summary>
        /// Validate integer is within range.
        /// </summary>
        /// <param name="value">Integer to validate</param>
        /// <param name="fieldName">Name of field (for error message)</param>
        /// <param name="minValue">Minimum allowed value (inclusive)</param>
        /// <param name="maxValue">Maximum allowed value (inclusive)</param>
        /// <returns>Tuple with validation result and error message if invalid</returns>
        public static (bool isValid, string error) ValidateRange(
            int value,
            string fieldName,
            int minValue,
            int maxValue)
        {
            if (value < minValue || value > maxValue)
                return (false, $"{fieldName} must be between {minValue} and {maxValue}");

            return (true, "");
        }

        /// <summary>
        /// Validate decimal is within range.
        /// </summary>
        /// <param name="value">Decimal to validate</param>
        /// <param name="fieldName">Name of field (for error message)</param>
        /// <param name="minValue">Minimum allowed value (inclusive)</param>
        /// <param name="maxValue">Maximum allowed value (inclusive)</param>
        /// <returns>Tuple with validation result and error message if invalid</returns>
        public static (bool isValid, string error) ValidateRange(
            decimal value,
            string fieldName,
            decimal minValue,
            decimal maxValue)
        {
            if (value < minValue || value > maxValue)
                return (false, $"{fieldName} must be between {minValue} and {maxValue}");

            return (true, "");
        }
    }
}
