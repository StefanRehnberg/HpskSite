using HpskSite.Models.Competitions;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Enda vägen in till tävlingarna</b> (C3-planen S1, <c>notes/c3-plan-2026-10-13.md</c>). Samma form
    /// som start- och resultatlistornas lagringar.
    ///
    /// <para>I dag läser och skriver <see cref="NodeCompetitionStore"/> <c>competition</c>-noden. Sedan tar
    /// en tabell över, med bevarade id:n (LAGEN). Därför får ingen kod utanför lagringen läsa noden, dess
    /// egenskaper eller slå upp ett tävlings-id med <c>Umbraco.Content(id)</c> — läsarna flyttas hit i S3,
    /// och spärrtestet (<c>CompetitionStoreGuardTests</c>) låter bara talet gå ned.</para>
    ///
    /// <para><b>Två läsningar, som i dag:</b> <see cref="Get"/> är det PUBLICERADE läget (det sidorna visar,
    /// i dag <c>Umbraco.Content(id)</c>), <see cref="GetSaved"/> det senast SPARADE, även opublicerat (det
    /// redigeringen visar, i dag <c>IContentService.GetById</c>). I tabellen är de samma rad.</para>
    /// </summary>
    public interface ICompetitionStore
    {
        /// <summary>Den publicerade tävlingen, eller null när den inte finns eller inte är publicerad.</summary>
        CompetitionInfo? Get(int id);

        /// <summary>Den senast sparade tävlingen, även opublicerad. Null när id:t inte är en tävling.</summary>
        CompetitionInfo? GetSaved(int id);

        /// <summary>Flera publicerade på en gång (en uppslagning för en lista). Saknade id:n saknas i svaret.</summary>
        IReadOnlyDictionary<int, CompetitionInfo> GetMany(IReadOnlyCollection<int> ids);

        /// <summary>Alla publicerade tävlingar, sorterade på id.</summary>
        IReadOnlyList<CompetitionInfo> List();

        /// <summary>Seriens publicerade omgångar, i seriens ordning.</summary>
        IReadOnlyList<CompetitionInfo> ListBySeries(int seriesId);

        /// <summary>
        /// Läser tävlingen färskt, kör <paramref name="change"/> och sparar de fält som ändrades.
        /// <paramref name="expectedVersion"/> = den version användaren såg (<see cref="CompetitionInfo.Version"/>);
        /// skiljer den sig svarar lagringen <see cref="CompetitionUpdateOutcome.Conflict"/> och skriver ingenting.
        /// Null = jämför inte (för system- och bakgrundsskrivningar).
        ///
        /// <para><b>⚠️ <paramref name="change"/> ska vara ren:</b> ingen I/O, ingen avgiftsomräkning, ingen
        /// bokföring. Sådant körs av anroparen EFTER att uppdateringen lyckats.</para>
        /// </summary>
        CompetitionUpdateResult<CompetitionInfo> Update(int id, long? expectedVersion, Action<CompetitionInfo> change);

        /// <summary>
        /// En ny tävling under <paramref name="parentId"/> (serien eller årsmappen). <paramref name="init"/> fyller
        /// posten — samma regel som <see cref="Update"/>: ren, ingen I/O. Tävlingen publiceras.
        /// <see cref="CompetitionUpdateResult{T}.PublishProblem"/> säger om publiceringen inte gick.
        /// </summary>
        CompetitionUpdateResult<CompetitionInfo> Create(int parentId, string name, Action<CompetitionInfo> init);

        /// <summary>
        /// Lägger tävlingen i serien, eller (<paramref name="seriesId"/> null) utanför serier. Kastar
        /// <see cref="InvalidOperationException"/> med ett meddelande som kan visas när serien inte finns.
        /// </summary>
        CompetitionUpdateResult<CompetitionInfo> MoveToSeries(int id, int? seriesId);

        /// <summary>
        /// <b>Mjuk radering</b> (§9a): tävlingen läggs i papperskorgen och kan läggas tillbaka; inget under
        /// den raderas. Spärrarna (lag, resultat, betalningar …) prövas av anroparen genom
        /// <see cref="CompetitionDeleteGuard"/> först.
        /// </summary>
        CompetitionUpdateResult<CompetitionInfo> Trash(int id);
    }

    /// <summary>Seriernas lagring — samma regler som <see cref="ICompetitionStore"/>.</summary>
    public interface ICompetitionSeriesStore
    {
        CompetitionSeriesInfo? Get(int id);
        CompetitionSeriesInfo? GetSaved(int id);
        IReadOnlyDictionary<int, CompetitionSeriesInfo> GetMany(IReadOnlyCollection<int> ids);
        IReadOnlyList<CompetitionSeriesInfo> List();
        CompetitionUpdateResult<CompetitionSeriesInfo> Update(int id, long? expectedVersion, Action<CompetitionSeriesInfo> change);

        /// <summary>En ny serie under <paramref name="parentId"/> (årsmappen). Publiceras.</summary>
        CompetitionUpdateResult<CompetitionSeriesInfo> Create(int parentId, string name, Action<CompetitionSeriesInfo> init);

        /// <summary>Mjuk radering: serien läggs i papperskorgen. Anroparen prövar först att den är tom.</summary>
        CompetitionUpdateResult<CompetitionSeriesInfo> Trash(int id);
    }

    public enum CompetitionUpdateOutcome
    {
        /// <summary>Sparad (eller ingenting att spara).</summary>
        Saved,

        /// <summary>Någon annan har ändrat sedan användaren läste. Ingenting skrevs.</summary>
        Conflict,

        /// <summary>Id:t är ingen tävling (eller serie).</summary>
        NotFound,
    }

    public sealed class CompetitionUpdateResult<T> where T : class
    {
        public CompetitionUpdateOutcome Outcome { get; init; }

        /// <summary>Posten som den sparades — vid konflikt det läge som gäller nu.</summary>
        public T? Record { get; init; }

        /// <summary>Fälten (nodens alias) som skrevs.</summary>
        public IReadOnlyList<string> ChangedFields { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Satt när ändringen sparades men en publicerad tävling inte kunde publiceras om (t.ex. ett obligatoriskt
        /// fält är tomt): då ser sidorna fortfarande det gamla. Anroparen ska säga det i stället för att rapportera
        /// lyckat. Null = publicerat, eller inget att publicera (utkast, tabelläget).
        /// </summary>
        public string? PublishProblem { get; init; }

        public bool Ok => Outcome == CompetitionUpdateOutcome.Saved;

        public const string ConflictMessage = "Någon annan har ändrat tävlingen sedan du öppnade den. Ladda om och gör ändringen igen.";
    }
}
