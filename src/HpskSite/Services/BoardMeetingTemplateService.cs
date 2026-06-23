using System.Text.Json;
using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Club/region-editable agenda templates per meeting type. Returns the saved template when one
    /// exists, otherwise the built-in default from <see cref="BoardMeetingTemplates"/>. See Board Work.
    /// </summary>
    public class BoardMeetingTemplateService
    {
        private readonly IScopeProvider _scopeProvider;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public BoardMeetingTemplateService(IScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        /// <summary>The saved template row for an owner+type, or null if none (→ use the built-in default).</summary>
        public BoardMeetingTemplate? GetSavedTemplate(int ownerType, int ownerId, string meetingTypeKey)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<BoardMeetingTemplate>(
                "SELECT * FROM BoardMeetingTemplates WHERE OwnerType=@0 AND OwnerId=@1 AND MeetingTypeKey=@2 AND IsActive=1 ORDER BY Id DESC",
                ownerType, ownerId, meetingTypeKey);
        }

        /// <summary>
        /// The effective agenda for a meeting type: the saved template if present, else the built-in default.
        /// </summary>
        public List<BoardTemplateItem> GetEffectiveAgenda(int ownerType, int ownerId, string meetingTypeKey)
        {
            var saved = GetSavedTemplate(ownerType, ownerId, meetingTypeKey);
            if (saved != null)
            {
                var items = DeserializeItems(saved.ItemsJson);
                if (items.Count > 0) return items;
            }
            return BoardMeetingTemplates.GetDefaultAgenda(meetingTypeKey)
                .Select(d => new BoardTemplateItem
                {
                    ItemType = d.ItemType,
                    Heading = d.Heading,
                    ElectionRole = d.ElectionRole,
                    ElectionCount = d.ElectionCount,
                    ElectionSource = d.ElectionSource
                }).ToList();
        }

        public bool HasSavedTemplate(int ownerType, int ownerId, string meetingTypeKey)
            => GetSavedTemplate(ownerType, ownerId, meetingTypeKey) != null;

        /// <summary>Save (replace) the owner's template for a meeting type. Empty list resets to default.</summary>
        public void SaveTemplate(int ownerType, int ownerId, string meetingTypeKey, List<BoardTemplateItem> items, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            // Deactivate any existing rows, then insert the new one (keeps history; simplest replace).
            db.Execute("UPDATE BoardMeetingTemplates SET IsActive=0 WHERE OwnerType=@0 AND OwnerId=@1 AND MeetingTypeKey=@2 AND IsActive=1",
                ownerType, ownerId, meetingTypeKey);
            if (items == null || items.Count == 0) return; // reset to built-in default

            db.Insert(new BoardMeetingTemplate
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                MeetingTypeKey = meetingTypeKey,
                ItemsJson = JsonSerializer.Serialize(items, JsonOpts),
                UpdatedByMemberId = byMemberId,
                UpdatedDate = DateTime.UtcNow,
                IsActive = true
            });
        }

        /// <summary>Reset to the built-in default (drops any saved template).</summary>
        public void ResetTemplate(int ownerType, int ownerId, string meetingTypeKey)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE BoardMeetingTemplates SET IsActive=0 WHERE OwnerType=@0 AND OwnerId=@1 AND MeetingTypeKey=@2 AND IsActive=1",
                ownerType, ownerId, meetingTypeKey);
        }

        private static List<BoardTemplateItem> DeserializeItems(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonSerializer.Deserialize<List<BoardTemplateItem>>(json, JsonOpts) ?? new(); }
            catch { return new(); }
        }
    }
}
