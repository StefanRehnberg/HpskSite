using NPoco;

namespace HpskSite.Models
{
    [TableName("Documents")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class Document
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int? CategoryId { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string StoredFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public int AccessLevel { get; set; }
        public int SortOrder { get; set; }
        public bool ShowInQuickLinks { get; set; }
        public int DownloadCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public int CreatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
