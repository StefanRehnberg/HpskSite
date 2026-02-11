using NPoco;

namespace HpskSite.Models
{
    [TableName("DocumentStorageQuotas")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class DocumentStorageQuota
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int StorageLimitMB { get; set; }
    }
}
