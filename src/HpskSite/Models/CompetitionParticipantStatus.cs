using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Records that a shooter will not produce any further results in a competition — either
    /// they never started (DNS) or they broke off part way (DNF).
    ///
    /// Why this exists: a missing result row is ambiguous between three states — still shooting,
    /// never started, and withdrew. Nothing could tell them apart, so anything asking "is this
    /// shooter finished?" had to guess. The finals särskjutning gate is the first real consumer.
    ///
    /// Identity-based: keyed by (CompetitionId, MemberId, ShootingClass) so regenerating start
    /// lists or merging classes cannot orphan a status. A shooter competing in several weapon
    /// classes can withdraw from one and finish another, hence ShootingClass in the key.
    ///
    /// This deliberately does NOT live on the result-entry tables. Their Shots column is NOT NULL
    /// and validation demands exactly five valid shots, where "0" is valid — so a placeholder row
    /// would be indistinguishable from a genuine zero series.
    /// </summary>
    [TableName("CompetitionParticipantStatus")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionParticipantStatus
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";

        /// <summary>"DNS" (never started) or "DNF" (started, did not finish).</summary>
        public string Status { get; set; } = "";

        /// <summary>
        /// The first series the shooter did NOT shoot. Null means "from the very start", which is
        /// the normal shape for a plain DNS.
        ///
        /// One nullable int expresses every case the organiser needs: 1 (or null) = never took part
        /// at all; qualifying+1 = shot the qualifying round but skipped the final; 9 = broke off
        /// after series 8. Anything at or after this number will never arrive.
        /// </summary>
        public int? FromSeriesNumber { get; set; }

        /// <summary>Optional free-text reason ("sjuk", "vapenfel"). Shown to the organiser only.</summary>
        public string? Note { get; set; }

        public int SetBy { get; set; }
        public DateTime SetAt { get; set; } = DateTime.Now;

        public const string Dns = "DNS";
        public const string Dnf = "DNF";

        public static bool IsValidStatus(string? status) =>
            status == Dns || status == Dnf;

        /// <summary>Swedish label for result lists and admin screens.</summary>
        public static string DisplayLabel(string? status) => status switch
        {
            Dns => "Ej start",
            Dnf => "Bruten",
            _ => ""
        };
    }
}
