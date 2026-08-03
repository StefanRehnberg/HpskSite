using HpskSite.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// "Samlingsfaktura" — a club pays many registration invoices in one payment.
    ///
    /// Shape (decided with Stefan 2026-08-03):
    ///   * One PARENT invoice per COMPETITION. Selecting invoices across several competitions gives
    ///     one payment per competition, because each competition has its own organiser to pay.
    ///   * The parent lives under the same competition's `registrationInvoicesHub` as its children,
    ///     so every existing surface (club/admin lists, Kvitto, Swish QR, audit) finds it already.
    ///   * The children are NOT deleted or rewritten. They stay the source of truth for the
    ///     registration, fee breakdown, deltävlingsavgift split and Bokföringsunderlag; they simply
    ///     gain `settledByInvoiceId` so they stop being separately payable.
    ///   * The parent's total is a SUM OF STORED CHILD AMOUNTS, never a recomputation — otherwise a
    ///     mid-season fee change would silently alter an already-issued invoice.
    ///
    /// An issued parent is never edited afterwards. Corrections are credit notes (kreditfaktura).
    /// </summary>
    public class ConsolidatedInvoiceService
    {
        public const string KindConsolidated = "consolidated";
        public const string KindCreditNote = "creditNote";

        private readonly IContentService _contentService;
        private readonly PaymentService _paymentService;
        private readonly ClubService _clubService;
        private readonly ILogger<ConsolidatedInvoiceService> _logger;

        public ConsolidatedInvoiceService(
            IContentService contentService,
            PaymentService paymentService,
            ClubService clubService,
            ILogger<ConsolidatedInvoiceService> logger)
        {
            _contentService = contentService;
            _paymentService = paymentService;
            _clubService = clubService;
            _logger = logger;
        }

        // ── reading ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything the preview and the create path need about one candidate invoice, plus WHY it
        /// can or cannot be consolidated. Read through IContentService (not the published cache) so a
        /// parent created moments ago is visible immediately.
        /// </summary>
        public sealed class Candidate
        {
            public int InvoiceId { get; init; }
            public string InvoiceNumber { get; init; } = "";
            public int CompetitionId { get; init; }
            public string CompetitionName { get; init; } = "";
            public string MemberName { get; init; } = "";
            public decimal Amount { get; init; }
            public string PaymentStatus { get; init; } = "";
            public bool Eligible { get; init; }
            public string? Reason { get; init; }
        }

        public Candidate Inspect(int invoiceId)
        {
            var invoice = _contentService.GetById(invoiceId);
            if (invoice == null || invoice.ContentType.Alias != "registrationInvoice")
                return new Candidate { InvoiceId = invoiceId, Eligible = false, Reason = "Fakturan finns inte." };

            var competitionId = ReadInt(invoice, "competitionId");
            var status = invoice.GetValue<string>("paymentStatus") ?? "";
            var kind = invoice.GetValue<string>("invoiceKind") ?? "";
            var settledBy = ReadInt(invoice, "settledByInvoiceId");
            var amount = ReadDecimal(invoice, "totalAmount");

            var candidate = new
            {
                Number = invoice.GetValue<string>("invoiceNumber") ?? "",
                Member = invoice.GetValue<string>("memberName") ?? "",
                CompName = competitionId > 0 ? (_contentService.GetById(competitionId)?.Name ?? "") : ""
            };

            string? reason = null;
            if (competitionId <= 0) reason = "Fakturan saknar tävling.";
            // UNSET means active — same convention as InvoiceAdminService.MapInvoice. GetValue<bool>
            // would read an unset property as false and reject every older invoice.
            else if (!(invoice.GetValue<bool?>("isActive") ?? true)) reason = "Fakturan är inaktiv.";
            else if (!string.IsNullOrWhiteSpace(kind))
                reason = kind == KindConsolidated
                    ? "Detta är redan en samlingsfaktura."
                    : "Detta är en kreditfaktura.";
            else if (!string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
                reason = $"Fakturan har status {status} — bara obetalda fakturor kan samlas.";
            else if (settledBy > 0 && !IsSettlementVoid(settledBy))
                reason = "Fakturan ingår redan i en samlingsfaktura.";
            else if (amount <= 0) reason = "Fakturan har inget belopp.";

            return new Candidate
            {
                InvoiceId = invoiceId,
                InvoiceNumber = candidate.Number,
                CompetitionId = competitionId,
                CompetitionName = candidate.CompName,
                MemberName = candidate.Member,
                Amount = amount,
                PaymentStatus = status,
                Eligible = reason == null,
                Reason = reason
            };
        }

        /// <summary>
        /// Who gets paid for this competition: the hosting club, or the region when it hosts its own
        /// (clubId unset, regionalFederation set) — the same two host states CompetitionUrlProvider and
        /// InvoiceAdminService.ResolveCompetitionRegion deal with. The Swish number is read from the
        /// COMPETITION, since that is where it lives and it can differ between a club's competitions.
        /// </summary>
        public Payee ResolvePayee(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new Payee();

            var swish = (competition.GetValue<string>("swishNumber") ?? "").Trim();

            var clubId = ReadInt(competition, "clubId");
            if (clubId > 0)
            {
                return new Payee
                {
                    Key = $"club:{clubId}",
                    Name = _clubService.GetClubNameById(clubId) ?? $"Förening #{clubId}",
                    SwishNumber = swish
                };
            }

            var region = InvoiceAdminService.NormalizeRegionCode(competition.GetValue<string>("regionalFederation"));
            if (region.Length > 0)
            {
                return new Payee
                {
                    Key = $"region:{region}",
                    Name = ResolveRegionName(region),
                    SwishNumber = swish
                };
            }

            return new Payee { Key = "", Name = "Okänd mottagare", SwishNumber = swish };
        }

        /// <summary>
        /// A competition stores a region CODE ("Halland"); the readable name ("Hallands
        /// Pistolskyttekrets") is the enum's Description — the same source
        /// MemberAdminController.GetRegionsForUserManagement uses, so the two can't drift. This name
        /// goes on a money document and into a warning the user reads, so don't show a bare code.
        /// </summary>
        private static string ResolveRegionName(string regionCode)
        {
            foreach (var federation in Enum.GetValues<Federations.RegionalFederations>())
            {
                if (string.Equals(federation.ToString(), regionCode, StringComparison.OrdinalIgnoreCase))
                    return federation.GetDescription();
            }
            return regionCode;
        }

        /// <summary>
        /// A child pointing at a Cancelled parent is free again — otherwise a mistaken consolidation
        /// that was later makulerad would lock those invoices out of ever being paid.
        /// </summary>
        private bool IsSettlementVoid(int parentInvoiceId)
        {
            var parent = _contentService.GetById(parentInvoiceId);
            if (parent == null) return true;
            var status = parent.GetValue<string>("paymentStatus") ?? "";
            return string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase);
        }

        // ── preview ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Who receives the money. A competition is organised EITHER by a club or by a region, and the
        /// Swish number is a per-COMPETITION property — so two competitions run by the same club can
        /// still collect to different Swish numbers. One parent invoice produces ONE QR, so the payee
        /// and the Swish number both have to be single-valued for a parent to be legitimate.
        /// </summary>
        public sealed class Payee
        {
            public string Key { get; init; } = "";        // "club:1098" / "region:Halland" / "" when unresolved
            public string Name { get; init; } = "";
            public string SwishNumber { get; init; } = "";
        }

        public sealed class PreviewGroup
        {
            public int CompetitionId { get; init; }
            public string CompetitionName { get; init; } = "";
            public Payee Payee { get; init; } = new();
            public List<Candidate> Invoices { get; init; } = new();
            public decimal Total { get; init; }
            /// <summary>A single invoice needs no parent — the club just pays that invoice.</summary>
            public bool NeedsParent { get; init; }
        }

        public sealed class PreviewResult
        {
            public List<PreviewGroup> Groups { get; init; } = new();
            public List<Candidate> Rejected { get; init; } = new();
            public decimal GrandTotal { get; init; }
            public List<string> MissingProperties { get; init; } = new();
            public bool Ready => MissingProperties.Count == 0;

            /// <summary>Distinct payees in the selection — each one is a SEPARATE payment.</summary>
            public int PayeeCount => Groups.Select(g => g.Payee.Key).Distinct().Count();
            public bool SpansMultipleCompetitions => Groups.Count > 1;
            public bool SpansMultiplePayees => PayeeCount > 1;

            /// <summary>
            /// Plain-language warning for the confirmation dialog. Money going to more than one
            /// recipient can never be one payment, so the user must see that before committing.
            /// </summary>
            public string? Warning =>
                !SpansMultipleCompetitions ? null
                : SpansMultiplePayees
                    ? $"Ditt val gäller {Groups.Count} tävlingar hos {PayeeCount} olika mottagare "
                      + $"({string.Join(", ", Groups.Select(g => g.Payee.Name).Distinct())}). "
                      + "Det blir en separat faktura och en separat betalning för varje tävling."
                    : $"Ditt val gäller {Groups.Count} tävlingar. Det blir en separat faktura och "
                      + "betalning per tävling, eftersom varje tävling har sitt eget Swish-nummer.";
        }

        /// <summary>
        /// Group the selected invoices per competition and say which are payable. Pure read — call
        /// this before showing a confirmation, and again inside Create (the client list goes stale,
        /// and paying the wrong invoices is the failure mode we must not have).
        /// </summary>
        public PreviewResult Preview(int[] invoiceIds)
        {
            var result = new List<Candidate>();
            foreach (var id in (invoiceIds ?? Array.Empty<int>()).Distinct())
                result.Add(Inspect(id));

            var eligible = result.Where(c => c.Eligible).ToList();
            var groups = eligible
                .GroupBy(c => c.CompetitionId)
                .Select(g => new PreviewGroup
                {
                    CompetitionId = g.Key,
                    CompetitionName = g.First().CompetitionName,
                    Payee = ResolvePayee(g.Key),
                    Invoices = g.OrderBy(c => c.MemberName).ToList(),
                    Total = g.Sum(c => c.Amount),
                    NeedsParent = g.Count() > 1
                })
                .OrderBy(g => g.Payee.Name).ThenBy(g => g.CompetitionName)
                .ToList();

            return new PreviewResult
            {
                Groups = groups,
                Rejected = result.Where(c => !c.Eligible).ToList(),
                GrandTotal = groups.Sum(g => g.Total),
                MissingProperties = _paymentService.MissingInvoiceProperties()
            };
        }

        // ── create ───────────────────────────────────────────────────────────────────────────────

        public sealed class CreatedParent
        {
            public int CompetitionId { get; init; }
            public string CompetitionName { get; init; } = "";
            public int? ParentInvoiceId { get; init; }
            public string ParentInvoiceNumber { get; init; } = "";
            public decimal Total { get; init; }
            public int CoveredCount { get; init; }
            /// <summary>Set when the group was a single invoice — pay it directly, no parent minted.</summary>
            public int? PayDirectlyInvoiceId { get; init; }
            public string? Error { get; init; }
        }

        /// <summary>
        /// Create one parent invoice per competition for the payable subset of <paramref name="invoiceIds"/>.
        /// Re-validates from scratch; never trusts the caller's list.
        /// </summary>
        public async Task<(bool success, string message, List<CreatedParent> parents, List<Candidate> rejected)>
            CreateAsync(int payerClubId, int[] invoiceIds, int actingMemberId)
        {
            var missing = _paymentService.MissingInvoiceProperties();
            if (missing.Count > 0)
            {
                return (false,
                    "Samlingsfakturor är inte aktiverade — egenskaper saknas på fakturatypen: " + string.Join(", ", missing),
                    new List<CreatedParent>(), new List<Candidate>());
            }

            if (payerClubId <= 0)
                return (false, "Ingen betalande förening angiven.", new List<CreatedParent>(), new List<Candidate>());

            var preview = Preview(invoiceIds);
            if (preview.Groups.Count == 0)
            {
                var why = preview.Rejected.FirstOrDefault()?.Reason;
                return (false, why ?? "Inga fakturor kunde samlas.", new List<CreatedParent>(), preview.Rejected);
            }

            var payerName = _clubService.GetClubNameById(payerClubId) ?? $"Förening #{payerClubId}";
            var parents = new List<CreatedParent>();

            foreach (var group in preview.Groups)
            {
                if (!group.NeedsParent)
                {
                    // One invoice — no point minting a second document for the same money.
                    parents.Add(new CreatedParent
                    {
                        CompetitionId = group.CompetitionId,
                        CompetitionName = group.CompetitionName,
                        Total = group.Total,
                        CoveredCount = 1,
                        PayDirectlyInvoiceId = group.Invoices[0].InvoiceId
                    });
                    continue;
                }

                // A parent invoice is a claim for one payment to ONE recipient. If we cannot say who
                // that is, we must not issue it — money would be collected with no identifiable payee.
                if (string.IsNullOrEmpty(group.Payee.Key))
                {
                    parents.Add(new CreatedParent
                    {
                        CompetitionId = group.CompetitionId,
                        CompetitionName = group.CompetitionName,
                        Total = group.Total,
                        CoveredCount = group.Invoices.Count,
                        Error = $"{group.CompetitionName} har ingen tydlig mottagare (varken förening eller krets) "
                              + "— ingen samlingsfaktura kan skapas."
                    });
                    continue;
                }

                try
                {
                    var covered = group.Invoices.Select(i => i.InvoiceId).ToList();
                    var extra = new Dictionary<string, object?>
                    {
                        ["invoiceKind"] = KindConsolidated,
                        ["coveredInvoiceIds"] = JsonSerializer.Serialize(covered),
                        ["payerClubId"] = payerClubId.ToString(),
                        // Stefan: the invoice must state that it is for N registrations and list them.
                        ["notes"] = BuildParentNotes(payerName, group)
                        // NB no swishNumber here — the QR is generated from the COMPETITION's
                        // swishNumber, which is exactly why a parent may never span competitions.
                    };

                    var parent = await _paymentService.CreateStandaloneInvoiceAsync(
                        competitionId: group.CompetitionId,
                        memberId: $"club-{payerClubId}",           // mirrors the existing team-{id} convention
                        memberName: payerName,
                        totalAmount: group.Total,                  // sum of STORED child amounts
                        paymentMethod: "Swish",
                        extraProperties: extra,
                        auditNote: $"Samlingsfaktura skapad för {covered.Count} anmälningar ({payerName})");

                    if (parent == null)
                    {
                        parents.Add(new CreatedParent
                        {
                            CompetitionId = group.CompetitionId,
                            CompetitionName = group.CompetitionName,
                            Total = group.Total,
                            CoveredCount = covered.Count,
                            Error = "Kunde inte skapa samlingsfakturan."
                        });
                        continue;
                    }

                    // Point the children at the parent. If this half-fails the parent is rolled back,
                    // because a parent that only covers some of what it charges for is worse than none.
                    var linked = LinkChildren(covered, parent.Id, out var linkError);
                    if (!linked)
                    {
                        UnlinkChildren(covered, parent.Id);
                        _contentService.Unpublish(parent);
                        _contentService.Delete(parent);
                        parents.Add(new CreatedParent
                        {
                            CompetitionId = group.CompetitionId,
                            CompetitionName = group.CompetitionName,
                            Total = group.Total,
                            CoveredCount = covered.Count,
                            Error = linkError ?? "Kunde inte koppla fakturorna till samlingsfakturan."
                        });
                        continue;
                    }

                    parents.Add(new CreatedParent
                    {
                        CompetitionId = group.CompetitionId,
                        CompetitionName = group.CompetitionName,
                        ParentInvoiceId = parent.Id,
                        ParentInvoiceNumber = parent.GetValue<string>("invoiceNumber") ?? "",
                        Total = group.Total,
                        CoveredCount = covered.Count
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to consolidate competition {CompetitionId} for club {ClubId}",
                        group.CompetitionId, payerClubId);
                    parents.Add(new CreatedParent
                    {
                        CompetitionId = group.CompetitionId,
                        CompetitionName = group.CompetitionName,
                        Total = group.Total,
                        Error = "Ett fel uppstod när samlingsfakturan skapades."
                    });
                }
            }

            var made = parents.Count(p => p.ParentInvoiceId.HasValue);
            var direct = parents.Count(p => p.PayDirectlyInvoiceId.HasValue);
            var failed = parents.Count(p => p.Error != null);

            var msg = made switch
            {
                0 when direct > 0 && failed == 0 => "Endast en faktura per tävling valdes — betala dem direkt.",
                0 => "Ingen samlingsfaktura kunde skapas.",
                1 => "En samlingsfaktura har skapats.",
                _ => $"{made} samlingsfakturor har skapats (en per tävling)."
            };
            if (failed > 0) msg += $" {failed} tävling(ar) misslyckades.";

            return (made > 0 || direct > 0, msg, parents, preview.Rejected);
        }

        /// <summary>
        /// Undo an unpaid samlingsfaktura: free its children and cancel the parent. The payer must be
        /// able to do this — they can consolidate invoices on ANOTHER club's competition (that is the
        /// point of the feature), but the organiser's CancelInvoice would refuse them, leaving a club
        /// that ticked the wrong boxes with no way back.
        ///
        /// Only while Pending. Once the organiser has marked it Paid the money has moved and the
        /// correction is a kreditfaktura, never a cancellation.
        /// </summary>
        public (bool success, string message, int freedCount, int? payerClubId, string parentStatus)
            CancelUnpaidParent(int parentInvoiceId)
        {
            var parent = _contentService.GetById(parentInvoiceId);
            if (parent == null || parent.ContentType.Alias != "registrationInvoice")
                return (false, "Fakturan finns inte.", 0, null, "");

            var kind = parent.GetValue<string>("invoiceKind") ?? "";
            if (kind != KindConsolidated)
                return (false, "Fakturan är inte en samlingsfaktura.", 0, null, "");

            var status = parent.GetValue<string>("paymentStatus") ?? "";
            var payerClubId = ReadInt(parent, "payerClubId");

            if (string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase))
                return (false, "Samlingsfakturan är betald — använd kreditfaktura istället.", 0, payerClubId, status);
            if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                return (false, "Samlingsfakturan är redan makulerad.", 0, payerClubId, status);

            var covered = ReadCoveredIds(parent);
            UnlinkChildren(covered, parentInvoiceId);

            // Raw SetValue THROWS on an alias the doctype doesn't have (unlike PaymentService's
            // SetInvoicePropertySafely, which warns). `isActive` is documented but absent on some
            // installs, and paymentStatus is what actually decides everything downstream — so guard
            // both rather than let a missing optional property abort a cancellation that has already
            // freed the children.
            if (parent.HasProperty("paymentStatus")) parent.SetValue("paymentStatus", "Cancelled");
            if (parent.HasProperty("isActive")) parent.SetValue("isActive", false);
            if (!_contentService.Save(parent).Success)
                return (false, "Kunde inte makulera samlingsfakturan.", 0, payerClubId, status);
            _contentService.Publish(parent, new[] { "*" }, -1);

            return (true,
                covered.Count == 0
                    ? "Samlingsfakturan är makulerad."
                    : $"Samlingsfakturan är makulerad. {covered.Count} fakturor kan betalas var för sig igen.",
                covered.Count, payerClubId, "Cancelled");
        }

        /// <summary>The invoice ids a parent covers. Empty for anything that isn't a parent.</summary>
        public List<int> ReadCoveredIds(IContent parent)
        {
            var raw = parent.GetValue<string>("coveredInvoiceIds") ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return new List<int>();
            try
            {
                return JsonSerializer.Deserialize<List<int>>(raw) ?? new List<int>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not parse coveredInvoiceIds '{Raw}' on invoice {InvoiceId}", raw, parent.Id);
                return new List<int>();
            }
        }

        /// <summary>The paying club recorded on a parent invoice, or 0.</summary>
        public int ReadPayerClubId(int invoiceId)
        {
            var invoice = _contentService.GetById(invoiceId);
            return invoice == null ? 0 : ReadInt(invoice, "payerClubId");
        }

        private static string BuildParentNotes(string payerName, PreviewGroup group)
        {
            var lines = group.Invoices
                .Select(i => $"  {i.InvoiceNumber}  {i.MemberName}  {i.Amount:0.##} kr");
            return $"Samlingsfaktura – {payerName} betalar {group.Invoices.Count} anmälningar "
                 + $"till {group.CompetitionName} (mottagare: {group.Payee.Name}):{Environment.NewLine}"
                 + string.Join(Environment.NewLine, lines);
        }

        private bool LinkChildren(List<int> invoiceIds, int parentId, out string? error)
        {
            error = null;
            foreach (var id in invoiceIds)
            {
                var child = _contentService.GetById(id);
                if (child == null) { error = $"Faktura #{id} kunde inte läsas."; return false; }

                if (!child.HasProperty("settledByInvoiceId"))
                {
                    error = "Egenskapen settledByInvoiceId saknas på fakturatypen.";
                    return false;
                }
                child.SetValue("settledByInvoiceId", parentId.ToString());
                if (!_contentService.Save(child).Success) { error = $"Kunde inte spara faktura #{id}."; return false; }
                if (!_contentService.Publish(child, new[] { "*" }, -1).Success)
                {
                    error = $"Kunde inte publicera faktura #{id}.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Best-effort rollback of <see cref="LinkChildren"/>.</summary>
        private void UnlinkChildren(List<int> invoiceIds, int parentId)
        {
            foreach (var id in invoiceIds)
            {
                try
                {
                    var child = _contentService.GetById(id);
                    if (child == null) continue;
                    if (!child.HasProperty("settledByInvoiceId")) continue;
                    if (ReadInt(child, "settledByInvoiceId") != parentId) continue;
                    child.SetValue("settledByInvoiceId", "");
                    _contentService.Save(child);
                    _contentService.Publish(child, new[] { "*" }, -1);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Rollback: could not unlink invoice {InvoiceId} from parent {ParentId}", id, parentId);
                }
            }
        }

        // ── shared readers ───────────────────────────────────────────────────────────────────────
        // The invoice doctype stores ids/amounts as Textstring in places, so parse defensively rather
        // than trusting GetValue<int>/<decimal> (which yields 0 for an unparseable value).

        internal static int ReadInt(IContent content, string alias)
        {
            var raw = content.GetValue(alias)?.ToString() ?? "";
            return int.TryParse(raw.Trim(), out var v) ? v : 0;
        }

        internal static decimal ReadDecimal(IContent content, string alias)
        {
            var raw = content.GetValue(alias);
            if (raw is decimal d) return d;
            if (raw is double dbl) return (decimal)dbl;
            var s = raw?.ToString()?.Trim().Replace(',', '.') ?? "";
            return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
        }
    }
}
