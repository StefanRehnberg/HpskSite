using HpskSite.Models;
using HpskSite.Services.Addresses;
using HpskSite.Services.Competitions;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// <b>Raderar de gamla händelsenoderna (<c>clubSimpleEvent</c>) för gott</b> (C3-planen §5.5 punkt 7, samma mönster
    /// som <see cref="CompetitionNodeDeletionService"/>). Händelserna står på <c>dbo.ClubEvent</c> sedan 2026-10-11 och
    /// noderna är avpublicerade. Spärrarna i <see cref="Blockers"/> prövas före varje omgång: tabellen gäller, ingen
    /// frysning, inga publicerade noder, och varje nod finns i <c>dbo.ClubEvent</c> — eller flyttades till en träning
    /// (<c>ClubTraining.LegacyEventNodeId</c>; de fanns aldrig i händelsetabellen och nås inte i tabelläget ändå).
    /// Noder i papperskorgen spärrar inte: händelsen var borttagen av en användare före flytten och flyttades därför
    /// inte (dev 2026-10-19: "Höststädning", 10912, med två anmälda). De arkiveras och raderas som de andra.
    ///
    /// <para>Före raderingen skrivs varje nod i arkivet (<c>CompetitionNodeArchive</c>, med sin sort) och Umbracos
    /// lagrade omdirigeringar kopieras till <c>dbo.LegacyRedirect</c>. Deltagare, lånevapen, liggaren och kopplingarna
    /// bär händelsens id i egna tabeller och rörs inte.</para>
    /// </summary>
    public class ClubEventNodeDeletionService
    {
        private readonly IScopeProvider _scopes;
        private readonly SiteSettingService _settings;
        private readonly LegacyRedirectService _redirects;
        private readonly IContentService _content;
        private readonly ILogger<ClubEventNodeDeletionService> _logger;

        public ClubEventNodeDeletionService(IScopeProvider scopes, SiteSettingService settings, LegacyRedirectService redirects,
            IContentService content, ILogger<ClubEventNodeDeletionService> logger)
        {
            _scopes = scopes;
            _settings = settings;
            _redirects = redirects;
            _content = content;
            _logger = logger;
        }

        public sealed class State
        {
            public int Events { get; init; }
            public int MovedToTraining { get; init; }
            public int Trashed { get; init; }
            public int Descendants { get; init; }
            public List<string> Blockers { get; init; } = new();
        }

        public State Preview()
        {
            var blockers = Blockers();
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            return new State
            {
                Events = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM umbracoNode n {OfType}", ClubEvents.EventAlias),
                MovedToTraining = TrainingTableExists(db)
                    ? db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM umbracoNode n {OfType}
                        AND EXISTS (SELECT 1 FROM dbo.ClubTraining t WHERE t.LegacyEventNodeId = n.id)", ClubEvents.EventAlias)
                    : 0,
                Trashed = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM umbracoNode n {OfType} AND n.trashed = 1", ClubEvents.EventAlias),
                Descendants = db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM umbracoNode d WHERE EXISTS (
                    SELECT 1 FROM umbracoNode n {OfType} AND d.path LIKE n.path + ',%')", ClubEvents.EventAlias),
                Blockers = blockers,
            };
        }

        public List<string> Blockers()
        {
            var blockers = new List<string>();
            string store, frozen;
            try
            {
                store = _settings.GetFresh(ClubEventStoreSettings.StoreKey, ClubEventStoreSettings.StoreNode);
                frozen = _settings.GetFresh(ClubEventStoreSettings.WriteFreezeKey, "0");
            }
            catch (SiteSettingUnavailableException)
            {
                return new List<string> { "Växeln kunde inte läsas — försök igen." };
            }
            if (!string.Equals(store, ClubEventStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase))
                blockers.Add("Händelserna står på noderna — flytta dem till tabellen först.");
            if (frozen != "0") blockers.Add("Händelserna är frysta — släpp frysningen först.");
            if (!_redirects.TableExists())
                blockers.Add("dbo.LegacyRedirect saknas — kör Migrations/create-legacy-redirect-table.sql, annars försvinner de gamla omdirigeringarna med noderna.");

            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var published = db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM umbracoNode n {OfType} AND n.trashed = 0
                AND EXISTS (SELECT 1 FROM umbracoDocument d WHERE d.nodeId = n.id AND d.published = 1)", ClubEvents.EventAlias);
            if (published > 0) blockers.Add($"{published} händelsenoder är publicerade — avpublicera dem först (Händelser → 3).");

            var training = TrainingTableExists(db) ? "OR EXISTS (SELECT 1 FROM dbo.ClubTraining t WHERE t.LegacyEventNodeId = n.id)" : "";
            // Papperskorgens noder flyttades aldrig (en användare hade tagit bort händelsen) — de arkiveras och raderas.
            var missing = db.Fetch<int>($@"SELECT n.id FROM umbracoNode n {OfType} AND n.trashed = 0
                AND NOT EXISTS (SELECT 1 FROM dbo.ClubEvent e WHERE e.Id = n.id) {(training.Length > 0 ? "AND NOT (1 = 0 " + training + ")" : "")}
                ORDER BY n.id", ClubEvents.EventAlias);
            if (missing.Count > 0)
                blockers.Add($"{missing.Count} händelsenoder finns varken i händelsetabellen eller bland de flyttade träningarna: "
                    + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? " …" : "") + ". Raderas inte förrän de är utredda.");

            var under = db.Fetch<OtherRow>($@"SELECT ct.alias AS Alias, COUNT(*) AS Count FROM umbracoNode d
                JOIN umbracoContent c ON c.nodeId = d.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                WHERE EXISTS (SELECT 1 FROM umbracoNode n {OfType} AND d.path LIKE n.path + ',%')
                GROUP BY ct.alias", ClubEvents.EventAlias);
            if (under.Count > 0)
                blockers.Add("Under händelserna ligger andra noder: " + string.Join(", ", under.Select(o => $"{o.Count} {o.Alias}"))
                    + ". Raderas inte förrän de är utredda.");
            return blockers;
        }

        private sealed class OtherRow
        {
            public string Alias { get; set; } = "";
            public int Count { get; set; }
        }

        /// <summary>En omgång: arkiverar och raderar händelsenoder tills tidsgränsen eller antalet nås.</summary>
        public (bool Ok, int Deleted, int Remaining, string Message, List<string> Errors) Run(int batch, TimeSpan budget)
        {
            var blockers = Blockers();
            if (blockers.Count > 0) return (false, 0, 0, "Raderingen vägras: " + string.Join(" ", blockers), new List<string>());
            var copied = _redirects.CopyFromUmbraco();
            if (copied > 0) _logger.LogWarning("Raderingen av händelsenoderna: {Copied} omdirigeringar kopierades först.", copied);

            List<int> queue;
            using (var s = _scopes.CreateScope(autoComplete: true))
                queue = s.Database.Fetch<int>($"SELECT n.id FROM umbracoNode n {OfType} ORDER BY n.level DESC, n.id", ClubEvents.EventAlias);

            var started = DateTime.UtcNow;
            int deleted = 0;
            var errors = new List<string>();
            foreach (var id in queue.Take(Math.Clamp(batch, 1, 200)))
            {
                if (DateTime.UtcNow - started > budget) break;
                try
                {
                    var node = _content.GetById(id);
                    if (node == null) continue;
                    using (var s = _scopes.CreateScope())
                    {
                        CompetitionMigrationService.ArchiveNode(s.Database, node, DateTime.UtcNow);
                        s.Complete();
                    }
                    var r = _content.Delete(node, Umbraco.Cms.Core.Constants.Security.SuperUserId);
                    if (r.Success) deleted++;
                    else errors.Add($"{id}: vägrades ({r.Result})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Händelsenod {Id} kunde inte raderas", id);
                    errors.Add($"{id}: {ex.Message}");
                }
            }
            int remaining;
            using (var s = _scopes.CreateScope(autoComplete: true))
                remaining = s.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM umbracoNode n {OfType}", ClubEvents.EventAlias);
            _logger.LogWarning("Raderingen av händelsenoderna: {Deleted} raderade, {Remaining} kvar, {Errors} fel.", deleted, remaining, errors.Count);
            var message = remaining == 0 ? "Alla händelsenoder är raderade."
                : errors.Count > 0 && deleted == 0 ? "Ingenting kunde raderas — se felen."
                : $"{remaining} kvar — kör igen.";
            return (errors.Count == 0 || deleted > 0, deleted, remaining, message, errors);
        }

        /// <summary>FROM-tillägg: noden <c>n</c> är av sorten @0.</summary>
        private const string OfType = @"JOIN umbracoContent c0 ON c0.nodeId = n.id JOIN cmsContentType ct0 ON ct0.nodeId = c0.contentTypeId
            WHERE ct0.alias = @0";

        private static bool TrainingTableExists(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db)
            => db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.ClubTraining', 'U') IS NULL THEN 0 ELSE 1 END") == 1;
    }
}
