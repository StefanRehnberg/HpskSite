using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Planeringens dokumentarkiv (2026-10-23): kategorier, dokument och var de är bifogade
    /// (meddelanden, möten, punkter att prata om, uppgifter, områden). Alla som är med läser allt;
    /// se <see cref="PlanningAccess"/> för vem som lägger till, ändrar, bifogar och tar bort.
    /// </summary>
    public partial class PlanningController
    {
        private long StorageLimitBytes => Math.Max(1, _archiveOptions.Value.DefaultPlanningStorageLimitMB) * 1024L * 1024L;

        /// <summary>Hela arkivet: kategorier, dokument med sina bifogningar, och lagringen.</summary>
        [HttpGet]
        public async Task<IActionResult> Documents(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var docs = _planning.Documents(id);
            var refs = _planning.DocumentRefs(id);
            var targets = _planning.DocumentRefTargets(id, refs);
            var live = docs.Select(d => d.Id).ToHashSet();
            var used = docs.Sum(d => d.FileSize ?? 0);
            return Ok(new
            {
                success = true,
                categories = _planning.DocumentCategories(id).Select(c => new { id = c.Id, name = c.Name, areaId = c.AreaId }),
                documents = docs.Select(d => DocJson(d, x.Caller, names, t,
                    refs.Where(r => r.DocumentId == d.Id).Select(r => RefJson(r, x.Caller, targets)).ToList())),
                // Bifogningar till dokument som tagits bort ur arkivet: de visas som "borttaget" där de satt.
                removedRefs = refs.Where(r => !live.Contains(r.DocumentId)).Select(r => new { refId = r.Id, kind = r.TargetKind, targetId = r.TargetId }),
                storage = new { usedBytes = used, limitBytes = StorageLimitBytes },
                canAdd = PlanningAccess.CanAddDocument(x.Caller),
                canManageCategories = PlanningAccess.CanManageDocumentCategories(x.Caller),
                me = x.Caller.ParticipantId,
            });
        }

        private object DocJson(PlanningDocumentRow d, PlanningCaller c, IReadOnlyDictionary<int, string> names, string? t, IReadOnlyList<object>? refs = null) => new
        {
            id = d.Id, title = d.Title, description = d.Description, categoryId = d.CategoryId, isFile = d.IsFile,
            url = d.IsFile ? $"/umbraco/surface/Planning/DownloadDocument?id={d.PlanningId}&documentId={d.Id}" + (string.IsNullOrEmpty(t) ? "" : "&t=" + Uri.EscapeDataString(t)) : d.Url,
            fileName = d.OriginalFileName, fileSize = d.FileSize,
            addedBy = d.AddedBy > 0 ? names.GetValueOrDefault(d.AddedBy, "") : "", addedAt = D(d.AddedAt),
            canEdit = PlanningAccess.CanEditDocument(c, d), canRemove = PlanningAccess.CanRemoveDocument(c, d),
            refs = refs ?? Array.Empty<object>(),
        };

        private static object RefJson(PlanningDocumentRefRow r, PlanningCaller c, IReadOnlyDictionary<(string, int), PlanningService.RefTargetInfo> targets)
        {
            targets.TryGetValue((r.TargetKind, r.TargetId), out var info);
            return new
            {
                refId = r.Id, kind = r.TargetKind, targetId = r.TargetId,
                title = info?.Title ?? "", at = DT(info?.At), meetingId = info?.MeetingId, gone = info == null || info.Gone,
                canDetach = PlanningAccess.CanDetachDocument(c, r),
            };
        }

        // ---------------------------------------------------------------- kategorier

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDocumentCategory(int id, int? categoryId, string? name, string? areaId = null)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageDocumentCategories(x.Caller)) return No("Kategorierna ändras av den som kan ändra planeringen.");
            // Utelämnat = rör inte området (en äldre klient får inte ta bort kopplingen); "" eller "0" = inget område.
            int? area = areaId == null ? null : int.TryParse(areaId, out var a) && a > 0 ? a : 0;
            var r = _planning.SaveDocumentCategory(id, categoryId ?? 0, name, x.Caller.ParticipantId, area);
            return r.Error == null ? Ok(new { success = true, categoryId = r.Id }) : No(r.Error);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocumentCategory(int id, int categoryId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageDocumentCategories(x.Caller)) return No("Kategorierna ändras av den som kan ändra planeringen.");
            var err = _planning.DeleteDocumentCategory(id, categoryId);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveDocumentCategory(int id, int categoryId, int dir)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageDocumentCategories(x.Caller)) return No("Kategorierna ändras av den som kan ändra planeringen.");
            var err = _planning.MoveDocumentCategory(id, categoryId, dir);
            return err == null ? Ok() : No(err);
        }

        // ---------------------------------------------------------------- dokumenten

        /// <summary>Målet för en ny fil eller länk som ska bifogas direkt, eller null. Meddelanden bifogas när de skickas.</summary>
        private PlanningService.DocTarget? AttachTarget(string? kind, int? targetId) =>
            targetId > 0 && PlanningDocTarget.Normalize(kind) is { } k && k != PlanningDocTarget.Message
                ? new PlanningService.DocTarget(k, targetId.Value) : null;

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddDocumentLink(int id, string? title, string? description, int? categoryId, string url, string? attachKind, int? attachId, string? t = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanAddDocument(x.Caller)) return No("Bara den som deltar lägger till dokument.");
            var r = _planning.AddDocument(id, x.Caller.ParticipantId,
                new PlanningService.DocumentInput(title, description, categoryId, url, null, null, null, AttachTarget(attachKind, attachId)));
            return r.Error == null ? Ok(new { success = true, documentId = r.Id }) : No(r.Error);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(26L * 1024 * 1024)]
        public async Task<IActionResult> UploadDocument(int id, string? title, string? description, int? categoryId, string? attachKind, int? attachId, IFormFile? file, string? t = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanAddDocument(x.Caller)) return No("Bara den som deltar lägger till dokument.");
            if (file == null) return No("Välj en fil.");
            var (ok, error) = _docs.Validate(file.FileName, file.Length);
            if (!ok) return No(error ?? "Filen kan inte laddas upp.");
            var used = _planning.DocumentStorageUsed(id);
            if (used + file.Length > StorageLimitBytes)
                return No($"Planeringens utrymme för filer är fullt ({Mb(used)} av {Mb(StorageLimitBytes)} MB). Ta bort filer som inte behövs, eller lägg till en länk i stället.");
            string stored;
            await using (var stream = file.OpenReadStream()) stored = await _docs.SaveAsync(stream, file.FileName);
            var r = _planning.AddDocument(id, x.Caller.ParticipantId,
                new PlanningService.DocumentInput(title, description, categoryId, null, stored, file.FileName, file.Length, AttachTarget(attachKind, attachId)));
            if (r.Error != null) { await _docs.DeleteAsync(stored); return No(r.Error); }
            return Ok(new { success = true, documentId = r.Id });
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", Sv);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateDocument(int id, int documentId, string? title, string? description, int? categoryId, string? t = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var d = _planning.Document(id, documentId);
            if (d == null) return No("Dokumentet finns inte.");
            if (!PlanningAccess.CanEditDocument(x.Caller, d)) return No("Dokumentet ändras av den som lade till det, eller av den som kan ändra planeringen.");
            var err = _planning.UpdateDocument(id, documentId, title, description, categoryId);
            return err == null ? Ok() : No(err);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadDocument(int id, int documentId, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return NotFound();
            var d = _planning.Document(id, documentId);
            if (d == null || !d.IsFile) return NotFound();
            var stream = await _docs.OpenReadAsync(d.StoredFileName);
            if (stream == null) return NotFound();
            var name = string.IsNullOrWhiteSpace(d.OriginalFileName) ? d.StoredFileName! : d.OriginalFileName!;
            return File(stream, PrepDocumentStorage.ContentTypeFor(d.StoredFileName!), name, enableRangeProcessing: true);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveDocument(int id, int documentId, string? t = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var d = _planning.Document(id, documentId);
            if (d == null) return No("Dokumentet finns inte.");
            if (!PlanningAccess.CanRemoveDocument(x.Caller, d)) return No("Dokumentet tas bort av den som lade till det, eller av den som kan ändra planeringen.");
            var err = _planning.RemoveDocument(id, documentId);
            return err == null ? Ok() : No(err);
        }

        // ---------------------------------------------------------------- bifogningar

        /// <summary>Bifogar befintliga dokument (kommaseparerade id) till ett möte, en punkt, en uppgift eller ett område.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AttachDocuments(int id, string? documentIds, string? kind, int targetId, string? t = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanAttachDocument(x.Caller)) return No("Bara den som deltar bifogar dokument.");
            var k = PlanningDocTarget.Normalize(kind);
            if (k == PlanningDocTarget.Message && !IsOwnMessage(id, targetId, x.Caller.ParticipantId))
                return No("Bara den som skrev meddelandet kan bifoga dokument till det.");
            var r = _planning.AttachDocuments(id, ParseIds(documentIds), k ?? "", targetId, x.Caller.ParticipantId);
            return r.Error == null ? Ok(new { success = true, added = r.Added }) : No(r.Error);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DetachDocument(int id, int refId, string? t = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var r = _planning.DocumentRef(id, refId);
            if (r == null) return No("Bifogningen finns inte.");
            if (!PlanningAccess.CanDetachDocument(x.Caller, r)) return No("Bifogningen tas bort av den som bifogade, eller av den som kan ändra planeringen.");
            var err = _planning.DetachDocument(id, refId, x.Caller.ParticipantId);
            return err == null ? Ok() : No(err);
        }

        private bool IsOwnMessage(int planningId, int messageId, int participantId) =>
            participantId > 0 && _planning.Message(planningId, messageId) is { DeletedAt: null } m && m.ParticipantId == participantId;

        private static List<int> ParseIds(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Distinct().ToList();
    }
}
