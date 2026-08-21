using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core.Cache;
using Umbraco.Extensions;
using Microsoft.Extensions.Logging;
using HpskSite.Models;
using System.Globalization;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for aggregating and managing invoices across multiple competitions
    /// Provides efficient invoice retrieval with server-side filtering
    /// </summary>
    public class InvoiceAdminService
    {
        private readonly ILogger<InvoiceAdminService> _logger;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberService _memberService;
        private readonly AppCaches _appCaches;
        private readonly Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabaseFactory _databaseFactory;

        /// <summary>
        /// Content-tree scan cache. Deliberately under the "admin_invoices_" prefix so the existing
        /// InvalidateInvoiceCaches() (ClearByRegex("^admin_invoices_")) already drops it whenever an
        /// invoice is marked paid, cancelled or created — no new invalidation call sites.
        /// </summary>
        private const string TreeScanCacheKey = "admin_invoices_treescan";
        private const string ClubMembersCacheKey = "admin_invoices_clubmembers_{0}";
        private const string LiveTeamIdsCacheKey = "admin_invoices_liveteamids";

        /// <summary>
        /// Short on purpose. The scan only has to survive one operator's burst of filter/page/view
        /// switches — each of which used to re-walk the whole tree — not to stay fresh for minutes.
        /// </summary>
        private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromSeconds(60);

        public InvoiceAdminService(
            ILogger<InvoiceAdminService> logger,
            IContentService contentService,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberService memberService,
            AppCaches appCaches,
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabaseFactory databaseFactory)
        {
            _databaseFactory = databaseFactory ?? throw new ArgumentNullException(nameof(databaseFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
            _umbracoContextAccessor = umbracoContextAccessor ?? throw new ArgumentNullException(nameof(umbracoContextAccessor));
            _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
            _appCaches = appCaches ?? throw new ArgumentNullException(nameof(appCaches));
        }

        /// <summary>
        /// The four node sets the whole aggregation is built from. Everything downstream filters
        /// these in memory, so scanning the tree once per minute serves every view and every page.
        /// </summary>
        private sealed class ContentScan
        {
            public List<IContent> Competitions { get; init; } = new();
            public List<IContent> InvoiceHubs { get; init; } = new();
            public List<IContent> TeamRegistrations { get; init; } = new();
            public List<IContent> RegistrationHubs { get; init; } = new();
            public List<IContent> Clubs { get; init; } = new();
        }

        /// <summary>
        /// Scan the content tree for the nodes the invoice views need, cached briefly.
        ///
        /// This used to run on EVERY call — every filter change, every page step, every view switch —
        /// and it walked the tree exhaustively: one GetPagedChildren per node, including one per
        /// registration and one per invoice. On a site with real data that is the whole 20–30 s.
        /// </summary>
        private ContentScan GetContentScan()
        {
            var cached = _appCaches.RuntimeCache.Get(TreeScanCacheKey) as ContentScan;
            if (cached != null) return cached;

            var started = DateTime.UtcNow;
            var scan = new ContentScan();

            foreach (var root in _contentService.GetRootContent())
            {
                foreach (var node in GetFlatDescendants(root))
                {
                    switch (node.ContentType.Alias)
                    {
                        case "competition": scan.Competitions.Add(node); break;
                        case "registrationInvoicesHub": scan.InvoiceHubs.Add(node); break;
                        case "competitionTeamRegistration": scan.TeamRegistrations.Add(node); break;
                        case "competitionRegistrationsHub": scan.RegistrationHubs.Add(node); break;
                        case "club": scan.Clubs.Add(node); break;
                    }
                }
            }

            _logger.LogInformation(
                "Invoice content scan: {Competitions} competitions, {Hubs} invoice hubs, {Teams} team registrations, {Clubs} clubs in {Ms} ms",
                scan.Competitions.Count, scan.InvoiceHubs.Count, scan.TeamRegistrations.Count, scan.Clubs.Count,
                (int)(DateTime.UtcNow - started).TotalMilliseconds);

            _appCaches.RuntimeCache.Insert(TreeScanCacheKey, () => scan, ScanCacheDuration);
            return scan;
        }

        /// <summary>
        /// Get all invoices with optional filtering
        /// Uses efficient flat traversal to aggregate invoices from multiple competitions
        /// </summary>
        public InvoiceAggregationResult GetAllInvoices(InvoiceFilterOptions? filters = null)
        {
            filters ??= new InvoiceFilterOptions();

            try
            {
                _logger.LogInformation("Starting invoice aggregation with filters: CompetitionId={CompetitionId}, Status={Status}, ActiveOnly={ActiveOnly}",
                    filters.CompetitionId, filters.PaymentStatus, filters.ActiveCompetitionsOnly);

                // Step 1: the content-tree scan — cached, pruned, and shared by every view and page.
                var scan = GetContentScan();
                var allCompetitions = scan.Competitions;
                var allInvoicesHubs = scan.InvoiceHubs;
                var allTeamRegDocs = scan.TeamRegistrations;
                var allRegistrationHubs = scan.RegistrationHubs;

                _logger.LogInformation("Found {CompetitionCount} competitions and {HubCount} invoice hubs",
                    allCompetitions.Count, allInvoicesHubs.Count);

                // Step 2: Filter competitions (active only by default)
                var filteredCompetitions = allCompetitions;
                if (filters.ActiveCompetitionsOnly)
                {
                    filteredCompetitions = allCompetitions
                        .Where(comp => IsCompetitionActive(comp))
                        .ToList();

                    _logger.LogInformation("Filtered to {ActiveCount} active competitions", filteredCompetitions.Count);
                }

                // If filtering by specific competition, narrow down further
                if (filters.CompetitionId.HasValue)
                {
                    filteredCompetitions = filteredCompetitions
                        .Where(comp => comp.Id == filters.CompetitionId.Value)
                        .ToList();
                }

                // If filtering by specific club, apply view-type-specific logic
                if (filters.ClubId.HasValue && filters.ClubId.Value > 0)
                {
                    // For "outgoing" and "members" views, we need all competitions (not just club's own)
                    // For "incoming" (default when no viewType), filter to club's own competitions
                    if (filters.ViewType != "outgoing" && filters.ViewType != "members")
                    {
                        filteredCompetitions = filteredCompetitions
                            .Where(comp => comp.GetValue<int>("clubId") == filters.ClubId.Value)
                            .ToList();

                        _logger.LogInformation("Filtered to {ClubCount} competitions for club {ClubId} (incoming view)",
                            filteredCompetitions.Count, filters.ClubId.Value);
                    }
                }

                // If filtering by region, keep competitions hosted in that region — EITHER by a club
                // that belongs to it, OR by the region itself.
                if (!string.IsNullOrEmpty(filters.Region))
                {
                    // Club regions lookup — clubs come from the same cached scan. This block used to
                    // walk the ENTIRE tree a SECOND time, on top of Step 1, just to find club nodes.
                    var allClubs = scan.Clubs;
                    var clubRegions = allClubs.ToDictionary(
                        club => club.Id,
                        club => NormalizeRegionCode(club.GetValue<string>("regionalFederation"))
                    );

                    var wantedRegion = NormalizeRegionCode(filters.Region);

                    filteredCompetitions = filteredCompetitions
                        .Where(comp =>
                        {
                            if (ResolveCompetitionRegion(comp, clubRegions) != wantedRegion) return false;
                            // Region's own competitions only = no host club.
                            return !filters.RegionOwnCompetitionsOnly || comp.GetValue<int>("clubId") <= 0;
                        })
                        .ToList();

                    _logger.LogInformation("Filtered to {RegionCount} competitions for region {Region} (ownOnly={OwnOnly})",
                        filteredCompetitions.Count, filters.Region, filters.RegionOwnCompetitionsOnly);
                }

                // Step 3: Group invoice hubs by competition ID for O(1) lookup.
                //
                // A competition is SUPPOSED to have exactly one invoice hub, but nothing enforces it:
                // both create sites (PaymentService.EnsureInvoicesHub / CreateStandaloneInvoiceAsync)
                // look the hub up and create it when absent, with no lock between the two steps. Two
                // registrations landing at the same moment — i.e. a registration burst, exactly what a
                // competition opening looks like — can therefore mint two hubs. ToDictionary THREW on
                // that, which took out the whole invoice list for every competition and every admin,
                // not just the affected one. Group instead, and read invoices from every hub, so a
                // duplicate degrades to a log line rather than an outage or invisible invoices.
                var hubsByCompetition = allInvoicesHubs
                    .GroupBy(hub => hub.ParentId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var dup in hubsByCompetition.Where(kv => kv.Value.Count > 1))
                {
                    _logger.LogWarning(
                        "Competition {CompetitionId} has {HubCount} registrationInvoicesHub nodes ({HubIds}). " +
                        "Invoices are read from all of them, but the duplicate should be merged and removed.",
                        dup.Key, dup.Value.Count, string.Join(", ", dup.Value.Select(h => h.Id)));
                }

                // Step 4: Aggregate invoices based on view type
                var allInvoices = new List<InvoiceInfo>();

                if (filters.ViewType == "outgoing" && filters.ClubId.HasValue && filters.ClubId.Value > 0)
                {
                    // "Fakturor att betala" — team invoices the club needs to pay
                    allInvoices = GetOutgoingTeamInvoices(filteredCompetitions, hubsByCompetition,
                        allTeamRegDocs, allRegistrationHubs, filters.ClubId.Value);
                }
                else if (filters.ViewType == "members" && filters.ClubId.HasValue && filters.ClubId.Value > 0)
                {
                    // "Medlemmars Fakturor" — individual invoices for club members
                    allInvoices = GetMemberInvoices(filteredCompetitions, hubsByCompetition, filters.ClubId.Value);
                }
                else
                {
                    // "Fakturor att få betalt för" (incoming) or no view type — current behavior
                    foreach (var competition in filteredCompetitions)
                    {
                        if (hubsByCompetition.TryGetValue(competition.Id, out var hubs))
                        {
                            foreach (var hub in hubs)
                            {
                                allInvoices.AddRange(GetInvoicesFromHub(hub, competition));
                            }
                        }
                    }
                }

                _logger.LogInformation("Aggregated {InvoiceCount} total invoices", allInvoices.Count);

                // Step 5: Apply server-side filtering
                var filteredInvoices = ApplyFilters(allInvoices, filters);

                _logger.LogInformation("Filtered to {FilteredCount} invoices", filteredInvoices.Count);

                // Step 6: Calculate metadata
                var metadata = CalculateMetadata(allInvoices, filteredInvoices, filters, filteredCompetitions.Count);

                // Step 7: Apply pagination
                var paginatedInvoices = filteredInvoices
                    .Skip((filters.Page - 1) * filters.PageSize)
                    .Take(filters.PageSize)
                    .ToList();

                // Flag orphaned team invoices — only on the page being returned, so the lookup
                // costs one query regardless of how large the unfiltered set is.
                var teamRows = paginatedInvoices
                    .Select(i => (invoice: i, teamId: TeamIdFromMemberId(i.MemberId)))
                    .Where(x => x.teamId.HasValue)
                    .ToList();
                if (teamRows.Count > 0)
                {
                    var liveTeamIds = GetLiveTeamIds();
                    if (liveTeamIds != null)
                    {
                        foreach (var (invoice, teamId) in teamRows)
                            invoice.IsOrphanedTeamInvoice = !liveTeamIds.Contains(teamId!.Value);
                    }
                }

                return new InvoiceAggregationResult
                {
                    Success = true,
                    Invoices = paginatedInvoices,
                    Metadata = metadata
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error aggregating invoices");
                return new InvoiceAggregationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Get team invoices the club needs to pay (outgoing).
        /// Finds team registration docs for the club, then matches invoices with memberId="team-{teamId}".
        /// </summary>
        private List<InvoiceInfo> GetOutgoingTeamInvoices(
            List<IContent> competitions,
            Dictionary<int, List<IContent>> hubsByCompetition,
            List<IContent> allTeamRegDocs,
            List<IContent> allRegistrationHubs,
            int clubId)
        {
            var result = new List<InvoiceInfo>();

            // Map registration hub ID -> competition ID
            var regHubToCompetitionId = allRegistrationHubs.ToDictionary(h => h.Id, h => h.ParentId);
            var competitionLookup = competitions.ToDictionary(c => c.Id);

            // Find team IDs belonging to this club and their competitions
            var clubTeamIds = new HashSet<int>();
            var teamCompetitionIds = new HashSet<int>();

            foreach (var teamReg in allTeamRegDocs)
            {
                if (teamReg.GetValue<int>("clubId") != clubId) continue;

                var teamId = teamReg.GetValue<int>("teamId");
                if (teamId > 0) clubTeamIds.Add(teamId);

                if (regHubToCompetitionId.TryGetValue(teamReg.ParentId, out var compId)
                    && competitionLookup.ContainsKey(compId))
                {
                    teamCompetitionIds.Add(compId);
                }
            }

            if (clubTeamIds.Count == 0) return result;

            _logger.LogInformation("Found {TeamCount} teams for club {ClubId} across {CompCount} competitions",
                clubTeamIds.Count, clubId, teamCompetitionIds.Count);

            // Get team invoices from those competitions
            foreach (var compId in teamCompetitionIds)
            {
                if (hubsByCompetition.TryGetValue(compId, out var hubs)
                    && competitionLookup.TryGetValue(compId, out var comp))
                {
                    var invoices = hubs.SelectMany(hub => GetInvoicesFromHub(hub, comp));
                    foreach (var inv in invoices)
                    {
                        if (inv.MemberId.StartsWith("team-")
                            && int.TryParse(inv.MemberId.Substring(5), out var teamId)
                            && clubTeamIds.Contains(teamId))
                        {
                            result.Add(inv);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get individual invoices for club members across all competitions.
        /// Looks up all members with primaryClubId matching the club, then finds their invoices.
        /// </summary>
        private List<InvoiceInfo> GetMemberInvoices(
            List<IContent> competitions,
            Dictionary<int, List<IContent>> hubsByCompetition,
            int clubId)
        {
            var result = new List<InvoiceInfo>();

            var clubMemberIds = GetClubMemberIds(clubId);
            if (clubMemberIds.Count == 0) return result;

            _logger.LogInformation("Found {MemberCount} members for club {ClubId}", clubMemberIds.Count, clubId);

            // Get individual invoices (non-team) for these members from all competitions
            foreach (var competition in competitions)
            {
                if (hubsByCompetition.TryGetValue(competition.Id, out var hubs))
                {
                    var invoices = hubs.SelectMany(hub => GetInvoicesFromHub(hub, competition));
                    foreach (var inv in invoices)
                    {
                        // Skip team invoices, only include individual member invoices
                        if (inv.MemberId.StartsWith("team-")) continue;
                        if (clubMemberIds.Contains(inv.MemberId))
                        {
                            result.Add(inv);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Which region hosts this competition? A competition is hosted EITHER by a club (clubId set,
        /// region comes from the club) OR by the region itself (clubId unset, region code sits on the
        /// competition's own `regionalFederation`) — the same two host states
        /// <see cref="Routing.CompetitionUrlProvider"/> builds URLs for.
        ///
        /// This used to require `clubId > 0`, which silently hid EVERY region-hosted competition's
        /// invoices from the admin Fakturor tab (and there is no "Alla kretsar" option to fall back
        /// on, so they were invisible everywhere). Reported after the 2026-08 SM dress rehearsal.
        /// Returns "" when neither host resolves.
        /// </summary>
        private static string ResolveCompetitionRegion(IContent competition, Dictionary<int, string> clubRegions)
        {
            // NB Value<int>/GetValue<int> yields 0 (not null) for an unset property, so test > 0.
            var clubId = competition.GetValue<int>("clubId");
            if (clubId > 0 && clubRegions.TryGetValue(clubId, out var clubRegion) && clubRegion.Length > 0)
                return clubRegion;

            return NormalizeRegionCode(competition.GetValue<string>("regionalFederation"));
        }

        /// <summary>
        /// Region codes are compared as plain strings, but a dropdown-backed property can be stored
        /// as a JSON array (["Halland"]) rather than a bare value — normalize both to "halland".
        /// </summary>
        /// <summary>
        /// The invoices that represent MONEY EARNED, for any sum. Drops samlingsfakturor (a payment
        /// instrument duplicating its children's amounts) and credit notes (stored positive, so they
        /// would add to income rather than reduce it). Everything else is untouched, so a competition
        /// with no consolidated payments sums exactly as it always did.
        /// </summary>
        public static IEnumerable<InvoiceInfo> MoneyRows(IEnumerable<InvoiceInfo> invoices) =>
            invoices.Where(i => i.InvoiceKind != "consolidated" && i.InvoiceKind != "creditNote");

        /// <summary>
        /// The competitions this club HOSTS — the same scoping the "att få betalt för" invoice view
        /// uses (<c>competition.clubId == clubId</c>), so a caller building receivables sees exactly
        /// the rows that list shows.
        ///
        /// Goes through the cached tree scan rather than walking the content tree again: that walk is
        /// what took the Fakturor page from 12 s to 33 ms when it was pruned and cached, and a second
        /// unrelated caller re-introducing it would quietly undo that.
        /// </summary>
        public List<IContent> GetCompetitionsHostedByClub(int clubId)
        {
            if (clubId <= 0) return new List<IContent>();
            return GetContentScan().Competitions
                .Where(c => c.GetValue<int>("clubId") == clubId)
                .ToList();
        }

        /// <summary>
        /// The competitions the REGION hosts in its own name — the other host shape. A competition is
        /// hosted either by a club (<c>clubId</c> set) or by the krets itself (<c>clubId</c> unset,
        /// region code on the competition's own <c>regionalFederation</c>); an SM is the latter.
        ///
        /// Same predicate the region's Fakturor tab uses for "egna tävlingar"
        /// (<c>RegionOwnCompetitionsOnly</c>): region matches AND <c>clubId &lt;= 0</c>. Without the
        /// second half this would also sweep in every club-hosted competition in the region, whose
        /// invoices belong to those clubs and not to the krets.
        /// </summary>
        public List<IContent> GetCompetitionsHostedByRegion(string regionCode)
        {
            var wanted = NormalizeRegionCode(regionCode);
            if (wanted.Length == 0) return new List<IContent>();
            return GetContentScan().Competitions
                .Where(c => c.GetValue<int>("clubId") <= 0
                         && NormalizeRegionCode(c.GetValue<string>("regionalFederation")) == wanted)
                .ToList();
        }

        public static string NormalizeRegionCode(string? raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) return "";

            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(value);
                    value = (parsed != null && parsed.Length > 0 ? parsed[0] : "").Trim();
                }
                catch
                {
                    value = value.Trim('[', ']', '"', ' ');
                }
            }

            return value.Trim('"').Trim().ToLowerInvariant();
        }

        /// <summary>
        /// MemberIds whose primary club is <paramref name="clubId"/>, cached briefly.
        ///
        /// This pages through the ENTIRE member register (thousands of members, 500 at a time) and
        /// used to run on every single request for the "Medlemmars fakturor" view — including every
        /// page step through the results, where the answer cannot have changed.
        ///
        /// Cached per club rather than as one big lookup: an operator works one club at a time, and
        /// this way a site admin flipping between clubs doesn't pay for all of them at once.
        /// </summary>
        private HashSet<string> GetClubMemberIds(int clubId)
        {
            var cacheKey = string.Format(ClubMembersCacheKey, clubId);
            if (_appCaches.RuntimeCache.Get(cacheKey) is HashSet<string> cached) return cached;

            var clubMemberIds = new HashSet<string>();
            int pageIndex = 0;
            const int pageSize = 500;
            long totalRecords;
            do
            {
                var members = _memberService.GetAll(pageIndex, pageSize, out totalRecords);
                foreach (var member in members)
                {
                    var primaryClubIdRaw = member.GetValue("primaryClubId")?.ToString();
                    if (!string.IsNullOrEmpty(primaryClubIdRaw)
                        && int.TryParse(primaryClubIdRaw, out var memberClubId)
                        && memberClubId == clubId)
                    {
                        clubMemberIds.Add(member.Id.ToString());
                    }
                }
                pageIndex++;
            } while (pageIndex * pageSize < totalRecords);

            _appCaches.RuntimeCache.Insert(cacheKey, () => clubMemberIds, ScanCacheDuration);
            return clubMemberIds;
        }

        /// <summary>
        /// Node types the scan records but never descends into: nothing the aggregation collects
        /// (competition · registrationInvoicesHub · competitionTeamRegistration ·
        /// competitionRegistrationsHub · club) can live underneath any of them.
        ///
        /// Each entry saves one GetPagedChildren per node of that type, and the counts are what
        /// matter: ~500 clubs each with their events and news, plus a start list, finals start list
        /// and result node per competition. That traffic was pure waste — the nodes were fetched,
        /// examined and thrown away.
        ///
        /// ⚠ Adding a type here is a claim that NOTHING the aggregation needs can appear below it.
        /// Check against the five aliases collected in GetContentScan before extending this.
        /// </summary>
        private static readonly HashSet<string> DoNotDescend = new(StringComparer.OrdinalIgnoreCase)
        {
            // Invoices are read per hub, on demand, by GetInvoicesFromHub.
            "registrationInvoicesHub",
            "registrationInvoice",
            // Club events and news. Competitions do not live under a club — they hang off
            // competitionsHub — so the club node itself is all this needs.
            "club",
            // Per-competition leaves.
            "precisionStartList",
            "finalsStartList",
            "competitionResult",
            "competitionRegistration",
            "competitionTeamRegistration",
        };

        /// <summary>
        /// Flat BFS traversal, PRUNED to the branches the invoice views actually need.
        ///
        /// The unpruned version enqueued every node in the tree and issued one GetPagedChildren per
        /// node — so the cost grew with the number of registrations and invoices, which is exactly
        /// what grows fastest on this site. Two branches are cut:
        ///
        ///   registrationInvoicesHub  — its children are the invoices, and those are read on demand
        ///                              per hub by GetInvoicesFromHub. Walking them here bought
        ///                              nothing and cost one query per invoice.
        ///   competitionRegistrationsHub — the aggregation needs the competitionTeamRegistration
        ///                              children (for club/team mapping) but nothing below them.
        ///                              Registrations are leaves; enqueuing them cost one query each
        ///                              to discover they have no children.
        ///
        /// ⚠ The team-registration children are added straight to the result and NOT enqueued.
        /// Doing both would add them twice, and every downstream lookup is keyed on their ids.
        /// </summary>
        private List<IContent> GetFlatDescendants(IContent root)
        {
            var result = new List<IContent>();
            var queue = new Queue<IContent>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                if (DoNotDescend.Contains(current.ContentType.Alias))
                {
                    continue;
                }

                var children = _contentService.GetPagedChildren(current.Id, 0, int.MaxValue, out _);

                if (current.ContentType.Alias == "competitionRegistrationsHub")
                {
                    foreach (var child in children)
                    {
                        if (child.ContentType.Alias == "competitionTeamRegistration")
                        {
                            result.Add(child);
                        }
                    }
                    continue;
                }

                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        /// <summary>
        /// Check if competition is active (for default filtering)
        /// Active = isActive=true AND registrationCloseDate within last 30 days
        /// </summary>
        private bool IsCompetitionActive(IContent competition)
        {
            var isActive = competition.GetValue<bool>("isActive");
            if (!isActive) return false;

            var regCloseDate = competition.GetValue<DateTime?>("registrationCloseDate");
            if (!regCloseDate.HasValue) return true; // If no close date, consider active

            // Consider active if closed within last 30 days
            return regCloseDate.Value >= DateTime.Now.AddDays(-30);
        }

        /// <summary>
        /// Get all invoices from a registrationInvoicesHub
        /// </summary>
        private List<InvoiceInfo> GetInvoicesFromHub(IContent hub, IContent competition)
        {
            var invoices = new List<InvoiceInfo>();

            try
            {
                var invoiceNodes = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "registrationInvoice")
                    .ToList();

                foreach (var invoiceNode in invoiceNodes)
                {
                    try
                    {
                        var invoice = MapInvoiceToInfo(invoiceNode, competition);
                        invoices.Add(invoice);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to map invoice {InvoiceId}", invoiceNode.Id);
                    }
                }

                // Name the samlingsfaktura each covered invoice belongs to, so its tag can link there.
                // Resolved from the batch we already loaded (a parent always lives in the same hub as
                // its children) — this list is slow enough without a lookup per row.
                var numberById = invoices
                    .GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First().InvoiceNumber);
                foreach (var invoice in invoices)
                {
                    if (invoice.SettledByInvoiceId > 0
                        && numberById.TryGetValue(invoice.SettledByInvoiceId, out var parentNumber))
                        invoice.SettledByInvoiceNumber = parentNumber;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoices from hub {HubId}", hub.Id);
            }

            return invoices;
        }

        /// <summary>
        /// How many invoices a samlingsfaktura covers, from its own JSON array — no extra lookups.
        /// 0 for an ordinary invoice or a credit note.
        /// </summary>
        private static int CountCoveredInvoices(IContent invoiceNode)
        {
            var raw = invoiceNode.GetValue<string>("coveredInvoiceIds") ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<int>>(raw)?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Map IContent invoice node to InvoiceInfo DTO
        /// Handles safe reading of paymentStatus (known tricky property)
        /// </summary>
        private InvoiceInfo MapInvoiceToInfo(IContent invoiceNode, IContent competition)
        {
            // Safe read of paymentStatus - handle JSON array format ["Paid"] or plain string "Paid"
            var paymentStatus = invoiceNode.GetValue<string>("paymentStatus");
            if (string.IsNullOrWhiteSpace(paymentStatus))
            {
                paymentStatus = "Pending";
            }
            else
            {
                // Clean any quotes or whitespace
                paymentStatus = paymentStatus.Trim('"', '\'', ' ');

                // Handle JSON array format: ["Paid"] -> Paid
                if (paymentStatus.StartsWith("[") && paymentStatus.EndsWith("]"))
                {
                    try
                    {
                        var array = System.Text.Json.JsonSerializer.Deserialize<string[]>(paymentStatus);
                        if (array != null && array.Length > 0)
                        {
                            paymentStatus = array[0];
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, try to extract manually
                        paymentStatus = paymentStatus.Trim('[', ']', '"', '\'', ' ');
                    }
                }
            }

            return new InvoiceInfo
            {
                Id = invoiceNode.Id,
                InvoiceNumber = invoiceNode.GetValue<string>("invoiceNumber") ?? "",
                CompetitionId = competition.Id,
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                MemberId = invoiceNode.GetValue<string>("memberId") ?? "",
                MemberName = invoiceNode.GetValue<string>("memberName") ?? "",
                TotalAmount = invoiceNode.GetValue<decimal>("totalAmount"),
                InvoiceKind = invoiceNode.GetValue<string>("invoiceKind") ?? "",
                SettledByInvoiceId = int.TryParse(
                    (invoiceNode.GetValue<string>("settledByInvoiceId") ?? "").Trim(), out var settledBy) ? settledBy : 0,
                CoveredCount = CountCoveredInvoices(invoiceNode),
                PaymentStatus = paymentStatus,
                PaymentMethod = invoiceNode.GetValue<string>("paymentMethod") ?? "Swish",
                CreatedDate = invoiceNode.GetValue<DateTime?>("createdDate") ?? invoiceNode.CreateDate,
                PaymentDate = invoiceNode.GetValue<DateTime?>("paymentDate"),
                RegistrationId = invoiceNode.GetValue<int>("registrationId"),
                IsActive = invoiceNode.GetValue<bool?>("isActive") ?? true,
                PaymentSentDate = invoiceNode.GetValue<DateTime?>("paymentSentDate"),
                PaymentSentBy = invoiceNode.GetValue<string>("paymentSentBy")
            };
        }

        /// <summary>
        /// Every team id that still exists, for spotting invoices whose team is gone.
        ///
        /// One query for the whole table rather than one per invoice — the list renders hundreds of
        /// rows and this is a hot path (the Fakturor page was 12 s per click until the tree scan was
        /// pruned; don't put a per-row query back). Cached under the "admin_invoices_" prefix so the
        /// existing InvalidateInvoiceCaches() drops it with everything else.
        ///
        /// A failure here returns null, which is read as "can't tell" — no row is labelled orphaned.
        /// Wrongly branding a live team's invoice as junk is far worse than missing one.
        /// </summary>
        private HashSet<int>? GetLiveTeamIds()
        {
            var cached = _appCaches.RuntimeCache.Get(LiveTeamIdsCacheKey) as HashSet<int>;
            if (cached != null) return cached;

            try
            {
                using var scope = _databaseFactory.CreateDatabase();
                var ids = scope.Fetch<int>("SELECT Id FROM CompetitionTeam");
                var set = new HashSet<int>(ids);
                _appCaches.RuntimeCache.Insert(LiveTeamIdsCacheKey, () => set, ScanCacheDuration);
                return set;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read live team ids; orphaned team invoices will not be labelled");
                return null;
            }
        }

        /// <summary>
        /// The team id inside a <c>team-{id}</c> memberId, or null when this is not a team invoice.
        /// </summary>
        private static int? TeamIdFromMemberId(string? memberId)
        {
            if (string.IsNullOrEmpty(memberId) || !memberId.StartsWith("team-", StringComparison.Ordinal))
                return null;
            return int.TryParse(memberId.AsSpan(5), out var id) ? id : null;
        }

        /// <summary>
        /// Apply filters to invoice list (server-side filtering)
        /// </summary>
        private List<InvoiceInfo> ApplyFilters(List<InvoiceInfo> invoices, InvoiceFilterOptions filters)
        {
            var filtered = invoices.AsEnumerable();

            // Exclude paid and cancelled invoices (default: true)
            if (filters.ExcludePaid)
            {
                filtered = filtered.Where(inv =>
                    !inv.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) &&
                    !inv.PaymentStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase));
            }

            // Filter by payment status
            if (!string.IsNullOrEmpty(filters.PaymentStatus))
            {
                filtered = filtered.Where(inv => inv.PaymentStatus.Equals(filters.PaymentStatus, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by member name (contains, case-insensitive)
            if (!string.IsNullOrEmpty(filters.MemberSearch))
            {
                filtered = filtered.Where(inv => inv.MemberName.Contains(filters.MemberSearch, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by invoice number (contains or exact match)
            if (!string.IsNullOrEmpty(filters.InvoiceNumberSearch))
            {
                filtered = filtered.Where(inv => inv.InvoiceNumber.Contains(filters.InvoiceNumberSearch, StringComparison.OrdinalIgnoreCase));
            }

            // Sort by created date (newest first)
            filtered = filtered.OrderByDescending(inv => inv.CreatedDate);

            return filtered.ToList();
        }

        /// <summary>
        /// Calculate metadata for aggregation result
        /// </summary>
        private InvoiceMetadata CalculateMetadata(
            List<InvoiceInfo> allInvoices,
            List<InvoiceInfo> filteredInvoices,
            InvoiceFilterOptions filters,
            int activeCompetitionsCount)
        {
            var totalPages = (int)Math.Ceiling((double)filteredInvoices.Count / filters.PageSize);

            return new InvoiceMetadata
            {
                TotalInvoices = allInvoices.Count,
                FilteredInvoices = filteredInvoices.Count,
                Page = filters.Page,
                PageSize = filters.PageSize,
                TotalPages = totalPages,
                ActiveCompetitions = activeCompetitionsCount,
                // Money sums EXCLUDE samlingsfakturor: a parent carries the same money as the invoices
                // it covers, so counting both doubles every consolidated payment. The children are kept
                // because they hold the per-registration detail (fee breakdown, deltävling split) that
                // the parent has none of. Credit notes are excluded too — they are stored as a positive
                // amount and would otherwise ADD to income instead of reducing it.
                TotalAmount = MoneyRows(allInvoices).Sum(inv => inv.TotalAmount),
                PaidAmount = MoneyRows(allInvoices).Where(inv => inv.PaymentStatus == "Paid").Sum(inv => inv.TotalAmount),
                PendingAmount = MoneyRows(allInvoices).Where(inv => inv.PaymentStatus == "Pending").Sum(inv => inv.TotalAmount)
            };
        }
    }
}
