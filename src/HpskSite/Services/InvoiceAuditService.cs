using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Single writer + reader for the InvoicePaymentEvents table. Every payment-related
    /// state transition on a registrationInvoice should produce one row here, so the
    /// per-invoice history modal and the Bokföringsunderlag both have a reliable trail
    /// independent of the invoice's overwrite-able notes field.
    ///
    /// Audit logging is a side effect of business actions — it must never propagate
    /// failures up. All Log* methods swallow exceptions and log them to the application
    /// logger so the underlying action (paying an invoice, sending an email) still
    /// completes for the user.
    /// </summary>
    public class InvoiceAuditService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<InvoiceAuditService> _logger;

        public InvoiceAuditService(IUmbracoDatabaseFactory databaseFactory, ILogger<InvoiceAuditService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Insert one audit event. Returns the new row's Id, or 0 on failure.
        /// </summary>
        public async Task<int> LogAsync(
            int invoiceId,
            int competitionId,
            string eventType,
            int? byMemberId = null,
            string? byMemberName = null,
            string? paymentMethod = null,
            decimal? amount = null,
            string? reference = null,
            string? notes = null)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var row = new InvoicePaymentEvent
                {
                    InvoiceId = invoiceId,
                    CompetitionId = competitionId,
                    EventType = eventType ?? InvoicePaymentEventTypes.StatusChanged,
                    OccurredAt = DateTime.UtcNow,
                    ByMemberId = byMemberId,
                    ByMemberName = byMemberName,
                    PaymentMethod = paymentMethod,
                    Amount = amount,
                    Reference = reference,
                    Notes = notes
                };
                var id = await db.InsertAsync(row);
                return id is int i ? i : (int)Convert.ToInt64(id ?? 0);
            }
            catch (Exception ex)
            {
                // Never fail the calling action because of an audit insert.
                _logger.LogError(ex, "Failed to log invoice payment event for invoice {InvoiceId} ({EventType})",
                    invoiceId, eventType);
                return 0;
            }
        }

        /// <summary>
        /// Get the history (newest first) for one invoice.
        /// </summary>
        public async Task<List<InvoicePaymentEvent>> GetForInvoiceAsync(int invoiceId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return await db.FetchAsync<InvoicePaymentEvent>(
                    "WHERE InvoiceId = @0 ORDER BY OccurredAt DESC, Id DESC", invoiceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch invoice payment events for invoice {InvoiceId}", invoiceId);
                return new List<InvoicePaymentEvent>();
            }
        }

        /// <summary>
        /// Get every event for a competition, newest first. Used by Bokföringsunderlag
        /// (Phase 4b) and to count "how many reminders did we send for this comp".
        /// </summary>
        public async Task<List<InvoicePaymentEvent>> GetForCompetitionAsync(int competitionId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return await db.FetchAsync<InvoicePaymentEvent>(
                    "WHERE CompetitionId = @0 ORDER BY OccurredAt DESC, Id DESC", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch invoice payment events for competition {CompetitionId}", competitionId);
                return new List<InvoicePaymentEvent>();
            }
        }
    }
}
