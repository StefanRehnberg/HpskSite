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
        /// <summary>Optional Projekt this config belongs to. Null = standalone. Members of the
        /// project get view + edit on the config (access rolls up).</summary>
        public int? ProjectId { get; set; }
        /// <summary>Private | Club | Region | Public. Overridden by SecretUntil while still in force.</summary>
        public string Visibility { get; set; } = "Private";
        /// <summary>While &gt; UtcNow only owner + collaborators see the config, regardless of Visibility.</summary>
        public DateTime? SecretUntil { get; set; }
        public string JsonBlob { get; set; } = "";
        /// <summary>Draft | PendingApproval | Approved. Null treated as Draft for legacy rows.</summary>
        public string? ApprovalStatus { get; set; }
        /// <summary>The Banläggare the owner picked to ask. Populated when ApprovalStatus = PendingApproval; cleared on Unapprove.</summary>
        public int? RequestedApproverMemberId { get; set; }
        /// <summary>The Banläggare cert holder who actually approved. Populated only when ApprovalStatus = Approved.</summary>
        public int? ApprovedByMemberId { get; set; }
        public DateTime? ApprovedDate { get; set; }
        /// <summary>Omgång 3 (3.3): banläggarens kommentar när konfigurationen skickades tillbaka (åter till utkast).</summary>
        public string? ReturnComment { get; set; }
        public int? ReturnedByMemberId { get; set; }
        public DateTime? ReturnedDate { get; set; }
        /// <summary>
        /// Omgång 3 (3.3): en delkonfiguration för ett område börjar på områdets första station (stationerna 3–6 heter 3–6,
        /// inte 1–4), så att den kan länkas in i hela banans konfiguration station för station. Null = 1.
        /// </summary>
        public int? FirstStationNumber { get; set; }
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
        /// <summary>Projekt this config belongs to (null = standalone).</summary>
        public int? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        /// <summary>True when the owning project is archived — used by the listing to hide by default.</summary>
        public bool IsInArchivedProject { get; set; }
        public string Visibility { get; set; } = "Private";
        public DateTime? SecretUntil { get; set; }
        public bool IsSecret { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        /// <summary>Omgång 3 (K9): versionen för <see cref="UpdateFaltskytteConfigurationRequest.ExpectedVersion"/>.</summary>
        public string Version => ModifiedDate.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public int StationCount { get; set; }
        public List<CollaboratorView> Collaborators { get; set; } = new();
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
        public bool CanApprove { get; set; }
        public string ApprovalStatus { get; set; } = "Draft";
        public int? RequestedApproverMemberId { get; set; }
        public string? RequestedApproverName { get; set; }
        public int? ApprovedByMemberId { get; set; }
        public string? ApprovedByName { get; set; }
        public DateTime? ApprovedDate { get; set; }
        /// <summary>Omgång 3: senaste återsändningen (visas i utkastet tills någon begär godkännande igen).</summary>
        public string? ReturnComment { get; set; }
        public string? ReturnedByName { get; set; }
        public DateTime? ReturnedDate { get; set; }
        public int FirstStationNumber { get; set; } = 1;
        /// <summary>True when ApprovalStatus == Approved — config-data edits are forbidden.</summary>
        public bool IsLocked { get; set; }
        /// <summary>True when ApprovalStatus = PendingApproval and viewer is the requested approver (or site admin).</summary>
        public bool IsRequestedApprover { get; set; }
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
        /// <summary>
        /// Omgång 3 (Expertgruppen K9): versionen editorn läste (<see cref="FaltskytteConfigurationView.Version"/>). Skiljer den
        /// sig vägras en ändring av banan — annars skrev den som sparade sist över någon annans stationer utan att någon
        /// märkte det. Tom = ingen kontroll (äldre klienter, metadata).
        /// </summary>
        public string? ExpectedVersion { get; set; }
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

    /// <summary>Body for Approve / Unapprove — no extra payload beyond the config id.</summary>
    public class ApprovalActionRequest
    {
        public int ConfigId { get; set; }
        /// <summary>Omgång 3: kommentaren när banläggaren skickar tillbaka (ReturnForChanges).</summary>
        public string? Comment { get; set; }
    }

    /// <summary>
    /// RequestApproval body — owner picks a specific Banläggare to ask.
    /// RequestedApproverMemberId = 0 (or omitted) means owner self-approval (only valid when owner has Banläggare cert).
    /// </summary>
    public class RequestApprovalRequest
    {
        public int ConfigId { get; set; }
        public int RequestedApproverMemberId { get; set; }
    }

    public class BanlaggareCandidateView
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string? ClubName { get; set; }
    }

    /// <summary>
    /// En annan konfiguration som länkar till stationer i den här. <see cref="Name"/>
    /// är null när betraktaren inte får se den konfigurationen.
    /// </summary>
    public class FaltskytteConfigurationLinker
    {
        public string? Name { get; set; }
        public List<int> StationNumbers { get; set; } = new();
    }
}
