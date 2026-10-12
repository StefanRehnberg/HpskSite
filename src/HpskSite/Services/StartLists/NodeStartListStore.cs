using HpskSite.Models.StartLists;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// Startlistorna som <c>precisionStartList</c>/<c>finalsStartList</c>-noder — dagens lagring, och den som
    /// gäller tills listorna flyttats till tabellen (skalplanen fas C2).
    ///
    /// <para><b>Hubben:</b> äldre tävlingar har sina listor under en <c>competitionStartListsHub</c>. De räknas
    /// som tävlingens listor (efter de direkta), med tävlingen = hubbens förälder.</para>
    ///
    /// <para>Skrivningarna sparar och publicerar efter <see cref="StartListPublish"/> — de publika sidorna,
    /// spärren för anmälan och Mitt schema läser den publicerade cachen.</para>
    /// </summary>
    public class NodeStartListStore : IStartListStore
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<NodeStartListStore> _logger;
        private readonly IShortStringHelper _shortStrings;

        public NodeStartListStore(IContentService contentService, IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<NodeStartListStore> logger, IShortStringHelper shortStrings)
        {
            _contentService = contentService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
            _shortStrings = shortStrings;
        }

        /// <summary>Segmentet ur namnet — samma regel som Umbraco använder för nodens adress.</summary>
        public static string Segment(IShortStringHelper shortStrings, string? name) =>
            string.IsNullOrWhiteSpace(name) ? "" : shortStrings.CleanStringForUrlSegment(name);

        private StartListRecord WithSegment(StartListRecord r)
        {
            if (string.IsNullOrEmpty(r.UrlSegment)) r.UrlSegment = Segment(_shortStrings, r.Name);
            return r;
        }

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<StartListRecord> List(int competitionId, StartListKind? kind = null)
        {
            if (competitionId <= 0) return Array.Empty<StartListRecord>();
            var result = new List<StartListRecord>();
            var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out _).ToList();
            foreach (var c in children)
            {
                var k = StartListNodes.KindOf(c.ContentType.Alias);
                if (k != null && (kind == null || k == kind)) result.Add(WithSegment(FromContent(c, competitionId, legacyHub: false)));
            }
            foreach (var hub in children.Where(c => c.ContentType.Alias == StartListNodes.LegacyHubAlias))
            {
                foreach (var c in _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _))
                {
                    var k = StartListNodes.KindOf(c.ContentType.Alias);
                    if (k != null && (kind == null || k == kind)) result.Add(WithSegment(FromContent(c, competitionId, legacyHub: true)));
                }
            }
            return result;
        }

        public IReadOnlyList<StartListRecord> ListPublished(int competitionId, StartListKind? kind = null)
        {
            if (competitionId <= 0) return Array.Empty<StartListRecord>();
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return List(competitionId, kind).Where(r => r.IsPublished).ToList();
            var comp = ctx.Content.GetById(competitionId);
            if (comp == null) return Array.Empty<StartListRecord>();

            var result = new List<StartListRecord>();
            var children = comp.Children().ToList();
            foreach (var c in children)
            {
                var k = StartListNodes.KindOf(c.ContentType.Alias);
                if (k != null && (kind == null || k == kind)) result.Add(FromPublished(c, competitionId, k.Value, legacyHub: false));
            }
            foreach (var hub in children.Where(c => c.ContentType.Alias == StartListNodes.LegacyHubAlias))
            {
                foreach (var c in hub.Children())
                {
                    var k = StartListNodes.KindOf(c.ContentType.Alias);
                    if (k != null && (kind == null || k == kind)) result.Add(FromPublished(c, competitionId, k.Value, legacyHub: true));
                }
            }
            return result;
        }

        public StartListRecord? Get(int startListId)
        {
            if (startListId <= 0) return null;
            var node = _contentService.GetById(startListId);
            if (node == null || StartListNodes.KindOf(node.ContentType.Alias) == null) return null;
            var (competitionId, legacyHub) = CompetitionOf(node);
            return WithSegment(FromContent(node, competitionId, legacyHub));
        }

        public StartListRecord? Primary(int competitionId)
        {
            var qual = List(competitionId, StartListKind.Qualifying);
            return qual.FirstOrDefault(r => !r.LegacyHub)
                ?? qual.Where(r => r.LegacyHub).OrderByDescending(r => r.GeneratedDate ?? DateTime.MinValue).FirstOrDefault();
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public StartListRecord Create(int competitionId, StartListKind kind, string name, Action<StartListRecord> init,
            StartListPublish publish = StartListPublish.Always)
        {
            if (_contentService.GetById(competitionId) is null)
                throw new InvalidOperationException($"Tävling {competitionId} finns inte.");
            var node = _contentService.Create(string.IsNullOrWhiteSpace(name) ? StartListNodes.QualifyingName : name,
                competitionId, StartListNodes.Alias(kind));
            var before = FromContent(node, competitionId, legacyHub: false);
            var after = before.Clone();
            after.Kind = kind;
            after.CompetitionId = competitionId;
            init(after);
            Write(node, before, after, all: true);
            SaveAndPublish(node, publish, wasPublished: false);
            return WithSegment(FromContent(node, competitionId, legacyHub: false));
        }

        public StartListRecord? Update(int startListId, Action<StartListRecord> change, StartListPublish publish = StartListPublish.Always)
        {
            var node = startListId > 0 ? _contentService.GetById(startListId) : null;
            if (node == null || StartListNodes.KindOf(node.ContentType.Alias) == null) return null;
            var (competitionId, legacyHub) = CompetitionOf(node);
            var before = FromContent(node, competitionId, legacyHub);
            var after = before.Clone();
            change(after);
            var wasPublished = node.Published;
            if (!Write(node, before, after, all: false))
            {
                // Inget ändrat — men ett utkast som SKA publiceras publiceras ändå (förut sparade och
                // publicerade varje skrivare villkorslöst). Edited = utkastet skiljer sig från det
                // publicerade, t.ex. efter en tidigare skrivning med StartListPublish.Never.
                if (publish == StartListPublish.Always && (!wasPublished || node.Edited)) SaveAndPublish(node, publish, wasPublished);
                return WithSegment(before);
            }
            SaveAndPublish(node, publish, wasPublished);
            return WithSegment(FromContent(node, competitionId, legacyHub));
        }

        public bool Delete(int startListId)
        {
            var node = startListId > 0 ? _contentService.GetById(startListId) : null;
            if (node == null || StartListNodes.KindOf(node.ContentType.Alias) == null) return false;
            if (node.Published) _contentService.Unpublish(node);
            _contentService.Delete(node);
            return true;
        }

        // ── Hjälpare ───────────────────────────────────────────────────────────────────────

        private (int CompetitionId, bool LegacyHub) CompetitionOf(IContent node)
        {
            var parent = _contentService.GetById(node.ParentId);
            if (parent?.ContentType.Alias == StartListNodes.LegacyHubAlias) return (parent.ParentId, true);
            return (node.ParentId, false);
        }

        private string OfficialAlias(StartListKind kind) =>
            kind == StartListKind.Finals ? "isOfficialFinalsStartList" : "isOfficialStartList";

        /// <summary>Skriver ändrade fält till noden. Returnerar om något ändrades.</summary>
        private bool Write(IContent node, StartListRecord before, StartListRecord after, bool all)
        {
            bool changed = false;
            void Set(string alias, object? oldV, object? newV)
            {
                if (!all && Equals(oldV, newV)) return;
                if (!node.HasProperty(alias))
                {
                    if (!all && newV is string s && s.Length > 0)
                        _logger.LogWarning("Startlistan {Id} saknar egenskapen {Alias} — värdet sparades inte.", node.Id, alias);
                    return;
                }
                node.SetValue(alias, newV);
                changed = true;
            }
            var kind = StartListNodes.KindOf(node.ContentType.Alias) ?? after.Kind;
            Set("configurationData", before.ConfigurationData, after.ConfigurationData ?? "");
            Set("startListContent", before.StartListContent, after.StartListContent ?? "");
            Set(OfficialAlias(kind), before.IsOfficial, after.IsOfficial);
            if (all || before.GeneratedDate != after.GeneratedDate) Set("generatedDate", null, after.GeneratedDate);
            Set("generatedBy", before.GeneratedBy, after.GeneratedBy ?? "");
            Set("teamFormat", before.TeamFormat, after.TeamFormat ?? "");
            Set("notes", before.Notes, after.Notes ?? "");
            Set("qualifyingResultsSnapshot", before.QualifyingResultsSnapshot, after.QualifyingResultsSnapshot ?? "");
            Set("perClassConfigData", before.PerClassConfigData, after.PerClassConfigData ?? "");
            Set("totalFinalists", before.TotalFinalists, after.TotalFinalists);
            Set("maxShootersPerTeam", before.MaxShootersPerTeam, after.MaxShootersPerTeam);
            Set("qualificationStartListId", before.QualificationStartListId, after.QualificationStartListId);
            // Nodens eget competitionId-fält — speglar tävlingen, som många äldre läsare förväntar sig.
            if (all && node.HasProperty("competitionId")) { node.SetValue("competitionId", after.CompetitionId); changed = true; }
            if (!string.IsNullOrWhiteSpace(after.Name) && after.Name != before.Name) { node.Name = after.Name; changed = true; }
            return changed || all;
        }

        private void SaveAndPublish(IContent node, StartListPublish publish, bool wasPublished)
        {
            var save = _contentService.Save(node);
            if (!save.Success)
            {
                _logger.LogWarning("Startlistan {Id} kunde inte sparas: {Result}", node.Id, save.Result);
                return;
            }
            if (publish == StartListPublish.Never) return;
            if (publish == StartListPublish.IfPublished && !wasPublished) return;
            try
            {
                var p = _contentService.Publish(node, new[] { "*" }, -1);
                if (!p.Success)
                    _logger.LogWarning("Startlistan {Id} sparades men publicerades inte: {Result}", node.Id, p.Result);
            }
            catch (Exception ex)
            {
                // Publiceringen är en sidoeffekt — sparningen har gått igenom. Loggas, sväljs inte tyst.
                _logger.LogWarning(ex, "Startlistan {Id} sparades men publiceringen kastade.", node.Id);
            }
        }

        private static StartListRecord FromContent(IContent node, int competitionId, bool legacyHub)
        {
            var kind = StartListNodes.KindOf(node.ContentType.Alias) ?? StartListKind.Qualifying;
            return new StartListRecord
            {
                Id = node.Id,
                CompetitionId = competitionId,
                Kind = kind,
                Name = node.Name ?? "",
                ConfigurationData = Str(node, "configurationData"),
                StartListContent = Str(node, "startListContent"),
                IsOfficial = Bool(node.HasProperty(kind == StartListKind.Finals ? "isOfficialFinalsStartList" : "isOfficialStartList")
                    ? node.GetValue(kind == StartListKind.Finals ? "isOfficialFinalsStartList" : "isOfficialStartList") : null),
                GeneratedDate = Date(node.HasProperty("generatedDate") ? node.GetValue("generatedDate") : null),
                GeneratedBy = Str(node, "generatedBy"),
                TeamFormat = Str(node, "teamFormat"),
                Notes = Str(node, "notes"),
                QualifyingResultsSnapshot = Str(node, "qualifyingResultsSnapshot"),
                PerClassConfigData = Str(node, "perClassConfigData"),
                TotalFinalists = Int(node.HasProperty("totalFinalists") ? node.GetValue("totalFinalists") : null),
                MaxShootersPerTeam = Int(node.HasProperty("maxShootersPerTeam") ? node.GetValue("maxShootersPerTeam") : null),
                QualificationStartListId = Int(node.HasProperty("qualificationStartListId") ? node.GetValue("qualificationStartListId") : null),
                LegacyHub = legacyHub,
                IsPublished = node.Published,
                SortOrder = node.SortOrder,
            };
        }

        private static StartListRecord FromPublished(IPublishedContent p, int competitionId, StartListKind kind, bool legacyHub)
        {
            string S(string a) { try { return p.Value<string>(a) ?? ""; } catch { return p.Value(a)?.ToString() ?? ""; } }
            object? O(string a) { try { return p.Value(a); } catch { return null; } }
            return new StartListRecord
            {
                Id = p.Id,
                CompetitionId = competitionId,
                Kind = kind,
                Name = p.Name ?? "",
                ConfigurationData = S("configurationData"),
                StartListContent = S("startListContent"),
                IsOfficial = Bool(O(kind == StartListKind.Finals ? "isOfficialFinalsStartList" : "isOfficialStartList")),
                GeneratedDate = Date(O("generatedDate")),
                GeneratedBy = S("generatedBy"),
                TeamFormat = S("teamFormat"),
                Notes = S("notes"),
                QualifyingResultsSnapshot = S("qualifyingResultsSnapshot"),
                PerClassConfigData = S("perClassConfigData"),
                TotalFinalists = Int(O("totalFinalists")),
                MaxShootersPerTeam = Int(O("maxShootersPerTeam")),
                QualificationStartListId = Int(O("qualificationStartListId")),
                LegacyHub = legacyHub,
                IsPublished = true,
                SortOrder = p.SortOrder,
                UrlSegment = p.UrlSegment ?? "",
            };
        }

        private static string Str(IContent node, string alias)
        {
            if (!node.HasProperty(alias)) return "";
            try { return node.GetValue<string>(alias) ?? ""; } catch { return node.GetValue(alias)?.ToString() ?? ""; }
        }

        private static bool Bool(object? v) => v switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        private static int Int(object? v) => v switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            decimal d => (int)d,
            string s when int.TryParse(s, out var n) => n,
            _ => 0,
        };

        /// <summary>DateTime.MinValue är Umbracos "inget värde" — läs det som null.</summary>
        private static DateTime? Date(object? v)
        {
            DateTime? d = v switch
            {
                DateTime dt => dt,
                string s when DateTime.TryParse(s, out var p) => p,
                _ => null,
            };
            return d is null || d.Value == DateTime.MinValue || d.Value.Year < 1900 ? null : d;
        }
    }
}
