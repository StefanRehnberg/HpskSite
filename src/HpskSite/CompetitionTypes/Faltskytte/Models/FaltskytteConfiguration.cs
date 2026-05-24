using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// Standalone Fältskytte station-set configuration that can be reused
    /// across competitions and shared with collaborators. JsonBlob holds the
    /// same shape as the inline competition.stationConfig property; competitions
    /// attach by snapshot-copying JsonBlob into their stationConfig.
    /// </summary>
    [TableName("FaltskytteConfiguration")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskytteConfiguration
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int OwnerMemberId { get; set; }
        public int? OwnerClubId { get; set; }
        /// <summary>Private | Club | Region | Public. Overridden by SecretUntil while still in force.</summary>
        public string Visibility { get; set; } = "Private";
        /// <summary>While &gt; UtcNow only owner + collaborators see the config, regardless of Visibility.</summary>
        public DateTime? SecretUntil { get; set; }
        public string JsonBlob { get; set; } = "";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Member-list collaborator on a configuration. No roles — anyone in the
    /// list can view + edit regardless of Visibility / SecretUntil.
    /// </summary>
    [TableName("FaltskytteConfigurationCollaborator")]
    [PrimaryKey("ConfigId,MemberId", AutoIncrement = false)]
    public class FaltskytteConfigurationCollaborator
    {
        public int ConfigId { get; set; }
        public int MemberId { get; set; }
        public DateTime AddedDate { get; set; } = DateTime.Now;
    }

    // ─── API view models ────────────────────────────────────────────────

    /// <summary>API-side view of a configuration with derived authorization fields.</summary>
    public class FaltskytteConfigurationView
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int OwnerMemberId { get; set; }
        public string OwnerMemberName { get; set; } = "";
        public int? OwnerClubId { get; set; }
        public string? OwnerClubName { get; set; }
        public string Visibility { get; set; } = "Private";
        public DateTime? SecretUntil { get; set; }
        public bool IsSecret { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public int StationCount { get; set; }
        public List<CollaboratorView> Collaborators { get; set; } = new();
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
        /// <summary>JSON included only when caller has view rights and is on the editor page.</summary>
        public string? JsonBlob { get; set; }
    }

    public class CollaboratorView
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public DateTime AddedDate { get; set; }
    }

    // ─── Request DTOs ───────────────────────────────────────────────────

    public class CreateFaltskytteConfigurationRequest
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int? OwnerClubId { get; set; }
        public string Visibility { get; set; } = "Private";
        /// <summary>String to avoid System.Text.Json's strict ISO 8601 binding (Flatpickr sends "Y-m-d H:i").</summary>
        public string? SecretUntil { get; set; }
        /// <summary>Optional starting JSON. If null, an empty default is generated.</summary>
        public string? JsonBlob { get; set; }
    }

    public class UpdateFaltskytteConfigurationRequest
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int? OwnerClubId { get; set; }
        public string? Visibility { get; set; }
        /// <summary>String — see CreateFaltskytteConfigurationRequest.SecretUntil for the rationale.</summary>
        public string? SecretUntil { get; set; }
        public bool ClearSecretUntil { get; set; }
        public string? JsonBlob { get; set; }
    }

    public class AddCollaboratorRequest
    {
        public int ConfigId { get; set; }
        public int MemberId { get; set; }
    }

    public class RemoveCollaboratorRequest
    {
        public int ConfigId { get; set; }
        public int MemberId { get; set; }
    }
}
