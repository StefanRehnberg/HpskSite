using HpskSite.Models;
using HpskSite.Models.Events;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// <b>Flytten av händelserna från noder till tabellen</b> (sajtstrukturen fas 2d). Samma fönster som
    /// anmälningarna: <see cref="Preview"/> → <see cref="SetWriteFreeze"/>(true) → <see cref="Apply"/> →
    /// <see cref="Verify"/> → <see cref="Switch"/>("table") → <see cref="SetWriteFreeze"/>(false) →
    /// <see cref="UnpublishNodes"/> i portioner.
    ///
    /// <para><b>Återställning</b> = <see cref="Switch"/>("node"); noderna ligger orörda kvar tills de
    /// avpubliceras. Efter nya skrivningar i tabellen är det en framåträttning.</para>
    /// </summary>
    public class ClubEventMigrationService
    {
        private readonly NodeClubEventStore _nodes;
        private readonly SqlClubEventStore _table;
        private readonly SiteSettingService _settings;
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IShortStringHelper _shortStrings;
        private readonly ILogger<ClubEventMigrationService> _logger;

        public ClubEventMigrationService(NodeClubEventStore nodes, SqlClubEventStore table, SiteSettingService settings,
            IScopeProvider scopeProvider, IContentService contentService, IShortStringHelper shortStrings,
            ILogger<ClubEventMigrationService> logger)
        {
            _nodes = nodes;
            _table = table;
            _settings = settings;
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _shortStrings = shortStrings;
            _logger = logger;
        }

        public string CurrentStore => _settings.Get(ClubEventStoreSettings.StoreKey, ClubEventStoreSettings.StoreNode);
        public bool IsWriteFrozen => _settings.Get(ClubEventStoreSettings.WriteFreezeKey, "0") == "1";

        public void SetWriteFreeze(bool on, int? byMemberId)
            => _settings.Set(ClubEventStoreSettings.WriteFreezeKey, on ? "1" : "0", byMemberId);

        public List<string> MissingSchema()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var missing = new List<string>();
            foreach (var t in new[] { "ClubEvent", "ClubEventUrl", "LandingPage", "CompetitionLandingPage", "SiteSetting" })
                if (scope.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + t) == 0)
                    missing.Add(t);
            return missing;
        }

        public ClubEventMigrationPlan BuildPlan()
        {
            var nodes = _nodes.ListAllForOwnerType(ClubEventRecord.OwnerClub)
                .Concat(_nodes.ListAllForOwnerType(ClubEventRecord.OwnerRegion)).ToList();
            HashSet<int> moved;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                try
                {
                    moved = scope.Database.Fetch<int>("SELECT LegacyEventNodeId FROM dbo.ClubTraining WHERE LegacyEventNodeId IS NOT NULL").ToHashSet();
                }
                catch { moved = new HashSet<int>(); }
            }
            return ClubEventMigrationPlanner.Plan(nodes, moved, s => _shortStrings.CleanStringForUrlSegment(s));
        }

        public object Preview()
        {
            var missing = MissingSchema();
            var plan = BuildPlan();
            int inTable = 0;
            if (missing.Count == 0)
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                inTable = scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ClubEvent");
            }
            return new
            {
                store = CurrentStore,
                writeFrozen = IsWriteFrozen,
                nodes = plan.Nodes,
                rowsToWrite = plan.Rows.Count,
                skippedMovedToTraining = plan.SkippedMovedToTraining,
                slugCollisions = plan.SlugCollisions,
                unpublished = plan.Unpublished,
                withLegacyUrl = plan.Rows.Count(r => r.LegacySlug.Length > 0),
                withLandingPage = plan.Rows.Count(r => HasBlocks(r.Row)),
                rowsAlreadyInTable = inTable,
                missingSchema = missing,
                notes = plan.Notes.Take(100)
            };
        }

        private static bool HasBlocks(ClubEventRecord r)
            => (!string.IsNullOrWhiteSpace(r.ContentBlocksJson) && r.ContentBlocksJson.Trim() != "[]")
               || (!string.IsNullOrWhiteSpace(r.QuickLinksJson) && r.QuickLinksJson.Trim() != "[]");

        public (bool Ok, string Message, int Inserted, int Skipped, int LegacyUrls, int Reseed, List<string> Errors) Apply()
        {
            if (!IsWriteFrozen) return (false, "Frys händelseskrivningarna först (Events:WriteFreeze).", 0, 0, 0, 0, new());
            if (!string.Equals(CurrentStore, ClubEventStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
                return (false, "Tabellagringen gäller redan — flytten körs bara från nodlagringen.", 0, 0, 0, 0, new());
            var missing = MissingSchema();
            if (missing.Count > 0) return (false, "Tabellerna saknas: " + string.Join(", ", missing), 0, 0, 0, 0, new());

            var plan = BuildPlan();
            int inserted = 0, skipped = 0, legacy = 0;
            var errors = new List<string>();
            foreach (var group in plan.Rows.GroupBy(r => r.Row.OwnerId).OrderBy(g => g.Key))
            {
                try
                {
                    using var scope = _scopeProvider.CreateScope();
                    var db = scope.Database;
                    int gi = 0, gs = 0, gl = 0;
                    foreach (var p in group)
                    {
                        var r = p.Row;
                        if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ClubEvent WHERE Id = @0", r.Id) > 0) { gs++; continue; }

                        int? pageId = null;
                        if (HasBlocks(r))
                            pageId = db.ExecuteScalar<int>(@"INSERT INTO dbo.LandingPage (OwnerType, OwnerId, ContentBlocksJson, QuickLinksJson) VALUES (@0, @1, @2, @3);
SELECT CAST(SCOPE_IDENTITY() AS INT);", r.OwnerType, r.OwnerId, Blank(r.ContentBlocksJson), Blank(r.QuickLinksJson));

                        db.Execute("SET IDENTITY_INSERT dbo.ClubEvent ON");
                        try
                        {
                            db.Execute(@"
INSERT INTO dbo.ClubEvent
    (Id, OwnerType, OwnerId, Name, PageName, Slug, UrlYear, StartsAt, EndsAt, EventType, Venue, Description, ImageRef,
     ContactPerson, ContactEmail, ContactPhone, EquipmentRequired, TargetAudience, IsActive, IsPublished,
     RegistrationRequired, RegistrationUrl, MaxParticipants, RegistrationDeadline, IsMandatory, LanevapenOffered,
     PricesJson, Audience, SwishNumber, LandingPageId, CreatedDate, UpdatedDate)
VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19,@20,@21,@22,@23,@24,@25,@26,@27,@28,@29,@30,@31)",
                                r.Id, r.OwnerType, r.OwnerId, r.Name ?? "", r.PageName ?? "", p.Slug, p.UrlYear, r.StartsAt, r.EndsAt,
                                r.EventType ?? "", Blank(r.Venue), Blank(r.Description), Blank(r.ImageRef), Blank(r.ContactPerson),
                                Blank(r.ContactEmail), Blank(r.ContactPhone), Blank(r.EquipmentRequired), Blank(r.TargetAudience),
                                r.IsActive, r.IsPublished, r.RegistrationRequired, Blank(r.RegistrationUrl), Math.Max(0, r.MaxParticipants),
                                r.RegistrationDeadline?.Date, r.IsMandatory, r.LanevapenOffered, Blank(r.PricesJson),
                                EventAudience.Normalise(r.Audience), Blank(r.SwishNumber), pageId,
                                r.CreatedDate == default ? DateTime.Now : r.CreatedDate,
                                r.UpdatedDate == default ? DateTime.Now : r.UpdatedDate);
                        }
                        finally
                        {
                            db.Execute("SET IDENTITY_INSERT dbo.ClubEvent OFF");
                        }

                        db.Execute("INSERT INTO dbo.ClubEventUrl (OwnerType, OwnerId, UrlYear, Slug, EventId, IsCurrent) VALUES (@0, @1, @2, @3, @4, 1)",
                            r.OwnerType, r.OwnerId, p.UrlYear, p.Slug, r.Id);
                        if (p.LegacySlug.Length > 0
                            && db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ClubEventUrl WHERE OwnerId = @0 AND UrlYear IS NULL AND Slug = @1", r.OwnerId, p.LegacySlug) == 0)
                        {
                            db.Execute("INSERT INTO dbo.ClubEventUrl (OwnerType, OwnerId, UrlYear, Slug, EventId, IsCurrent) VALUES (@0, @1, NULL, @2, @3, 0)",
                                r.OwnerType, r.OwnerId, p.LegacySlug, r.Id);
                            gl++;
                        }
                        gi++;
                    }
                    scope.Complete();
                    inserted += gi; skipped += gs; legacy += gl;
                }
                catch (Exception ex)
                {
                    errors.Add($"Ägare {group.Key}: {ex.Message}");
                    _logger.LogError(ex, "Flytten av ägare {OwnerId}s händelser misslyckades — rullad tillbaka", group.Key);
                }
            }

            int reseed;
            using (var scope = _scopeProvider.CreateScope())
            {
                reseed = scope.Database.ExecuteScalar<int>("SELECT ISNULL(MAX(Id), 0) FROM dbo.ClubEvent");
                if (reseed > 0) scope.Database.Execute($"DBCC CHECKIDENT('dbo.ClubEvent', RESEED, {reseed})");
                scope.Complete();
            }
            _logger.LogWarning("Händelseflytten: {Inserted} skrivna, {Skipped} fanns redan, {Legacy} gamla adresser, {Errors} fel, RESEED {Reseed}",
                inserted, skipped, legacy, errors.Count, reseed);
            return (errors.Count == 0, errors.Count == 0 ? "Flytten är klar." : $"{errors.Count} ägare kunde inte flyttas — se felen. Kör om när de är åtgärdade.",
                inserted, skipped, legacy, reseed, errors);
        }

        /// <summary>Jämför noderna med tabellen fält för fält, och att den gamla adressen leder rätt. Läser bara.</summary>
        public (int Compared, List<string> Problems) Verify()
        {
            var plan = BuildPlan();
            var problems = new List<string>();
            foreach (var p in plan.Rows)
            {
                var n = p.Row;
                var t = _table.Get(n.Id);
                if (t == null) { problems.Add($"{n.Id}: saknas i tabellen"); continue; }
                void Diff(string f, object? a, object? b) { if (!Equals(a, b)) problems.Add($"{n.Id}: {f} nod={a} rad={b}"); }
                string N(string? s) => (s ?? "").Trim();
                Diff("Ägare", $"{n.OwnerType}:{n.OwnerId}", $"{t.OwnerType}:{t.OwnerId}");
                Diff("Name", N(n.Name), N(t.Name));
                Diff("PageName", N(n.PageName), N(t.PageName));
                Diff("StartsAt", n.StartsAt?.ToString("yyyy-MM-dd HH:mm:ss"), t.StartsAt?.ToString("yyyy-MM-dd HH:mm:ss"));
                Diff("EndsAt", n.EndsAt?.ToString("yyyy-MM-dd HH:mm:ss"), t.EndsAt?.ToString("yyyy-MM-dd HH:mm:ss"));
                Diff("EventType", N(n.EventType), N(t.EventType));
                Diff("Venue", N(n.Venue), N(t.Venue));
                Diff("Description", N(n.Description), N(t.Description));
                Diff("ImageRef", N(n.ImageRef), N(t.ImageRef));
                Diff("Kontakt", $"{N(n.ContactPerson)}|{N(n.ContactEmail)}|{N(n.ContactPhone)}", $"{N(t.ContactPerson)}|{N(t.ContactEmail)}|{N(t.ContactPhone)}");
                Diff("Utrustning/målgrupp", $"{N(n.EquipmentRequired)}|{N(n.TargetAudience)}", $"{N(t.EquipmentRequired)}|{N(t.TargetAudience)}");
                Diff("IsActive", n.IsActive, t.IsActive);
                Diff("IsPublished", n.IsPublished, t.IsPublished);
                Diff("Anmälan", $"{n.RegistrationRequired}|{N(n.RegistrationUrl)}|{n.MaxParticipants}|{n.RegistrationDeadline?.Date}|{n.IsMandatory}|{n.LanevapenOffered}",
                    $"{t.RegistrationRequired}|{N(t.RegistrationUrl)}|{t.MaxParticipants}|{t.RegistrationDeadline?.Date}|{t.IsMandatory}|{t.LanevapenOffered}");
                Diff("Priser", N(n.PricesJson), N(t.PricesJson));
                Diff("Publik", n.Audience, t.Audience);
                Diff("Swish", N(n.SwishNumber), N(t.SwishNumber));
                Diff("Block", HasBlocks(n) ? N(n.ContentBlocksJson) + "|" + N(n.QuickLinksJson) : "", HasBlocks(t) ? N(t.ContentBlocksJson) + "|" + N(t.QuickLinksJson) : "");
                if (p.LegacySlug.Length > 0)
                {
                    var hit = _table.FindByAddress(n.OwnerId, null, p.LegacySlug);
                    if (hit?.EventId != n.Id) problems.Add($"{n.Id}: den gamla adressen {p.LegacySlug} leder inte hit");
                }
            }
            return (plan.Rows.Count, problems);
        }

        public (bool Ok, string Message, List<string> Problems) Switch(string store, int? byMemberId)
        {
            if (string.Equals(store, ClubEventStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Set(ClubEventStoreSettings.StoreKey, ClubEventStoreSettings.StoreNode, byMemberId);
                return (true, "Nodlagringen gäller igen.", new());
            }
            if (!string.Equals(store, ClubEventStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase)) return (false, "Okänd lagring.", new());
            if (!IsWriteFrozen) return (false, "Frys händelseskrivningarna först.", new());
            var (compared, problems) = Verify();
            if (problems.Count > 0) return (false, $"{problems.Count} avvikelser mellan noderna och tabellen — växeln slås inte om.", problems.Take(50).ToList());
            _settings.Set(ClubEventStoreSettings.StoreKey, ClubEventStoreSettings.StoreTable, byMemberId);
            return (true, $"Tabellagringen gäller nu ({compared} händelser jämförda).", new());
        }

        /// <summary>Avpublicerar högst <paramref name="batch"/> händelsenoder. Bara efter bytet; raderar ingenting.</summary>
        public (int Unpublished, int Remaining, string Message) UnpublishNodes(int batch)
        {
            if (!ClubEventStoreSettings.UsesTable(_settings))
                return (0, 0, "Avpubliceringen körs först efter bytet till tabellagringen.");
            batch = Math.Clamp(batch, 1, 200);
            List<int> published;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                published = scope.Database.Fetch<int>(@"
SELECT n.id FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias = @0
JOIN umbracoDocument d ON d.nodeId = n.id
WHERE d.published = 1 AND n.trashed = 0 ORDER BY n.id", ClubEvents.EventAlias);
            }
            int done = 0;
            foreach (var id in published.Take(batch))
            {
                try
                {
                    var node = _contentService.GetById(id);
                    if (node != null && node.Published) { _contentService.Unpublish(node); done++; }
                }
                catch (Exception ex) { _logger.LogError(ex, "Händelsenod {Id} kunde inte avpubliceras", id); }
            }
            var remaining = Math.Max(0, published.Count - done);
            return (done, remaining, remaining == 0 ? "Alla händelsenoder är avpublicerade." : $"{remaining} kvar — kör igen.");
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
