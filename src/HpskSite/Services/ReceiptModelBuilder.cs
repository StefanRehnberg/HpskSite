using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Builds a <see cref="ReceiptModel"/> for a paid registrationInvoice. Single source
    /// of truth for receipt content so the printable Kvitto page and (the textual parts of)
    /// the Betalningsbekräftelse email stay consistent.
    ///
    /// Issuer resolution mirrors PaymentService: the hosting club (competition.clubId),
    /// or the region (competition.regionalFederation → regionCode) for region-hosted comps.
    /// Every lookup degrades to "" / empty so a missing field renders blank rather than
    /// throwing.
    /// </summary>
    public class ReceiptModelBuilder
    {
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IPublishedUrlProvider _publishedUrlProvider;

        public ReceiptModelBuilder(
            IContentService contentService,
            IMemberService memberService,
            IUmbracoContextAccessor umbracoContextAccessor,
            IPublishedUrlProvider publishedUrlProvider)
        {
            _contentService = contentService;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _publishedUrlProvider = publishedUrlProvider;
        }

        /// <summary>
        /// Build the receipt model for an invoice id. Returns null when the invoice or its
        /// competition can't be resolved. Sets <see cref="ReceiptModel.IsPaid"/> from the
        /// aggregate of Paid invoices for the registration.
        /// </summary>
        public ReceiptModel? Build(int invoiceId)
        {
            var invoice = _contentService.GetById(invoiceId);
            if (invoice == null || invoice.ContentType.Alias != "registrationInvoice") return null;

            var competitionId = invoice.GetValue<int>("competitionId");
            var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
            if (competition == null) return null;

            var memberIdStr = invoice.GetValue<string>("memberId") ?? "";
            int.TryParse(memberIdStr, out var memberId);
            var member = memberId > 0 ? _memberService.GetById(memberId) : null;

            var registrationId = invoice.GetValue<int>("registrationId");

            // --- Money: sum every Paid invoice for the registration so top-ups count. ---
            decimal totalPaid = 0m;
            DateTime? latestPaidDate = null;
            var anyPaid = false;
            foreach (var inv in GetInvoicesForRegistration(competition, registrationId, invoice))
            {
                var status = (inv.GetValue<string>("paymentStatus") ?? "").Trim();
                if (status != "Paid") continue;
                anyPaid = true;
                var billed = inv.GetValue<decimal>("totalAmount");
                var actual = inv.GetValue<decimal?>("actualPaidAmount") ?? billed;
                totalPaid += actual;
                var paidDate = inv.GetValue<DateTime?>("paymentDate");
                if (paidDate.HasValue && (latestPaidDate == null || paidDate.Value > latestPaidDate.Value))
                    latestPaidDate = paidDate.Value;
            }

            var paidAt = latestPaidDate
                ?? invoice.GetValue<DateTime?>("paymentDate")
                ?? invoice.UpdateDate;

            var paymentMethod = invoice.GetValue<string>("paymentMethod") ?? "";
            var transactionId = invoice.GetValue<string>("transactionId") ?? "";
            var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();

            // --- Classes from the linked registration. ---
            var classes = "";
            if (registrationId > 0)
            {
                var reg = _contentService.GetById(registrationId);
                if (reg != null)
                {
                    var json = reg.GetValue<string>("shootingClasses") ?? "";
                    var entries = CompetitionRegistrationDocument.DeserializeShootingClasses(json);
                    classes = string.Join(", ", entries.Select(e => e.Class).Where(c => !string.IsNullOrEmpty(c)));
                }
            }

            // --- Issuer (club or region). ---
            var issuerClubId = competition.GetValue<int>("clubId");
            IContent? issuerNode = null;
            if (issuerClubId > 0)
            {
                var clubNode = _contentService.GetById(issuerClubId);
                if (clubNode?.ContentType.Alias == "club") issuerNode = clubNode;
            }
            else
            {
                var regionCode = competition.GetValue<string>("regionalFederation") ?? "";
                if (!string.IsNullOrWhiteSpace(regionCode)) issuerNode = FindRegionByCode(regionCode);
            }

            var issuerName = issuerNode == null
                ? ""
                : (issuerNode.GetValue<string>("clubName")
                   ?? issuerNode.GetValue<string>("regionName")
                   ?? issuerNode.Name ?? "");

            return new ReceiptModel
            {
                Found = true,
                InvoiceId = invoice.Id,
                RegistrationId = registrationId,
                CompetitionId = competitionId,
                MemberId = memberId,

                MemberName = invoice.GetValue<string>("memberName") ?? member?.Name ?? "",
                MemberEmail = member?.Email ?? "",

                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                CompetitionDate = competition.GetValue<DateTime?>("competitionDate"),
                ShootingClasses = classes,

                IssuerName = issuerName,
                IssuerOrgNumber = issuerNode?.GetValue<string>("orgNumber") ?? "",
                IssuerStreet = issuerNode?.GetValue<string>("address") ?? "",
                IssuerPostalCode = issuerNode?.GetValue<string>("postalCode") ?? "",
                IssuerCity = issuerNode?.GetValue<string>("city") ?? "",
                IssuerContactEmail = ResolveReceiptEmail(issuerNode),
                IssuerLogoUrl = issuerNode != null ? ResolveLogoUrl(issuerNode.Id) : "",

                AmountPaid = totalPaid,
                PaymentMethod = paymentMethod,
                Reference = !string.IsNullOrWhiteSpace(transactionId) ? transactionId : invoiceNumber,
                ReceiptNumber = invoiceNumber,
                PaidAt = paidAt,
                IsPaid = anyPaid
            };
        }

        /// <summary>
        /// Every registrationInvoice linked to the registration (by single-int registrationId
        /// or legacy relatedRegistrationIds JSON), scoped to the competition's invoices hub.
        /// Falls back to just the passed invoice if the hub can't be found.
        /// </summary>
        private List<IContent> GetInvoicesForRegistration(IContent competition, int registrationId, IContent fallback)
        {
            if (registrationId <= 0) return new List<IContent> { fallback };

            var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (hub == null) return new List<IContent> { fallback };

            var list = _contentService.GetPagedChildren(hub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "registrationInvoice")
                .Where(c =>
                {
                    if (c.GetValue<int>("registrationId") == registrationId) return true;
                    var related = c.GetValue<string>("relatedRegistrationIds") ?? "";
                    return related.Contains(registrationId.ToString());
                })
                .ToList();

            return list.Count > 0 ? list : new List<IContent> { fallback };
        }

        /// <summary>
        /// Email to print on the Kvitto: the dedicated <c>receiptEmail</c> when the issuer
        /// has set one, otherwise the general <c>contactEmail</c>. Returns "" when neither
        /// is set (the row is then omitted from the receipt).
        /// </summary>
        private static string ResolveReceiptEmail(IContent? issuerNode)
        {
            if (issuerNode == null) return "";
            var receiptEmail = (issuerNode.GetValue<string>("receiptEmail") ?? "").Trim();
            if (!string.IsNullOrEmpty(receiptEmail)) return receiptEmail;
            return (issuerNode.GetValue<string>("contactEmail") ?? "").Trim();
        }

        private IContent? FindRegionByCode(string regionCode)
        {
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) return null;
            var children = _contentService.GetPagedChildren(root.Id, 0, int.MaxValue, out _);
            return children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage" &&
                (c.GetValue<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolve the issuer's logo media URL (absolute) via the published cache. Returns
        /// "" when no logo is set or the cache is unavailable; the view falls back to the
        /// pistol.nu logo in that case.
        /// </summary>
        private string ResolveLogoUrl(int nodeId)
        {
            try
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return "";
                var node = ctx.Content.GetById(nodeId);
                var logo = node?.Value<IPublishedContent>("logo");
                return logo?.Url(_publishedUrlProvider, mode: UrlMode.Absolute) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
