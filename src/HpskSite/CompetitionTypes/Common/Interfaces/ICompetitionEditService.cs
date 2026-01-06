using System.Collections.Generic;
using System.Threading.Tasks;
using Umbraco.Cms.Core.Models;

namespace HpskSite.CompetitionTypes.Common.Interfaces
{
    /// <summary>
    /// Interface for competition type-specific edit/save operations.
    /// Each competition type should implement this to handle saving to Umbraco.
    /// </summary>
    public interface ICompetitionEditService
    {
        /// <summary>
        /// Save competition data to Umbraco content.
        /// Validates fields according to type-specific rules before saving.
        /// </summary>
        /// <param name="competitionId">The competition content node ID</param>
        /// <param name="fields">Dictionary of field names and values to save</param>
        /// <returns>Result object with success status and any validation errors</returns>
        Task<CompetitionEditResult> SaveCompetitionAsync(int competitionId, Dictionary<string, object> fields);

        /// <summary>
        /// Validate competition fields before saving.
        /// Type-specific validation logic should be implemented here.
        /// </summary>
        /// <param name="fields">Fields to validate</param>
        /// <returns>Validation result with any errors found</returns>
        ValidationResult ValidateFields(Dictionary<string, object> fields);

        /// <summary>
        /// Get all editable fields for this competition type.
        /// Used by the UI to determine which fields can be edited.
        /// </summary>
        /// <returns>List of editable field definitions</returns>
        List<EditableFieldDefinition> GetEditableFields();
    }

    /// <summary>
    /// Result of a competition save operation.
    /// </summary>
    public class CompetitionEditResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> Errors { get; set; } = new Dictionary<string, string>();
        public object Data { get; set; }

        public static CompetitionEditResult SuccessResult(string message = "Competition saved successfully", object data = null)
        {
            return new CompetitionEditResult
            {
                Success = true,
                Message = message,
                Data = data
            };
        }

        public static CompetitionEditResult ErrorResult(string message, Dictionary<string, string> errors = null)
        {
            return new CompetitionEditResult
            {
                Success = false,
                Message = message,
                Errors = errors ?? new Dictionary<string, string>()
            };
        }
    }

    /// <summary>
    /// Result of field validation.
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public Dictionary<string, string> Errors { get; set; } = new Dictionary<string, string>();

        public static ValidationResult Valid()
        {
            return new ValidationResult { IsValid = true };
        }

        public static ValidationResult Invalid(Dictionary<string, string> errors)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = errors ?? new Dictionary<string, string>()
            };
        }

        public static ValidationResult Invalid(string fieldName, string error)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = new Dictionary<string, string> { { fieldName, error } }
            };
        }
    }

    /// <summary>
    /// Definition of an editable field for a competition type.
    /// Used by the UI to render the edit form.
    /// </summary>
    public class EditableFieldDefinition
    {
        /// <summary>
        /// The Umbraco property alias (e.g., "competitionName", "competitionDate")
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// Display label for the field in the UI
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Field type for rendering: "text", "textarea", "number", "date", "datetime", "select", etc.
        /// </summary>
        public string FieldType { get; set; }

        /// <summary>
        /// Help text to display below the field
        /// </summary>
        public string HelpText { get; set; }

        /// <summary>
        /// Section grouping for organization in the form
        /// </summary>
        public string Section { get; set; }

        /// <summary>
        /// Whether this field is required
        /// </summary>
        public bool IsRequired { get; set; }

        /// <summary>
        /// For select fields: list of available options
        /// </summary>
        public List<SelectOption> Options { get; set; } = new List<SelectOption>();

        /// <summary>
        /// Display order within the section
        /// </summary>
        public int Order { get; set; }
    }

    /// <summary>
    /// Option for select/dropdown fields
    /// </summary>
    public class SelectOption
    {
        public string Value { get; set; }
        public string Label { get; set; }
    }
}
