using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Ekonomi usage for the Statistik tab: how many associations use the ledger for real, how many
    /// only try it in a sandbox, and which parts of it are in use.
    ///
    /// <para><b>⚠️ Having ledger data is NOT using the ledger.</b> Every association with settings
    /// already has a live issuer, and most journal entries are posted automatically: a confirmed
    /// Swish payment for a membership fee, an event or a competition registration writes its own
    /// verifikation. A club can have hundreds of live entries without the treasurer ever opening
    /// the app. So every figure here separates <i>automatic</i> sources from <i>own</i> ones
    /// (<see cref="EkonomiOwnSources"/>) — only the latter prove someone chose to work in it.</para>
    ///
    /// <para><b>⚠️ Sandbox is decided by <c>LedgerIssuer.Kind</c>, never by the id.</b> Sandbox ids
    /// start at 1 000 000 in prod, but dev also has negative ones; the join is the only reliable
    /// test.</para>
    ///
    /// <para>Not measurable yet: SIE export and receivable export leave no trace anywhere, so the
    /// export-shaped clubs are invisible here apart from their payments. Page views of /ekonomi are
    /// in the visitor stats' feature list instead.</para>
    /// </summary>
    public partial class AdminStatisticsController
    {
        private const string EkonomiCacheKey = "admin_ekonomi_stats";

        /// <summary>Sources that only exist because someone at the association did something in the ledger.</summary>
        private static readonly HashSet<string> EkonomiOwnSources = new(StringComparer.OrdinalIgnoreCase)
        {
            LedgerSourceType.Manual,
            LedgerSourceType.BankImport,
            LedgerSourceType.Expense,
            LedgerSourceType.SieImport,
            LedgerSourceType.AssetDepreciation,
            LedgerSourceType.OpeningBalance
        };

        private class EkIssuerRow
        {
            public int Id { get; set; }
            public int OwnerType { get; set; }
            public int OwnerId { get; set; }
            public string Kind { get; set; } = "";
            public DateTime CreatedUtc { get; set; }
            public DateTime? AbandonedUtc { get; set; }
        }

        /// <summary>Generic per-issuer aggregate. Columns a query doesn't select stay 0/null.</summary>
        private class EkCountRow
        {
            public int IssuerType { get; set; }
            public int IssuerId { get; set; }
            public string? SourceType { get; set; }
            public int Cnt { get; set; }
            public int Cnt30 { get; set; }
            public int A { get; set; }
            public int B { get; set; }
            public DateTime? FirstUtc { get; set; }
            public DateTime? LastUtc { get; set; }
        }

        private class EkDayRow
        {
            public int IssuerType { get; set; }
            public int IssuerId { get; set; }
            public string? SourceType { get; set; }
            public DateTime Day { get; set; }
            public int Cnt { get; set; }
        }

        private class EkMemberRow
        {
            public int IssuerType { get; set; }
            public int IssuerId { get; set; }
            public int MemberId { get; set; }
        }

        private class EkShapeRow
        {
            public int IssuerId { get; set; }
            public string? Shape { get; set; }
        }

        /// <summary>What one association did on ONE side (live or sandbox).</summary>
        private class EkSide
        {
            public int OwnEntries, OwnEntries30, AutoEntries, Corrections, SieImported;
            public DateTime? FirstOwnUtc, LastActivityUtc;
            public int PaymentsConfirmed, PaymentsConfirmed30;
            public int Expenses, ExpensesApproved, ExpensesWaiting, ExpenseReceipts;
            public int Approvals, Attachments;
            public int BankImports, BankRows, BankRowsHandled;
            public int Budgets, OwnProjects, Assets, Established, Invoices, OpenDrafts;
            public int Auditors, AuditorsAccepted;

            public void Touch(DateTime? utc)
            {
                if (utc == null) return;
                if (LastActivityUtc == null || utc > LastActivityUtc) LastActivityUtc = utc;
            }

            /// <summary>Someone at the association acted — not just an automatic posting.</summary>
            public bool HasHumanActivity =>
                OwnEntries > 0 || PaymentsConfirmed > 0 || Expenses > 0 || BankImports > 0 ||
                Invoices > 0 || Budgets > 0 || Assets > 0 || OwnProjects > 0 || Established > 0;
        }

        private class EkOwner
        {
            public int OwnerType, OwnerId;
            public bool HasLive;
            public string? Shape;
            public int Sandboxes, SandboxesActive;
            public DateTime? FirstSandboxUtc;
            public readonly EkSide Live = new();
            public readonly EkSide Sandbox = new();
        }

        /// <summary>The module list — one row per part of the ledger, counted per ASSOCIATION.</summary>
        private static readonly (string Key, string Label, Func<EkSide, int> Volume)[] EkonomiModules =
        {
            ("egna",      "Egna verifikationer",      s => s.OwnEntries),
            ("betalning", "Betalningar bekräftade",   s => s.PaymentsConfirmed),
            ("utgift",    "Utgifter (utlägg/fakturor)", s => s.Expenses),
            ("underlag",  "Underlag bifogade",        s => s.Attachments + s.ExpenseReceipts),
            ("attest",    "Attesterade verifikationer", s => s.Approvals),
            ("rattelse",  "Rättelser",                s => s.Corrections),
            ("bank",      "Bankavstämning (filer)",   s => s.BankImports),
            ("budget",    "Budget",                   s => s.Budgets),
            ("projekt",   "Egna projekt",             s => s.OwnProjects),
            ("anlaggning","Anläggningsregister",      s => s.Assets),
            ("faktura",   "Fakturor utställda",       s => s.Invoices),
            ("sie",       "SIE-import",               s => s.SieImported),
            ("bokslut",   "Bokslut fastställt",       s => s.Established),
            ("revisor",   "Revisorsåtkomst",          s => s.Auditors)
        };

        [HttpGet]
        public async Task<IActionResult> GetEkonomiStats(bool force = false)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            if (!force && _memoryCache.TryGetValue(EkonomiCacheKey, out object? cached) && cached != null)
            {
                return Json(cached);
            }

            try
            {
                var result = new { success = true, data = BuildEkonomiStats() };
                _memoryCache.Set(EkonomiCacheKey, result, CacheDuration);
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building ekonomi statistics");
                return Json(new { success = false, message = "Error loading ekonomi statistics: " + ex.Message });
            }
        }

        private object BuildEkonomiStats()
        {
            var now = DateTime.UtcNow;
            var thirtyDaysAgo = now.AddDays(-30);
            var fourteenDaysAgo = now.AddDays(-14);
            var sevenDaysAgo = now.AddDays(-7);
            const int weeks = 26;
            var today = now.Date;
            var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var weeklyStart = thisMonday.AddDays(-7 * (weeks - 1));

            // Names + demo exclusion (Ankeborg / Ankeland) from the content tree.
            var names = new Dictionary<(int, int), string>();
            var excludedClubIds = new HashSet<int>();
            var excludedRegionIds = new HashSet<int>();
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                if (root != null)
                {
                    ComputeDemoExclusions(root, excludedClubIds, excludedRegionIds);
                    void AddClubs(IPublishedContent? clubsPage)
                    {
                        if (clubsPage == null) return;
                        foreach (var club in clubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                            names[(0, club.Id)] = club.Name ?? "";
                    }
                    foreach (var rp in root.Children.Where(c => c.ContentType.Alias == "regionalPage"))
                    {
                        names[(1, rp.Id)] = rp.Name ?? "";
                        AddClubs(rp.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage"));
                    }
                    AddClubs(root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage"));
                }
            }
            bool IsDemo(int ownerType, int ownerId) =>
                ownerType == 1 ? excludedRegionIds.Contains(ownerId) : excludedClubIds.Contains(ownerId);

            var owners = new Dictionary<(int, int), EkOwner>();
            var issuers = new Dictionary<int, EkIssuerRow>();
            var weeklyAuto = new int[weeks];
            var weeklyOwn = new int[weeks];
            var weeklySandbox = new int[weeks];
            var activeLive = new HashSet<int>();
            var activeSandbox = new HashSet<int>();

            EkOwner OwnerFor(int ownerType, int ownerId)
            {
                if (!owners.TryGetValue((ownerType, ownerId), out var o))
                {
                    o = new EkOwner { OwnerType = ownerType, OwnerId = ownerId };
                    owners[(ownerType, ownerId)] = o;
                }
                return o;
            }

            // Resolves a row's issuer to (owner, side). An issuer missing from LedgerIssuer is a
            // pre-sandbox live issuer whose id is the node id. Demo owners resolve to null.
            (EkOwner Owner, EkSide Side, bool Sandbox)? Resolve(int issuerType, int issuerId)
            {
                int ot = issuerType, oid = issuerId;
                bool sandbox = false;
                if (issuers.TryGetValue(issuerId, out var iss))
                {
                    ot = iss.OwnerType; oid = iss.OwnerId;
                    sandbox = string.Equals(iss.Kind, "sandbox", StringComparison.OrdinalIgnoreCase);
                }
                // A negative id is always a sandbox (LedgerSchema.For). Missing from LedgerIssuer it
                // has no known owner — counting it as live bookkeeping would invent usage.
                else if (issuerId < 0) return null;
                if (IsDemo(ot, oid)) return null;
                var o = OwnerFor(ot, oid);
                if (!sandbox) o.HasLive = true;
                return (o, sandbox ? o.Sandbox : o.Live, sandbox);
            }

            // Runs one per-issuer aggregate; a missing table/column (partially migrated prod) is
            // logged and skipped so the rest still renders.
            void Each<T>(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, string what, string sql, Action<T> apply, params object[] args)
            {
                try
                {
                    foreach (var row in db.Fetch<T>(sql, args)) apply(row);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ekonomi statistics: {What} skipped", what);
                }
            }

            // Sandlådornas rader ligger i schemat sbx, de skarpa i dbo (LedgerSchema). Varje
            // utställarscopad fråga körs därför mot BÅDA — mot bara dbo hade all sandlådeaktivitet
            // saknats, tyst. LedgerIssuer och LedgerAuditorGrant finns bara i dbo och frågas med Each.
            void Both<T>(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, string what, string sql, Action<T> apply, params object[] args)
            {
                Each(db, what, sql, apply, args);
                Each(db, what + " (sandlåda)", LedgerSchema.Sql(-1, sql), apply, args);
            }

            using (var db = _databaseFactory.CreateDatabase())
            {
                foreach (var i in db.Fetch<EkIssuerRow>(
                    "SELECT Id, OwnerType, OwnerId, Kind, CreatedUtc, AbandonedUtc FROM dbo.LedgerIssuer"))
                {
                    issuers[i.Id] = i;
                    if (IsDemo(i.OwnerType, i.OwnerId)) continue;
                    var o = OwnerFor(i.OwnerType, i.OwnerId);
                    if (string.Equals(i.Kind, "sandbox", StringComparison.OrdinalIgnoreCase))
                    {
                        o.Sandboxes++;
                        if (i.AbandonedUtc == null) o.SandboxesActive++;
                        if (o.FirstSandboxUtc == null || i.CreatedUtc < o.FirstSandboxUtc) o.FirstSandboxUtc = i.CreatedUtc;
                    }
                    else
                    {
                        o.HasLive = true;
                    }
                }

                Each<EkShapeRow>(db, "shape", "SELECT IssuerId, Shape FROM dbo.LedgerIssuerSettings", r =>
                {
                    if (issuers.TryGetValue(r.IssuerId, out var iss) &&
                        string.Equals(iss.Kind, "live", StringComparison.OrdinalIgnoreCase) &&
                        !IsDemo(iss.OwnerType, iss.OwnerId))
                    {
                        OwnerFor(iss.OwnerType, iss.OwnerId).Shape = r.Shape;
                    }
                });

                Both<EkCountRow>(db, "journal entries",
                    @"SELECT IssuerType, IssuerId, SourceType,
                             COUNT(*) AS Cnt,
                             SUM(CASE WHEN RegisteredUtc >= @0 THEN 1 ELSE 0 END) AS Cnt30,
                             SUM(CASE WHEN CorrectsEntryId IS NOT NULL THEN 1 ELSE 0 END) AS A,
                             MIN(RegisteredUtc) AS FirstUtc, MAX(RegisteredUtc) AS LastUtc
                        FROM dbo.LedgerJournalEntry
                       GROUP BY IssuerType, IssuerId, SourceType",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        var s = res.Value.Side;
                        s.Corrections += r.A;
                        if (EkonomiOwnSources.Contains(r.SourceType ?? ""))
                        {
                            s.OwnEntries += r.Cnt;
                            s.OwnEntries30 += r.Cnt30;
                            if (string.Equals(r.SourceType, LedgerSourceType.SieImport, StringComparison.OrdinalIgnoreCase))
                                s.SieImported += r.Cnt;
                            if (s.FirstOwnUtc == null || r.FirstUtc < s.FirstOwnUtc) s.FirstOwnUtc = r.FirstUtc;
                            s.Touch(r.LastUtc);
                        }
                        else
                        {
                            s.AutoEntries += r.Cnt;
                        }
                    }, thirtyDaysAgo);

                Both<EkDayRow>(db, "weekly entries",
                    @"SELECT IssuerType, IssuerId, SourceType, CAST(RegisteredUtc AS date) AS Day, COUNT(*) AS Cnt
                        FROM dbo.LedgerJournalEntry
                       WHERE RegisteredUtc >= @0
                       GROUP BY IssuerType, IssuerId, SourceType, CAST(RegisteredUtc AS date)",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        int w = (int)((r.Day.Date - weeklyStart).TotalDays / 7);
                        if (w < 0 || w >= weeks) return;
                        if (res.Value.Sandbox) weeklySandbox[w] += r.Cnt;
                        else if (EkonomiOwnSources.Contains(r.SourceType ?? "")) weeklyOwn[w] += r.Cnt;
                        else weeklyAuto[w] += r.Cnt;
                    }, weeklyStart);

                Both<EkCountRow>(db, "payments",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt,
                             SUM(CASE WHEN ConfirmedUtc >= @0 THEN 1 ELSE 0 END) AS Cnt30,
                             MAX(ConfirmedUtc) AS LastUtc
                        FROM dbo.LedgerPayment
                       WHERE ConfirmedUtc IS NOT NULL AND VoidedUtc IS NULL
                       GROUP BY IssuerType, IssuerId",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        res.Value.Side.PaymentsConfirmed += r.Cnt;
                        res.Value.Side.PaymentsConfirmed30 += r.Cnt30;
                        res.Value.Side.Touch(r.LastUtc);
                    }, thirtyDaysAgo);

                Both<EkCountRow>(db, "expenses",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt,
                             SUM(CASE WHEN Status IN (@1, @2) THEN 1 ELSE 0 END) AS A,
                             SUM(CASE WHEN Status = @3 AND RegisteredUtc < @0 THEN 1 ELSE 0 END) AS B,
                             SUM(CASE WHEN ReceiptStoredAs IS NOT NULL THEN 1 ELSE 0 END) AS Cnt30,
                             MAX(RegisteredUtc) AS LastUtc
                        FROM dbo.LedgerExpense
                       GROUP BY IssuerType, IssuerId",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        var s = res.Value.Side;
                        s.Expenses += r.Cnt;
                        s.ExpensesApproved += r.A;
                        s.ExpensesWaiting += r.B;
                        s.ExpenseReceipts += r.Cnt30;
                        s.Touch(r.LastUtc);
                    }, fourteenDaysAgo, LedgerExpenseStatus.Approved, LedgerExpenseStatus.Paid, LedgerExpenseStatus.Registered);

                Both<EkCountRow>(db, "approvals",
                    @"SELECT e.IssuerType, e.IssuerId, COUNT(*) AS Cnt
                        FROM dbo.LedgerApproval a
                        JOIN dbo.LedgerJournalEntry e ON e.Id = a.JournalEntryId
                       GROUP BY e.IssuerType, e.IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.Approvals += r.Cnt; });

                Both<EkCountRow>(db, "attachments",
                    @"SELECT e.IssuerType, e.IssuerId, COUNT(*) AS Cnt
                        FROM dbo.LedgerAttachment a
                        JOIN dbo.LedgerJournalEntry e ON e.Id = a.JournalEntryId
                       WHERE a.VoidedUtc IS NULL
                       GROUP BY e.IssuerType, e.IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.Attachments += r.Cnt; });

                Both<EkCountRow>(db, "bank imports",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt, MAX(ImportedUtc) AS LastUtc
                        FROM dbo.LedgerBankImport
                       GROUP BY IssuerType, IssuerId",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        res.Value.Side.BankImports += r.Cnt;
                        res.Value.Side.Touch(r.LastUtc);
                    });

                Both<EkCountRow>(db, "bank rows",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt,
                             SUM(CASE WHEN MatchedLineId IS NOT NULL OR MatchKind IS NOT NULL THEN 1 ELSE 0 END) AS A
                        FROM dbo.LedgerBankRow
                       GROUP BY IssuerType, IssuerId",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        res.Value.Side.BankRows += r.Cnt;
                        res.Value.Side.BankRowsHandled += r.A;
                    });

                Both<EkCountRow>(db, "budgets",
                    "SELECT IssuerType, IssuerId, COUNT(*) AS Cnt FROM dbo.LedgerBudget GROUP BY IssuerType, IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.Budgets += r.Cnt; });

                // Competition/event projects are created automatically at the first krona — only
                // the ones a treasurer made by hand count as using the project dimension.
                Both<EkCountRow>(db, "projects",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt FROM dbo.LedgerProject
                       WHERE SourceType IS NULL OR SourceType NOT IN (@0, @1)
                       GROUP BY IssuerType, IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.OwnProjects += r.Cnt; },
                    LedgerProjectSource.Competition, LedgerProjectSource.Event);

                Both<EkCountRow>(db, "assets",
                    "SELECT IssuerType, IssuerId, COUNT(*) AS Cnt FROM dbo.LedgerAsset GROUP BY IssuerType, IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.Assets += r.Cnt; });

                Both<EkCountRow>(db, "fiscal years",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt FROM dbo.LedgerFiscalYear
                       WHERE Status = 'established' GROUP BY IssuerType, IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.Established += r.Cnt; });

                Both<EkCountRow>(db, "invoices",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt, MAX(CreatedUtc) AS LastUtc FROM dbo.LedgerCharge
                       WHERE Kind = 'invoice' AND VoidedUtc IS NULL GROUP BY IssuerType, IssuerId",
                    r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        res.Value.Side.Invoices += r.Cnt;
                        res.Value.Side.Touch(r.LastUtc);
                    });

                // A draft untouched for a week and never posted — someone started and gave up.
                Both<EkCountRow>(db, "drafts",
                    @"SELECT IssuerType, IssuerId, COUNT(*) AS Cnt FROM dbo.LedgerJournalEntryDraft
                       WHERE PostedEntryId IS NULL AND ISNULL(UpdatedUtc, CreatedUtc) < @0
                       GROUP BY IssuerType, IssuerId",
                    r => { var res = Resolve(r.IssuerType, r.IssuerId); if (res != null) res.Value.Side.OpenDrafts += r.Cnt; },
                    sevenDaysAgo);

                // The auditor grant belongs to the ASSOCIATION, not an issuer — always the live side.
                Each<EkCountRow>(db, "auditor grants",
                    @"SELECT OwnerType AS IssuerType, OwnerId AS IssuerId, COUNT(*) AS Cnt,
                             SUM(CASE WHEN AcceptedUtc IS NOT NULL THEN 1 ELSE 0 END) AS A
                        FROM dbo.LedgerAuditorGrant
                       WHERE RevokedUtc IS NULL AND ExpiresUtc > @0
                       GROUP BY OwnerType, OwnerId",
                    r =>
                    {
                        if (IsDemo(r.IssuerType, r.IssuerId)) return;
                        var o = OwnerFor(r.IssuerType, r.IssuerId);
                        o.Live.Auditors += r.Cnt;
                        o.Live.AuditorsAccepted += r.A;
                    }, now);

                // People who acted in the last 30 days: own entries, confirmed payments, expenses.
                void Active(string what, string sql, params object[] args)
                {
                    Both<EkMemberRow>(db, what, sql, r =>
                    {
                        var res = Resolve(r.IssuerType, r.IssuerId);
                        if (res == null) return;
                        (res.Value.Sandbox ? activeSandbox : activeLive).Add(r.MemberId);
                    }, args);
                }
                // The source list is six constants — far below NPoco's IN-expansion parameter cap.
                Active("active (entries)",
                    @"SELECT IssuerType, IssuerId, CreatedByMemberId AS MemberId FROM dbo.LedgerJournalEntry
                       WHERE RegisteredUtc >= @0 AND CreatedByMemberId > 0 AND SourceType IN (@1)
                       GROUP BY IssuerType, IssuerId, CreatedByMemberId",
                    thirtyDaysAgo, EkonomiOwnSources.ToArray());
                Active("active (payments)",
                    @"SELECT IssuerType, IssuerId, ConfirmedByMemberId AS MemberId FROM dbo.LedgerPayment
                       WHERE ConfirmedUtc >= @0 AND ConfirmedByMemberId > 0
                       GROUP BY IssuerType, IssuerId, ConfirmedByMemberId",
                    thirtyDaysAgo);
                Active("active (expenses)",
                    @"SELECT IssuerType, IssuerId, RegisteredByMemberId AS MemberId FROM dbo.LedgerExpense
                       WHERE RegisteredUtc >= @0 AND RegisteredByMemberId > 0
                       GROUP BY IssuerType, IssuerId, RegisteredByMemberId",
                    thirtyDaysAgo);
            }

            var all = owners.Values.ToList();
            var withLive = all.Where(o => o.HasLive).ToList();
            var usingLive = all.Where(o => o.Live.HasHumanActivity).ToList();
            var ownLive = all.Where(o => o.Live.OwnEntries > 0).ToList();
            var withSandbox = all.Where(o => o.Sandboxes > 0).ToList();

            // Sandbox → live: tried the sandbox BEFORE their first own live entry.
            var convertedDays = withSandbox
                .Where(o => o.Live.FirstOwnUtc != null && o.FirstSandboxUtc != null && o.FirstSandboxUtc <= o.Live.FirstOwnUtc)
                .Select(o => (o.Live.FirstOwnUtc!.Value - o.FirstSandboxUtc!.Value).TotalDays)
                .OrderBy(d => d)
                .ToList();
            double? medianDays = convertedDays.Count == 0 ? null
                : convertedDays.Count % 2 == 1 ? convertedDays[convertedDays.Count / 2]
                : (convertedDays[convertedDays.Count / 2 - 1] + convertedDays[convertedDays.Count / 2]) / 2;

            string ShapeLabel(string? shape) => shape switch
            {
                LedgerIssuerShape.FullLedger => "Hela bokföringen",
                LedgerIssuerShape.FeesAndExport => "Avgifter + SIE-export",
                LedgerIssuerShape.FeesOnly => "Bara avgifter",
                _ => "Ej vald"
            };

            // Every known shape is listed (0 is an answer); "Ej vald" only when it occurs.
            string NormShape(string? s) => LedgerIssuerShape.All.Contains(s ?? "") ? s! : "";
            var shapes = LedgerIssuerShape.All.Append("")
                .Select(sh => new
                {
                    shape = sh,
                    label = ShapeLabel(sh),
                    associations = withLive.Count(o => NormShape(o.Shape) == sh),
                    usingLive = usingLive.Count(o => NormShape(o.Shape) == sh)
                })
                .Where(x => x.shape != "" || x.associations > 0)
                .ToList();

            var modules = EkonomiModules.Select(m => new
            {
                key = m.Key,
                label = m.Label,
                liveAssociations = all.Count(o => m.Volume(o.Live) > 0),
                sandboxAssociations = all.Count(o => m.Volume(o.Sandbox) > 0),
                liveVolume = all.Sum(o => m.Volume(o.Live)),
                sandboxVolume = all.Sum(o => m.Volume(o.Sandbox))
            }).ToList();

            var byOwner = all
                .Where(o => o.Live.HasHumanActivity || o.Sandboxes > 0 || o.Live.AutoEntries > 0)
                .Select(o =>
                {
                    var isRegion = o.OwnerType == 1;
                    var last = new[] { o.Live.LastActivityUtc, o.Sandbox.LastActivityUtc }.Where(d => d != null).DefaultIfEmpty(null).Max();
                    return new
                    {
                        name = names.TryGetValue((o.OwnerType, o.OwnerId), out var nm) && !string.IsNullOrEmpty(nm)
                            ? nm : (isRegion ? "Krets" : "Klubb") + " #" + o.OwnerId,
                        type = isRegion ? "Krets" : "Klubb",
                        shape = ShapeLabel(o.Shape),
                        sandboxes = o.Sandboxes,
                        sandboxesActive = o.SandboxesActive,
                        sandboxOwnEntries = o.Sandbox.OwnEntries,
                        ownEntries = o.Live.OwnEntries,
                        ownEntries30 = o.Live.OwnEntries30,
                        autoEntries = o.Live.AutoEntries,
                        paymentsConfirmed = o.Live.PaymentsConfirmed,
                        modules = EkonomiModules.Where(m => m.Key != "egna" && m.Key != "betalning" && m.Volume(o.Live) > 0).Select(m => m.Label).ToList(),
                        sandboxModules = EkonomiModules.Where(m => m.Volume(o.Sandbox) > 0 && m.Volume(o.Live) == 0).Select(m => m.Label).ToList(),
                        lastActivity = last?.ToString("yyyy-MM-dd") ?? ""
                    };
                })
                .OrderByDescending(x => x.ownEntries)
                .ThenByDescending(x => x.paymentsConfirmed)
                .ThenByDescending(x => x.sandboxOwnEntries)
                .ThenByDescending(x => x.lastActivity)
                .Cast<object>()
                .ToList();

            var weekly = Enumerable.Range(0, weeks).Select(i => new
            {
                weekStart = weeklyStart.AddDays(7 * i).ToString("yyyy-MM-dd"),
                auto = weeklyAuto[i],
                own = weeklyOwn[i],
                sandbox = weeklySandbox[i]
            }).ToList();

            return new
            {
                summary = new
                {
                    usingLive = usingLive.Count,
                    usingLiveClubs = usingLive.Count(o => o.OwnerType == 0),
                    usingLiveRegions = usingLive.Count(o => o.OwnerType == 1),
                    ownLive = ownLive.Count,
                    triedSandbox = withSandbox.Count,
                    sandboxesTotal = withSandbox.Sum(o => o.Sandboxes),
                    sandboxesActive = withSandbox.Sum(o => o.SandboxesActive),
                    activePeople30Live = activeLive.Count,
                    activePeople30Sandbox = activeSandbox.Count,
                    ownEntries30Live = all.Sum(o => o.Live.OwnEntries30),
                    ownEntries30Sandbox = all.Sum(o => o.Sandbox.OwnEntries30),
                    autoEntriesLive = all.Sum(o => o.Live.AutoEntries),
                    ownEntriesLive = all.Sum(o => o.Live.OwnEntries),
                    paymentsConfirmed30Live = all.Sum(o => o.Live.PaymentsConfirmed30)
                },
                funnel = new List<object>
                {
                    new { label = "Har en skarp liggare",            count = withLive.Count,   hint = "Förening med ekonomiinställningar. Säger ingenting om användning." },
                    new { label = "Har provat sandlådan",            count = withSandbox.Count, hint = "Minst en sandlåda, även övergivna." },
                    new { label = "Använder skarpt",                 count = usingLive.Count,  hint = "Någon har gjort något själv i den skarpa liggaren: bokfört, bekräftat en betalning, registrerat en utgift, importerat bank, ställt ut en faktura …" },
                    new { label = "Bokför skarpt på egen hand",      count = ownLive.Count,    hint = "Minst en egen verifikation (manuell, utgift, bank, SIE, avskrivning, ingående balans) i den skarpa liggaren." },
                    new { label = "Minst 10 egna verifikationer",    count = all.Count(o => o.Live.OwnEntries >= 10), hint = "Mer än att bara prova." },
                    new { label = "Stämmer av mot banken",           count = all.Count(o => o.Live.BankImports > 0), hint = "Minst en kontoutdragsfil importerad skarpt." },
                    new { label = "Har fastställt ett bokslut",      count = all.Count(o => o.Live.Established > 0), hint = "Minst ett räkenskapsår med status fastställt." }
                },
                sandbox = new
                {
                    associations = withSandbox.Count,
                    total = withSandbox.Sum(o => o.Sandboxes),
                    active = withSandbox.Sum(o => o.SandboxesActive),
                    abandoned = withSandbox.Sum(o => o.Sandboxes - o.SandboxesActive),
                    restartedAssociations = withSandbox.Count(o => o.Sandboxes > 1),
                    withOwnEntries = withSandbox.Count(o => o.Sandbox.OwnEntries > 0),
                    withSieImport = withSandbox.Count(o => o.Sandbox.SieImported > 0),
                    convertedToLive = convertedDays.Count,
                    medianDaysToLive = medianDays.HasValue ? Math.Round(medianDays.Value, 1) : (double?)null,
                    ownEntriesTotal = all.Sum(o => o.Sandbox.OwnEntries)
                },
                friction = new
                {
                    staleDrafts = all.Sum(o => o.Live.OpenDrafts + o.Sandbox.OpenDrafts),
                    staleDraftsLive = all.Sum(o => o.Live.OpenDrafts),
                    expensesWaiting = all.Sum(o => o.Live.ExpensesWaiting),
                    bankRows = all.Sum(o => o.Live.BankRows),
                    bankRowsHandled = all.Sum(o => o.Live.BankRowsHandled),
                    corrections = all.Sum(o => o.Live.Corrections),
                    ownEntries = all.Sum(o => o.Live.OwnEntries),
                    auditorsInvited = all.Sum(o => o.Live.Auditors),
                    auditorsAccepted = all.Sum(o => o.Live.AuditorsAccepted)
                },
                shapes,
                modules,
                weekly,
                byOwner
            };
        }
    }
}
