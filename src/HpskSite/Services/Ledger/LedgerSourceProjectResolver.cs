using HpskSite.Models.Ledger;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Ger en postning ur en tävling eller ett evenemang dess projekt — <b>vid första kronan</b>.
    ///
    /// <para><b>⚠️⚠️ ANROPAS FRÅN POSTNINGSVÄGEN, ALDRIG FRÅN TÄVLINGSSKAPANDET</b> (Stefans beslut
    /// 2026-09-23). En avgiftsfri tävling ska inte ha något projekt, och den som skapar tävlingen
    /// ska aldrig göra något val alls — hen kan inte veta i januari vad kassören vill se i
    /// december. Projektet föds därför när pengar faktiskt bokförs, och då finns det per
    /// definition något att visa.</para>
    ///
    /// <para><b>⚠️ Seriens grupp följer med vid FÖDSELN</b> — bara när projektet skapas nu, aldrig
    /// vid senare postningar. Har kassören tagit bort projektet ur seriegruppen ska nästa krona inte
    /// lägga tillbaka det.</para>
    ///
    /// <para><b>⚠️ ETT FEL HÄR STOPPAR ALDRIG BOKFÖRINGEN.</b> Pengarna har redan tagits emot, och en
    /// betalning som inte kan kvitteras för att ett projekt inte gick att skapa är ett värre fel än
    /// en rad utan projekt. Felet loggas som ett fel, eftersom raden är fryst och märkningen inte
    /// går att sätta i efterhand.</para>
    /// </summary>
    public class LedgerSourceProjectResolver
    {
        private readonly LedgerProjectService _projects;
        private readonly LedgerProjectGroupService _groups;
        private readonly IContentService _contentService;
        private readonly ILogger<LedgerSourceProjectResolver> _logger;

        public LedgerSourceProjectResolver(
            LedgerProjectService projects,
            LedgerProjectGroupService groups,
            IContentService contentService,
            ILogger<LedgerSourceProjectResolver> logger)
        {
            _projects = projects;
            _groups = groups;
            _contentService = contentService;
            _logger = logger;
        }

        /// <summary>
        /// Projektet för en postning ur källan, eller <c>null</c> när källan inte är en tävling
        /// eller ett evenemang — eller när det inte gick att avgöra.
        /// </summary>
        public int? Resolve(int issuerType, int issuerId, string? sourceType, int? sourceId, int byMemberId)
        {
            var source = LedgerProjectSource.For(sourceType, sourceId);
            if (source is null) return null;

            var (kind, id) = source.Value;

            try
            {
                var node = _contentService.GetById(id);

                // ⚠️ En RADERAD tävling har fortfarande pengar — det vanliga fallet är en betalning
                //    som bekräftades innan händelsen togs bort och bokförs i efterhand. Den får ett
                //    namn som SÄGER det, med id:t, samma form som översikten redan använder. Utan
                //    det hette den bara "Evenemang", och två sådana gick inte att skilja åt.
                var candidates = node is null
                    ? new List<string>
                    {
                        kind == LedgerProjectSource.Event ? $"Borttagen händelse (#{id})" : $"Borttagen tävling (#{id})"
                    }
                    : LedgerProjectSource.NameCandidates(NameOf(node, kind), DateOf(node, kind), kind, id);

                var ensured = _projects.EnsureForSource(
                    issuerType, issuerId, kind, id, candidates, byMemberId);

                if (ensured is null)
                {
                    _logger.LogError(
                        "Postningen ur {Typ}/{Id} bokförs UTAN projekt — projektet gick inte att skapa. "
                        + "Raden är fryst och kan inte märkas i efterhand.", sourceType, sourceId);
                    return null;
                }

                if (ensured.Value.Created && kind == LedgerProjectSource.Competition && node is not null)
                    AddToSeriesGroup(issuerType, issuerId, node, ensured.Value.Project.Id);

                return ensured.Value.Project.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Projektet för {Typ}/{Id} kunde inte avgöras — postningen bokförs utan projekt. "
                    + "Raden är fryst och kan inte märkas i efterhand.", sourceType, sourceId);
                return null;
            }
        }

        /// <summary>
        /// Lägger tävlingens nya projekt i seriens grupp. Serien är den gruppering som redan finns i
        /// datat — "onsdagsserien"-fallet — så ingen inställning behövs.
        /// </summary>
        private void AddToSeriesGroup(int issuerType, int issuerId, IContent competition, int projectId)
        {
            try
            {
                if (competition.ParentId <= 0) return;

                var parent = _contentService.GetById(competition.ParentId);
                if (parent is null || parent.ContentType.Alias != "competitionSeries") return;

                var seriesName = string.IsNullOrWhiteSpace(parent.Name) ? "Serie" : parent.Name!.Trim();

                // Samma namnkedja som projekten, av samma skäl: serien återkommer varje år.
                var group = _groups.EnsureSeriesGroup(issuerType, issuerId, parent.Id,
                    LedgerProjectSource.NameCandidates(seriesName, null, LedgerProjectSource.Series, parent.Id));

                if (group is null)
                {
                    _logger.LogWarning("Seriegruppen för serie {Serie} kunde inte skapas.", parent.Id);
                    return;
                }

                // 0 = lagd av systemet. Kassören kan ta bort den när som helst.
                _groups.SetMember(group.Id, projectId, true, 0);
            }
            catch (Exception ex)
            {
                // Gruppen är en bekvämlighet ovanpå projektet — inte värd att tappa projektet för.
                _logger.LogWarning(ex, "Tävlingen {Id} kunde inte läggas i sin seriegrupp.", competition.Id);
            }
        }

        private static string? NameOf(IContent node, string kind)
        {
            var name = kind == LedgerProjectSource.Event
                ? node.GetValue<string>("eventName")
                : node.GetValue<string>("competitionName");

            return string.IsNullOrWhiteSpace(name) ? node.Name : name;
        }

        /// <summary>
        /// Tävlingens eller händelsens datum. <b>Defensivt läst</b> — datumegenskaper ligger i olika
        /// former i olika delar av trädet, och ett oläsbart datum ska ge ett namn utan årtal, inte
        /// ett undantag.
        /// </summary>
        private static DateTime? DateOf(IContent node, string kind)
        {
            var raw = node.GetValue(kind == LedgerProjectSource.Event ? "eventDate" : "competitionDate");

            return raw switch
            {
                DateTime d when d.Year > 1900 => d,
                string s when DateTime.TryParse(s, out var parsed) && parsed.Year > 1900 => parsed,
                _ => null
            };
        }
    }
}
