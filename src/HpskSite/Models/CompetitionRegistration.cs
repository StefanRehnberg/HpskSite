using System.Text.Json;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Models
{
    /// <summary>
    /// Represents a shooting class entry with start preference
    /// </summary>
    public class ShootingClassEntry
    {
        public string Class { get; set; } = "";
        public string StartPreference { get; set; } = "No Preference";
    }

    public class CompetitionRegistrationDocument : BasePage
    {
        public CompetitionRegistrationDocument(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        /// <summary>
        /// Competition ID this registration is for
        /// </summary>
        public int CompetitionId => this.Value<int>("competitionId");

        /// <summary>
        /// Member ID who registered (string for Umbraco member IDs)
        /// </summary>
        public string MemberId => this.Value<string>("memberId") ?? "";

        /// <summary>
        /// Member name for display purposes
        /// </summary>
        public string MemberName => this.Value<string>("memberName") ?? "";

        /// <summary>
        /// Member email for contact
        /// </summary>
        public string MemberEmail => this.Value<string>("memberEmail") ?? "";

        /// <summary>
        /// Member's club ID or name
        /// </summary>
        public string MemberClub => this.Value<string>("memberClub") ?? "";

        /// <summary>
        /// Shooting classes for this registration (JSON array)
        /// </summary>
        public List<ShootingClassEntry> ShootingClasses
        {
            get
            {
                var json = this.Value<string>("shootingClasses");
                return DeserializeShootingClasses(json);
            }
        }

        /// <summary>
        /// Shooting class for this registration (DEPRECATED - use ShootingClasses)
        /// </summary>
        [Obsolete("Use ShootingClasses property instead. This property is kept for backward compatibility.")]
        public string ShootingClass => this.Value<string>("shootingClass") ?? "";

        /// <summary>
        /// Start preference (Early, Late, No preference) (DEPRECATED - use ShootingClasses)
        /// </summary>
        [Obsolete("Use ShootingClasses property instead. This property is kept for backward compatibility.")]
        public string StartPreference => this.Value<string>("startPreference") ?? "Inget";

        /// <summary>
        /// When the registration was made
        /// </summary>
        public DateTime RegistrationDate => this.Value<DateTime>("registrationDate");

        /// <summary>
        /// Who performed the registration (for admin registrations)
        /// </summary>
        public string RegisteredBy => this.Value<string>("registeredBy") ?? "";

        /// <summary>
        /// Whether this registration is active
        /// </summary>
        public bool IsActive => this.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true);

        /// <summary>
        /// ID of the payment invoice for this registration
        /// </summary>
        public int? InvoiceId => this.Value<int?>("invoiceId");

        /// <summary>
        /// Additional notes for this registration
        /// </summary>
        public string Notes => this.Value<string>("notes") ?? "";

        // Payment-related helper properties
        public bool HasInvoice => InvoiceId.HasValue;

        /// <summary>
        /// Get payment status from linked invoice
        /// Note: This is a placeholder implementation. 
        /// In practice, you would inject a service to look up the invoice.
        /// </summary>
        public string GetPaymentStatus()
        {
            if (!InvoiceId.HasValue) return "No Invoice";
            
            // This would need to be implemented with proper service injection
            // For now, return a placeholder
            return "Unknown";
        }

        /// <summary>
        /// Check if this registration is paid
        /// </summary>
        public bool IsPaid()
        {
            var status = GetPaymentStatus();
            return status == "Paid";
        }

        /// <summary>
        /// Get payment status display text
        /// </summary>
        public string GetPaymentStatusDisplay()
        {
            return GetPaymentStatus() switch
            {
                "No Invoice" => "Ingen faktura",
                "Pending" => "Väntar på betalning",
                "Paid" => "Betald",
                "Failed" => "Betalning misslyckades",
                "Refunded" => "Återbetalad",
                "Cancelled" => "Makulerad",
                "Unknown" => "Okänd status",
                _ => "Okänd status"
            };
        }

        /// <summary>
        /// Get payment status color class for UI
        /// </summary>
        public string GetPaymentStatusColorClass()
        {
            return GetPaymentStatus() switch
            {
                "No Invoice" => "secondary",
                "Pending" => "warning",
                "Paid" => "success",
                "Failed" => "danger",
                "Refunded" => "info",
                "Cancelled" => "secondary",
                "Unknown" => "secondary",
                _ => "secondary"
            };
        }

        /// <summary>
        /// Get start preference display text
        /// </summary>
        public string GetStartPreferenceDisplay()
        {
            return StartPreference switch
            {
                "Early" => "Tidigt",
                "Late" => "Sent",
                "Inget" => "Ingen preferens",
                _ => StartPreference
            };
        }

        /// <summary>
        /// Check if this is a valid registration
        /// </summary>
        public bool IsValidRegistration()
        {
            return CompetitionId > 0 &&
                   !string.IsNullOrWhiteSpace(MemberId) &&
                   (ShootingClasses.Any() || !string.IsNullOrWhiteSpace(ShootingClass)) &&
                   RegistrationDate != default;
        }

        // Static Helper Methods for JSON Serialization/Deserialization

        /// <summary>
        /// Serialize shooting classes to JSON for storage
        /// </summary>
        public static string SerializeShootingClasses(List<ShootingClassEntry> classes)
        {
            if (classes == null || !classes.Any())
                return "[]";

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            return JsonSerializer.Serialize(classes, options);
        }

        /// <summary>
        /// Deserialize shooting classes from JSON
        /// </summary>
        public static List<ShootingClassEntry> DeserializeShootingClasses(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<ShootingClassEntry>();

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var classes = JsonSerializer.Deserialize<List<ShootingClassEntry>>(json, options);
                return classes ?? new List<ShootingClassEntry>();
            }
            catch
            {
                return new List<ShootingClassEntry>();
            }
        }

        /// <summary>
        /// Get all class codes as a flat list
        /// </summary>
        public static List<string> GetClassCodes(List<ShootingClassEntry> classes)
        {
            return classes?.Select(sc => sc.Class).ToList() ?? new List<string>();
        }

        /// <summary>
        /// Get class codes as comma-separated string
        /// </summary>
        public string GetClassCodesDisplay()
        {
            return string.Join(", ", ShootingClasses.Select(sc => sc.Class));
        }
    }
}
