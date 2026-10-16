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
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly ILogger? _logger;

        /// <summary>
        /// Nycklar som sköts på annat håll och inte skrivs här: serien flyttas med Move efter sparningen,
        /// klienten omvandlar <c>competitionManagerIds</c> till <c>competitionManagers</c>, och grenen byts bara
        /// genom grenbytet (<c>ChangeCompetitionType</c>), som prövar att tävlingen är orörd.
        /// </summary>
        private static readonly HashSet<string> HandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
        {
            "seriesId", "competitionManagerIds", "competitionType"
        };

        public PrecisionCompetitionEditService(HpskSite.Services.Competitions.ICompetitionStore competitions, ILogger? logger = null)
        {
            _competitions = competitions;
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

                // Fältpåsen genom den gemensamma regeln (C3-planen S5b), och nodens namn följer tävlingens.
                // ⚠️ Lagringen publicerar bara en tävling som redan var publicerad — redigeringen publicerade
                // förut alltid, även ett utkast.
                List<string> ignored = new();
                var result = _competitions.Update(competitionId, null, c =>
                {
                    ignored = HpskSite.Services.Competitions.CompetitionFieldBag.Apply(c, fields, HandledElsewhere).Ignored;
                    if (!string.IsNullOrWhiteSpace(c.CompetitionName)) c.Name = c.CompetitionName!;
                });
                if (result.Outcome == HpskSite.Services.Competitions.CompetitionUpdateOutcome.NotFound)
                    return CompetitionEditResult.ErrorResult("Competition not found");

                // ⚠️ SÄG IFRÅN. Ett okänt fält är antingen en klient som skickar skräp eller ett nytt fält
                // någon glömt — och det andra är en tyst dataförlust.
                foreach (var key in ignored)
                    _logger?.LogWarning("SaveCompetition: fältet {Field} känns inte igen och SPARAS INTE.", key);

                if (result.PublishProblem != null)
                {
                    return CompetitionEditResult.SuccessResult(
                        "Competition saved (publish pending)",
                        new { competitionId }
                    );
                }

                return CompetitionEditResult.SuccessResult(
                    "Competition updated successfully",
                    new { competitionId }
                );
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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
