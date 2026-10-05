using System.Text.Json;
using HpskSite.Models;
using HpskSite.Models.Training;
using HpskSite.Shared.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Training
{
    /// <summary>
    /// Fas D: serier som en funktionär registrerar åt en kursdeltagare, på plats, på ett tillfälle.
    ///
    /// <para>Varje serie skriver TVÅ saker (beslut 2026-10-02):</para>
    /// <list type="bullet">
    /// <item><b>Träningsloggen</b> — EN rad per deltagare och tillfälle (<c>TrainingScores</c>), som
    /// serierna läggs till i. Alla serier, även under brons: trappans poängsteg (30/32 p) bygger på dem.
    /// Raden räknas som funktionärsregistrerad i aktivitetssammanställningen, eftersom den finns i
    /// <c>TrainingCourseSeries</c>.</item>
    /// <item><b>En märkesserie</b> (<c>MarkenSeries</c>, godkänd) när totalen når brons, med deltagarens
    /// ålderseftergift. Funktionären som registrerar ÄR valideringen — samma som klubbliggarimporten.</item>
    /// </list>
    ///
    /// <para><b>Vapengrupp C, alltid</b> — nybörjare skjuter .22LR (beslut 2026-10-02).</para>
    ///
    /// <para><b>⚠️ Trappans steg bockas INTE av här än.</b> Stegtexterna i <c>TrainingDefinitions</c> är
    /// fritext ("alla i det svarta", "i följd") och kräver en tolkning till regler. Tills den finns bockar
    /// instruktören stegen för hand i Skyttetrappan.</para>
    /// </summary>
    public class TrainingCourseSeriesService
    {
        public const string WeaponGroup = "C";
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly MarkenLedgerService _ledger;
        private readonly MarkenBaseValorService _baseValor;
        private readonly MarkenCandidateService _candidates;
        private readonly ILogger<TrainingCourseSeriesService> _logger;

        public TrainingCourseSeriesService(IUmbracoDatabaseFactory databaseFactory, MarkenLedgerService ledger,
            MarkenBaseValorService baseValor, MarkenCandidateService candidates, ILogger<TrainingCourseSeriesService> logger)
        {
            _databaseFactory = databaseFactory;
            _ledger = ledger;
            _baseValor = baseValor;
            _candidates = candidates;
            _logger = logger;
        }

        public class SeriesView
        {
            public int Id { get; set; }
            public int MemberId { get; set; }
            public int SeriesNumber { get; set; }
            public int Total { get; set; }
            public int XCount { get; set; }
            public bool ShotByShot { get; set; }
            public string? Valor { get; set; }
            public bool InMarken { get; set; }
            public int Threshold { get; set; }
        }

        public List<SeriesView> ForOccasion(int groupId, int trainingId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<TrainingCourseSeries>(
                    "WHERE TrainingGroupId=@0 AND TrainingId=@1 ORDER BY MemberId, SeriesNumber", groupId, trainingId)
                .Select(s => new SeriesView
                {
                    Id = s.Id, MemberId = s.MemberId, SeriesNumber = s.SeriesNumber, Total = s.Total,
                    XCount = s.XCount, ShotByShot = s.ShotByShot, Valor = s.Valor, InMarken = s.MarkenSeriesId != null
                }).ToList();
        }

        /// <summary>Kraven för deltagaren i år — brons/silver/guld med eftergift — så vyn kan visa dem före första serien.</summary>
        public (int Brons, int Silver, int Guld) Thresholds(int memberId, int year)
        {
            var birth = _candidates.GetBirthYear(memberId, year);
            return (Marken.ThresholdFor(Marken.LevelBrons, WeaponGroup, year, birth),
                    Marken.ThresholdFor(Marken.LevelSilver, WeaponGroup, year, birth),
                    Marken.ThresholdFor(Marken.LevelGuld, WeaponGroup, year, birth));
        }

        public async Task<(SeriesView? Series, string? Error)> RecordAsync(
            int groupId, int clubId, ClubTraining training, int memberId, List<string>? shots, int? total, int actingMemberId)
        {
            var parsed = TrainingCourseRules.ParseSeries(shots, total);
            if (parsed.Error != null) return (null, parsed.Error);

            var year = training.Date.Year;
            var birth = _candidates.GetBirthYear(memberId, year);
            var valor = Marken.ValorFor(parsed.Total, WeaponGroup, year, birth);
            var shotByShot = parsed.Shots != null;

            using var db = _databaseFactory.CreateDatabase();
            int scoreId, number;
            using (var tx = db.GetTransaction())
            {
                // Träningsloggens rad för deltagaren och tillfället — skapas vid första serien.
                scoreId = db.ExecuteScalar<int?>(
                    "SELECT TOP 1 TrainingScoreId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=@0 AND TrainingId=@1 AND MemberId=@2",
                    groupId, training.Id, memberId) ?? 0;
                var entry = new TrainingScoreEntry { MemberId = memberId, TrainingDate = training.Date, WeaponClass = WeaponGroup, Discipline = "Precision" };
                if (scoreId > 0)
                {
                    var json = db.ExecuteScalar<string?>("SELECT SeriesScores FROM dbo.TrainingScores WHERE Id=@0", scoreId);
                    if (json == null) scoreId = 0;   // loggraden borttagen av medlemmen — börja om
                    else entry.DeserializeSeries(json);
                }
                number = entry.Series.Count + 1;
                entry.Series.Add(new TrainingSeries
                {
                    SeriesNumber = number,
                    Shots = parsed.Shots,
                    Total = parsed.Total,
                    XCount = parsed.XCount,
                    EntryMethod = shotByShot ? "ShotByShot" : "SeriesTotal"
                });
                entry.CalculateTotals();
                var now = DateTime.Now;
                if (scoreId > 0)
                {
                    db.Execute("UPDATE dbo.TrainingScores SET SeriesScores=@1, TotalScore=@2, XCount=@3, UpdatedAt=@4 WHERE Id=@0",
                        scoreId, entry.SerializeSeries(), entry.TotalScore, entry.XCount, now);
                }
                else
                {
                    scoreId = Convert.ToInt32(db.Insert("TrainingScores", "Id", true, new
                    {
                        MemberId = memberId,
                        TrainingDate = training.Date,
                        WeaponClass = WeaponGroup,
                        Discipline = "Precision",
                        IsCompetition = false,
                        SeriesScores = entry.SerializeSeries(),
                        TotalScore = entry.TotalScore,
                        XCount = entry.XCount,
                        Notes = $"Kurs: {training.Name}",
                        CreatedAt = now,
                        UpdatedAt = now
                    }));
                }
                tx.Complete();
            }

            int? markenId = null;
            if (valor != null)
            {
                markenId = await _ledger.InsertSeriesAsync(new MarkenSeries
                {
                    MemberId = memberId,
                    ClubId = clubId,
                    BadgeFamily = Marken.FamilyPistolskytte,
                    SeriesType = Marken.SeriesTypePrecision,
                    Year = year,
                    SeriesDate = training.Date,
                    WeaponGroup = WeaponGroup,
                    ClaimedLevel = valor,
                    Shots = shotByShot ? JsonSerializer.Serialize(parsed.Shots) : "[]",
                    Total = parsed.Total,
                    Threshold = Marken.PrecisionThreshold(WeaponGroup, year, birth),
                    Qualifies = parsed.Total >= Marken.PrecisionThreshold(WeaponGroup, year, birth),
                    Status = Marken.StatusVerified,
                    ValidatedByMemberId = actingMemberId,
                    ValidatedDate = DateTime.Now,
                    EnteredByMemberId = actingMemberId,
                    Notes = $"Kursserie: {training.Name} {training.Date:yyyy-MM-dd}"
                });
                try { await _baseValor.RecomputeAsync(memberId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Brons/silver kunde inte räknas om för medlem {Member}", memberId); }
            }

            var row = new TrainingCourseSeries
            {
                TrainingGroupId = groupId, TrainingId = training.Id, MemberId = memberId, TrainingScoreId = scoreId,
                SeriesNumber = number, Total = parsed.Total, XCount = parsed.XCount, ShotByShot = shotByShot,
                Valor = valor, MarkenSeriesId = markenId is > 0 ? markenId : null, RecordedByMemberId = actingMemberId
            };
            db.Insert(row);
            return (new SeriesView
            {
                Id = row.Id, MemberId = memberId, SeriesNumber = number, Total = parsed.Total, XCount = parsed.XCount,
                ShotByShot = shotByShot, Valor = valor, InMarken = row.MarkenSeriesId != null,
                Threshold = Marken.ThresholdFor(Marken.LevelBrons, WeaponGroup, year, birth)
            }, null);
        }

        /// <summary>
        /// Tar bort en felregistrerad serie: ur träningsloggens rad (numreras om), märkesserien om den
        /// skrevs, och kursraden. Raden i loggen tas bort när sista serien försvinner.
        /// </summary>
        public async Task<string?> DeleteAsync(int groupId, int seriesId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var s = db.SingleOrDefault<TrainingCourseSeries>("WHERE Id=@0 AND TrainingGroupId=@1", seriesId, groupId);
            if (s == null) return "Serien finns inte.";

            var json = db.ExecuteScalar<string?>("SELECT SeriesScores FROM dbo.TrainingScores WHERE Id=@0", s.TrainingScoreId);
            if (json != null)
            {
                var entry = new TrainingScoreEntry();
                entry.DeserializeSeries(json);
                entry.Series.RemoveAll(x => x.SeriesNumber == s.SeriesNumber);
                for (var i = 0; i < entry.Series.Count; i++) entry.Series[i].SeriesNumber = i + 1;
                if (entry.Series.Count == 0)
                    db.Execute("DELETE FROM dbo.TrainingScores WHERE Id=@0", s.TrainingScoreId);
                else
                {
                    entry.CalculateTotals();
                    db.Execute("UPDATE dbo.TrainingScores SET SeriesScores=@1, TotalScore=@2, XCount=@3, UpdatedAt=GETDATE() WHERE Id=@0",
                        s.TrainingScoreId, entry.SerializeSeries(), entry.TotalScore, entry.XCount);
                }
            }
            db.Execute("DELETE FROM dbo.TrainingCourseSeries WHERE Id=@0", s.Id);
            // Följande serier på samma tillfälle flyttar upp ett nummer, samma som i loggen.
            db.Execute(@"UPDATE dbo.TrainingCourseSeries SET SeriesNumber = SeriesNumber - 1
                         WHERE TrainingScoreId=@0 AND SeriesNumber > @1", s.TrainingScoreId, s.SeriesNumber);

            if (s.MarkenSeriesId is > 0)
            {
                await _ledger.DeleteSeriesAsync(s.MarkenSeriesId.Value);
                try { await _baseValor.RecomputeAsync(s.MemberId); } catch (Exception ex) { _logger.LogWarning(ex, "Omräkning efter borttagen kursserie"); }
            }
            return null;
        }
    }
}
