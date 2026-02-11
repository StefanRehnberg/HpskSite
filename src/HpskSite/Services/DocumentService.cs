using HpskSite.Models;
using HpskSite.Models.Configuration;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for managing document archive operations including CRUD for categories/documents,
    /// file storage, access level filtering, and storage quota enforcement.
    /// </summary>
    public class DocumentService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly DocumentArchiveOptions _options;
        private readonly IWebHostEnvironment _environment;

        public DocumentService(
            IScopeProvider scopeProvider,
            IOptions<DocumentArchiveOptions> options,
            IWebHostEnvironment environment)
        {
            _scopeProvider = scopeProvider;
            _options = options.Value;
            _environment = environment;
        }

        // ===== CATEGORIES =====

        public List<DocumentCategory> GetCategories(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Fetch<DocumentCategory>(
                "SELECT * FROM DocumentCategories WHERE OwnerType = @0 AND OwnerId = @1 ORDER BY SortOrder, Name",
                ownerType, ownerId);
        }

        public DocumentCategory? GetCategory(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.SingleOrDefaultById<DocumentCategory>(id);
        }

        public DocumentCategory CreateCategory(DocumentCategory category)
        {
            category.CreatedAt = DateTime.UtcNow;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Insert(category);
            return category;
        }

        public void UpdateCategory(DocumentCategory category)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Update(category);
        }

        public bool DeleteCategory(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            // Check if any documents use this category - set them to uncategorized
            db.Execute("UPDATE Documents SET CategoryId = NULL WHERE CategoryId = @0", id);
            return db.Delete<DocumentCategory>(id) > 0;
        }

        // ===== DOCUMENTS =====

        public List<Document> GetDocuments(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Fetch<Document>(
                "SELECT * FROM Documents WHERE OwnerType = @0 AND OwnerId = @1 ORDER BY SortOrder, Title",
                ownerType, ownerId);
        }

        public Document? GetDocument(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.SingleOrDefaultById<Document>(id);
        }

        public List<Document> GetDocumentsByAccessLevel(int ownerType, int ownerId, int maxAccessLevel)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Fetch<Document>(
                "SELECT * FROM Documents WHERE OwnerType = @0 AND OwnerId = @1 AND AccessLevel <= @2 ORDER BY SortOrder, Title",
                ownerType, ownerId, maxAccessLevel);
        }

        public List<Document> GetQuickLinkDocuments(int ownerType, int ownerId, int maxAccessLevel)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Fetch<Document>(
                "SELECT * FROM Documents WHERE OwnerType = @0 AND OwnerId = @1 AND ShowInQuickLinks = 1 AND AccessLevel <= @2 ORDER BY SortOrder, Title",
                ownerType, ownerId, maxAccessLevel);
        }

        public Document CreateDocument(Document document)
        {
            document.CreatedAt = DateTime.UtcNow;
            document.UpdatedAt = DateTime.UtcNow;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Insert(document);
            return document;
        }

        public void UpdateDocument(Document document)
        {
            document.UpdatedAt = DateTime.UtcNow;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Update(document);
        }

        public bool DeleteDocument(int id)
        {
            var document = GetDocument(id);
            if (document == null) return false;

            // Delete the physical file
            DeleteStoredFile(document.StoredFileName);

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Delete<Document>(id) > 0;
        }

        public void IncrementDownloadCount(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("UPDATE Documents SET DownloadCount = DownloadCount + 1 WHERE Id = @0", id);
        }

        // ===== FILE OPERATIONS =====

        public string GetStoragePath()
        {
            var appDataPath = Path.Combine(_environment.ContentRootPath, "App_Data", _options.StoragePath);
            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }
            return appDataPath;
        }

        public (bool isValid, string errorMessage) ValidateFile(string fileName, long fileSize)
        {
            // Check extension
            var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                return (false, "Filen saknar filändelse.");
            }

            var allowedExtensions = _options.GetAllowedExtensionsArray();
            if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return (false, $"Filtypen '{extension}' är inte tillåten. Tillåtna: {_options.AllowedExtensions}");
            }

            // Check size
            var maxSizeBytes = (long)_options.MaxFileSizeMB * 1024 * 1024;
            if (fileSize > maxSizeBytes)
            {
                return (false, $"Filen är för stor. Max storlek: {_options.MaxFileSizeMB} MB.");
            }

            return (true, string.Empty);
        }

        public async Task<string> SaveFileAsync(Stream fileStream, string originalFileName)
        {
            var extension = Path.GetExtension(originalFileName);
            var storedFileName = $"{Guid.NewGuid()}{extension}";
            var storagePath = GetStoragePath();
            var filePath = Path.Combine(storagePath, storedFileName);

            using var outputStream = new FileStream(filePath, FileMode.Create);
            await fileStream.CopyToAsync(outputStream);

            return storedFileName;
        }

        public string? GetFilePath(string storedFileName)
        {
            var storagePath = GetStoragePath();
            var filePath = Path.Combine(storagePath, storedFileName);
            return File.Exists(filePath) ? filePath : null;
        }

        private void DeleteStoredFile(string storedFileName)
        {
            var storagePath = GetStoragePath();
            var filePath = Path.Combine(storagePath, storedFileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        // ===== STORAGE QUOTA =====

        public long GetStorageUsage(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.ExecuteScalar<long>(
                "SELECT ISNULL(SUM(FileSize), 0) FROM Documents WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId);
        }

        public int GetStorageLimit(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            // Check for custom quota override
            var quota = db.FirstOrDefault<DocumentStorageQuota>(
                "SELECT * FROM DocumentStorageQuotas WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId);

            if (quota != null)
            {
                return quota.StorageLimitMB;
            }

            // Fall back to default from config
            return ownerType == DocumentOwnerType.Region
                ? _options.DefaultRegionStorageLimitMB
                : _options.DefaultClubStorageLimitMB;
        }

        public (long usedBytes, int limitMB, double percentage) GetStorageInfo(int ownerType, int ownerId)
        {
            var usedBytes = GetStorageUsage(ownerType, ownerId);
            var limitMB = GetStorageLimit(ownerType, ownerId);
            var limitBytes = (long)limitMB * 1024 * 1024;
            var percentage = limitBytes > 0 ? (double)usedBytes / limitBytes * 100 : 0;
            return (usedBytes, limitMB, Math.Round(percentage, 1));
        }

        public bool CanUpload(int ownerType, int ownerId, long newFileSize)
        {
            var usedBytes = GetStorageUsage(ownerType, ownerId);
            var limitMB = GetStorageLimit(ownerType, ownerId);
            var limitBytes = (long)limitMB * 1024 * 1024;
            return (usedBytes + newFileSize) <= limitBytes;
        }

        public void UpdateStorageQuota(int ownerType, int ownerId, int storageLimitMB)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var existing = db.FirstOrDefault<DocumentStorageQuota>(
                "SELECT * FROM DocumentStorageQuotas WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId);

            if (existing != null)
            {
                existing.StorageLimitMB = storageLimitMB;
                db.Update(existing);
            }
            else
            {
                db.Insert(new DocumentStorageQuota
                {
                    OwnerType = ownerType,
                    OwnerId = ownerId,
                    StorageLimitMB = storageLimitMB
                });
            }
        }
    }
}
