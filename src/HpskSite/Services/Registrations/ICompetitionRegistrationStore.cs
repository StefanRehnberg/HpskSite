using HpskSite.Models.Registrations;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// <b>Enda vägen in till tävlingsanmälningarna</b> (sajtstrukturen fas 1a —
    /// <c>notes/sajtstruktur-plan-2026-10-09.md</c>).
    ///
    /// <para>I dag finns en implementation, <see cref="NodeRegistrationStore"/>, som läser och skriver
    /// <c>competitionRegistration</c>-noderna. I fas 1b tillkommer en SQL-implementation och
    /// anmälningarna flyttas EN gång, med bevarade id:n. Därför får ingen kod utanför lagringen läsa
    /// noderna, deras hubb eller deras egenskaper direkt — varje sådan läsning är ett ställe som slutar
    /// fungera vid bytet.</para>
    ///
    /// <para><b>Läser alltid ALLA anmälningar, publicerade eller inte.</b> Den publicerade cachen har
    /// aldrig varit en pålitlig källa för anmälningar (publiceringen sker i efterhand och misslyckas
    /// tyst under en opublicerad hubb), och efter bytet finns ingen cache alls.</para>
    /// </summary>
    public interface ICompetitionRegistrationStore
    {
        /// <summary>Tävlingens alla anmälningar, i lagringens ordning.</summary>
        IReadOnlyList<RegistrationRecord> List(int competitionId);

        /// <summary>
        /// En anmälan. <b>Tävlingens id krävs</b> — en anmälan identifieras av (tävling, id). En anmälan
        /// som hör till en annan tävling ger null, så ett id från fel tävling aldrig kan träffa.
        /// </summary>
        RegistrationRecord? Get(int competitionId, int registrationId);

        /// <summary>
        /// En anmälan när bara id:t är känt (gamla fakturanoder och liggarrader bär bara det).
        /// Använd <see cref="Get"/> när tävlingen är känd.
        /// </summary>
        RegistrationRecord? GetById(int registrationId);

        /// <summary>Medlemmens anmälan till tävlingen, eller null. Finns flera (äldre data: en per klass)
        /// returneras den första; se <see cref="ListForMemberInCompetition"/>.</summary>
        RegistrationRecord? FindForMember(int competitionId, int memberId);

        /// <summary>Alla medlemmens anmälningar till tävlingen (äldre tävlingar har en per klass).</summary>
        IReadOnlyList<RegistrationRecord> ListForMemberInCompetition(int competitionId, int memberId);

        /// <summary>Alla medlemmens anmälningar, över alla tävlingar.</summary>
        IReadOnlyList<RegistrationRecord> ListForMember(int memberId);

        /// <summary>
        /// Vilka tävlingar medlemmarna är anmälda till, som (medlem, tävling)-par — för listor över
        /// många medlemmar (aktivitetsmärkena). Lätt med flit: laddar inga hela anmälningar.
        /// </summary>
        IReadOnlyList<(int MemberId, int CompetitionId)> CompetitionsForMembers(IReadOnlyCollection<int> memberIds);

        /// <summary>Antal anmälningar per tävling. Utan lista: alla tävlingar.</summary>
        IReadOnlyDictionary<int, int> CountByCompetition(IReadOnlyCollection<int>? competitionIds = null);

        /// <summary>
        /// Tävlingarna som har minst en anmälan registrerad av <paramref name="registeredBy"/> (exakt). En läsning för
        /// hela sajten — statistiken frågade förut per tävling efter simuleringsseederns anmälningar.
        /// </summary>
        IReadOnlySet<int> CompetitionsWithRegisteredBy(string registeredBy);

        /// <summary>
        /// Sparar anmälan: skapar den när <see cref="RegistrationRecord.Id"/> är 0, annars uppdateras
        /// den. Returnerar id:t. Kastar när tävlingen inte finns.
        /// </summary>
        int Save(RegistrationRecord registration);

        /// <summary>Tar bort anmälan. False när den inte finns i tävlingen.</summary>
        bool Delete(int competitionId, int registrationId);

        /// <summary>
        /// <b>Enda skrivvägen för arrangörens beslut "Utanför mästerskapet"</b> för en klassrad (utanför-mästerskapet, S5). Beslutet bor i
        /// <c>CompetitionRegistrationClassDecision</c>, som <see cref="Save"/> aldrig skriver. <paramref name="value"/> null tar bort beslutet
        /// (då gäller förslaget). Varje ändring loggas i <c>ChampionshipDecisionLog</c>. Anmälan låses (UPDLOCK) så att en samtidig
        /// <see cref="Save"/> inte kan städa bort beslutet mitt i. Klassnyckeln tas i valfri form (Id eller visningsnamn).
        /// </summary>
        OutOfCompetitionOutcome SetOutOfCompetition(int competitionId, int registrationId, string classKey, bool? value,
            int? decidedByMemberId, string reason = "organiser");

        /// <summary>
        /// Tar bort ALLA beslut på anmälan (flaggan följer inte med när en anmälan flyttas till en annan tävling eller plats).
        /// Loggas med <paramref name="reason"/>. Returnerar antalet borttagna beslut.
        /// </summary>
        int ClearOutOfCompetition(int competitionId, int registrationId, string reason);
    }

    /// <summary>Utfallet av <see cref="ICompetitionRegistrationStore.SetOutOfCompetition"/>.</summary>
    public enum OutOfCompetitionOutcome
    {
        /// <summary>Beslutet sattes, ändrades eller togs bort.</summary>
        Changed,
        /// <summary>Beslutet fanns redan med samma värde (eller fanns inte och skulle tas bort). Inget loggades.</summary>
        Unchanged,
        /// <summary>Anmälan finns inte i tävlingen.</summary>
        RegistrationNotFound,
        /// <summary>Anmälan har ingen sådan klassrad — ett beslut kan inte sättas på något som inte finns.</summary>
        ClassNotFound
    }
}
