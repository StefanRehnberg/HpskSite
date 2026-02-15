using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Cms.Core.Security;
using HpskSite.Services;
using HpskSite.Models;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for document archive operations.
    /// Provides admin endpoints for managing documents/categories and public endpoints for viewing/downloading.
    /// </summary>
    public class DocumentController : SurfaceController
    {
        private readonly DocumentService _documentService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public DocumentController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            DocumentService documentService,
            AdminAuthorizationService authorizationService,
            IMemberManager memberManager,
            IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _documentService = documentService;
            _authorizationService = authorizationService;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        // ===== ADMIN: CATEGORIES =====

        [HttpGet]
        public async Task<IActionResult> GetCategories(int ownerType, int ownerId)
        {
            if (!await IsAdminForOwner(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var categories = _documentService.GetCategories(ownerType, ownerId);
            return Json(new { success = true, data = categories });
        }

        [HttpPost]
        public async Task<IActionResult> CreateCategory(string name, string? description, int ownerType, int ownerId, bool showInQuickLinks = false)
        {
            if (!await IsAdminForOwner(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, message = "Namn krävs." });

            var memberId = await GetCurrentMemberId();
            if (memberId == 0)
                return Json(new { success = false, message = "Kunde inte identifiera användaren." });

            var category = new DocumentCategory
            {
                Name = name.Trim(),
                Description = description?.Trim(),
                OwnerType = ownerType,
                OwnerId = ownerId,
                ShowInQuickLinks = showInQuickLinks,
                CreatedBy = memberId
            };

            _documentService.CreateCategory(category);
            return Json(new { success = true, data = category });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateCategory(int id, string name, string? description, bool showInQuickLinks, int sortOrder = 0)
        {
            var category = _documentService.GetCategory(id);
            if (category == null)
                return Json(new { success = false, message = "Kategori hittades inte." });

            if (!await IsAdminForOwner(category.OwnerType, category.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, message = "Namn krävs." });

            category.Name = name.Trim();
            category.Description = description?.Trim();
            category.ShowInQuickLinks = showInQuickLinks;
            category.SortOrder = sortOrder;

            _documentService.UpdateCategory(category);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCategory(int id)
        {
            var category = _documentService.GetCategory(id);
            if (category == null)
                return Json(new { success = false, message = "Kategori hittades inte." });

            if (!await IsAdminForOwner(category.OwnerType, category.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            _documentService.DeleteCategory(id);
            return Json(new { success = true });
        }

        // ===== ADMIN: DOCUMENTS =====

        [HttpGet]
        public async Task<IActionResult> GetDocuments(int ownerType, int ownerId)
        {
            if (!await IsAdminForOwner(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var documents = _documentService.GetDocuments(ownerType, ownerId);
            var categories = _documentService.GetCategories(ownerType, ownerId);

            return Json(new
            {
                success = true,
                data = documents.Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.Description,
                    d.CategoryId,
                    categoryName = categories.FirstOrDefault(c => c.Id == d.CategoryId)?.Name,
                    d.FileName,
                    d.FileSize,
                    d.AccessLevel,
                    accessLevelLabel = DocumentAccessLevel.GetLabel(d.AccessLevel),
                    d.SortOrder,
                    d.ShowInQuickLinks,
                    d.DownloadCount,
                    d.CreatedAt
                })
            });
        }

        [HttpPost]
        public async Task<IActionResult> UploadDocument(
            string title,
            string? description,
            int? categoryId,
            int ownerType,
            int ownerId,
            int accessLevel,
            bool showInQuickLinks,
            IFormFile file)
        {
            if (!await IsAdminForOwner(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (file == null)
                return Json(new { success = false, message = "Ingen fil vald." });

            if (file.Length == 0)
                return Json(new { success = false, message = "Filen är tom (0 bytes). Välj en fil med innehåll." });

            if (string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Titel krävs." });

            // Validate file
            var (isValid, errorMessage) = _documentService.ValidateFile(file.FileName, file.Length);
            if (!isValid)
                return Json(new { success = false, message = errorMessage });

            // Check storage quota
            if (!_documentService.CanUpload(ownerType, ownerId, file.Length))
            {
                var info = _documentService.GetStorageInfo(ownerType, ownerId);
                return Json(new { success = false, message = $"Lagringskvoten är full. Använt {info.usedBytes / (1024 * 1024):F1} MB av {info.limitMB} MB." });
            }

            var memberId = await GetCurrentMemberId();
            if (memberId == 0)
                return Json(new { success = false, message = "Kunde inte identifiera användaren." });

            // Save file
            using var stream = file.OpenReadStream();
            var storedFileName = await _documentService.SaveFileAsync(stream, file.FileName);

            var document = new Document
            {
                Title = title.Trim(),
                Description = description?.Trim(),
                CategoryId = categoryId,
                OwnerType = ownerType,
                OwnerId = ownerId,
                FileName = file.FileName,
                StoredFileName = storedFileName,
                ContentType = file.ContentType,
                FileSize = file.Length,
                AccessLevel = accessLevel,
                ShowInQuickLinks = showInQuickLinks,
                CreatedBy = memberId
            };

            _documentService.CreateDocument(document);
            return Json(new { success = true, data = new { document.Id } });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateDocument(int id, string title, string? description, int? categoryId, int accessLevel, bool showInQuickLinks, int sortOrder = 0)
        {
            var document = _documentService.GetDocument(id);
            if (document == null)
                return Json(new { success = false, message = "Dokument hittades inte." });

            if (!await IsAdminForOwner(document.OwnerType, document.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Titel krävs." });

            document.Title = title.Trim();
            document.Description = description?.Trim();
            document.CategoryId = categoryId;
            document.AccessLevel = accessLevel;
            document.ShowInQuickLinks = showInQuickLinks;
            document.SortOrder = sortOrder;

            _documentService.UpdateDocument(document);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            var document = _documentService.GetDocument(id);
            if (document == null)
                return Json(new { success = false, message = "Dokument hittades inte." });

            if (!await IsAdminForOwner(document.OwnerType, document.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            _documentService.DeleteDocument(id);
            return Json(new { success = true });
        }

        // ===== PUBLIC ENDPOINTS =====

        [HttpGet]
        public async Task<IActionResult> GetPublicDocuments(int ownerType, int ownerId)
        {
            var maxAccess = await GetUserAccessLevel(ownerType, ownerId);
            var documents = _documentService.GetDocumentsByAccessLevel(ownerType, ownerId, maxAccess);
            var categories = _documentService.GetCategories(ownerType, ownerId);

            return Json(new
            {
                success = true,
                data = documents.Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.Description,
                    d.CategoryId,
                    categoryName = categories.FirstOrDefault(c => c.Id == d.CategoryId)?.Name,
                    d.FileName,
                    d.FileSize,
                    d.AccessLevel,
                    accessLevelLabel = DocumentAccessLevel.GetLabel(d.AccessLevel),
                    d.ContentType
                }),
                categories = categories.Select(c => new { c.Id, c.Name })
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetQuickLinks(int ownerType, int ownerId)
        {
            var maxAccess = await GetUserAccessLevel(ownerType, ownerId);
            var documents = _documentService.GetQuickLinkDocuments(ownerType, ownerId, maxAccess);

            return Json(new
            {
                success = true,
                data = documents.Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.FileName,
                    d.ContentType,
                    icon = GetFileIcon(d.FileName)
                })
            });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadDocument(int id)
        {
            var document = _documentService.GetDocument(id);
            if (document == null)
                return NotFound();

            if (!await CanAccessDocument(document))
                return Unauthorized();

            var filePath = _documentService.GetFilePath(document.StoredFileName);
            if (filePath == null)
                return NotFound();

            _documentService.IncrementDownloadCount(id);

            return PhysicalFile(filePath, document.ContentType, document.FileName);
        }

        // ===== STORAGE QUOTA =====

        [HttpGet]
        public async Task<IActionResult> GetStorageInfo(int ownerType, int ownerId)
        {
            if (!await IsAdminForOwner(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var (usedBytes, limitMB, percentage) = _documentService.GetStorageInfo(ownerType, ownerId);
            return Json(new
            {
                success = true,
                data = new
                {
                    usedBytes,
                    usedMB = Math.Round((double)usedBytes / (1024 * 1024), 1),
                    limitMB,
                    percentage
                }
            });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStorageQuota(int ownerType, int ownerId, int storageLimitMB)
        {
            // Only site admins can change storage quotas
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast webbplatsadministratörer kan ändra lagringskvoter." });

            if (storageLimitMB <= 0)
                return Json(new { success = false, message = "Lagringsgränsen måste vara större än 0." });

            _documentService.UpdateStorageQuota(ownerType, ownerId, storageLimitMB);
            return Json(new { success = true });
        }

        // ===== HELPERS =====

        private async Task<bool> IsAdminForOwner(int ownerType, int ownerId)
        {
            if (ownerType == DocumentOwnerType.Region)
            {
                // For regions, ownerId is the content node ID - we need to get the region code
                var regionCode = GetRegionCodeFromNodeId(ownerId);
                if (string.IsNullOrEmpty(regionCode)) return false;
                return await _authorizationService.IsRegionalAdminForRegion(regionCode);
            }
            else
            {
                return await _authorizationService.IsClubAdminForClub(ownerId);
            }
        }

        private string? GetRegionCodeFromNodeId(int nodeId)
        {
            if (UmbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext) && umbracoContext.Content != null)
            {
                var node = umbracoContext.Content.GetById(nodeId);
                if (node != null && node.ContentType.Alias == "regionalPage")
                {
                    return node.Value<string>("regionCode");
                }
            }
            return null;
        }

        private async Task<bool> CanAccessDocument(Document document)
        {
            // Public documents are accessible to everyone
            if (document.AccessLevel == DocumentAccessLevel.Public)
                return true;

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return false;

            // Authenticated - any logged-in user
            if (document.AccessLevel == DocumentAccessLevel.Authenticated)
                return true;

            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (member == null) return false;

            // Club Members - check membership
            if (document.AccessLevel == DocumentAccessLevel.ClubMembers)
            {
                if (document.OwnerType == DocumentOwnerType.Club)
                {
                    var primaryClubId = member.GetValue("primaryClubId")?.ToString() ?? "";
                    var memberClubIds = member.GetValue("memberClubIds")?.ToString() ?? "";
                    return primaryClubId == document.OwnerId.ToString() ||
                           memberClubIds.Split(',').Select(s => s.Trim()).Contains(document.OwnerId.ToString());
                }
                // Region documents at ClubMembers level - allow any logged-in member
                return true;
            }

            // Club Admins
            if (document.AccessLevel == DocumentAccessLevel.ClubAdmins)
            {
                return await IsAdminForOwner(document.OwnerType, document.OwnerId);
            }

            return false;
        }

        private async Task<int> GetUserAccessLevel(int ownerType, int ownerId)
        {
            // Check from highest to lowest access
            if (await IsAdminForOwner(ownerType, ownerId))
                return DocumentAccessLevel.ClubAdmins;

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return DocumentAccessLevel.Public;

            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (member == null)
                return DocumentAccessLevel.Authenticated;

            // Check club membership for club-owned documents
            if (ownerType == DocumentOwnerType.Club)
            {
                var primaryClubId = member.GetValue("primaryClubId")?.ToString() ?? "";
                var memberClubIds = member.GetValue("memberClubIds")?.ToString() ?? "";
                if (primaryClubId == ownerId.ToString() ||
                    memberClubIds.Split(',').Select(s => s.Trim()).Contains(ownerId.ToString()))
                {
                    return DocumentAccessLevel.ClubMembers;
                }
            }
            else
            {
                // For region documents, any member is considered a "member"
                return DocumentAccessLevel.ClubMembers;
            }

            return DocumentAccessLevel.Authenticated;
        }

        private async Task<int> GetCurrentMemberId()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return 0;

            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            return member?.Id ?? 0;
        }

        private static string GetFileIcon(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "bi-file-earmark-pdf",
                ".doc" or ".docx" => "bi-file-earmark-word",
                ".xls" or ".xlsx" => "bi-file-earmark-excel",
                ".ppt" or ".pptx" => "bi-file-earmark-ppt",
                ".txt" or ".csv" => "bi-file-earmark-text",
                ".zip" => "bi-file-earmark-zip",
                ".jpg" or ".jpeg" or ".png" => "bi-file-earmark-image",
                _ => "bi-file-earmark"
            };
        }
    }
}
