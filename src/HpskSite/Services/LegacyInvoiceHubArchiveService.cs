using HpskSite.Services.Competitions;
using HpskSite.Services.CompetitionFees;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Flyttar den gamla betalmodellens fakturamappar från tävlingsnoderna till fakturaarkivet (C3-planen F1, §5e).
    ///
    /// <para><b>Varför före avpubliceringen:</b> Umbraco 17:s publicerade cache döljer allt under en avpublicerad
    /// förälder. Ligger mappen kvar under tävlingsnoden när den avpubliceras ser Swish-betalningen av en gammal faktura
    /// och kvittona ingen mapp (provat på kopian 2026-10-18).</para>
    ///
    /// <para><b>Vad som flyttas:</b> mappen, oförändrad och publicerad, med sina fakturor. Arkivtabellen
    /// (<see cref="LegacyInvoiceHubs.ArchiveTable"/>) skriver in vilken tävling den hörde till, FÖRE flytten —
    /// fakturornas <c>competitionId</c> räcker inte för en tom mapp, och ett avbrott mellan stegen lämnar då en mapp
    /// som hittas på båda sätten i stället för ingetdera. Betalningsmodellens rad säkras först
    /// (<see cref="CompetitionPaymentModelService.Get"/> skriver <c>legacy</c> för en tävling med fakturor).</para>
    ///
    /// <para>Arkivet är en publicerad <c>contentPage</c> "Fakturaarkiv" under tävlingsnavet — samma sort som
    /// årsmapparna. Dess id sparas i <c>SiteSetting</c>; namnet slås aldrig upp.</para>
    /// </summary>
    public class LegacyInvoiceHubArchiveService
    {
        public const string FolderName = "Fakturaarkiv";

        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly LegacyInvoiceHubs _hubs;
        private readonly CompetitionTreePlacement _placement;
        private readonly CompetitionPaymentModelService _paymentModels;
        private readonly SiteSettingService _settings;
        private readonly ILogger<LegacyInvoiceHubArchiveService> _logger;

        public LegacyInvoiceHubArchiveService(IScopeProvider scopeProvider, IContentService contentService, LegacyInvoiceHubs hubs,
            CompetitionTreePlacement placement, CompetitionPaymentModelService paymentModels, SiteSettingService settings,
            ILogger<LegacyInvoiceHubArchiveService> logger)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _hubs = hubs;
            _placement = placement;
            _paymentModels = paymentModels;
            _settings = settings;
            _logger = logger;
        }

        public sealed class HubRow
        {
            public int HubId { get; set; }
            public int CompetitionId { get; set; }
            public string CompetitionName { get; set; } = "";
            public int Invoices { get; set; }
            /// <summary>Fakturor som inte bär mappens tävling i <c>competitionId</c> — arkivtabellen bär den åt dem.</summary>
            public int InvoicesWithoutCompetitionId { get; set; }
        }

        public sealed class PreviewResult
        {
            public bool TableExists { get; init; }
            public int ArchiveFolderId { get; init; }
            public int AlreadyArchived { get; init; }
            public List<HubRow> ToMove { get; init; } = new();
        }

        private List<HubRow> HubsUnderCompetitions()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<HubRow>(@"
SELECT h.id AS HubId, p.id AS CompetitionId, ISNULL(p.text, '') AS CompetitionName,
       (SELECT COUNT(*) FROM umbracoNode i WHERE i.parentId = h.id AND i.trashed = 0) AS Invoices,
       (SELECT COUNT(*) FROM umbracoNode i WHERE i.parentId = h.id AND i.trashed = 0 AND NOT EXISTS (
            SELECT 1 FROM umbracoContentVersion cv
            JOIN umbracoPropertyData pd ON pd.versionId = cv.id
            JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
            WHERE cv.nodeId = i.id AND cv.[current] = 1 AND pt.alias = 'competitionId' AND pd.intValue = p.id)) AS InvoicesWithoutCompetitionId
FROM umbracoNode h
JOIN umbracoContent c ON c.nodeId = h.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
JOIN umbracoNode p ON p.id = h.parentId
JOIN umbracoContent pc ON pc.nodeId = p.id
JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId
WHERE ct.alias = @0 AND pct.alias = @1 AND h.trashed = 0
ORDER BY h.id", LegacyInvoiceHubs.HubAlias, LegacyInvoiceHubs.CompetitionAlias);
        }

        public PreviewResult Preview()
        {
            bool exists;
            int archived = 0;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                exists = _hubs.ArchiveTableExists(scope.Database);
                if (exists) archived = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {LegacyInvoiceHubs.ArchiveTable}");
            }
            return new PreviewResult
            {
                TableExists = exists,
                ArchiveFolderId = _hubs.ArchiveFolderId(),
                AlreadyArchived = archived,
                ToMove = HubsUnderCompetitions(),
            };
        }

        /// <summary>Arkivets nod, skapad och publicerad under tävlingsnavet om den saknas.</summary>
        public int EnsureFolder(int? byMemberId)
        {
            var id = _hubs.ArchiveFolderId();
            if (id > 0)
            {
                var existing = _contentService.GetById(id);
                if (existing != null && !existing.Trashed) return id;
            }
            var hub = _placement.Hub() ?? throw new InvalidOperationException("Tävlingsnavet (Competitions) finns inte.");
            var folder = _contentService.Create(FolderName, hub.Id, "contentPage");
            if (!_contentService.Save(folder).Success)
                throw new InvalidOperationException("Fakturaarkivet kunde inte skapas.");
            if (!_contentService.Publish(folder, new[] { "*" }, -1).Success)
                throw new InvalidOperationException("Fakturaarkivet kunde inte publiceras — mapparna hade då inte synts för betalningen.");
            _settings.Set(LegacyInvoiceHubs.ArchiveFolderSettingKey, folder.Id.ToString(), byMemberId);
            _logger.LogWarning("Fakturaarkivet skapat som nod {FolderId} under tävlingsnavet {HubId}.", folder.Id, hub.Id);
            return folder.Id;
        }

        public sealed class RunResult
        {
            public bool Ok { get; init; }
            public string Message { get; init; } = "";
            public int Moved { get; init; }
            public int Remaining { get; init; }
            public List<string> Errors { get; init; } = new();
        }

        /// <summary>Flyttar högst <paramref name="batch"/> mappar. Tål att köras om — det som redan flyttats ligger inte kvar under en tävling.</summary>
        public RunResult Run(int batch, int? byMemberId)
        {
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                if (!_hubs.ArchiveTableExists(scope.Database))
                    return new RunResult { Message = $"Tabellen {LegacyInvoiceHubs.ArchiveTable} saknas — kör Migrations/create-competition-tables.sql." };

            int folderId;
            try { folderId = EnsureFolder(byMemberId); }
            catch (InvalidOperationException ex) { return new RunResult { Message = ex.Message }; }

            batch = Math.Clamp(batch, 1, 200);
            var rows = HubsUnderCompetitions();
            var errors = new List<string>();
            int moved = 0;
            foreach (var row in rows.Take(batch))
            {
                try
                {
                    // Betalningsmodellens rad först: för en tävling med gamla fakturor skrivs "legacy" om raden saknas,
                    // och raden är oföränderlig — den får aldrig bero på var mappen ligger. Egen anslutning, inget scope öppet.
                    _paymentModels.Get(row.CompetitionId);

                    using (var scope = _scopeProvider.CreateScope())
                    {
                        scope.Database.Execute($@"
IF NOT EXISTS (SELECT 1 FROM {LegacyInvoiceHubs.ArchiveTable} WHERE HubId = @0)
    INSERT INTO {LegacyInvoiceHubs.ArchiveTable} (HubId, CompetitionId, OldParentId, MovedUtc, MovedBy) VALUES (@0, @1, @1, GETUTCDATE(), @2)",
                            row.HubId, row.CompetitionId, byMemberId);
                        scope.Complete();
                    }

                    var hub = _contentService.GetById(row.HubId);
                    if (hub == null) { errors.Add($"Mapp {row.HubId}: finns inte"); continue; }
                    var result = _contentService.Move(hub, folderId, -1);
                    if (!result.Success) { errors.Add($"Mapp {row.HubId} ({row.CompetitionName}): flytten gick inte igenom"); continue; }
                    moved++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fakturamapp {HubId} (tävling {CompetitionId}) kunde inte flyttas till arkivet", row.HubId, row.CompetitionId);
                    errors.Add($"Mapp {row.HubId} ({row.CompetitionName}): {ex.Message}");
                }
            }
            var remaining = HubsUnderCompetitions().Count;
            if (moved > 0)
                _logger.LogWarning("Fakturaarkivet: {Moved} mappar flyttade, {Remaining} kvar under tävlingarna, {Failed} fel.", moved, remaining, errors.Count);
            return new RunResult
            {
                Ok = errors.Count == 0,
                Moved = moved,
                Remaining = remaining,
                Errors = errors,
                Message = remaining == 0 && errors.Count == 0
                    ? "Alla fakturamappar ligger i arkivet."
                    : $"{moved} flyttade, {remaining} kvar{(errors.Count > 0 ? $", {errors.Count} fel" : "")}.",
            };
        }
    }
}
