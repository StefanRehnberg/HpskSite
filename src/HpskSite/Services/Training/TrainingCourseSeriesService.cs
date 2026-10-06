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
            /// <summary>Precision eller Speed (tillämpning).</summary>
            public string SeriesType { get; set; } = Marken.SeriesTypePrecision;
            public string? Target { get; set; }
            public string WeaponGroup { get; set; } = TrainingCourseSeriesService.WeaponGroup;
            /// <summary>Märkesserien väntar på styrelsen/skjutledare (en guldserie registrerad av någon utan den rätten).</summary>
            public bool Pending { get; set; }
        }

        private class SeriesRow : TrainingCourseSeries { public string? MarkenStatus { get; set; } }

        public List<SeriesView> ForOccasion(int groupId, int trainingId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<SeriesRow>(@"SELECT s.*, m.Status AS MarkenStatus FROM dbo.TrainingCourseSeries s
LEFT JOIN dbo.MarkenSeries m ON m.Id = s.MarkenSeriesId
WHERE s.TrainingGroupId=@0 AND s.TrainingId=@1 ORDER BY s.MemberId, s.SeriesType, s.SeriesNumber", groupId, trainingId)
                .Select(s => new SeriesView
                {
                    Id = s.Id, MemberId = s.MemberId, SeriesNumber = s.SeriesNumber, Total = s.Total,
                    XCount = s.XCount, ShotByShot = s.ShotByShot, Valor = s.Valor, InMarken = s.MarkenSeriesId != null,
                    SeriesType = s.SeriesType ?? Marken.SeriesTypePrecision, Target = s.Target,
                    WeaponGroup = s.WeaponGroup ?? WeaponGroup,
                    Pending = s.MarkenStatus == Marken.SeriesStatusPending
                }).ToList();
        }

        /// <summary>Godkända vapengrupper för kursens serier. Allt annat läses som C.</summary>
        public static string NormaliseGroup(string? g) => g is "A" or "B" or "C" ? g : WeaponGroup;

        /// <summary>Kraven för deltagaren i år — brons/silver/guld med eftergift — så vyn kan visa dem före första serien.</summary>
        public (int Brons, int Silver, int Guld) Thresholds(int memberId, int year, string? weaponGroup = null)
        {
            var wg = NormaliseGroup(weaponGroup);
            var birth = _candidates.GetBirthYear(memberId, year);
            return (Marken.ThresholdFor(Marken.LevelBrons, wg, year, birth),
                    Marken.ThresholdFor(Marken.LevelSilver, wg, year, birth),
                    Marken.ThresholdFor(Marken.LevelGuld, wg, year, birth));
        }

        /// <summary>Vad instruktören registrerar. Tillämpning: mål + valören skytten klarade (tom = inte godkänd).</summary>
        public class RecordInput
        {
            public string? SeriesType { get; set; }
            public string? WeaponGroup { get; set; }
            public List<string>? Shots { get; set; }
            public int? Total { get; set; }
            public string? Target { get; set; }
            public string? ClaimedLevel { get; set; }
        }

        /// <param name="canSignOffGuld">Får den som registrerar signera guld (styrelse/skjutledare enligt
        /// <c>MarkenSignoffAuthority</c>)? Annars blir en GULDserie väntande i klubbens valideringskö —
        /// brons och silver godkänner kursens instruktör själv (Stefans beslut 2026-10-06).</param>
        public async Task<(SeriesView? Series, string? Error)> RecordAsync(
            int groupId, int clubId, ClubTraining training, int memberId, RecordInput input, int actingMemberId, bool canSignOffGuld)
        {
            var wg = NormaliseGroup(input.WeaponGroup);
            if (input.SeriesType == Marken.SeriesTypeSpeed)
                return await RecordSpeedAsync(groupId, clubId, training, memberId, wg, input, actingMemberId, canSignOffGuld);

            var parsed = TrainingCourseRules.ParseSeries(input.Shots, input.Total);
            if (parsed.Error != null) return (null, parsed.Error);

            var year = training.Date.Year;
            var birth = _candidates.GetBirthYear(memberId, year);
            var valor = Marken.ValorFor(parsed.Total, wg, year, birth);
            var shotByShot = parsed.Shots != null;

            using var db = _databaseFactory.CreateDatabase();
            int scoreId, number;
            using (var tx = db.GetTransaction())
            {
                // Träningsloggens rad för deltagaren, tillfället och VAPENGRUPPEN — skapas vid första serien.
                scoreId = db.ExecuteScalar<int?>(
                    @"SELECT TOP 1 TrainingScoreId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=@0 AND TrainingId=@1 AND MemberId=@2
                      AND ISNULL(SeriesType,'Precision')='Precision' AND ISNULL(WeaponGroup,'C')=@3 AND TrainingScoreId > 0",
                    groupId, training.Id, memberId, wg) ?? 0;
                var entry = new TrainingScoreEntry { MemberId = memberId, TrainingDate = training.Date, WeaponClass = wg, Discipline = "Precision" };
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
                        WeaponClass = wg,
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
            var pending = false;
            if (valor != null)
            {
                pending = valor == Marken.LevelGuld && !canSignOffGuld;
                markenId = await _ledger.InsertSeriesAsync(Badge(memberId, clubId, training, wg, Marken.SeriesTypePrecision, valor,
                    shotByShot ? JsonSerializer.Serialize(parsed.Shots) : "[]", parsed.Total,
                    Marken.PrecisionThreshold(wg, year, birth), parsed.Total >= Marken.PrecisionThreshold(wg, year, birth),
                    null, pending, actingMemberId));
                await RecomputeAsync(memberId);
            }

            var row = new TrainingCourseSeries
            {
                TrainingGroupId = groupId, TrainingId = training.Id, MemberId = memberId, TrainingScoreId = scoreId,
                SeriesNumber = number, Total = parsed.Total, XCount = parsed.XCount, ShotByShot = shotByShot,
                Valor = valor, MarkenSeriesId = markenId is > 0 ? markenId : null, RecordedByMemberId = actingMemberId,
                SeriesType = Marken.SeriesTypePrecision, WeaponGroup = wg
            };
            db.Insert(row);
            return (new SeriesView
            {
                Id = row.Id, MemberId = memberId, SeriesNumber = number, Total = parsed.Total, XCount = parsed.XCount,
                ShotByShot = shotByShot, Valor = valor, InMarken = row.MarkenSeriesId != null,
                Threshold = Marken.ThresholdFor(Marken.LevelBrons, wg, year, birth),
                SeriesType = Marken.SeriesTypePrecision, WeaponGroup = wg, Pending = pending
            }, null);
        }

        /// <summary>
        /// Tillämpningsserie (B100 50 m / 1/6 C30 25 m): träff inom tiden, ingen poäng. Instruktören anger
        /// vilken valör skytten klarade; tomt = inte godkänd (sparas i kursen, ingen märkesserie). Ingen rad
        /// i träningsloggen — den bär poängserier.
        /// </summary>
        private async Task<(SeriesView? Series, string? Error)> RecordSpeedAsync(int groupId, int clubId, ClubTraining training,
            int memberId, string wg, RecordInput input, int actingMemberId, bool canSignOffGuld)
        {
            if (input.Target is not (Marken.SpeedTargetB100 or Marken.SpeedTargetC30))
                return (null, "Välj mål för tillämpningsserien.");
            var level = string.IsNullOrWhiteSpace(input.ClaimedLevel) ? null : input.ClaimedLevel;
            if (level != null && Marken.LevelOrdinal(level) == 0) return (null, "Välj valör: brons, silver, guld eller inte godkänd.");

            using var db = _databaseFactory.CreateDatabase();
            var number = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=@0 AND TrainingId=@1 AND MemberId=@2 AND SeriesType=@3",
                groupId, training.Id, memberId, Marken.SeriesTypeSpeed) + 1;

            int? markenId = null;
            var pending = false;
            if (level != null)
            {
                pending = level == Marken.LevelGuld && !canSignOffGuld;
                markenId = await _ledger.InsertSeriesAsync(Badge(memberId, clubId, training, wg, Marken.SeriesTypeSpeed, level,
                    "[]", 0, 0, true, input.Target, pending, actingMemberId));
                await RecomputeAsync(memberId);
            }

            var row = new TrainingCourseSeries
            {
                TrainingGroupId = groupId, TrainingId = training.Id, MemberId = memberId, TrainingScoreId = 0,
                SeriesNumber = number, Total = 0, XCount = 0, ShotByShot = false, Valor = level,
                MarkenSeriesId = markenId is > 0 ? markenId : null, RecordedByMemberId = actingMemberId,
                SeriesType = Marken.SeriesTypeSpeed, Target = input.Target, WeaponGroup = wg
            };
            db.Insert(row);
            return (new SeriesView
            {
                Id = row.Id, MemberId = memberId, SeriesNumber = number, Valor = level, InMarken = row.MarkenSeriesId != null,
                SeriesType = Marken.SeriesTypeSpeed, Target = input.Target, WeaponGroup = wg, Pending = pending
            }, null);
        }

        /// <summary>
        /// Märkesserien. Godkänd direkt (instruktören ÄR valideringen) — utom en guldserie från någon som
        /// inte får signera guld: den läggs VÄNTANDE i klubbens valideringskö, utan validerare.
        /// </summary>
        private static MarkenSeries Badge(int memberId, int clubId, ClubTraining training, string wg, string type, string level,
            string shots, int total, int threshold, bool qualifies, string? target, bool pending, int actingMemberId) => new()
        {
            MemberId = memberId,
            ClubId = clubId,
            BadgeFamily = Marken.FamilyPistolskytte,
            SeriesType = type,
            Year = training.Date.Year,
            SeriesDate = training.Date,
            WeaponGroup = wg,
            ClaimedLevel = level,
            Shots = shots,
            Total = total,
            Threshold = threshold,
            Qualifies = qualifies,
            Target = target,
            Status = pending ? Marken.SeriesStatusPending : Marken.StatusVerified,
            ValidatedByMemberId = pending ? null : actingMemberId,
            ValidatedDate = pending ? null : DateTime.Now,
            EnteredByMemberId = actingMemberId,
            Notes = $"Kursserie: {training.Name} {training.Date:yyyy-MM-dd}"
        };

        private async Task RecomputeAsync(int memberId)
        {
            try { await _baseValor.RecomputeAsync(memberId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Brons/silver kunde inte räknas om för medlem {Member}", memberId); }
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

            // Tillämpning har ingen rad i träningsloggen och numreras för sig.
            if (s.SeriesType == Marken.SeriesTypeSpeed)
            {
                db.Execute("DELETE FROM dbo.TrainingCourseSeries WHERE Id=@0", s.Id);
                db.Execute(@"UPDATE dbo.TrainingCourseSeries SET SeriesNumber = SeriesNumber - 1
                             WHERE TrainingGroupId=@0 AND TrainingId=@1 AND MemberId=@2 AND SeriesType=@3 AND SeriesNumber > @4",
                    s.TrainingGroupId, s.TrainingId, s.MemberId, Marken.SeriesTypeSpeed, s.SeriesNumber);
                if (s.MarkenSeriesId is > 0)
                {
                    await _ledger.DeleteSeriesAsync(s.MarkenSeriesId.Value);
                    await RecomputeAsync(s.MemberId);
                }
                return null;
            }

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
