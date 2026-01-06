using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Models
{
    public class RegistrationInvoice : BasePage
    {
        public RegistrationInvoice(IPublishedContent content, IPublishedValueFallback publishedValueFallback) 
            : base(content, publishedValueFallback)
        {
        }

        /// <summary>
        /// JSON array of registration IDs this invoice covers (DEPRECATED - use RegistrationId)
        /// </summary>
        [Obsolete("Use RegistrationId property instead. This property is kept for backward compatibility.")]
        public string RelatedRegistrationIds => this.Value<string>("relatedRegistrationIds") ?? "";

        /// <summary>
        /// Single registration ID this invoice covers
        /// </summary>
        public int RegistrationId => this.Value<int>("registrationId");

        /// <summary>
        /// How the payment was made (Swish, Bank Transfer, Cash, etc.)
        /// </summary>
        public string PaymentMethod => this.Value<string>("paymentMethod") ?? "";

        /// <summary>
        /// Current payment status (Pending, Paid, Failed, Refunded, Cancelled)
        /// </summary>
        public string PaymentStatus => this.Value<string>("paymentStatus") ?? "Pending";

        /// <summary>
        /// When the payment was completed
        /// </summary>
        public DateTime? PaymentDate => this.Value<DateTime?>("paymentDate");

        /// <summary>
        /// External payment system reference (Swish reference, bank transaction ID, etc.)
        /// </summary>
        public string TransactionId => this.Value<string>("transactionId") ?? "";

        /// <summary>
        /// Additional notes about the payment
        /// </summary>
        public string Notes => this.Value<string>("notes") ?? "";

        /// <summary>
        /// Total amount for this invoice
        /// </summary>
        public decimal TotalAmount => this.Value<decimal>("totalAmount");

        /// <summary>
        /// Competition ID this invoice is for
        /// </summary>
        public int CompetitionId => this.Value<int>("competitionId");

        /// <summary>
        /// Member ID who this invoice belongs to
        /// </summary>
        public string MemberId => this.Value<string>("memberId") ?? "";

        /// <summary>
        /// Member name for display purposes
        /// </summary>
        public string MemberName => this.Value<string>("memberName") ?? "";

        /// <summary>
        /// When this invoice was created
        /// </summary>
        public DateTime CreatedDate => this.Value<DateTime>("createdDate");

        /// <summary>
        /// Whether this invoice is active
        /// </summary>
        public bool IsActive => this.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true);

        /// <summary>
        /// Unique invoice number for this invoice
        /// </summary>
        public string InvoiceNumber => this.Value<string>("invoiceNumber") ?? "";

        // Helper properties
        public bool IsPaid => PaymentStatus == "Paid";
        public bool IsPending => PaymentStatus == "Pending";
        public bool IsFailed => PaymentStatus == "Failed";
        public bool IsRefunded => PaymentStatus == "Refunded";
        public bool IsCancelled => PaymentStatus == "Cancelled";

        /// <summary>
        /// Get the list of registration IDs as integers (DEPRECATED - use RegistrationId)
        /// </summary>
        [Obsolete("Use RegistrationId property instead. This method is kept for backward compatibility.")]
        public List<int> GetRegistrationIds()
        {
            if (string.IsNullOrEmpty(RelatedRegistrationIds))
                return new List<int>();

            try
            {
                // Handle JSON array format: [123, 124, 125]
                var jsonArray = RelatedRegistrationIds.Trim('[', ']');
                if (string.IsNullOrEmpty(jsonArray))
                    return new List<int>();

                return jsonArray.Split(',')
                    .Select(id => id.Trim())
                    .Where(id => int.TryParse(id, out _))
                    .Select(int.Parse)
                    .ToList();
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// Get payment status display text
        /// </summary>
        public string GetPaymentStatusDisplay()
        {
            return PaymentStatus switch
            {
                "Pending" => "Väntar på betalning",
                "Paid" => "Betald",
                "Failed" => "Betalning misslyckades",
                "Refunded" => "Återbetalad",
                "Cancelled" => "Makulerad",
                _ => "Okänd status"
            };
        }

        /// <summary>
        /// Get payment status color class for UI
        /// </summary>
        public string GetPaymentStatusColorClass()
        {
            return PaymentStatus switch
            {
                "Pending" => "warning",
                "Paid" => "success",
                "Failed" => "danger",
                "Refunded" => "info",
                "Cancelled" => "secondary",
                _ => "secondary"
            };
        }

        /// <summary>
        /// Get payment method display text
        /// </summary>
        public string GetPaymentMethodDisplay()
        {
            return PaymentMethod switch
            {
                "Swish" => "Swish",
                "Bank Transfer" => "Banköverföring",
                "Cash" => "Kontant",
                "Other" => "Annat",
                _ => PaymentMethod
            };
        }
    }
}
