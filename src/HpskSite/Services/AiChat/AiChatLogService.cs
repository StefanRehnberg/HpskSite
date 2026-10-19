using System.Globalization;
using System.Text.RegularExpressions;
using HpskSite.Services.Hosting;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.AiChat
{
    /// <summary>En loggad fråga till AI-chatten. Anonym — ingen frågeställare lagras (GDPR).</summary>
    [TableName("AiChatLog")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class AiChatLogRow
    {
        public int Id { get; set; }
        /// <summary>UTC (registrerad i <c>StoredTimeBasis</c>).</summary>
        public DateTime AskedUtc { get; set; }
        /// <summary>Styckena ur kunskapsbasen frågan fick med sig ("[hela kunskapsbasen]" vid fallback).</summary>
        public string? Sources { get; set; }
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
    }

    public sealed record AiChatLogEntry(DateTime AskedUtc, string Question, string Answer);

    /// <summary>
    /// AI-chattens logg i databasen (skalplanen D3, 2026-10-19). Låg som månadsfiler i
    /// <c>App_Data/AiChatLogs</c>, alltså på en server.
    ///
    /// <para><b>Övergången:</b> saknas tabellen skrivs till filen som förut och statistiken läser
    /// filerna. Finns den importerar <see cref="AiChatLogImportHostedService"/> de gamla filerna en gång
    /// (filen döps om till <c>imported-…</c> när dess rader ligger i tabellen).</para>
    /// </summary>
    public class AiChatLogService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AiChatLogService> _logger;
        private volatile bool _warnedMissingTable;

        public AiChatLogService(IUmbracoDatabaseFactory databaseFactory, IWebHostEnvironment env, ILogger<AiChatLogService> logger)
        {
            _databaseFactory = databaseFactory;
            _env = env;
            _logger = logger;
        }

        public string LegacyDirectory => Path.Combine(_env.ContentRootPath, "App_Data", "AiChatLogs");

        public bool TableExists()
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.AiChatLog', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        }

        /// <summary>Loggar en fråga. Kastar aldrig — chatten får inte falla på loggningen.</summary>
        public void Add(string question, string answer, string sources)
        {
            var asked = DateTime.UtcNow;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                db.Insert(new AiChatLogRow { AskedUtc = asked, Sources = sources, Question = question, Answer = answer });
                return;
            }
            catch (Exception ex)
            {
                if (!_warnedMissingTable)
                {
                    _warnedMissingTable = true;
                    _logger.LogWarning(ex, "AI-chattens logg kunde inte skrivas till tabellen AiChatLog — skriver till filen i stället. Kör Migrations/create-ai-chat-log-table.sql.");
                }
            }
            AppendToLegacyFile(asked, question, answer, sources);
        }

        /// <summary>Alla loggade frågor: tabellen, plus de gamla filer som ännu inte importerats.</summary>
        public List<AiChatLogEntry> All()
        {
            var entries = new List<AiChatLogEntry>();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                entries.AddRange(db.Fetch<AiChatLogRow>("SELECT Id, AskedUtc, Sources, Question, Answer FROM dbo.AiChatLog")
                    .Select(r => new AiChatLogEntry(r.AskedUtc, r.Question, r.Answer)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AiChatLog kunde inte läsas — bara filerna räknas.");
            }
            foreach (var file in LegacyFiles())
            {
                try { entries.AddRange(ParseLegacy(File.ReadAllText(file))); }
                catch { /* en oläsbar fil ska inte fälla statistiken */ }
            }
            return entries;
        }

        public IEnumerable<string> LegacyFiles() =>
            Directory.Exists(LegacyDirectory)
                ? Directory.GetFiles(LegacyDirectory, "chat-*.log")
                : Enumerable.Empty<string>();

        /// <summary>Importerar en gammal fil i en transaktion. Svarar med antalet rader.</summary>
        public int ImportLegacyFile(string file)
        {
            var parsed = ParseLegacy(File.ReadAllText(file));
            using var db = _databaseFactory.CreateDatabase();
            db.BeginTransaction();
            try
            {
                foreach (var e in parsed)
                    db.Insert(new AiChatLogRow { AskedUtc = e.AskedUtc, Question = e.Question, Answer = e.Answer });
                db.CompleteTransaction();
            }
            catch
            {
                db.AbortTransaction();
                throw;
            }
            // Nytt namn som inte börjar med "chat-", så att filen aldrig läses eller importeras igen.
            File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, "imported-" + Path.GetFileName(file)), overwrite: true);
            return parsed.Count;
        }

        private void AppendToLegacyFile(DateTime asked, string question, string answer, string sources)
        {
            try
            {
                Directory.CreateDirectory(LegacyDirectory);
                var logFile = Path.Combine(LegacyDirectory, $"chat-{asked:yyyy-MM}.log");
                var timestamp = asked.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                // "K:" = vilka stycken av kunskapsbasen frågan fick med sig. Står FÖRE "Q:", så
                // läsaren (som läser frågan från "Q:" till "A:") hoppar över den.
                var entry = $"[{timestamp}]\nK: {sources.Replace('\n', ' ')}\nQ: {question}\nA: {answer}\n---\n";
                File.AppendAllText(logFile, entry);
            }
            catch { }
        }

        /// <summary>
        /// Början på en loggad fråga: "[2026-04-10 14:32:15]" först på en rad.
        /// ⚠️ GRÄNSEN ÄR DEN HÄR RUBRIKEN, ALDRIG "---" som skrivaren lägger efter svaret — ett svar är
        /// Markdown och innehåller ofta en vågrät linje (rättat 2026-09-03).
        /// </summary>
        private static readonly Regex ChatEntryHeaderRegex = new(
            @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Läser en gammal loggfil. Tiden i rubriken är UTC.</summary>
        public static List<AiChatLogEntry> ParseLegacy(string text)
        {
            var entries = new List<AiChatLogEntry>();
            // Normaliserat till LF så samma positioner gäller oavsett hur filen skrevs.
            var content = text.Replace("\r\n", "\n");
            var headers = ChatEntryHeaderRegex.Matches(content);
            for (int i = 0; i < headers.Count; i++)
            {
                var start = headers[i].Index;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : content.Length;
                var block = content.Substring(start, end - start);

                // Bara tiden läses ur rubriken; äldre rader bar en e-postadress där, och den ignoreras
                // med flit så att ingen identitet når appen.
                if (!DateTime.TryParseExact(headers[i].Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                    continue;

                var lines = block.Split('\n').ToList();
                lines.RemoveAt(0);

                // Skrivaren avslutar varje fråga med en "---"-rad. Ta bort exakt EN — ett svar vars
                // sista rad själv är en linje behåller den.
                while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0 && lines[^1].Trim() == "---") lines.RemoveAt(lines.Count - 1);

                // Frågan är allt från "Q: " till "A: "-raden — en inklistrad fråga kan vara flera rader.
                var answerAt = lines.FindIndex(l => l.StartsWith("A: ", StringComparison.Ordinal));
                var questionAt = lines.FindIndex(l => l.StartsWith("Q: ", StringComparison.Ordinal));

                var question = "";
                if (questionAt >= 0)
                {
                    var qEnd = answerAt > questionAt ? answerAt : lines.Count;
                    question = string.Join("\n", lines.GetRange(questionAt, qEnd - questionAt)).Substring(3).Trim();
                }

                // Radbrytningarna BEHÅLLS — svaret är Markdown.
                var answer = "";
                if (answerAt >= 0)
                    answer = string.Join("\n", lines.GetRange(answerAt, lines.Count - answerAt)).Substring(3).Trim();

                if (question.Length == 0 && answer.Length == 0) continue;
                entries.Add(new AiChatLogEntry(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc), question, answer));
            }
            return entries;
        }
    }

    /// <summary>
    /// Importerar AI-chattens gamla loggfiler till tabellen, en gång, två minuter efter start.
    /// Gör ingenting när tabellen saknas eller när inga filer finns kvar.
    /// </summary>
    public class AiChatLogImportHostedService : IsolatedBackgroundService
    {
        private readonly AiChatLogService _log;
        private readonly ILogger<AiChatLogImportHostedService> _logger;

        public AiChatLogImportHostedService(AiChatLogService log, ILogger<AiChatLogImportHostedService> logger)
        {
            _log = log;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                var files = _log.LegacyFiles().ToList();
                if (files.Count == 0 || !_log.TableExists()) return;
                foreach (var file in files)
                {
                    var n = _log.ImportLegacyFile(file);
                    _logger.LogInformation("AI-chattens logg: {Count} frågor ur {File} fördes in i tabellen.", n, Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI-chattens gamla loggfiler kunde inte föras in i tabellen.");
            }
        }
    }
}
