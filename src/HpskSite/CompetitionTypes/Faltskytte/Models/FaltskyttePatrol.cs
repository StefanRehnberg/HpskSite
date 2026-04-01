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
