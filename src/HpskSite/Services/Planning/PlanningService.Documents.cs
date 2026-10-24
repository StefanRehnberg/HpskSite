using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Planeringens dokumentarkiv (2026-10-23): kategorier, dokument (länk eller fil) och var de är
    /// bifogade — ett meddelande, ett möte, en punkt att prata om, en uppgift, ett område. Ett dokument
    /// bor i arkivet och kan bifogas på flera ställen; att ta bort en bifogning rör aldrig dokumentet.
    ///
    /// <para>Alla som är med i planeringen läser alla dokument (Stefan: inga behörighetsnivåer per
    /// dokument). Behörigheten prövas av anroparen (<see cref="PlanningAccess"/>); varje id prövas här
    /// mot planeringen.</para>
    /// </summary>
    public partial class PlanningService
    {
        public const int MaxCategoryName = 200;
        public const int MaxDocDescription = 1000;

        // =====================================================================================
        //  Kategorier
        // =====================================================================================

        public List<PlanningDocumentCategoryRow> DocumentCategories(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningDocumentCategoryRow>(
                "SELECT * FROM PlanningDocumentCategory WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY SortOrder, Id", planningId);
        }

        /// <summary>Ny kategori (id 0) eller nytt namn. Svarar kategorins id.</summary>
        public (int Id, string? Error) SaveDocumentCategory(int planningId, int categoryId, string? name, int byParticipantId)
        {
            var n = Clean(name, MaxCategoryName);
            if (n == null) return (0, "Skriv vad kategorin heter.");
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var others = db.Fetch<PlanningDocumentCategoryRow>(
                "SELECT * FROM PlanningDocumentCategory WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId);
            if (others.Any(c => c.Id != categoryId && string.Equals(c.Name, n, StringComparison.CurrentCultureIgnoreCase)))
                return (0, $"Det finns redan en kategori som heter {n}.");
            if (categoryId > 0)
            {
                var row = others.FirstOrDefault(c => c.Id == categoryId);
                if (row == null) return (0, "Kategorin finns inte.");
                row.Name = n;
                db.Update(row);
                Bump(db, planningId);
                return (row.Id, null);
            }
            var created = new PlanningDocumentCategoryRow
            {
                PlanningId = planningId, Name = n, SortOrder = others.Count == 0 ? 0 : others.Max(c => c.SortOrder) + 1,
                CreatedAt = Now, CreatedBy = byParticipantId,
            };
            db.Insert(created);
            Bump(db, planningId);
            return (created.Id, null);
        }

        /// <summary>Tar bort kategorin. Dokumenten står kvar, utan kategori.</summary>
        public string? DeleteDocumentCategory(int planningId, int categoryId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var n = db.Execute("UPDATE PlanningDocumentCategory SET DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL",
                categoryId, planningId, Now);
            if (n == 0) return "Kategorin finns inte.";
            db.Execute("UPDATE PlanningDocument SET CategoryId = NULL WHERE PlanningId = @0 AND CategoryId = @1", planningId, categoryId);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Flyttar kategorin ett steg upp (-1) eller ned (+1).</summary>
        public string? MoveDocumentCategory(int planningId, int categoryId, int dir)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var list = db.Fetch<PlanningDocumentCategoryRow>(
                "SELECT * FROM PlanningDocumentCategory WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY SortOrder, Id", planningId);
            var i = list.FindIndex(c => c.Id == categoryId);
            if (i < 0) return "Kategorin finns inte.";
            var j = i + Math.Sign(dir);
            if (j < 0 || j >= list.Count) return null;
            (list[i], list[j]) = (list[j], list[i]);
            for (var k = 0; k < list.Count; k++)
                if (list[k].SortOrder != k) { list[k].SortOrder = k; db.Update(list[k]); }
            Bump(db, planningId);
            return null;
        }

        // =====================================================================================
        //  Dokumenten
        // =====================================================================================

        public List<PlanningDocumentRow> Documents(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningDocumentRow>(
                "SELECT * FROM PlanningDocument WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY Title, Id", planningId);
        }

        public PlanningDocumentRow? Document(int planningId, int documentId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningDocumentRow>(
                "SELECT * FROM PlanningDocument WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", documentId, planningId);
        }

        /// <summary>Hur många byte planeringens filer tar (borttagna räknas inte).</summary>
        public long DocumentStorageUsed(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<long>(
                "SELECT ISNULL(SUM(FileSize), 0) FROM PlanningDocument WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId);
        }

        /// <summary>Var dokumentet ska bifogas när det läggs till (valfritt).</summary>
        public sealed record DocTarget(string Kind, int Id);

        public sealed record DocumentInput(string? Title, string? Description, int? CategoryId, string? Url,
            string? StoredFileName, string? OriginalFileName, long? FileSize, DocTarget? AttachTo);

        public (int Id, string? Error) AddDocument(int planningId, int byParticipantId, DocumentInput d)
        {
            var hasFile = !string.IsNullOrEmpty(d.StoredFileName);
            var url = Clean(d.Url, 1000);
            if (!hasFile && url == null) return (0, "Ange en länk eller välj en fil.");
            if (!hasFile && !IsSafeUrl(url!)) return (0, "Länken måste börja med https://.");
            var title = Clean(d.Title, MaxDocTitle) ?? (hasFile ? Clean(d.OriginalFileName, MaxDocTitle) : url!.Length > MaxDocTitle ? url.Substring(0, MaxDocTitle) : url) ?? "Dokument";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var catErr = CategoryError(db, planningId, d.CategoryId);
            if (catErr != null) return (0, catErr);
            string? kind = null;
            if (d.AttachTo != null)
            {
                kind = PlanningDocTarget.Normalize(d.AttachTo.Kind);
                if (kind == null || !TargetExists(db, planningId, kind, d.AttachTo.Id)) return (0, "Det dokumentet skulle bifogas till finns inte.");
            }
            var row = new PlanningDocumentRow
            {
                PlanningId = planningId, CategoryId = d.CategoryId > 0 ? d.CategoryId : null,
                Title = title, Description = Clean(d.Description, MaxDocDescription),
                Url = hasFile ? null : url, StoredFileName = hasFile ? d.StoredFileName : null,
                OriginalFileName = hasFile ? Clean(d.OriginalFileName, 260) : null,
                FileSize = hasFile && d.FileSize > 0 ? d.FileSize : null,
                AddedAt = Now, AddedBy = byParticipantId,
            };
            db.Insert(row);
            if (kind != null) InsertRef(db, planningId, row.Id, kind, d.AttachTo!.Id, byParticipantId, Now);
            Bump(db, planningId);
            return (row.Id, null);
        }

        /// <summary>Namn, beskrivning och kategori. Filen eller länken byts inte — lägg till ett nytt dokument.</summary>
        public string? UpdateDocument(int planningId, int documentId, string? title, string? description, int? categoryId)
        {
            var t = Clean(title, MaxDocTitle);
            if (t == null) return "Skriv vad dokumentet heter.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var row = db.SingleOrDefault<PlanningDocumentRow>(
                "SELECT * FROM PlanningDocument WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", documentId, planningId);
            if (row == null) return "Dokumentet finns inte.";
            var catErr = CategoryError(db, planningId, categoryId);
            if (catErr != null) return catErr;
            row.Title = t;
            row.Description = Clean(description, MaxDocDescription);
            row.CategoryId = categoryId > 0 ? categoryId : null;
            db.Update(row);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Tar bort dokumentet ur arkivet. Bifogningarna visar då "borttaget".</summary>
        public string? RemoveDocument(int planningId, int documentId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var n = s.Database.Execute("UPDATE PlanningDocument SET DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", documentId, planningId, Now);
            if (n == 0) return "Dokumentet finns inte.";
            Bump(s.Database, planningId);
            return null;
        }

        // =====================================================================================
        //  Bifogningarna
        // =====================================================================================

        /// <summary>Alla aktiva bifogningar i planeringen, också till borttagna dokument (de visas som borttagna).</summary>
        public List<PlanningDocumentRefRow> DocumentRefs(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningDocumentRefRow>(
                "SELECT * FROM PlanningDocumentRef WHERE PlanningId = @0 AND RemovedAt IS NULL ORDER BY Id", planningId);
        }

        public PlanningDocumentRefRow? DocumentRef(int planningId, int refId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningDocumentRefRow>(
                "SELECT * FROM PlanningDocumentRef WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL", refId, planningId);
        }

        /// <summary>
        /// Bifogar ett eller flera befintliga dokument. Ett dokument som redan är bifogat där hoppas över.
        /// Svarar hur många som bifogades.
        /// </summary>
        public (int Added, string? Error) AttachDocuments(int planningId, IEnumerable<int> documentIds, string kind, int targetId, int byParticipantId)
        {
            var k = PlanningDocTarget.Normalize(kind);
            if (k == null) return (0, "Okänd sorts bifogning.");
            var ids = documentIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return (0, "Välj ett dokument.");
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (!TargetExists(db, planningId, k, targetId)) return (0, "Det dokumentet skulle bifogas till finns inte.");
            var docs = db.Fetch<int>("SELECT Id FROM PlanningDocument WHERE PlanningId = @0 AND DeletedAt IS NULL AND Id IN (@1)", planningId, ids).ToHashSet();
            if (docs.Count != ids.Count) return (0, "Något av dokumenten finns inte.");
            var already = db.Fetch<int>(
                "SELECT DocumentId FROM PlanningDocumentRef WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND TargetKind = @1 AND TargetId = @2 AND RemovedAt IS NULL",
                planningId, k, targetId).ToHashSet();
            var added = 0;
            foreach (var id in ids.Where(i => !already.Contains(i)))
            {
                InsertRef(db, planningId, id, k, targetId, byParticipantId, Now);
                added++;
            }
            if (added > 0) Bump(db, planningId);
            return (added, null);
        }

        public string? DetachDocument(int planningId, int refId, int byParticipantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var n = s.Database.Execute("UPDATE PlanningDocumentRef SET RemovedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL",
                refId, planningId, Now);
            if (n == 0) return "Bifogningen finns inte.";
            Bump(s.Database, planningId);
            return null;
        }

        /// <summary>Det en bifogning pekar på, för listan "Bifogat i": rubrik, och mötet (för att kunna gå dit).</summary>
        public sealed record RefTargetInfo(string Kind, int Id, string Title, DateTime? At, int? MeetingId, bool Gone);

        /// <summary>Rubrikerna för målen, en fråga per sort. Ett borttaget mål svarar Gone = true.</summary>
        public Dictionary<(string Kind, int Id), RefTargetInfo> DocumentRefTargets(int planningId, IEnumerable<PlanningDocumentRefRow> refs)
        {
            var result = new Dictionary<(string, int), RefTargetInfo>();
            var byKind = refs.GroupBy(r => r.TargetKind).ToDictionary(g => g.Key, g => g.Select(r => r.TargetId).Distinct().ToList());
            if (byKind.Count == 0) return result;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            foreach (var (kind, ids) in byKind)
            {
                List<TargetRow> rows = kind switch
                {
                    PlanningDocTarget.Message => db.Fetch<TargetRow>(
                        @"SELECT m.Id, ISNULL(p.DisplayName, N'') AS Title, m.CreatedAt AS At, NULL AS MeetingId,
                                 CASE WHEN m.DeletedAt IS NULL THEN 0 ELSE 1 END AS Gone
                          FROM PlanningMessage m LEFT JOIN PlanningMember p ON p.Id = m.ParticipantId
                          WHERE m.PlanningId = @0 AND m.Id IN (@1)", planningId, ids),
                    PlanningDocTarget.Meeting => db.Fetch<TargetRow>(
                        @"SELECT Id, Title, StartsAt AS At, Id AS MeetingId, CASE WHEN DeletedAt IS NULL THEN 0 ELSE 1 END AS Gone
                          FROM PlanningMeeting WHERE PlanningId = @0 AND Id IN (@1)", planningId, ids),
                    PlanningDocTarget.Topic => db.Fetch<TargetRow>(
                        @"SELECT t.Id, t.Text AS Title, m.StartsAt AS At, t.PlanningMeetingId AS MeetingId,
                                 CASE WHEN t.DeletedAt IS NULL AND m.DeletedAt IS NULL THEN 0 ELSE 1 END AS Gone
                          FROM PlanningMeetingTopic t JOIN PlanningMeeting m ON m.Id = t.PlanningMeetingId
                          WHERE t.PlanningId = @0 AND t.Id IN (@1)", planningId, ids),
                    PlanningDocTarget.Task => db.Fetch<TargetRow>(
                        @"SELECT Id, Title, DueDate AS At, NULL AS MeetingId, CASE WHEN DeletedAt IS NULL THEN 0 ELSE 1 END AS Gone
                          FROM PlanningTask WHERE PlanningId = @0 AND Id IN (@1)", planningId, ids),
                    PlanningDocTarget.Area => db.Fetch<TargetRow>(
                        @"SELECT Id, Name AS Title, NULL AS At, NULL AS MeetingId, CASE WHEN DeletedAt IS NULL THEN 0 ELSE 1 END AS Gone
                          FROM PlanningArea WHERE PlanningId = @0 AND Id IN (@1)", planningId, ids),
                    PlanningDocTarget.Decision => db.Fetch<TargetRow>(
                        @"SELECT Id, Title, ProposedAt AS At, NULL AS MeetingId, CASE WHEN DeletedAt IS NULL THEN 0 ELSE 1 END AS Gone
                          FROM PlanningDecision WHERE PlanningId = @0 AND Id IN (@1)", planningId, ids),
                    _ => new List<TargetRow>(),
                };
                foreach (var r in rows)
                    result[(kind, r.Id)] = new RefTargetInfo(kind, r.Id, r.Title ?? "", r.At, r.MeetingId, r.Gone != 0);
            }
            return result;
        }

        private sealed class TargetRow
        {
            public int Id { get; set; }
            public string? Title { get; set; }
            public DateTime? At { get; set; }
            public int? MeetingId { get; set; }
            public int Gone { get; set; }
        }

        private static void InsertRef(IUmbracoDatabase db, int planningId, int documentId, string kind, int targetId, int by, DateTime at) =>
            db.Insert(new PlanningDocumentRefRow
            {
                PlanningId = planningId, DocumentId = documentId, TargetKind = kind, TargetId = targetId, AddedAt = at, AddedBy = by,
            });

        /// <summary>Att målet finns i planeringen och inte är borttaget. Ett id prövas alltid tillsammans med sin sort.</summary>
        private static bool TargetExists(IUmbracoDatabase db, int planningId, string kind, int targetId)
        {
            if (targetId <= 0) return false;
            var table = kind switch
            {
                PlanningDocTarget.Message => "PlanningMessage",
                PlanningDocTarget.Meeting => "PlanningMeeting",
                PlanningDocTarget.Topic => "PlanningMeetingTopic",
                PlanningDocTarget.Task => "PlanningTask",
                PlanningDocTarget.Area => "PlanningArea",
                PlanningDocTarget.Decision => "PlanningDecision",
                _ => null,
            };
            if (table == null) return false;
            var extra = kind == PlanningDocTarget.Message ? " AND Kind = N'Message'" : "";
            return db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL{extra}",
                targetId, planningId) == 1;
        }

        private static string? CategoryError(IUmbracoDatabase db, int planningId, int? categoryId)
        {
            if (!(categoryId > 0)) return null;
            return db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningDocumentCategory WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL",
                categoryId, planningId) == 1 ? null : "Kategorin finns inte.";
        }
    }
}
