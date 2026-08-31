using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One person's relationship to one club/krets event: the sign-up, the attendance, or both.
    ///
    /// ONE row per (event, member) carries both acts deliberately — the upprop screen IS the
    /// sign-up list plus whoever turned up unannounced, so a shared row is the shape the screen
    /// has. Two tables would need an outer join on every read and could disagree about who is on
    /// the list, which is the whole question.
    ///
    /// The two acts are told apart by WHICH FIELDS are set, never by a type column:
    /// <list type="bullet">
    /// <item><c>SignedUpAt</c> set, <c>AttendanceStatus</c> null — signed up, roll-call not taken.</item>
    /// <item><c>SignedUpAt</c> null, <c>AttendanceStatus</c> set — turned up unannounced.</item>
    /// <item>both set — signed up and ticked off.</item>
    /// </list>
    /// </summary>
    [TableName("ClubEventParticipant")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubEventParticipant
    {
        public int Id { get; set; }

        /// <summary>The <c>clubSimpleEvent</c> node id. Clubs AND regions use that same doctype.</summary>
        public int EventId { get; set; }

        public int MemberId { get; set; }

        /// <summary>Snapshot, same reason as <c>StaffHelpSignup</c>: the list must stay readable
        /// for someone who changed their name or left the club.</summary>
        public string MemberName { get; set; } = "";

        // ── Sign-up ──
        public DateTime? SignedUpAt { get; set; }
        public int? SignedUpByMemberId { get; set; }
        public string? SignedUpNote { get; set; }

        /// <summary>Set when withdrawn. The row SURVIVES — a fee snapshot and the history hang off it.</summary>
        public DateTime? CancelledAt { get; set; }
        public int? CancelledByMemberId { get; set; }

        // ── Attendance ──
        /// <summary>
        /// <see cref="ClubEvents.AttendancePresent"/> / <see cref="ClubEvents.AttendanceAbsent"/> /
        /// <see cref="ClubEvents.AttendanceExcused"/>. <b>null = not recorded, which is a THIRD
        /// state and not the same as absent</b> — a mandatory event whose roll-call was never taken
        /// must never read as "nobody came", least of all into a Föreningsintyg.
        /// </summary>
        public string? AttendanceStatus { get; set; }

        /// <summary>The reason, when the board grants a valid absence.</summary>
        public string? AttendanceNote { get; set; }

        public int? RecordedByMemberId { get; set; }
        public DateTime? RecordedAt { get; set; }

        // ── Fee ──
        /// <summary>Snapshot of the event fee at sign-up, so a later change to the event does not
        /// rewrite what somebody already signed up to.</summary>
        public decimal? FeeAmount { get; set; }

        /// <summary>Reserved for the payment step — present now so it can be wired without a migration.</summary>
        public int? InvoiceId { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>Constants and the derived rules for club/krets event sign-up and attendance.</summary>
    public static class ClubEvents
    {
        public const string AttendancePresent = "Present";
        public const string AttendanceAbsent = "Absent";
        public const string AttendanceExcused = "Excused";

        public static readonly string[] AttendanceStatuses =
            { AttendancePresent, AttendanceAbsent, AttendanceExcused };

        public static bool IsAttendanceStatus(string? s) =>
            s == AttendancePresent || s == AttendanceAbsent || s == AttendanceExcused;

        public static string AttendanceDisplay(string? s) => s switch
        {
            AttendancePresent => "Närvarande",
            AttendanceAbsent => "Frånvarande",
            AttendanceExcused => "Giltig frånvaro",
            _ => "Ej registrerad"
        };

        // ── Owner ──
        /// <summary>Doctype aliases an event can hang under. Clubs and regions share the event doctype.</summary>
        public const string OwnerClubAlias = "club";
        public const string OwnerRegionAlias = "regionalPage";
        public const string EventAlias = "clubSimpleEvent";

        /// <summary>Doctype property carrying "deltagande är obligatoriskt" (operator-added).</summary>
        public const string MandatoryProperty = "isMandatory";

        /// <summary>Doctype property carrying the numeric fee. <c>feeAmount</c> is free text ("100 kr/person")
        /// and can never be billed from; parsing it would be a silent wrong-amount generator.</summary>
        public const string FeeProperty = "eventFee";
    }
}
