using HpskSite.Models.Ledger;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Föreningens projekt — bokföringens andra dimension. Se <see cref="LedgerProject"/> för
    /// varför den finns och varför den heter projekt och inte kostnadsställe.
    ///
    /// <para><b>⚠️ Projekten är MUTABEL data, till skillnad från allt annat i liggaren.</b> Ett
    /// projekt får döpas om och stängas; det är verifikationerna som är oföränderliga. Därför
    /// ligger ingen trigger på tabellen, och därför grupperar rapporterna på
    /// <see cref="LedgerJournalEntryLine.ProjectId"/> och inte på namnet — ett namnbyte ska flytta
    /// hela historiken, inte dela projektet i två.</para>
    /// </summary>
    public class LedgerProjectService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerProjectService> _logger;

        public LedgerProjectService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerProjectService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Projekten för en utställare. <paramref name="includeClosed"/> styr om de stängda följer
        /// med — väljaren i bokför-ytan vill ha dem borta, rapporterna vill ha dem kvar.
        /// </summary>
        public List<LedgerProject> List(int issuerType, int issuerId, bool includeClosed = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var sql = includeClosed
                ? "SELECT * FROM dbo.LedgerProject WHERE IssuerType = @0 AND IssuerId = @1 ORDER BY IsClosed, Name"
                : "SELECT * FROM dbo.LedgerProject WHERE IssuerType = @0 AND IssuerId = @1 AND IsClosed = 0 ORDER BY Name";

            return ldb.Fetch<LedgerProject>(sql, issuerType, issuerId);
        }

        public LedgerProject? Get(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, id);
            return ldb.SingleOrDefault<LedgerProject>("SELECT * FROM dbo.LedgerProject WHERE Id = @0", id);
        }

        /// <summary>
        /// Skapar ett projekt. Namnet är unikt per förening — två projekt som heter likadant är
        /// inte två projekt, det är en dubblett som delar rapporten i två.
        /// </summary>
        public LedgerProjectResult Create(
            int issuerType,
            int issuerId,
            string name,
            int byMemberId,
            string? description = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? sourceType = null,
            int? sourceId = null)
        {
            name = (name ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name))
                return LedgerProjectResult.Failed("Projektet måste ha ett namn.");

            if (name.Length > 120)
                return LedgerProjectResult.Failed("Projektnamnet får vara högst 120 tecken.");

            if (startDate is not null && endDate is not null && endDate < startDate)
                return LedgerProjectResult.Failed("Projektet kan inte sluta innan det börjar.");

            using var db = _databaseFactory.CreateDatabase();

            // Kollas före insert för att kunna svara begripligt; det unika indexet är ändå det som
            // garanterar saken när två personer skapar samtidigt.
            var ldb = new LedgerDb(db, issuerId);

            var existing = ldb.FirstOrDefault<LedgerProject>(
                "SELECT * FROM dbo.LedgerProject WHERE IssuerType = @0 AND IssuerId = @1 AND Name = @2",
                issuerType, issuerId, name);

            if (existing is not null)
                return LedgerProjectResult.Failed($"Det finns redan ett projekt som heter \"{name}\".");

            var project = new LedgerProject
            {
                IssuerType = issuerType,
                IssuerId = issuerId,
                Name = name,
                Description = description,
                StartDate = startDate?.Date,
                EndDate = endDate?.Date,
                SourceType = sourceType,
                SourceId = sourceId,
                CreatedUtc = DateTime.UtcNow,
                CreatedByMemberId = byMemberId
            };

            try
            {
                project.Id = ldb.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerProject
                          (IssuerType, IssuerId, Name, Description, StartDate, EndDate,
                           IsClosed, SourceType, SourceId, CreatedUtc, CreatedByMemberId)
                      OUTPUT INSERTED.Id
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10)",
                    project.IssuerType, project.IssuerId, project.Name, project.Description,
                    project.StartDate, project.EndDate, project.IsClosed, project.SourceType,
                    project.SourceId, project.CreatedUtc, project.CreatedByMemberId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Projektet {Namn} kunde inte skapas för utställare {Typ}/{Id}.",
                    name, issuerType, issuerId);

                return LedgerProjectResult.Failed("Projektet kunde inte sparas. Försök igen.");
            }

            return new LedgerProjectResult { Project = project };
        }

        /// <summary>
        /// Hämtar projektet för en källa, eller skapar det.
        ///
        /// <para><b>⚠️ Det är den här metoden som gör projektmärkningen gratis där den betyder
        /// mest.</b> De hundratals automatiska anmälnings- och kioskraderna vet redan vilken
        /// tävling de hör till, så ingen människa behöver välja något — Hallands krets bokförde
        /// ~300 Swish-rader för hand 2025, och varenda en av dem hörde till en tävling systemet
        /// känner. Kvar för handpåläggning blir bara kassörens egna rader.</para>
        ///
        /// <para>Namnet används bara när projektet skapas. Har föreningen döpt om det sedan dess
        /// behålls deras namn — deras ord vinner alltid över vårt.</para>
        ///
        /// <para><b>⚠️⚠️ NAMNKROCKEN FÅR ALDRIG KOPPLA IHOP TVÅ OLIKA KÄLLOR.</b> Första versionen
        /// föll tillbaka på "finns det ett projekt med samma namn, ta det". Men klubben kopierar
        /// "Nybörjarkväll" varje vecka, och "Klubbmästerskap" finns varje år — så den andra
        /// händelsens rader hade hamnat på den förstas projekt, frusna, för alltid. Nu prövas
        /// <paramref name="nameCandidates"/> i ordning (<see cref="LedgerProjectSource.NameCandidates"/>),
        /// och ett befintligt projekt med samma namn tas bara över om det är ett som kassören skapat
        /// för hand och som ännu inte hör till någon källa. Då BINDS det, så nästa källa med samma
        /// namn inte kan ta det också.</para>
        /// </summary>
        /// <returns>Projektet, och om det skapades nu. <c>null</c> om inget gick att skapa.</returns>
        public (LedgerProject Project, bool Created)? EnsureForSource(
            int issuerType,
            int issuerId,
            string sourceType,
            int sourceId,
            IReadOnlyList<string> nameCandidates,
            int byMemberId)
        {
            var bySource = FindBySource(issuerType, issuerId, sourceType, sourceId);
            if (bySource is not null) return (bySource, false);

            foreach (var raw in nameCandidates)
            {
                var name = (raw ?? "").Trim();
                if (name.Length == 0) continue;

                using (var db = _databaseFactory.CreateDatabase())
                {
                    var ldb = new LedgerDb(db, issuerId);

                    var sameName = ldb.FirstOrDefault<LedgerProject>(
                        "SELECT * FROM dbo.LedgerProject WHERE IssuerType = @0 AND IssuerId = @1 AND Name = @2",
                        issuerType, issuerId, name);

                    if (sameName is not null)
                    {
                        // Hör det redan till en källa (en annan tävling) är namnet upptaget — pröva
                        // nästa kandidat. Aldrig "ta det ändå".
                        if (!string.IsNullOrEmpty(sameName.SourceType)) continue;

                        // Ett handskapat, obundet projekt med exakt det namnet: kassören förutsåg
                        // tävlingen. Bind det. ⚠️ Villkoret i WHERE är spärren — två samtidiga
                        // postningar får inte båda binda samma projekt till var sin källa.
                        var bound = ldb.Execute(
                            @"UPDATE dbo.LedgerProject SET SourceType = @1, SourceId = @2
                               WHERE Id = @0 AND SourceType IS NULL",
                            sameName.Id, sourceType, sourceId);

                        if (bound == 1)
                        {
                            sameName.SourceType = sourceType;
                            sameName.SourceId = sourceId;
                            _logger.LogInformation(
                                "Projektet {Namn} ({Id}) kopplades till {Typ}/{KallId} — kassören hade skapat det i förväg.",
                                name, sameName.Id, sourceType, sourceId);
                            return (sameName, false);
                        }

                        // Någon annan hann före; kanske med just vår källa.
                        var raced = FindBySource(issuerType, issuerId, sourceType, sourceId);
                        if (raced is not null) return (raced, false);
                        continue;
                    }
                }

                var created = Create(issuerType, issuerId, name, byMemberId,
                                     sourceType: sourceType, sourceId: sourceId);

                if (created.Success) return (created.Project!, true);

                // Misslyckades skapandet kan det vara en samtidig postning för SAMMA källa som hann
                // före (det unika indexet på källan). Då finns projektet nu.
                var afterFail = FindBySource(issuerType, issuerId, sourceType, sourceId);
                if (afterFail is not null) return (afterFail, false);
            }

            _logger.LogWarning(
                "Inget projekt kunde skapas för {Typ}/{KallId} hos utställare {IssuerType}/{IssuerId} — "
                + "alla {Antal} namnförslag var upptagna eller föll.",
                sourceType, sourceId, issuerType, issuerId, nameCandidates.Count);
            return null;
        }

        private LedgerProject? FindBySource(int issuerType, int issuerId, string sourceType, int sourceId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return new LedgerDb(db, issuerId).FirstOrDefault<LedgerProject>(
                @"SELECT * FROM dbo.LedgerProject
                   WHERE IssuerType = @0 AND IssuerId = @1 AND SourceType = @2 AND SourceId = @3",
                issuerType, issuerId, sourceType, sourceId);
        }

        /// <summary>
        /// Döper om, beskriver om eller ändrar datum. <b>Historiken följer med</b> — raderna pekar
        /// på id:t, inte på namnet.
        /// </summary>
        public LedgerProjectResult Update(
            int id, string name, string? description, DateTime? startDate, DateTime? endDate)
        {
            name = (name ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name))
                return LedgerProjectResult.Failed("Projektet måste ha ett namn.");

            if (startDate is not null && endDate is not null && endDate < startDate)
                return LedgerProjectResult.Failed("Projektet kan inte sluta innan det börjar.");

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, id);

            var project = ldb.SingleOrDefault<LedgerProject>(
                "SELECT * FROM dbo.LedgerProject WHERE Id = @0", id);

            if (project is null)
                return LedgerProjectResult.Failed("Projektet hittades inte.");

            var clash = ldb.FirstOrDefault<LedgerProject>(
                @"SELECT * FROM dbo.LedgerProject
                   WHERE IssuerType = @0 AND IssuerId = @1 AND Name = @2 AND Id <> @3",
                project.IssuerType, project.IssuerId, name, id);

            if (clash is not null)
                return LedgerProjectResult.Failed($"Det finns redan ett projekt som heter \"{name}\".");

            project.Name = name;
            project.Description = description;
            project.StartDate = startDate?.Date;
            project.EndDate = endDate?.Date;

            ldb.Execute(
                @"UPDATE dbo.LedgerProject
                     SET Name = @1, Description = @2, StartDate = @3, EndDate = @4
                   WHERE Id = @0",
                project.Id, project.Name, project.Description, project.StartDate, project.EndDate);

            return new LedgerProjectResult { Project = project };
        }

        /// <summary>
        /// Stänger eller öppnar ett projekt.
        ///
        /// <para>⚠️ Stängt betyder bara <i>visa mig inte i väljaren</i>. Rapporterna läser det
        /// ändå, och en sen kostnad kan fortfarande bokföras på det — en tävlingsfaktura kommer
        /// ofta veckor efter tävlingen, och att vägra den hade tvingat den till "inget projekt".
        /// <b>Projekt raderas aldrig</b>, av samma skäl som konton inte gör det: bokförda rader
        /// pekar på dem, och en revisor ska kunna få dem förklarade.</para>
        /// </summary>
        public LedgerProjectResult SetClosed(int id, bool closed)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, id);

            var project = ldb.SingleOrDefault<LedgerProject>(
                "SELECT * FROM dbo.LedgerProject WHERE Id = @0", id);

            if (project is null)
                return LedgerProjectResult.Failed("Projektet hittades inte.");

            project.IsClosed = closed;

            ldb.Execute("UPDATE dbo.LedgerProject SET IsClosed = @1 WHERE Id = @0",
                        project.Id, project.IsClosed);

            return new LedgerProjectResult { Project = project };
        }

        /// <summary>
        /// Intäkter, kostnader och resultat per projekt för ett räkenskapsår.
        ///
        /// <para><b>Det här är hela skälet att dimensionen finns.</b> Hallands krets hade svaret i
        /// sina egna kolumner utan att någonsin räkna ut det: SSM 2025 gav in 93 064,95 och kostade
        /// 97 262,10 — ett underskott på 4 197,15 som var i princip hela årets underskott, och som
        /// aldrig stod någonstans i årsmöteshandlingen.</para>
        ///
        /// <para>Bara resultatkonton räknas: klass 3 och 8 på intäktssidan, klass 4–7 på
        /// kostnadssidan. Balanskonton (1 och 2) kan bära ett projekt — en projektspecifik
        /// bankdragning gör det — men de hör inte hemma i ett resultat.</para>
        ///
        /// <para><b>⚠️ Utan år är det projektets HELA resultat</b> — och det är standardfallet. Ett
        /// projekt är tidsbegränsat och vill ha ett slutresultat för hela sin livstid, inte per
        /// år; en tävling vars fakturor kommer i januari ska inte se ut att ha gått med vinst i
        /// december.</para>
        ///
        /// <para>⚠️ Årsfiltret ligger i JOIN:en, inte i WHERE. I WHERE föll varje projekt utan
        /// rader just det året bort ur listan helt — ett projekt som finns ska synas med noll, inte
        /// försvinna.</para>
        /// </summary>
        public List<LedgerProjectSummary> Summarise(int issuerType, int issuerId, int? fiscalYearId = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var sql = @"SELECT p.Id AS ProjectId,
                               p.Name AS ProjectName,
                               p.IsClosed,
                               p.SourceType,
                               p.SourceId,
                               ISNULL(SUM(CASE WHEN x.AccountNumber BETWEEN 3000 AND 3999
                                                 OR x.AccountNumber BETWEEN 8000 AND 8999
                                               THEN x.Credit - x.Debit ELSE 0 END), 0) AS Income,
                               ISNULL(SUM(CASE WHEN x.AccountNumber BETWEEN 4000 AND 7999
                                               THEN x.Debit - x.Credit ELSE 0 END), 0) AS Costs,
                               COUNT(DISTINCT x.JournalEntryId) AS EntryCount
                          FROM dbo.LedgerProject p
                          LEFT JOIN (SELECT l.ProjectId, l.AccountNumber, l.Debit, l.Credit, l.JournalEntryId
                                       FROM dbo.LedgerJournalEntryLine l
                                       JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                                      WHERE @2 IS NULL OR e.FiscalYearId = @2) x
                                 ON x.ProjectId = p.Id
                         WHERE p.IssuerType = @0 AND p.IssuerId = @1
                         GROUP BY p.Id, p.Name, p.IsClosed, p.SourceType, p.SourceId
                         ORDER BY p.IsClosed, p.Name";

            return ldb.Fetch<LedgerProjectSummary>(sql, issuerType, issuerId,
                (object?)fiscalYearId ?? DBNull.Value);
        }
    }

    /// <summary>Resultatet av en ändring. <see cref="Error"/> är null när det gick.</summary>
    public class LedgerProjectResult
    {
        public bool Success => Error is null;

        public string? Error { get; set; }

        public LedgerProject? Project { get; set; }

        public static LedgerProjectResult Failed(string error) => new() { Error = error };
    }

    /// <summary>Ett projekts siffror. Resultatet är härlett, aldrig lagrat.</summary>
    public class LedgerProjectSummary
    {
        public int ProjectId { get; set; }

        public string ProjectName { get; set; } = "";

        public bool IsClosed { get; set; }

        /// <summary>Satt för ett automatiskt projekt: <see cref="LedgerProjectSource"/>.</summary>
        public string? SourceType { get; set; }

        public int? SourceId { get; set; }

        public decimal Income { get; set; }

        public decimal Costs { get; set; }

        /// <summary>Plus = projektet gick ihop. Minus = det kostade mer än det gav.</summary>
        public decimal Net => Income - Costs;

        public int EntryCount { get; set; }
    }
}
