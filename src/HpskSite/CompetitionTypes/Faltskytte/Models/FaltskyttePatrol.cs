using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    [TableName("FaltskyttePatrol")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskyttePatrol
    {
        public int Id { get; set; }

        [Required]
        public int CompetitionId { get; set; }

        [Required]
        public int PatrolNumber { get; set; }

        public DateTime? StartTime { get; set; }

        /// <summary>Weapon group for this patrol, e.g. "C", "A", "A+R", "M"</summary>
        public string? WeaponGroup { get; set; }

        /// <summary>
        /// Self-service mode cursor: the station this patrol is currently at.
        /// Advanced when a shooter in the patrol scans a different station's QR;
        /// stations other than this one become read-only for shooters (staff
        /// always retain full edit). Null until the patrol's first scan.
        /// </summary>
        public int? CurrentStation { get; set; }

        /// <summary>
        /// Optional freeform short label appended to the patrol number in
        /// admin + public renderings (e.g. "Lördag fm", "Final"). Primary
        /// use is multi-day competitions.
        /// </summary>
        public string? Label { get; set; }

        /// <summary>
        /// When the patrol was sent off from the start line (UTC), set by a starter
        /// ticking it off on /patrullista. Null = not yet sent. Drives the send-off
        /// screen's "next" (lowest patrol number with DepartedAt null). Requires the
        /// add-departedat-to-faltskyttepatrol.sql column.
        /// </summary>
        public DateTime? DepartedAt { get; set; }
    }

    [TableName("FaltskyttePatrolMember")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskyttePatrolMember
    {
        public int Id { get; set; }

        [Required]
        public int PatrolId { get; set; }

        [Required]
        public int MemberId { get; set; }

        /// <summary>Order within patrol (1-12)</summary>
        [Required]
        public int Position { get; set; }

        [Required]
        public string ShootingClass { get; set; } = "";

        [Required]
        public string MemberName { get; set; } = "";

        [Required]
        public string ClubName { get; set; } = "";
    }
}
