using HpskSite.Models.StartLists;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// <b>Enda vägen in till tävlingarnas startlistor</b> (skalplanen fas C2 — <c>notes/skalplan-2026-10-12.md</c>).
    /// Samma form som <c>ICompetitionResultStore</c> och <c>ICompetitionRegistrationStore</c>.
    ///
    /// <para>I dag finns en implementation, <see cref="NodeStartListStore"/>, mot <c>precisionStartList</c>-
    /// och <c>finalsStartList</c>-noderna (även de under den gamla hubben). Sedan tillkommer en tabell och
    /// listorna flyttas EN gång, med bevarade id:n. Ingen kod utanför lagringen får läsa noderna.</para>
    ///
    /// <para><b>⚠️ Skriv genom <see cref="Update"/></b>: den läser listan färskt, låter anroparen ändra
    /// den och skriver bara det som ändrades.</para>
    /// </summary>
    public interface IStartListStore
    {
        /// <summary>Tävlingens listor (senast sparade läget), i nodordning. Utan sort: båda.</summary>
        IReadOnlyList<StartListRecord> List(int competitionId, StartListKind? kind = null);

        /// <summary>
        /// Tävlingens listor som de syns PUBLIKT (nodlagringen: den publicerade cachen), för sidor och kort.
        /// Billigt — ingen innehållsläsning per lista.
        /// </summary>
        IReadOnlyList<StartListRecord> ListPublished(int competitionId, StartListKind? kind = null);

        /// <summary>En lista, oavsett tävling. Null när id:t inte är en startlista.</summary>
        StartListRecord? Get(int startListId);

        /// <summary>
        /// Precisionsfamiljens grundomgång: tävlingens FÖRSTA grundomgångslista, annars (äldre tävlingar under
        /// hubben) den senast genererade. Samma regel som <c>UmbracoStartListRepository</c> alltid haft.
        /// </summary>
        StartListRecord? Primary(int competitionId);

        /// <summary>Skapar en lista. <paramref name="init"/> fyller den innan den sparas första gången.</summary>
        StartListRecord Create(int competitionId, StartListKind kind, string name, Action<StartListRecord> init,
            StartListPublish publish = StartListPublish.Always);

        /// <summary>
        /// Läser listan färskt, kör <paramref name="change"/> och sparar de fält som ändrades. Null när
        /// listan inte finns. <paramref name="publish"/> styr nodlagringens publicering.
        /// </summary>
        StartListRecord? Update(int startListId, Action<StartListRecord> change, StartListPublish publish = StartListPublish.Always);

        /// <summary>Tar bort listan (nodlagringen: avpublicerar och raderar). False när den inte finns.</summary>
        bool Delete(int startListId);
    }
}
