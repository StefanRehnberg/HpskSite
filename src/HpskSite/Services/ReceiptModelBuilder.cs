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
        /// The invoice ids a samlingsfaktura covers. Parsed locally rather than reaching into
        /// ConsolidatedInvoiceService so the receipt builder keeps no dependency on it; the JSON is a
        /// plain int array, written in exactly one place.
        /// </summary>
        private static List<int> ParseCoveredInvoiceIds(IContent parent)
        {
            var raw = parent.GetValue<string>("coveredInvoiceIds") ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return new List<int>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<int>>(raw) ?? new List<int>();
            }
            catch
            {
                return new List<int>();
            }
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

            // --- Consolidated payment: itemise what the one payment covered. ---
            // A club paying for N shooters cannot reconcile a single lump-sum "Anmälningsavgift" line,
            // so the covered registrations are listed individually. Amounts come from the CHILD
            // invoices as issued, matching how the parent's total was summed in the first place.
            var coveredLines = new List<ReceiptLine>();
            if ((invoice.GetValue<string>("invoiceKind") ?? "") == "consolidated")
            {
                foreach (var childId in ParseCoveredInvoiceIds(invoice))
                {
                    var child = _contentService.GetById(childId);
                    if (child == null || child.ContentType.Alias != "registrationInvoice") continue;

                    var childRegId = child.GetValue<int>("registrationId");
                    var childClasses = "";
                    if (childRegId > 0)
                    {
                        var childReg = _contentService.GetById(childRegId);
                        var childJson = childReg?.GetValue<string>("shootingClasses") ?? "";
                        if (!string.IsNullOrWhiteSpace(childJson))
                        {
                            var childEntries = CompetitionRegistrationDocument.DeserializeShootingClasses(childJson);
                            childClasses = string.Join(", ", childEntries.Select(e => e.Class).Where(c => !string.IsNullOrEmpty(c)));
                        }
                    }

                    coveredLines.Add(new ReceiptLine
                    {
                        InvoiceNumber = child.GetValue<string>("invoiceNumber") ?? "",
                        MemberName = child.GetValue<string>("memberName") ?? "",
                        ShootingClasses = childClasses,
                        Amount = child.GetValue<decimal?>("actualPaidAmount") ?? child.GetValue<decimal>("totalAmount")
                    });
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
                IssuerBgNumber = (issuerNode != null && issuerNode.HasProperty("bgNumber")
                    ? issuerNode.GetValue<string>("bgNumber") ?? "" : "").Trim(),
                IssuerLogoUrl = issuerNode != null ? ResolveLogoUrl(issuerNode.Id) : "",

                CoveredLines = coveredLines,

                AmountPaid = totalPaid,
                PaymentMethod = paymentMethod,
                Reference = !string.IsNullOrWhiteSpace(transactionId) ? transactionId : invoiceNumber,
                ReceiptNumber = invoiceNumber,
                PaidAt = paidAt,
                IsPaid = anyPaid
            };
        }

        /// <summary>
        /// Build the printable "Faktura" for an invoice id: everything <see cref="Build"/> resolves
        /// (issuer, buyer, competition, itemised samlingsfaktura lines) plus the invoice-side facts —
        /// status, issue date, who is billed, and the Swish number to pay to. The MONEY (issued total,
        /// credits, amount due) is deliberately left to the caller, which reads it from
        /// ConsolidatedInvoiceService.GetBalance so the amount due is derived in exactly one place.
        /// Returns null when the invoice or its competition can't be resolved.
        /// </summary>
        public InvoiceDocumentModel? BuildInvoice(int invoiceId)
        {
            var basis = Build(invoiceId);
            if (basis == null) return null;

            var invoice = _contentService.GetById(invoiceId);
            if (invoice == null) return null;
            var competition = _contentService.GetById(basis.CompetitionId);

            var kind = invoice.GetValue<string>("invoiceKind") ?? "";
            var status = NormalizeStatus(invoice.GetValue<string>("paymentStatus"));
            var settledBy = invoice.GetValue<int?>("settledByInvoiceId")
                            ?? ReadIntLoose(invoice, "settledByInvoiceId");
            var settledByNumber = "";
            if (settledBy > 0)
                settledByNumber = _contentService.GetById(settledBy)?.GetValue<string>("invoiceNumber") ?? "";

            var model = new InvoiceDocumentModel
            {
                // carry over everything the receipt already resolved
                Found = basis.Found,
                InvoiceId = basis.InvoiceId,
                RegistrationId = basis.RegistrationId,
                CompetitionId = basis.CompetitionId,
                MemberId = basis.MemberId,
                MemberName = basis.MemberName,
                MemberEmail = basis.MemberEmail,
                CompetitionName = basis.CompetitionName,
                CompetitionDate = basis.CompetitionDate,
                ShootingClasses = basis.ShootingClasses,
                CoveredLines = basis.CoveredLines,
                IssuerName = basis.IssuerName,
                IssuerOrgNumber = basis.IssuerOrgNumber,
                IssuerStreet = basis.IssuerStreet,
                IssuerPostalCode = basis.IssuerPostalCode,
                IssuerCity = basis.IssuerCity,
                IssuerContactEmail = basis.IssuerContactEmail,
                IssuerBgNumber = basis.IssuerBgNumber,
                IssuerLogoUrl = basis.IssuerLogoUrl,
                AmountPaid = basis.AmountPaid,
                PaymentMethod = basis.PaymentMethod,
                Reference = basis.Reference,
                ReceiptNumber = basis.ReceiptNumber,
                PaidAt = basis.PaidAt,
                IsPaid = basis.IsPaid,

                IssuedAt = invoice.GetValue<DateTime?>("createdDate") ?? invoice.CreateDate,
                PaymentStatus = status,
                IsCreditNote = kind == "creditNote",
                IsCancelled = status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase),
                IsSettledByParent = settledBy > 0,
                SettledByInvoiceId = settledBy,
                SettledByInvoiceNumber = settledByNumber,
                SwishNumber = (competition?.GetValue<string>("swishNumber") ?? "").Trim(),
                PaymentReference = basis.ReceiptNumber,
                BilledToName = ResolveBilledTo(invoice)
            };

            model.StatusLabel = model.IsCreditNote ? "Kreditfaktura" : status switch
            {
                "Paid" => "Betald",
                "Cancelled" => "Makulerad",
                "Refunded" => "Att återbetala",
                "Failed" => "Misslyckad",
                _ => "Obetald"
            };
            return model;
        }

        /// <summary>
        /// Who the invoice is addressed to. A samlingsfaktura is billed to a club ("club-1098") and a
        /// team invoice to a team ("team-38"), neither of which is a member — the stored memberName
        /// already carries the readable text in those cases, so prefer it and fall back to the member.
        /// </summary>
        private static string ResolveBilledTo(IContent invoice)
        {
            var name = (invoice.GetValue<string>("memberName") ?? "").Trim();
            return name;
        }

        /// <summary>Legacy rows can store paymentStatus JSON-wrapped as <c>["Paid"]</c>.</summary>
        private static string NormalizeStatus(string? raw)
        {
            var s = (raw ?? "").Trim();
            if (s.StartsWith("[")) s = s.Trim('[', ']', '"', ' ');
            return s;
        }

        /// <summary>settledByInvoiceId is a Textstring on this doctype, so read it defensively.</summary>
        private static int ReadIntLoose(IContent node, string alias)
        {
            var raw = node.GetValue<string>(alias) ?? "";
            return int.TryParse(raw.Trim(), out var v) ? v : 0;
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
