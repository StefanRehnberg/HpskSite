using HpskSite.Models.Events;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// <b>Lagringen bakom <see cref="IClubEventStore"/></b> (nod eller tabell). Läser UTAN synlighetsprövning och får därför bara anropas av
    /// <see cref="SwitchingClubEventStore"/> och av adressuppslag som själva prövar synligheten (omdirigeringar). Inget annat ska injicera den.
    ///
    /// <para>Historik (sajtstrukturen fas 2a —
    /// <c>notes/sajtstruktur-plan-2026-10-09.md</c>).
    ///
    /// <para>I dag finns <see cref="NodeClubEventStore"/>, som läser och skriver
    /// <c>clubSimpleEvent</c>-noderna. I fas 2 tillkommer en SQL-implementation och händelserna flyttas
    /// EN gång, med bevarade id:n. Därför får ingen kod utanför lagringen läsa händelsenoderna, deras
    /// förälder eller deras egenskaper direkt.</para>
    ///
    /// <para><b>Händelser är publika sidor</b>, till skillnad från anmälningar: en händelse skapas
    /// publicerad, och listorna för besökare visar bara publicerade.</para>
    /// </summary>
    public interface IClubEventBackend
    {
        /// <summary>Vilka operatörstillagda fält lagringen kan bära.</summary>
        ClubEventFieldSupport Support { get; }

        /// <summary>Händelsen, publicerad eller inte, eller null.</summary>
        ClubEventRecord? Get(int id);

        /// <summary>
        /// Flera händelser i en läsning (saknade id:n hoppas över). För listor som annars hade gjort
        /// <see cref="Get"/> per rad — banans kalender gjorde det vid varje QR-incheckning (genomgången 2026-10-13, B).
        /// </summary>
        IReadOnlyList<ClubEventRecord> GetMany(IReadOnlyCollection<int> ids);

        /// <summary>Ägarens händelser. <paramref name="publishedOnly"/> = det besökare ser.</summary>
        IReadOnlyList<ClubEventRecord> ListForOwner(int ownerId, bool publishedOnly = true);

        /// <summary>
        /// Publicerade händelser som börjar i [<paramref name="from"/>, <paramref name="to"/>) — startsidans flöde.
        /// Förut lästes alla publicerade händelser som någonsin funnits (genomgången 2026-10-13, B).
        /// </summary>
        IReadOnlyList<ClubEventRecord> ListPublishedBetween(DateTime from, DateTime to);

        /// <summary>
        /// Alla händelser med en ägare av typen <paramref name="ownerType"/>, publicerade eller inte
        /// (träningsflytten går igenom klubbarnas samtliga händelser).
        /// </summary>
        IReadOnlyList<ClubEventRecord> ListAllForOwnerType(string ownerType);

        /// <summary>
        /// Sparar: skapar när <see cref="ClubEventRecord.Id"/> är 0, annars uppdateras den.
        /// Publicerar när <see cref="ClubEventRecord.IsPublished"/> är satt. Returnerar id:t, eller ett
        /// färdigt felmeddelande (och sparar då ingenting).
        /// </summary>
        Task<(int Id, string? Error)> SaveAsync(ClubEventRecord record);

        /// <summary>Tar bort händelsen. False när den inte finns.</summary>
        bool Delete(int id);

        /// <summary>Publicerar eller avpublicerar (träningsflytten avpublicerar en flyttad händelse).</summary>
        bool SetPublished(int id, bool published);
    }

    /// <summary>
    /// <b>Enda vägen in till klubbarnas och kretsarnas händelser.</b> Varje läsning kräver en <see cref="EventViewer"/>: händelsens egen
    /// synlighet (<see cref="ClubEventRecord.ReadVisibility"/>) prövas i <see cref="SwitchingClubEventStore"/> och på inget annat ställe.
    /// En enskild händelse som tittaren inte får se svarar som en som inte finns (<c>null</c>); i listor lämnas den bort. Skrivning prövar ingen
    /// synlighet — den som får skriva har redan prövats av sin yta, och sparar posten med det <see cref="ClubEventRecord.ReadVisibility"/> den har.
    /// </summary>
    public interface IClubEventStore
    {
        /// <summary>Vilka operatörstillagda fält lagringen kan bära.</summary>
        ClubEventFieldSupport Support { get; }

        /// <summary>Händelsen, publicerad eller inte, eller null (saknas, eller tittaren får inte se den).</summary>
        ClubEventRecord? Get(int id, EventViewer viewer);

        /// <summary>Flera händelser i en läsning (saknade eller osynliga hoppas över).</summary>
        IReadOnlyList<ClubEventRecord> GetMany(IReadOnlyCollection<int> ids, EventViewer viewer);

        /// <summary>Ägarens händelser. <paramref name="publishedOnly"/> = det besökare ser.</summary>
        IReadOnlyList<ClubEventRecord> ListForOwner(int ownerId, EventViewer viewer, bool publishedOnly = true);

        /// <summary>Publicerade händelser som börjar i [<paramref name="from"/>, <paramref name="to"/>) — startsidans flöde.</summary>
        IReadOnlyList<ClubEventRecord> ListPublishedBetween(DateTime from, DateTime to, EventViewer viewer);

        /// <summary>Alla händelser med en ägare av typen <paramref name="ownerType"/>, publicerade eller inte (träningsflytten).</summary>
        IReadOnlyList<ClubEventRecord> ListAllForOwnerType(string ownerType, EventViewer viewer);

        /// <summary>Sparar: skapar när <see cref="ClubEventRecord.Id"/> är 0, annars uppdateras den. Returnerar id:t, eller ett färdigt felmeddelande.</summary>
        Task<(int Id, string? Error)> SaveAsync(ClubEventRecord record);

        /// <summary>Tar bort händelsen. False när den inte finns.</summary>
        bool Delete(int id);

        /// <summary>Publicerar eller avpublicerar (träningsflytten avpublicerar en flyttad händelse).</summary>
        bool SetPublished(int id, bool published);
    }
}
