using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Umbraco.Cms.Core.Services;
using HpskSite.CompetitionTypes.Common.Interfaces;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Service for editing and saving Precision competition data.
    /// Handles validation and persistence of Precision-specific competition properties.
    /// </summary>
    public class PrecisionCompetitionEditService : ICompetitionEditService
    {
        private readonly IContentService _contentService;

        public PrecisionCompetitionEditService(IContentService contentService)
        {
            _contentService = contentService;
        }

        public async Task<CompetitionEditResult> SaveCompetitionAsync(int competitionId, Dictionary<string, object> fields)
        {
            try
            {
                // Validate fields first
                var validationResult = ValidateFields(fields);
                if (!validationResult.IsValid)
                {
                    return CompetitionEditResult.ErrorResult(
                        "Validation failed",
                        validationResult.Errors
                    );
                }

                // Get the competition content
                var content = _contentService.GetById(competitionId);
                if (content == null)
                {
                    return CompetitionEditResult.ErrorResult("Competition not found");
                }

                // Apply field updates to content
                foreach (var field in fields)
                {
                    // Map field names to Umbraco property aliases
                    var propertyAlias = MapFieldNameToAlias(field.Key);
                    if (string.IsNullOrEmpty(propertyAlias))
                    {
                        continue; // Skip unknown fields
                    }

                    // Convert value to appropriate type
                    var convertedValue = ConvertFieldValue(field.Key, field.Value);
                    
                    // Set the property on the content
                    if (content.Properties.FirstOrDefault(p => p.Alias == propertyAlias) != null)
                    {
                        content.SetValue(propertyAlias, convertedValue);
                    }
                }

                // Save the content
                _contentService.Save(content);

                // Publish the content to make it visible on the frontend
                // For invariant content, use wildcard culture
                _contentService.Publish(content, new[] { "*" });

                return CompetitionEditResult.SuccessResult(
                    "Competition updated successfully",
                    new { competitionId = content.Id }
                );
            }
            catch (Exception ex)
            {
                return CompetitionEditResult.ErrorResult(
                    "Error saving competition",
                    new Dictionary<string, string> { { "general", ex.Message } }
                );
            }
        }

        public ValidationResult ValidateFields(Dictionary<string, object> fields)
        {
            var errors = new Dictionary<string, string>();

            foreach (var field in fields)
            {
                var error = ValidateField(field.Key, field.Value);
                if (!string.IsNullOrEmpty(error))
                {
                    errors[field.Key] = error;
                }
            }

            return errors.Any() 
                ? ValidationResult.Invalid(errors) 
                : ValidationResult.Valid();
        }

        public List<EditableFieldDefinition> GetEditableFields()
        {
            return new List<EditableFieldDefinition>
            {
                // Basic Information Section
                new EditableFieldDefinition
                {
                    FieldName = "competitionName",
                    Label = "Tävlingsnamn",
                    FieldType = "text",
                    Section = "Grundinformation",
                    IsRequired = true,
                    Order = 1
                },
                new EditableFieldDefinition
                {
                    FieldName = "description",
                    Label = "Beskrivning",
                    FieldType = "textarea",
                    Section = "Grundinformation",
                    IsRequired = false,
                    Order = 2
                },
                new EditableFieldDefinition
                {
                    FieldName = "venue",
                    Label = "Plats",
                    FieldType = "text",
                    Section = "Grundinformation",
                    IsRequired = true,
                    Order = 3
                },

                // Dates Section
                new EditableFieldDefinition
                {
                    FieldName = "competitionDate",
                    Label = "Tävlingsdatum",
                    FieldType = "datetime",
                    Section = "Datum",
                    IsRequired = true,
                    Order = 1
                },
                new EditableFieldDefinition
                {
                    FieldName = "competitionEndDate",
                    Label = "Slutdatum (för fleradagstävlingar)",
                    FieldType = "date",
                    Section = "Datum",
                    HelpText = "Lämna tomt för endagstävlingar",
                    IsRequired = false,
                    Order = 2
                },

                // Registration Section
                new EditableFieldDefinition
                {
                    FieldName = "registrationOpenDate",
                    Label = "Anmälan öppnar",
                    FieldType = "datetime",
                    Section = "Anmälan",
                    IsRequired = true,
                    Order = 1
                },
                new EditableFieldDefinition
                {
                    FieldName = "registrationCloseDate",
                    Label = "Anmälan stänger",
                    FieldType = "datetime",
                    Section = "Anmälan",
                    IsRequired = true,
                    Order = 2
                },
                new EditableFieldDefinition
                {
                    FieldName = "maxParticipants",
                    Label = "Max antal deltagare",
                    FieldType = "number",
                    Section = "Anmälan",
                    IsRequired = true,
                    Order = 3
                },
                new EditableFieldDefinition
                {
                    FieldName = "registrationFee",
                    Label = "Anmälningsavgift (kr)",
                    FieldType = "number",
                    Section = "Anmälan",
                    IsRequired = false,
                    Order = 4
                },

                // Contact Information Section
                new EditableFieldDefinition
                {
                    FieldName = "competitionDirector",
                    Label = "Tävlingsledare",
                    FieldType = "text",
                    Section = "Kontakt",
                    IsRequired = true,
                    Order = 1
                },
                new EditableFieldDefinition
                {
                    FieldName = "contactEmail",
                    Label = "Kontakt e-post",
                    FieldType = "text",
                    Section = "Kontakt",
                    IsRequired = true,
                    Order = 2
                },
                new EditableFieldDefinition
                {
                    FieldName = "contactPhone",
                    Label = "Kontakt telefon",
                    FieldType = "text",
                    Section = "Kontakt",
                    IsRequired = false,
                    Order = 3
                },

                // Configuration Section
                new EditableFieldDefinition
                {
                    FieldName = "numberOfSeriesOrStations",
                    Label = "Antal serier/stationer",
                    FieldType = "number",
                    Section = "Konfiguration",
                    IsRequired = true,
                    Order = 1
                },
                new EditableFieldDefinition
                {
                    FieldName = "showLiveResults",
                    Label = "Visa live-resultat",
                    FieldType = "boolean",
                    Section = "Konfiguration",
                    IsRequired = false,
                    Order = 2
                },
                new EditableFieldDefinition
                {
                    FieldName = "isActive",
                    Label = "Aktiv",
                    FieldType = "boolean",
                    Section = "Konfiguration",
                    IsRequired = false,
                    Order = 3
                }
            };
        }

        /// <summary>
        /// Validate a single field value.
        /// </summary>
        private string ValidateField(string fieldName, object value)
        {
            return fieldName switch
            {
                "competitionName" => ValidateString(value, "Tävlingsnamn", 1, 200),
                "venue" => ValidateString(value, "Plats", 1, 200),
                "competitionDirector" => ValidateString(value, "Tävlingsledare", 1, 200),
                "contactEmail" => ValidateEmail(value),
                "contactPhone" => ValidatePhone(value),
                "maxParticipants" => ValidatePositiveInt(value, "Max antal deltagare"),
                "registrationFee" => ValidateDecimal(value, "Anmälningsavgift"),
                "numberOfSeriesOrStations" => ValidatePositiveInt(value, "Antal serier"),
                "competitionDate" => ValidateDateTime(value, "Tävlingsdatum"),
                "competitionEndDate" => ValidateOptionalDateTime(value, "Slutdatum"),
                "registrationOpenDate" => ValidateDateTime(value, "Anmälan öppnar"),
                "registrationCloseDate" => ValidateDateTime(value, "Anmälan stänger"),
                _ => null // Unknown field, skip validation
            };
        }

        /// <summary>
        /// Map UI field names to Umbraco property aliases.
        /// </summary>
        private string MapFieldNameToAlias(string fieldName)
        {
            // Assuming field names match property aliases
            // Adjust this if your Umbraco aliases differ from field names
            return fieldName switch
            {
                "competitionName" => "competitionName",
                "description" => "description",
                "venue" => "venue",
                "competitionDate" => "competitionDate",
                "competitionEndDate" => "competitionEndDate",
                "registrationOpenDate" => "registrationOpenDate",
                "registrationCloseDate" => "registrationCloseDate",
                "maxParticipants" => "maxParticipants",
                "registrationFee" => "registrationFee",
                "competitionDirector" => "competitionDirector",
                "contactEmail" => "contactEmail",
                "contactPhone" => "contactPhone",
                "numberOfSeriesOrStations" => "numberOfSeriesOrStations",
                "showLiveResults" => "showLiveResults",
                "isActive" => "isActive",
                "isClubOnly" => "isClubOnly",
                "clubId" => "clubId",
                "competitionManagers" => "competitionManagers",
                "swishNumber" => "swishNumber",
                "addToMenu" => "addToMenu",
                "allowDualCClass" => "allowDualCClass",
                "numberOfFinalSeries" => "numberOfFinalSeries",
                "shootingClassIds" => "shootingClassIds",
                "competitionScope" => "competitionScope",
                "isAwardingStandardMedals" => "isAwardingStandardMedals",
                _ => null
            };
        }

        /// <summary>
        /// Convert field values to appropriate types for Umbraco properties.
        /// </summary>
        private object ConvertFieldValue(string fieldName, object value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return null;

            return fieldName switch
            {
                "maxParticipants" or "numberOfSeriesOrStations" or "numberOfFinalSeries" or "clubId" =>
                    int.TryParse(value.ToString(), out var intVal) && intVal >= 0 ? intVal : (object)null,

                "registrationFee" =>
                    decimal.TryParse(value.ToString(), out var decVal) && decVal >= 0 ? decVal : (object)null,

                "showLiveResults" or "isActive" or "isClubOnly" or "allowDualCClass" or "addToMenu" or "isAwardingStandardMedals" =>
                    bool.TryParse(value.ToString(), out var boolVal) ? boolVal : false,

                "competitionDate" or "competitionEndDate" or "registrationOpenDate" or "registrationCloseDate" =>
                    ConvertDateTime(value.ToString()),

                "shootingClassIds" =>
                    ConvertShootingClassIds(value.ToString()),

                "competitionManagers" =>
                    ConvertCompetitionManagers(value),

                _ => value.ToString()
            };
        }

        private object ConvertDateTime(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr))
                return null;

            // Handle datetime-local format (ISO 8601): "2025-01-20T14:30"
            if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dateVal))
            {
                // Validate the date is within SQL Server range
                if (dateVal >= new DateTime(1753, 1, 1) && dateVal <= new DateTime(9999, 12, 31))
                {
                    return dateVal;
                }
            }

            return null; // Invalid date
        }

        private object ConvertShootingClassIds(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            // If it's already a JSON array, return as-is
            if (value.TrimStart().StartsWith("["))
                return value;

            // Split CSV and convert to JSON array string
            var classIds = value.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            return System.Text.Json.JsonSerializer.Serialize(classIds);
        }

        private object ConvertCompetitionManagers(object value)
        {
            if (value == null)
                return "[]";

            // Handle array of integers (from frontend)
            if (value is int[] intArray)
            {
                return JsonConvert.SerializeObject(intArray);
            }

            // Handle JSON array string
            if (value is string strValue && !string.IsNullOrEmpty(strValue))
            {
                // If already valid JSON array, return as-is
                if (strValue.TrimStart().StartsWith("["))
                {
                    try
                    {
                        // Validate it's a valid JSON array of ints
                        JsonConvert.DeserializeObject<int[]>(strValue);
                        return strValue;
                    }
                    catch
                    {
                        return "[]";
                    }
                }

                // Try to parse CSV of IDs (migration scenario)
                var ids = strValue.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s) && int.TryParse(s, out _))
                    .Select(s => int.Parse(s))
                    .ToArray();

                return JsonConvert.SerializeObject(ids);
            }

            // Handle System.Text.Json.JsonElement (from model binding)
            if (value.GetType().Name == "JsonElement")
            {
                try
                {
                    var jsonStr = value.ToString();
                    // Validate and reserialize
                    var ids = JsonConvert.DeserializeObject<int[]>(jsonStr);
                    return JsonConvert.SerializeObject(ids ?? Array.Empty<int>());
                }
                catch
                {
                    return "[]";
                }
            }

            return "[]";
        }

        // Validation Helper Methods

        private string ValidateString(object value, string fieldLabel, int minLength, int maxLength)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return $"{fieldLabel} är obligatorisk";

            var str = value.ToString().Trim();
            if (str.Length < minLength)
                return $"{fieldLabel} måste innehålla minst {minLength} tecken";
            if (str.Length > maxLength)
                return $"{fieldLabel} kan inte överskrida {maxLength} tecken";

            return null;
        }

        private string ValidateEmail(object value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return "E-post är obligatorisk";

            var email = value.ToString().Trim();
            var emailPattern = @"^[^\s@]+@[^\s@]+\.[^\s@]+$";
            
            if (!Regex.IsMatch(email, emailPattern))
                return "Ogiltig e-postformat";

            return null;
        }

        private string ValidatePhone(object value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return null; // Phone is optional

            var phone = value.ToString().Trim();
            if (phone.Length < 5)
                return "Telefonnummer måste innehålla minst 5 tecken";
            if (phone.Length > 20)
                return "Telefonnummer kan inte överskrida 20 tecken";

            return null;
        }

        private string ValidatePositiveInt(object value, string fieldLabel)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return $"{fieldLabel} är obligatorisk";

            if (!int.TryParse(value.ToString(), out var intVal))
                return $"{fieldLabel} måste vara ett helt nummer";

            if (intVal <= 0)
                return $"{fieldLabel} måste vara större än 0";

            return null;
        }

        private string ValidateDecimal(object value, string fieldLabel)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return null; // Decimal fields are optional

            if (!decimal.TryParse(value.ToString(), out var decVal))
                return $"{fieldLabel} måste vara ett giltigt nummer";

            if (decVal < 0)
                return $"{fieldLabel} kan inte vara negativt";

            return null;
        }

        private string ValidateDateTime(object value, string fieldLabel)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return $"{fieldLabel} är obligatorisk";

            if (!DateTime.TryParse(value.ToString(), out var dateVal))
                return $"{fieldLabel} måste vara ett giltigt datum och tid";

            return null;
        }

        private string ValidateOptionalDateTime(object value, string fieldLabel)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return null; // Optional field

            if (!DateTime.TryParse(value.ToString(), out var dateVal))
                return $"{fieldLabel} måste vara ett giltigt datum";

            return null;
        }
    }
}
