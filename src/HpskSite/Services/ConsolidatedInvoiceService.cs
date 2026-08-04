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
            else if (!StatusIs(status, "Pending"))
                reason = $"Fakturan har status {NormalizeStatus(status)} — bara obetalda fakturor kan samlas.";
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
        /// InvoiceAdminService.ResolveCompetitionRegion deal with.
        ///
        /// The two payment numbers live at DIFFERENT levels on purpose:
        ///   * Swish is per COMPETITION (`competition.swishNumber`) — a club can collect different
        ///     competitions to different Swish numbers, and that routing is left exactly as it was.
        ///   * Bankgiro is per ORGANISATION (`club.bgNumber` / `regionalPage.bgNumber`) — a bankgiro
        ///     belongs to the association, not to an event, and clubs/kretsar paying each other's
        ///     invoices normally pay by BG (Stefan, 2026-08-04).
        /// </summary>
        public Payee ResolvePayee(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new Payee();

            var swish = (competition.GetValue<string>("swishNumber") ?? "").Trim();

            var clubId = ReadInt(competition, "clubId");
            if (clubId > 0)
            {
                var clubNode = _contentService.GetById(clubId);
                return new Payee
                {
                    Key = $"club:{clubId}",
                    Name = _clubService.GetClubNameById(clubId) ?? $"Förening #{clubId}",
                    SwishNumber = swish,
                    BgNumber = ReadBgNumber(clubNode)
                };
            }

            var region = InvoiceAdminService.NormalizeRegionCode(competition.GetValue<string>("regionalFederation"));
            if (region.Length > 0)
            {
                return new Payee
                {
                    Key = $"region:{region}",
                    Name = ResolveRegionName(region),
                    SwishNumber = swish,
                    BgNumber = ReadBgNumber(FindRegionNodeByCode(region))
                };
            }

            return new Payee { Key = "", Name = "Okänd mottagare", SwishNumber = swish };
        }

        /// <summary>The organisation's bankgiro, "" when unset or the property is absent on this install.</summary>
        private static string ReadBgNumber(IContent? organisationNode)
        {
            if (organisationNode == null || !organisationNode.HasProperty("bgNumber")) return "";
            return (organisationNode.GetValue<string>("bgNumber") ?? "").Trim();
        }

        /// <summary>The regionalPage node for a region code — needed to read the krets's own bankgiro.</summary>
        private IContent? FindRegionNodeByCode(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode)) return null;
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) return null;
            return _contentService.GetPagedChildren(root.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "regionalPage"
                    && (c.GetValue<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));
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
            return StatusIs(status, "Cancelled");
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
            /// <summary>The organisation's bankgiro (club/regionalPage level), "" when it has none.</summary>
            public string BgNumber { get; init; } = "";
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
                      + "betalning per tävling, eftersom varje tävling har sina egna betalningsuppgifter.";
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

            if (StatusIs(status, "Paid"))
                return (false, "Samlingsfakturan är betald — använd kreditfaktura istället.", 0, payerClubId, status);
            if (StatusIs(status, "Cancelled"))
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

        // ── balance & paid cascade ───────────────────────────────────────────────────────────────

        public sealed class ParentBalance
        {
            public bool IsParent { get; init; }
            public decimal Total { get; init; }
            public decimal Credited { get; init; }
            /// <summary>What is actually left to pay: total − credits, or 0 once settled.</summary>
            public decimal AmountDue { get; init; }
            public string Status { get; init; } = "";
            public int CoveredCount { get; init; }
            public int PayerClubId { get; init; }
        }

        /// <summary>
        /// The parent DOCUMENT keeps its issued total forever; what is left to pay is DERIVED
        /// (total − credit notes), because an issued invoice must never be edited. That is the whole
        /// reason corrections are kreditfakturor. Anything that asks "how much should this QR be for"
        /// has to come through here, or it will collect the pre-credit amount.
        /// </summary>
        public ParentBalance GetBalance(int invoiceId)
        {
            var invoice = _contentService.GetById(invoiceId);
            if (invoice == null) return new ParentBalance();

            var kind = invoice.GetValue<string>("invoiceKind") ?? "";
            var total = ReadDecimal(invoice, "totalAmount");
            var status = invoice.GetValue<string>("paymentStatus") ?? "";
            if (kind != KindConsolidated)
            {
                return new ParentBalance
                {
                    IsParent = false, Total = total, Credited = 0m,
                    AmountDue = IsSettled(status) ? 0m : total,
                    Status = status
                };
            }

            var credited = SumCreditNotesAgainst(invoiceId);
            var due = IsSettled(status) ? 0m : Math.Max(0m, total - credited);

            return new ParentBalance
            {
                IsParent = true,
                Total = total,
                Credited = credited,
                AmountDue = due,
                Status = status,
                CoveredCount = ReadCoveredIds(invoice).Count,
                PayerClubId = ReadInt(invoice, "payerClubId")
            };
        }

        private static bool IsSettled(string status) =>
            StatusIs(status, "Paid") || StatusIs(status, "Cancelled");

        /// <summary>
        /// Total of the credit notes issued against an invoice. Credit notes live in the same hub, so
        /// this is a sibling scan rather than a query.
        /// </summary>
        private decimal SumCreditNotesAgainst(int invoiceId)
        {
            var invoice = _contentService.GetById(invoiceId);
            if (invoice == null) return 0m;

            decimal sum = 0m;
            foreach (var sibling in _contentService.GetPagedChildren(invoice.ParentId, 0, 1000, out _))
            {
                if (sibling.ContentType.Alias != "registrationInvoice") continue;
                if ((sibling.GetValue<string>("invoiceKind") ?? "") != KindCreditNote) continue;
                if (ReadInt(sibling, "creditsInvoiceId") != invoiceId) continue;
                if (StatusIs(sibling.GetValue<string>("paymentStatus"), "Cancelled")) continue;   // a voided credit doesn't reduce anything
                sum += ReadDecimal(sibling, "totalAmount");
            }
            return sum;
        }

        /// <summary>
        /// The organiser marked the parent Paid, so every invoice it covers is paid too. Idempotent:
        /// children already Paid are skipped, so re-running (or a second Paid transition) can't double
        /// up. Each child goes through PaymentService so it gets its own audit row and the shooter gets
        /// their betalningsbekräftelse — the club paid, but the shooter still needs to know.
        /// </summary>
        public async Task<(int paid, int skipped, int failed)> CascadePaidToChildrenAsync(
            int parentInvoiceId, DateTime? paymentDate, string? paymentMethod,
            int? actorMemberId, string? actorMemberName, bool notifyShooters = true)
        {
            var parent = _contentService.GetById(parentInvoiceId);
            if (parent == null || (parent.GetValue<string>("invoiceKind") ?? "") != KindConsolidated)
                return (0, 0, 0);

            int paid = 0, skipped = 0, failed = 0;
            foreach (var childId in ReadCoveredIds(parent))
            {
                try
                {
                    var child = _contentService.GetById(childId);
                    if (child == null) { failed++; continue; }

                    var status = child.GetValue<string>("paymentStatus") ?? "";
                    if (StatusIs(status, "Paid")) { skipped++; continue; }

                    var ok = await _paymentService.UpdatePaymentStatusAsync(
                        invoiceId: childId,
                        paymentStatus: "Paid",
                        paymentDate: paymentDate ?? DateTime.Now,
                        transactionId: null,
                        notes: $"Betald via samlingsfaktura {parent.GetValue<string>("invoiceNumber")}",
                        paymentMethod: paymentMethod,
                        actorMemberId: actorMemberId,
                        actorMemberName: actorMemberName,
                        actualAmount: null,
                        sendReceiptOnPaid: notifyShooters);

                    if (ok) paid++; else failed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cascade: could not mark child invoice {ChildId} paid via parent {ParentId}",
                        childId, parentInvoiceId);
                    failed++;
                }
            }

            _logger.LogInformation("Cascade from parent {ParentId}: {Paid} paid, {Skipped} already paid, {Failed} failed",
                parentInvoiceId, paid, skipped, failed);
            return (paid, skipped, failed);
        }

        // ── detail (for the kreditfaktura UI) ────────────────────────────────────────────────────

        public sealed class CoveredLine
        {
            public int InvoiceId { get; init; }
            public string InvoiceNumber { get; init; } = "";
            public string MemberName { get; init; } = "";
            public decimal Amount { get; init; }
            public string PaymentStatus { get; init; } = "";
            /// <summary>Already credited in full, so offering it again would only over-credit.</summary>
            public bool AlreadyCredited { get; init; }
        }

        public sealed class ConsolidationDetail
        {
            public bool Found { get; init; }
            public int InvoiceId { get; init; }
            public string InvoiceNumber { get; init; } = "";
            public string CompetitionName { get; init; } = "";
            public string PayerName { get; init; } = "";
            public int PayerClubId { get; init; }
            public string Status { get; init; } = "";
            public decimal Total { get; init; }
            public decimal Credited { get; init; }
            public decimal AmountDue { get; init; }
            /// <summary>Highest credit that can still be issued without exceeding the issued total.</summary>
            public decimal MaxCreditable { get; init; }
            public bool IsPaid { get; init; }
            public List<CoveredLine> Covered { get; init; } = new();
            public List<CoveredLine> CreditNotes { get; init; } = new();
        }

        /// <summary>
        /// Everything the credit-note dialog needs: what the parent charges, what has already been
        /// credited, and which covered registrations are still creditable.
        /// </summary>
        public ConsolidationDetail GetDetail(int parentInvoiceId)
        {
            var parent = _contentService.GetById(parentInvoiceId);
            if (parent == null || parent.ContentType.Alias != "registrationInvoice"
                || (parent.GetValue<string>("invoiceKind") ?? "") != KindConsolidated)
                return new ConsolidationDetail();

            var balance = GetBalance(parentInvoiceId);
            var competitionId = ReadInt(parent, "competitionId");

            // Which covered invoices does an existing credit note already point at?
            var creditNotes = new List<CoveredLine>();
            var creditedInvoiceIds = new HashSet<int>();
            foreach (var sibling in _contentService.GetPagedChildren(parent.ParentId, 0, 1000, out _))
            {
                if (sibling.ContentType.Alias != "registrationInvoice") continue;
                if ((sibling.GetValue<string>("invoiceKind") ?? "") != KindCreditNote) continue;
                if (ReadInt(sibling, "creditsInvoiceId") != parentInvoiceId) continue;

                var status = sibling.GetValue<string>("paymentStatus") ?? "";
                var voided = StatusIs(status, "Cancelled");
                creditNotes.Add(new CoveredLine
                {
                    InvoiceId = sibling.Id,
                    InvoiceNumber = sibling.GetValue<string>("invoiceNumber") ?? "",
                    MemberName = sibling.GetValue<string>("memberName") ?? "",
                    Amount = ReadDecimal(sibling, "totalAmount"),
                    PaymentStatus = status
                });
                if (!voided) foreach (var id in ReadCoveredIds(sibling)) creditedInvoiceIds.Add(id);
            }

            var covered = new List<CoveredLine>();
            foreach (var childId in ReadCoveredIds(parent))
            {
                var child = _contentService.GetById(childId);
                if (child == null) continue;
                covered.Add(new CoveredLine
                {
                    InvoiceId = childId,
                    InvoiceNumber = child.GetValue<string>("invoiceNumber") ?? "",
                    MemberName = child.GetValue<string>("memberName") ?? "",
                    Amount = ReadDecimal(child, "totalAmount"),
                    PaymentStatus = child.GetValue<string>("paymentStatus") ?? "",
                    AlreadyCredited = creditedInvoiceIds.Contains(childId)
                });
            }

            return new ConsolidationDetail
            {
                Found = true,
                InvoiceId = parentInvoiceId,
                InvoiceNumber = parent.GetValue<string>("invoiceNumber") ?? "",
                CompetitionName = competitionId > 0 ? (_contentService.GetById(competitionId)?.Name ?? "") : "",
                PayerName = parent.GetValue<string>("memberName") ?? "",
                PayerClubId = balance.PayerClubId,
                Status = balance.Status,
                Total = balance.Total,
                Credited = balance.Credited,
                AmountDue = balance.AmountDue,
                MaxCreditable = Math.Max(0m, balance.Total - balance.Credited),
                IsPaid = StatusIs(balance.Status, "Paid"),
                Covered = covered.OrderBy(c => c.MemberName).ToList(),
                CreditNotes = creditNotes
            };
        }

        // ── kreditfaktura ────────────────────────────────────────────────────────────────────────

        public sealed class CreditNoteResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = "";
            public int? CreditNoteId { get; init; }
            public string CreditNoteNumber { get; init; } = "";
            public decimal Amount { get; init; }
            public decimal RemainingDue { get; init; }
            public bool ParentClosed { get; init; }
            public bool AwaitingRefund { get; init; }
        }

        /// <summary>
        /// Issue a kreditfaktura against a samlingsfaktura (Stefan, 2026-08-03: once a parent has been
        /// issued we never alter it — a correction is a credit note the payer subtracts).
        ///
        /// Two cases, and they are genuinely different:
        ///   * parent still UNPAID — the credit reduces what is left to pay, and the covered invoice is
        ///     cancelled along with it. If the credits reach the full total the parent closes as
        ///     Cancelled, since there is nothing left to collect.
        ///   * parent already PAID — nothing left to subtract from; the organiser owes money back. The
        ///     credit note is still issued (the accounting is identical) and flagged as att återbetala.
        ///     The covered invoice STAYS Paid: it was paid, and rewriting history would be a lie.
        ///
        /// The note carries its own invoice number, points at the invoice it credits via
        /// creditsInvoiceId, and records the credited registration invoice in coveredInvoiceIds so the
        /// reason is traceable from the document itself.
        /// </summary>
        public async Task<CreditNoteResult> CreateCreditNoteAsync(
            int parentInvoiceId, int creditedInvoiceId, decimal? explicitAmount, string reason,
            int? actorMemberId, string? actorMemberName)
        {
            var missing = _paymentService.MissingInvoiceProperties();
            if (missing.Count > 0)
                return new CreditNoteResult { Message = "Kreditfakturor är inte aktiverade — egenskaper saknas: " + string.Join(", ", missing) };

            var parent = _contentService.GetById(parentInvoiceId);
            if (parent == null || parent.ContentType.Alias != "registrationInvoice")
                return new CreditNoteResult { Message = "Fakturan finns inte." };
            if ((parent.GetValue<string>("invoiceKind") ?? "") != KindConsolidated)
                return new CreditNoteResult { Message = "Kreditfakturor kan bara skapas mot en samlingsfaktura." };

            var parentStatus = parent.GetValue<string>("paymentStatus") ?? "";
            if (StatusIs(parentStatus, "Cancelled"))
                return new CreditNoteResult { Message = "Samlingsfakturan är makulerad — det finns inget att kreditera." };

            var covered = ReadCoveredIds(parent);
            IContent? credited = null;
            if (creditedInvoiceId > 0)
            {
                if (!covered.Contains(creditedInvoiceId))
                    return new CreditNoteResult { Message = "Den fakturan ingår inte i samlingsfakturan." };
                credited = _contentService.GetById(creditedInvoiceId);
                if (credited == null)
                    return new CreditNoteResult { Message = "Fakturan som ska krediteras kunde inte läsas." };
            }

            var amount = explicitAmount ?? (credited != null ? ReadDecimal(credited, "totalAmount") : 0m);
            if (amount <= 0m)
                return new CreditNoteResult { Message = "Kreditbeloppet måste vara större än noll." };

            // Over-crediting would make the parent's balance negative, i.e. claim the organiser owes
            // more than was ever invoiced.
            var total = ReadDecimal(parent, "totalAmount");
            var alreadyCredited = SumCreditNotesAgainst(parentInvoiceId);
            if (alreadyCredited + amount > total)
            {
                return new CreditNoteResult
                {
                    Message = $"Kan inte kreditera {amount:0.##} kr — högst {(total - alreadyCredited):0.##} kr "
                            + $"återstår att kreditera av {total:0.##} kr."
                };
            }

            var parentWasPaid = StatusIs(parentStatus, "Paid");
            var competitionId = ReadInt(parent, "competitionId");
            var payerClubId = ReadInt(parent, "payerClubId");
            var payerName = parent.GetValue<string>("memberName") ?? "";
            var parentNumber = parent.GetValue<string>("invoiceNumber") ?? parentInvoiceId.ToString();

            var noteLines = new List<string>
            {
                $"KREDITFAKTURA mot samlingsfaktura {parentNumber}.",
                $"Belopp: {amount:0.##} kr."
            };
            if (credited != null)
                noteLines.Add($"Avser {credited.GetValue<string>("invoiceNumber")} – {credited.GetValue<string>("memberName")}.");
            if (!string.IsNullOrWhiteSpace(reason)) noteLines.Add($"Orsak: {reason.Trim()}");
            noteLines.Add(parentWasPaid
                ? "Samlingsfakturan är redan betald – beloppet ska ÅTERBETALAS till betalaren."
                : "Beloppet dras av från samlingsfakturans kvarvarande belopp.");

            var extra = new Dictionary<string, object?>
            {
                ["invoiceKind"] = KindCreditNote,
                ["creditsInvoiceId"] = parentInvoiceId.ToString(),
                ["payerClubId"] = payerClubId.ToString(),
                // Reuse coveredInvoiceIds for "what this note credits" — no extra property needed.
                ["coveredInvoiceIds"] = JsonSerializer.Serialize(
                    creditedInvoiceId > 0 ? new[] { creditedInvoiceId } : Array.Empty<int>()),
                ["notes"] = string.Join(Environment.NewLine, noteLines)
            };

            var note = await _paymentService.CreateStandaloneInvoiceAsync(
                competitionId: competitionId,
                memberId: $"club-{payerClubId}",
                memberName: payerName,
                totalAmount: amount,
                paymentMethod: "Swish",
                extraProperties: extra,
                auditNote: $"Kreditfaktura {amount:0.##} kr mot {parentNumber}");

            if (note == null)
                return new CreditNoteResult { Message = "Kunde inte skapa kreditfakturan." };

            // A credit note is not something to PAY. "Refunded" keeps it out of the payable lists while
            // still counting toward the credited sum (only a Cancelled note is treated as void).
            if (note.HasProperty("paymentStatus"))
            {
                note.SetValue("paymentStatus", "Refunded");
                _contentService.Save(note);
                _contentService.Publish(note, new[] { "*" }, -1);
            }

            // The covered invoice: cancel it when nothing has been paid yet. When the parent was
            // already paid it STAYS Paid — it genuinely was, and the credit note is the correction.
            if (credited != null && !parentWasPaid)
            {
                var creditedStatus = credited.GetValue<string>("paymentStatus") ?? "";
                if (!StatusIs(creditedStatus, "Paid"))
                {
                    await _paymentService.UpdatePaymentStatusAsync(
                        invoiceId: creditedInvoiceId,
                        paymentStatus: "Cancelled",
                        paymentDate: null,
                        transactionId: null,
                        notes: $"Makulerad – krediterad via {note.GetValue<string>("invoiceNumber")}",
                        paymentMethod: null,
                        actorMemberId: actorMemberId,
                        actorMemberName: actorMemberName,
                        actualAmount: null,
                        sendReceiptOnPaid: false,
                        // The one legitimate cancel of a covered invoice: the credit note that
                        // compensates for it has just been issued.
                        allowCancelWhenConsolidated: true);
                }
            }

            // Fully credited and never paid → there is nothing left to collect, so close the parent.
            var creditedNow = SumCreditNotesAgainst(parentInvoiceId);
            var remaining = Math.Max(0m, total - creditedNow);
            var parentClosed = false;
            if (!parentWasPaid && remaining <= 0m)
            {
                if (parent.HasProperty("paymentStatus")) parent.SetValue("paymentStatus", "Cancelled");
                _contentService.Save(parent);
                _contentService.Publish(parent, new[] { "*" }, -1);
                parentClosed = true;
            }

            return new CreditNoteResult
            {
                Success = true,
                CreditNoteId = note.Id,
                CreditNoteNumber = note.GetValue<string>("invoiceNumber") ?? "",
                Amount = amount,
                RemainingDue = parentWasPaid ? 0m : remaining,
                ParentClosed = parentClosed,
                AwaitingRefund = parentWasPaid,
                Message = parentWasPaid
                    ? $"Kreditfaktura på {amount:0.##} kr skapad. Samlingsfakturan var redan betald — beloppet ska återbetalas till betalaren."
                    : parentClosed
                        ? $"Kreditfaktura på {amount:0.##} kr skapad. Samlingsfakturan är nu helt krediterad och makulerad."
                        : $"Kreditfaktura på {amount:0.##} kr skapad. Kvar att betala: {remaining:0.##} kr."
            };
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

        /// <summary>
        /// paymentStatus is NOT always a bare string in older data — some of it is stored JSON-wrapped
        /// as ["Paid"]. InvoiceAdminService.MapInvoice and two separate CleanStatus helpers already
        /// defend against it, which is the evidence that such rows exist. Comparing the raw value would
        /// make a legacy invoice look neither Pending nor Paid: it would be reported as
        /// un-consolidatable, and the paid-cascade would fail to skip a child that is already settled
        /// (re-sending its betalningsbekräftelse). Every status comparison in this service goes through
        /// here.
        /// </summary>
        public static string NormalizeStatus(string? raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) return "Pending";          // unset means unpaid, as elsewhere
            if (s.StartsWith("[") && s.EndsWith("]"))
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(s);
                    if (arr != null && arr.Length > 0) s = arr[0];
                }
                catch
                {
                    s = s.Trim('[', ']');
                }
            }
            return s.Trim('"', '\'', ' ');
        }

        private static bool StatusIs(string? raw, string expected) =>
            string.Equals(NormalizeStatus(raw), expected, StringComparison.OrdinalIgnoreCase);

        internal static int ReadInt(IContent content, string alias)
        {
            var raw = content.GetValue(alias)?.ToString() ?? "";
            return int.TryParse(raw.Trim(), out var v) ? v : 0;
        }

        internal static decimal ReadDecimal(IContent content, string alias)
            => ParseAmount(content.GetValue(alias));

        /// <summary>
        /// Turn whatever is stored in a money property into a decimal. Split out from
        /// <see cref="ReadDecimal"/> so the odd shapes can be tested without an IContent.
        /// </summary>
        public static decimal ParseAmount(object? raw)
        {
            if (raw is decimal d) return d;
            if (raw is double dbl) return (decimal)dbl;
            // A stringly-stored amount can carry a Swedish decimal comma and a space or non-breaking
            // space as thousands separator ("1 050,00"). Invariant parsing would reject that outright
            // and this method would return 0 — a silently wrong AMOUNT, the worst failure available
            // here. Strip the grouping whitespace before parsing rather than trusting the format.
            var s = new string((raw?.ToString() ?? "")
                    .Where(c => !char.IsWhiteSpace(c))
                    .ToArray())
                .Replace(',', '.');
            return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
        }
    }
}
