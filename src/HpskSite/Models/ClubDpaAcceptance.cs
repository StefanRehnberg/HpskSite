using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Evidence that a club (the personuppgiftsansvarige / controller) accepted a given
    /// version of the Personuppgiftsbiträdesavtal (DPA). One row per (ClubId, DpaVersion):
    /// re-accepting the same version is an idempotent upsert; a new version yields a new row,
    /// so the table also serves as the acceptance history.
    ///
    /// The club is considered to have a *current* acceptance when a row exists for the club
    /// with DpaVersion == <see cref="DpaInfo.Version"/>.
    /// </summary>
    [TableName("ClubDpaAcceptance")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubDpaAcceptance
    {
        public int Id { get; set; }

        /// <summary>Content node id of the club (controller).</summary>
        public int ClubId { get; set; }

        /// <summary>The DPA version that was accepted (see <see cref="DpaInfo.Version"/>).</summary>
        public string DpaVersion { get; set; } = "";

        /// <summary>Member id of the club admin who accepted on the club's behalf.</summary>
        public int AcceptedByMemberId { get; set; }

        /// <summary>Display name captured at acceptance time (members can be renamed/removed later).</summary>
        public string? AcceptedByName { get; set; }

        public DateTime AcceptedDate { get; set; } = DateTime.Now;

        /// <summary>Best-effort client IP at acceptance time, for the evidence trail.</summary>
        public string? IpAddress { get; set; }
    }
}
