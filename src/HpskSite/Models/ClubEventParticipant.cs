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
    /// <para><b>⚠️⚠️ EN RAD ÄR EN PERSON, inte en anmälan.</b> Hugo som anmäler sig själv, sin fru
    /// och sin son blir TRE rader. Det är inte en detalj utan hela poängen: platserna räknas per
    /// rad, priset väljs per rad, och uppropet bockas av per rad. Skulle sällskapet dela en rad
    /// hade evenemanget trott att tre personer tog en plats, att Hugo var skyldig 180 i stället för
    /// 450, och funktionären hade haft en bock att sätta på tre personer.</para>
    ///
    /// <para>Frun och sonen har inga konton. Deras rader bär <c>MemberId = 0</c> och
    /// <see cref="GuestOfMemberId"/> = Hugo. Se <see cref="IsGuest"/>.</para>
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

        /// <summary>
        /// Medlemmens id, eller <see cref="ClubEvents.GuestMemberId"/> (0) för en gäst utan konto.
        ///
        /// <para><b>⚠️ Fråga aldrig på siffran, fråga <see cref="IsGuest"/>.</b> 0 är ett giltigt
        /// värde här och betyder "person utan konto" — inte "ofyllt". Samma sak som att avgiften 0
        /// betyder gratis och inte osatt.</para>
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>Snapshot, same reason as <c>StaffHelpSignup</c>: the list must stay readable
        /// for someone who changed their name or left the club. <b>För en gäst är det här det enda
        /// vi vet om personen</b>, och avsiktligt så — se <see cref="GuestOfMemberId"/>.</summary>
        public string MemberName { get; set; } = "";

        /// <summary>
        /// För en gästrad: den medlem gästen hör till, och som därmed står för platsen och avgiften.
        /// Null = raden är en medlem som står för sig själv.
        ///
        /// <para><b>⚠️⚠️ FÖRVÄXLA INTE MED <see cref="SignedUpByMemberId"/>.</b> Den säger vem som
        /// <i>utförde</i> anmälan och kan vara en funktionär i disken. Den här säger vem som
        /// <i>ansvarar</i>. De går isär exakt när det kostar pengar: anmäler en funktionär Hugos fru
        /// i disken blir SignedUpBy funktionären och GuestOf Hugo — en enda kolumn för båda hade
        /// skickat fakturan till funktionären.</para>
        ///
        /// <para>Den ansvariga medlemmen <b>är</b> kontaktvägen till gästen. Därför lagras ingen
        /// e-post, adress eller födelsedatum för en gäst: namnet räcker för uppropet, och allt
        /// därutöver hade varit personuppgifter om en icke-medlem som vi inte behöver.</para>
        /// </summary>
        public int? GuestOfMemberId { get; set; }

        /// <summary>Raden är en person utan konto. <b>Härlett, ingen kolumn</b> — två fält som kan
        /// säga emot varandra om samma sak är den bugg som aldrig upptäcks.</summary>
        [Ignore]
        public bool IsGuest => MemberId <= 0;

        // ── Sign-up ──
        public DateTime? SignedUpAt { get; set; }

        /// <summary>
        /// <b>Är personen anmäld?</b> Raden finns och är inte avbokad — punkt.
        ///
        /// <para>⚠⚠ <b>FRÅGA ALDRIG <c>SignedUpAt != null</c>.</b> Det var ett OMBUD för den här
        /// frågan, och det var korrekt bara så länge varje rad på listan hade en anmälningstid —
        /// vilket diskens rader aldrig haft före 2026-09-20. Ombudet låg på sex ställen och sa
        /// därför "du är inte anmäld" till någon som står i listan: värden för en gäst gick inte
        /// att välja, lånevapen gick inte att boka, anmälan gick inte att avboka, och det egna
        /// kortet erbjöd en anmälan som redan fanns.</para>
        ///
        /// <para>Tidsstämpeln är en UPPGIFT om anmälan (när den gjordes, och därmed
        /// platsordningen), aldrig beviset för att den finns. Att den kan saknas på en äldre
        /// rad får inte göra personen oanmäld.</para>
        /// </summary>
        /// <para>⚠️ <c>[Ignore]</c> är OBLIGATORISKT. NPoco mappar varje publik egenskap mot
        /// en kolumn, så utan attributet blir varje läsning av tabellen ett SQL-fel 207
        /// ("Invalid column name") — och det visar sig som att en HEL yta slutar svara, inte
        /// som något som rör den här egenskapen. Samma regel som <see cref="IsGuest"/> intill.</para>
        [Ignore]
        public bool IsSignedUp => CancelledAt == null;

        /// <summary>Vem som utförde anmälan. <b>Inte</b> vem som betalar — se <see cref="GuestOfMemberId"/>.</summary>
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

        /// <summary>
        /// Id på den prisrad deltagaren valde, som den såg ut vid anmälan. Null = inget val gjort,
        /// vilket är giltigt: evenemang utan avgift, och anmälningar gjorda innan priserna fanns.
        /// </summary>
        public string? FeePriceId { get; set; }

        /// <summary>
        /// Vad prisraden HETTE vid anmälan.
        ///
        /// <para><b>⚠️⚠️ SNAPSHOT, inte en referens.</b> Prisraderna bor i evenemangets JSON och är
        /// MUTABLA — arrangören får döpa om, ändra belopp och ta bort rader. Deltagarraden är
        /// däremot en överenskommelse och ska bära vad personen faktiskt sa ja till. Samma princip
        /// som verifikationsradens kontonamn och fakturans motpartsnamn.</para>
        ///
        /// <para>Följden är avsiktlig: höjs priset från 180 till 200 står de redan anmälda kvar på
        /// 180. Och tas raden bort helt kan uppslaget mot <c>eventPrices</c> misslyckas medan
        /// etiketten ändå går att visa — det är hela skälet att etiketten lagras och inte bara
        /// id:t.</para>
        /// </summary>
        public string? FeeLabel { get; set; }

        /// <summary>Reserved for the payment step — present now so it can be wired without a migration.</summary>
        public int? InvoiceId { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>Constants and the derived rules for club/krets event sign-up and attendance.</summary>
    public static class ClubEvents
    {
        /// <summary>
        /// <c>MemberId</c> för en deltagare utan konto. <b>0, inte null</b>: kolumnen är NOT NULL
        /// och hela kodbasen adresserar rader med <c>int</c>, men skälet är större än så.
        ///
        /// <para>⚠️ Med <c>int?</c> hade <c>RecordedByMemberId == MemberId</c> i roster-bygget
        /// blivit <c>null == null</c> = sant för varje oregistrerad gäst, och varenda gäst hade
        /// visats som självregistrerad. Med 0 är jämförelsen falsk av sig själv.</para>
        ///
        /// <para>Det unika indexet är därför filtrerat på <c>MemberId &gt; 0</c> — annars hade
        /// evenemanget rymt exakt en gäst.</para>
        /// </summary>
        public const int GuestMemberId = 0;

        /// <summary>Taket för en gästs namn. Väl under kolumnens 200 tecken, så gränsen nås här och
        /// aldrig i databasen: längre namn avvisas med ett besked i stället för att kapas tyst, och
        /// ett tyst kapat namn är fel person på uppropslistan.</summary>
        public const int GuestNameMaxLength = 80;

        /// <summary>Hur många gäster en medlem får ta med sig till ett evenemang. Ett tak finns för
        /// att en felklickad knapp inte ska kunna boka bort hela lokalen.</summary>
        public const int MaxGuestsPerMember = 10;

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

        /// <summary>
        /// Doctype property carrying "sista anmälningsdag" (operator-added, date picker).
        /// <b>The day itself is INCLUSIVE</b> — a deadline of the 20th closes at midnight going
        /// into the 21st, because a date with no clock time would otherwise close the deadline day
        /// before anyone had it, which is the same trap the event-day rule already avoids.
        /// </summary>
        public const string DeadlineProperty = "registrationDeadline";

        /// <summary>
        /// "Har detta datum ett värde?" — <b>och det är inte städning.</b>
        ///
        /// <para>⚠️ En TOM Umbraco-DateTime läses tillbaka som <see cref="DateTime.MinValue"/>, inte
        /// som null (samma fälla som <c>Value&lt;int&gt;()</c> som ger 0 i stället för null). Mätt
        /// 2026-09-04: `GetClubEvents` svarade <c>registrationDeadline: "0001-01-01"</c> för varje
        /// händelse utan deadline. Utan den här normaliseringen förfyller dialogen år 1, nästa
        /// sparning skriver deadlinen <c>0001-01-01</c> på riktigt, och
        /// <c>IsSignupOpen</c> stänger anmälan för alltid på en händelse ingen satt en deadline på.</para>
        ///
        /// <para>Gränsen är år 1900, samma som <c>ClubSimpleEvent.cshtml</c> redan använder för
        /// <c>eventDate</c> — inte likhet mot <c>MinValue</c>, eftersom tidszonskonvertering och
        /// olika lager kan flytta ett minimidatum några timmar och då slinker det igenom.</para>
        /// </summary>
        public static DateTime? RealDate(DateTime? value)
            => value.HasValue && value.Value.Year > 1900 ? value : null;

        /// <summary>
        /// Swish-numret pengarna ska till. <b>Samma alias som på klubben och kretsen</b>, med flit:
        /// händelsens värde är en ÅSIDOSÄTTNING och ägarens är standard, så en klubb som redan
        /// fyllt i sitt nummer får betalning på sina evenemang utan att skriva något alls.
        ///
        /// <para>⚠️ Ett obligatoriskt fält per händelse hade tvingat fram en omskrivning varje
        /// gång, och en felskrivning skickar pengarna till fel konto TYST — vi har ingen Swish-API
        /// som kan säga emot.</para>
        /// </summary>
        public const string SwishProperty = "swishNumber";

        /// <summary>Doctype property carrying the numeric fee. <c>feeAmount</c> is free text ("100 kr/person")
        /// and can never be billed from; parsing it would be a silent wrong-amount generator.</summary>
        public const string FeeProperty = "eventFee";
    }
}
