using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Umbraco.Cms.Core.Services;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Interfaces;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<PrecisionCompetitionEditService>? _logger;

        public PrecisionCompetitionEditService(IContentService contentService,
                                               ILogger<PrecisionCompetitionEditService>? logger = null)
        {
            _contentService = contentService;
            _logger = logger;
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

                // Sync Umbraco node name with competitionName property
                var updatedName = content.GetValue<string>("competitionName");
                if (!string.IsNullOrEmpty(updatedName))
                {
                    content.Name = updatedName;
                }

                // Save first, then publish as system user (-1) to ensure publish succeeds
                _contentService.Save(content);
                var publishResult = _contentService.Publish(content, new[] { "*" }, -1);

                if (!publishResult.Success)
                {
                    return CompetitionEditResult.SuccessResult(
                        "Competition saved (publish pending)",
                        new { competitionId = content.Id }
                    );
                }

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
                },
                new EditableFieldDefinition
                {
                    FieldName = "allowSelfReporting",
                    Label = "Tillåt resultatrapportering",
                    FieldType = "boolean",
                    Section = "Konfiguration",
                    IsRequired = false,
                    HelpText = "Klubbadmins och skjutledare kan rapportera resultat för sina skyttar",
                    Order = 4
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
                "juniorRegistrationFee" => ValidateDecimal(value, "Junioranmälningsavgift"),
                "subCompetitionFee" => ValidateDecimal(value, "Anmälningsavgift för Deltävling"),
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
        /// <summary>
        /// Fält som får sparas men INTE står i <see cref="CompetitionFieldCatalog"/> — de är
        /// inte redigerbara formulärfält utan sätts av kod (systemflaggor, härledda värden,
        /// och sammansatta konfigurationer som serialiseras till JSON).
        /// </summary>
        private static readonly HashSet<string> ExtraSparbaraFalt = new(StringComparer.OrdinalIgnoreCase)
        {
            "isActive",
            "competitionManagers",        // klienten skickar id-ARRAYEN under det här namnet
            "patrolSize",
            "patrolIntervalMinutes",
            "direktplaceringConfig"
        };

        /// <summary>
        /// Katalogfält som sätts på ANNAT håll och därför inte får skrivas här — annars
        /// finns två skribenter till samma egenskap, med var sin konvertering.
        /// </summary>
        private static readonly HashSet<string> HanterasAnnorstades = new(StringComparer.OrdinalIgnoreCase)
        {
            "rangeId",                    // CompetitionEditController läser och sätter den själv
            "seriesId",                   // serien flyttas med Move EFTER sparningen
            "competitionManagerIds"       // klienten omvandlar den till competitionManagers
        };

        /// <summary>
        /// Vilka fält får skrivas, och till vilken egenskapsalias.
        ///
        /// ⚠️ HÄRLEDD UR KATALOGEN, inte handskriven. Det här var en FJÄRDE lista över samma
        /// fältnamn — där varje rad mappade ett namn till sig självt — och `_ => null`
        /// släppte tyst allt den inte kände igen. Glömdes ett nytt fält här sparades det
        /// aldrig, utan felmeddelande. Det är det värsta stället att tappa något på:
        /// användaren SER att fältet är ifyllt, och nästa öppning visar det tomt igen.
        ///
        /// Namn och alias är identiska i hela registret; skulle de någon gång skilja sig är
        /// det katalogen som ska bära avvikelsen, inte en switch här.
        /// </summary>
        private string? MapFieldNameToAlias(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName)) return null;
            if (HanterasAnnorstades.Contains(fieldName)) return null;

            if (CompetitionFieldCatalog.Find(fieldName) != null) return fieldName;
            if (ExtraSparbaraFalt.Contains(fieldName)) return fieldName;

            // ⚠️ SÄG IFRÅN. Ett okänt fält är antingen en klient som skickar skräp eller ett
            // nytt fält någon glömt lägga i katalogen — och det andra fallet är en tyst
            // dataförlust som annars bara upptäcks av den som undrar vart värdet tog vägen.
            _logger?.LogWarning(
                "SaveCompetition: fältet {Field} känns inte igen och SPARAS INTE. " +
                "Lägg det i CompetitionFieldCatalog om det är ett redigerbart fält.",
                fieldName);
            return null;
        }

        /// <summary>
        /// Convert field values to appropriate types for Umbraco properties.
        /// </summary>
        /// <summary>
        /// Fält vars värde är ett BELOPP och ska lagras som decimal. Allt annat numeriskt
        /// är ett antal och lagras som int.
        ///
        /// ⚠️ Den här listan är den enda som INTE går att härleda ur katalogen: där heter
        /// både antal och belopp <c>FieldControl.Number</c>, eftersom katalogen beskriver
        /// formuläret och inte lagringen. Att lägga in lagringstyp där hade gett katalogen
        /// ett andra ansvar för att spara fem rader här.
        /// </summary>
        private static readonly HashSet<string> BeloppsFalt = new(StringComparer.OrdinalIgnoreCase)
        {
            "registrationFee", "teamRegistrationFee", "stafettRegistrationFee",
            "juniorRegistrationFee", "subCompetitionFee"
        };

        /// <summary>
        /// Katalogfält som lagras som HELTAL trots att kontrollen inte är
        /// <see cref="FieldControl.Number"/>.
        ///
        /// ⚠️ <c>clubId</c> är en dropdown i formuläret men ett heltal i lagringen. Utan den
        /// här listan skulle den härledas till text — och en klubbreferens lagrad som sträng
        /// är en dokumenterad fälla här: <c>GetValue&lt;int&gt;</c> på en strängegenskap ger
        /// TYST 0, vilket har gett både walk-in-anmälningar med <c>clubId=0</c> och
        /// "Okänd klubb" på startlistan.
        /// </summary>
        private static readonly HashSet<string> HeltalsFalt = new(StringComparer.OrdinalIgnoreCase)
        {
            "clubId"
        };

        /// <summary>
        /// Fält med egen serialisering — sammansatta värden som inte är en enkel typ.
        /// </summary>
        private static readonly HashSet<string> EgenKonvertering = new(StringComparer.OrdinalIgnoreCase)
        {
            "shootingClassIds", "competitionManagers"
        };

        /// <summary>
        /// Konverterar ett inkommande fältvärde till den typ egenskapen lagras som.
        ///
        /// ⚠️ TYPEN HÄRLEDS UR <see cref="CompetitionFieldCatalog"/> där det går. Det här var
        /// en femte handskriven fältlista, och tre av dess grupper (bool, datum, tid) sa
        /// exakt samma sak som <see cref="FieldControl"/> redan gör. Två beskrivningar av
        /// samma sak glider isär; det är hela skälet att katalogen finns.
        ///
        /// Fallbacken är strängen — en ny textruta fungerar därför av sig själv. Men ett
        /// NUMERISKT fält som varken är belopp eller känt av katalogen skulle tyst lagras
        /// som text, och det säger vi ifrån om i stället.
        /// </summary>
        private object ConvertFieldValue(string fieldName, object value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return null;

            var text = value.ToString();

            if (EgenKonvertering.Contains(fieldName))
            {
                return fieldName.Equals("shootingClassIds", StringComparison.OrdinalIgnoreCase)
                    ? ConvertShootingClassIds(text)
                    : ConvertCompetitionManagers(value);
            }

            if (BeloppsFalt.Contains(fieldName))
                return decimal.TryParse(text, out var dec) && dec >= 0 ? dec : (object?)null;

            if (HeltalsFalt.Contains(fieldName))
                return int.TryParse(text, out var hi) && hi >= 0 ? hi : (object?)null;

            var falt = CompetitionFieldCatalog.Find(fieldName);
            if (falt != null)
            {
                switch (falt.Control)
                {
                    case FieldControl.Checkbox:
                        return bool.TryParse(text, out var b) && b;
                    case FieldControl.Date:
                    case FieldControl.DateTime:
                        return ConvertDateTime(text);
                    case FieldControl.Number:
                        return int.TryParse(text, out var i) && i >= 0 ? i : (object?)null;
                }
                return text;
            }

            // Fält utanför katalogen: systemflaggor och härledda värden. De få som finns
            // är kända, resten är text.
            if (fieldName.Equals("isActive", StringComparison.OrdinalIgnoreCase))
                return bool.TryParse(text, out var ab) && ab;
            if (fieldName.Equals("patrolSize", StringComparison.OrdinalIgnoreCase)
                || fieldName.Equals("patrolIntervalMinutes", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(text, out var ai) && ai >= 0 ? ai : (object?)null;

            return text;
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

        /// <summary>
        /// Den här vägen var KORREKT hela tiden — den gör <c>value.ToString()</c> innan den
        /// konverterar, så CSV:n nådde fram. Den delegerar ändå, så att skapa- och redigera-vägen
        /// inte kan glida isär om formen ändras. Se <see cref="HpskSite.Models.ShootingClassIdsValue"/>.
        /// </summary>
        private object ConvertShootingClassIds(string value)
        {
            return HpskSite.Models.ShootingClassIdsValue.FromText(value);
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

            // Allow non-email values like "Ingen" for contact fields
            if (!email.Contains('@'))
                return null;

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
