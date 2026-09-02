using NPoco;

namespace HpskSite.Models.Firearms
{
    /// <summary>
    /// De grunder en läsning av skyddade vapenuppgifter kan ske på.
    ///
    /// <b>Det är SKÄLET, inte rollen.</b> Samma person kan läsa som ägare av sina egna vapen och som
    /// föreningsintygsansvarig i sin klubb, och i en granskning är de två helt olika handlingar.
    /// Loggades rollen i stället skulle "Anna läste" inte gå att skilja från "Anna läste någon
    /// annans".
    /// </summary>
    public static class FirearmAccessReason
    {
        /// <summary>Medlemmen läste sina egna uppgifter. Den överväldigande majoriteten av raderna.</summary>
        public const string Owner = "Owner";

        /// <summary>Klubbens föreningsintygsansvarige läste en medlems uppgifter för ett intyg.</summary>
        public const string Foreningsintyg = "Foreningsintyg";

        /// <summary>Klubbadmin läste ett KLUBBVAPENS uppgifter. Ingen fysisk person berörs.</summary>
        public const string ClubWeapon = "ClubWeapon";

        public static readonly string[] All = { Owner, Foreningsintyg, ClubWeapon };

        public static string Label(string reason) => reason switch
        {
            Owner => "Du själv",
            Foreningsintyg => "Föreningsintygsansvarig",
            ClubWeapon => "Klubbens vapen",
            _ => reason,
        };
    }

    /// <summary>
    /// En läsning av skyddade vapenuppgifter.
    ///
    /// <para><b>⚠️ Bär aldrig klartext.</b> Raden säger att uppgifterna lästes, aldrig vad de var —
    /// en revisionslogg som återger det den bevakar är en andra kopia av hemligheten, och den ligger
    /// dessutom okrypterad.</para>
    /// </summary>
    [TableName("FirearmAccessLog")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FirearmAccessLogEntry
    {
        public int Id { get; set; }

        /// <summary>Vems uppgifter. Null för ett klubbvapen — ingen fysisk person berörs.</summary>
        public int? SubjectMemberId { get; set; }

        /// <summary>Vilket vapen. Null när läsningen gällde hela innehavet (t.ex. en antalsräkning).</summary>
        public int? FirearmId { get; set; }

        /// <summary>⚠️ Aldrig 0. En läsning utan läsare är en logg som inte går att använda.</summary>
        public int ReaderMemberId { get; set; }

        /// <summary>Vilken klubb läsaren agerade för. Null när medlemmen läste sitt eget.</summary>
        public int? ReaderClubId { get; set; }

        /// <summary>Se <see cref="FirearmAccessReason"/>.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Fritext från anropande lager. Aldrig vapendata.</summary>
        public string? Note { get; set; }

        public DateTime OccurredAt { get; set; }

        // ── Visningsfält, inte kolumner ──────────────────────────────────────────────────────────

        [ResultColumn]
        public string? ReaderName { get; set; }

        [ResultColumn]
        public string? ReaderClubName { get; set; }

        [Ignore]
        public string ReasonLabel => FirearmAccessReason.Label(Reason);
    }
}
