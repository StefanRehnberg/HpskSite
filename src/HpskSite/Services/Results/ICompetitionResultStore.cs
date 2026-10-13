using HpskSite.Models.Results;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// <b>Enda vägen in till tävlingarnas resultatlistor</b> (skalplanen fas C1 —
    /// <c>notes/skalplan-2026-10-12.md</c>). Samma form som <c>ICompetitionRegistrationStore</c>.
    ///
    /// <para>I dag finns en implementation, <see cref="NodeCompetitionResultStore"/>, som läser och
    /// skriver <c>competitionResult</c>-noden under tävlingen. Sedan tillkommer en tabell och listorna
    /// flyttas EN gång, med bevarade id:n. Därför får ingen kod utanför lagringen läsa noden eller dess
    /// egenskaper direkt.</para>
    ///
    /// <para><b>⚠️ Skriv alltid genom <see cref="Update"/>, aldrig genom att spara en hel post.</b>
    /// Flera ytor skriver olika fält på samma lista (omräkningen, publiceringen, hederspriserna, klassnamnen),
    /// ofta nästan samtidigt. <see cref="Update"/> läser posten färskt, låter anroparen ändra den och skriver
    /// bara de fält som faktiskt ändrades — så en omräkning aldrig skriver tillbaka ett gammalt
    /// hederspris.</para>
    /// </summary>
    public interface ICompetitionResultStore
    {
        /// <summary>Tävlingens resultatlista, eller null när den inte finns. Läser senast SPARADE läget.</summary>
        CompetitionResultRecord? Get(int competitionId);

        /// <summary>
        /// Flera tävlingars listor på en gång, för listor och kort (en uppslagning, inte en per tävling).
        /// Tävlingar utan lista saknas i svaret.
        /// </summary>
        IReadOnlyDictionary<int, CompetitionResultRecord> GetMany(IReadOnlyCollection<int> competitionIds);

        /// <summary>
        /// Bara om huvudlistan är officiell, per tävling som har en lista — för klubbens och kretsens
        /// tävlingslistor. Läser inte resultatet: förut hämtade listorna hela <c>ResultData</c> per tävling, en
        /// fråga per rad (genomgången 2026-10-13, C).
        /// </summary>
        IReadOnlyDictionary<int, bool> OfficialByCompetition(IReadOnlyCollection<int> competitionIds);

        /// <summary>
        /// Läser listan färskt, kör <paramref name="change"/> och sparar de fält som ändrades. Saknas
        /// listan skapas den när <paramref name="createIfMissing"/> är satt (Preliminär, tom), annars
        /// returneras null och ingenting skrivs. Returnerar posten som den sparades.
        ///
        /// <para>Kastar <see cref="CompetitionResultFieldMissingException"/> när ett ändrat fält inte går
        /// att lagra (en äldre doctype som saknar egenskapen) — en tyst no-op är värre än ett fel. Fråga
        /// <see cref="CanStore"/> först där en saknad egenskap ska ge ett vänligt besked.</para>
        /// </summary>
        CompetitionResultRecord? Update(int competitionId, Action<CompetitionResultRecord> change, bool createIfMissing = false);

        /// <summary>Ser till att listan finns (Preliminär, tom). Returnerar posten.</summary>
        CompetitionResultRecord Ensure(int competitionId);

        /// <summary>Om fältet går att lagra. Tabellen bär alla; en äldre doctype kan sakna några.</summary>
        bool CanStore(CompetitionResultField field);
    }

    /// <summary>Ett ändrat fält som lagringen inte kan skriva (saknad egenskap på doctypen).</summary>
    public class CompetitionResultFieldMissingException : InvalidOperationException
    {
        public CompetitionResultField Field { get; }

        public CompetitionResultFieldMissingException(CompetitionResultField field)
            : base($"Resultatlistan kan inte spara {CompetitionResultFields.Alias(field)} — egenskapen saknas på doctypen competitionResult.")
        {
            Field = field;
        }
    }
}
