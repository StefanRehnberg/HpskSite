using HpskSite.Models.Events;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// <b>Enda vägen in till klubbarnas och kretsarnas händelser</b> (sajtstrukturen fas 2a —
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
    public interface IClubEventStore
    {
        /// <summary>Vilka operatörstillagda fält lagringen kan bära.</summary>
        ClubEventFieldSupport Support { get; }

        /// <summary>Händelsen, publicerad eller inte, eller null.</summary>
        ClubEventRecord? Get(int id);

        /// <summary>Ägarens händelser. <paramref name="publishedOnly"/> = det besökare ser.</summary>
        IReadOnlyList<ClubEventRecord> ListForOwner(int ownerId, bool publishedOnly = true);

        /// <summary>Alla publicerade händelser (flödet och statistiken).</summary>
        IReadOnlyList<ClubEventRecord> ListAllPublished();

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
}
