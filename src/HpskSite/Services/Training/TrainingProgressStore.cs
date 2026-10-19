using HpskSite.Models.ViewModels.Training;
using NPoco;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Training
{
    /// <summary>
    /// En medlems framsteg i Skyttetrappan — en rad per medlem. Tiderna är lokal tid (som förut på
    /// medlemmen). <c>CurrentLevel</c>/<c>CurrentStep</c> är härledda ur stegen men lagras för statistiken.
    /// </summary>
    [TableName("MemberTrainingProgress")]
    [PrimaryKey("MemberId", AutoIncrement = false)]
    public class MemberTrainingProgressRow
    {
        public int MemberId { get; set; }
        public int CurrentLevel { get; set; } = 1;
        public int CurrentStep { get; set; } = 1;
        public DateTime? TrainingStartDate { get; set; }
        public DateTime? LastActivity { get; set; }
        public string? Notes { get; set; }
        /// <summary>JSON-listan med <see cref="StepCompletion"/> (bara steg som gjorts här — aldrig märkeskrediterade).</summary>
        public string CompletedSteps { get; set; } = "[]";
    }

    /// <summary>
    /// <b>ENDA</b> stället Skyttetrappans framsteg läses och skrivs (skalplanen B5, 2026-10-19).
    ///
    /// <para><b>Varför:</b> framstegen låg som sex medlemsegenskaper, så varje godkänt steg var en hel
    /// medlemssparning — Umbracos skrivlås för medlemmar och omindexering i Examine — och statistiken
    /// fick läsa JSON:en ur egenskaperna för hela registret. Här är det en rad per medlem.</para>
    ///
    /// <para>⚠️ Märkeskrediterade steg (<see cref="StepCompletion.FromBadge"/>) lagras aldrig — de
    /// härleds vid varje läsning av <c>TrainingBadgeCreditService</c>. Samma regel som förut.</para>
    /// </summary>
    public class TrainingProgressStore
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public TrainingProgressStore(IUmbracoDatabaseFactory databaseFactory) => _databaseFactory = databaseFactory;

        private const string Columns = "MemberId, CurrentLevel, CurrentStep, TrainingStartDate, LastActivity, Notes, CompletedSteps";

        /// <summary>Medlemmens framsteg (tomt om medlemmen inte börjat).</summary>
        public MemberProgress Load(IMember member, string? clubName = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            var row = db.FirstOrDefault<MemberTrainingProgressRow>(
                $"SELECT {Columns} FROM MemberTrainingProgress WHERE MemberId = @0", member.Id);
            return ToProgress(row, member.Id, member.Name ?? "", member.Email ?? "", clubName);
        }

        /// <summary>Raderna för flera medlemmar (bitar om 1 000 — <c>IN (@0)</c> tar slut kring 2 100).</summary>
        public Dictionary<int, MemberTrainingProgressRow> LoadRows(IEnumerable<int> memberIds)
        {
            var result = new Dictionary<int, MemberTrainingProgressRow>();
            using var db = _databaseFactory.CreateDatabase();
            foreach (var chunk in memberIds.Distinct().Chunk(1000))
                foreach (var r in db.Fetch<MemberTrainingProgressRow>(
                             $"SELECT {Columns} FROM MemberTrainingProgress WHERE MemberId IN (@0)", chunk))
                    result[r.MemberId] = r;
            return result;
        }

        /// <summary>Alla rader — statistiken. En rad per medlem som någonsin rört trappan.</summary>
        public List<MemberTrainingProgressRow> All()
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<MemberTrainingProgressRow>($"SELECT {Columns} FROM MemberTrainingProgress");
        }

        /// <summary>Medlemmar som börjat trappan (<see cref="MemberProgress.IsActive"/>).</summary>
        public List<int> ActiveMemberIds()
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>("SELECT MemberId FROM MemberTrainingProgress WHERE TrainingStartDate IS NOT NULL");
        }

        /// <summary>Bygger framstegen ur en rad (null = inte börjat).</summary>
        public static MemberProgress ToProgress(MemberTrainingProgressRow? row, int memberId, string name, string email, string? clubName = null)
        {
            var progress = new MemberProgress
            {
                MemberId = memberId,
                MemberName = name,
                MemberEmail = email,
                PrimaryClubName = clubName
            };
            if (row == null) return progress;

            progress.CurrentLevel = row.CurrentLevel;
            progress.CurrentStep = row.CurrentStep;
            progress.TrainingStartDate = row.TrainingStartDate;
            progress.LastActivityDate = row.LastActivity;
            progress.Notes = row.Notes;
            progress.CompletedSteps = ParseSteps(row.CompletedSteps);
            progress.CalculateCurrentPosition();
            return progress;
        }

        public static List<StepCompletion> ParseSteps(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<StepCompletion>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<StepCompletion>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<StepCompletion>();
            }
            catch
            {
                return new List<StepCompletion>();
            }
        }

        /// <summary>Sparar framstegen (upsert). Senaste aktivitet = nu, som förut.</summary>
        public void Save(MemberProgress progress)
        {
            var stepsToStore = progress.CompletedSteps.Where(c => !c.FromBadge).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(stepsToStore);
            using var db = _databaseFactory.CreateDatabase();
            // ⚠️ Inget @@ROWCOUNT: NPoco läser det som en parameter. IF EXISTS med låset i stället.
            db.Execute(@"
SET XACT_ABORT ON; BEGIN TRANSACTION;
IF EXISTS (SELECT 1 FROM MemberTrainingProgress WITH (UPDLOCK, HOLDLOCK) WHERE MemberId = @0)
    UPDATE MemberTrainingProgress
       SET CurrentLevel = @1, CurrentStep = @2, TrainingStartDate = @3, LastActivity = @4,
           Notes = CASE WHEN @5 IS NULL OR @5 = '' THEN Notes ELSE @5 END, CompletedSteps = @6
     WHERE MemberId = @0;
ELSE
    INSERT INTO MemberTrainingProgress (MemberId, CurrentLevel, CurrentStep, TrainingStartDate, LastActivity, Notes, CompletedSteps)
    VALUES (@0, @1, @2, @3, @4, NULLIF(@5, ''), @6);
COMMIT TRANSACTION;",
                progress.MemberId, progress.CurrentLevel, progress.CurrentStep,
                progress.TrainingStartDate, DateTime.Now, progress.Notes, json);
        }

        /// <summary>Nollställer framstegen (sajtadmins "Återställ").</summary>
        public void Delete(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            db.Execute("DELETE FROM MemberTrainingProgress WHERE MemberId = @0", memberId);
        }
    }
}
