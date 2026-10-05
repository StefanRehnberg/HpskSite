using System.Globalization;
using HpskSite.Services.AiChat;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Cms.Core.Security;

namespace HpskSite.Controllers
{
    public class AiChatController : SurfaceController
    {
        private readonly AiChatService _chatService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AiChatController> _logger;

        public AiChatController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AiChatService chatService,
            IMemberManager memberManager,
            IMemberService memberService,
            IWebHostEnvironment env,
            ILogger<AiChatController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _logger = logger;
            _chatService = chatService;
            _memberManager = memberManager;
            _memberService = memberService;
            _env = env;
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
        {
            if (!_chatService.IsEnabled)
                return Json(new { success = false, message = "AI-chatten är inte aktiverad." });

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad för att använda chatten." });

            if (string.IsNullOrWhiteSpace(request?.Message))
                return Json(new { success = false, message = "Meddelandet kan inte vara tomt." });

            if (request.Message.Length > 2000)
                return Json(new { success = false, message = "Meddelandet är för långt (max 2000 tecken)." });

            try
            {
                var roles = GetUserRoles(currentMember);
                var history = request.History ?? new List<ChatMessage>();

                var result = await _chatService.GetResponseAsync(request.Message, history, roles);

                // GDPR data minimisation: the chat log is only used for feature-popularity
                // analytics, so we do NOT record who asked — only the timestamp, question and
                // answer. The identity is intentionally never passed to LogChat.
                LogChat(request.Message, result.Response, result.Sources);

                return Json(new { success = true, response = result.Response });
            }
            catch (Exception ex)
            {
                // ⚠️ LogError, aldrig Console.WriteLine: prod loggar Warning och uppåt via Serilog,
                // och konsolen syns ingenstans. Chatten svarade "Ett fel uppstod" på varje fråga
                // från 2026-09-03 i en månad utan ett spår — statistiken såg bara att inga frågor
                // loggades och det lästes som att ingen använde chatten.
                _logger.LogError(ex, "AI-chatten kunde inte svara: {Message}", ex.Message);
                return Json(new { success = false, message = "Ett fel uppstod. Försök igen senare." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetStatus()
        {
            var enabled = _chatService.IsEnabled;
            var loggedIn = false;

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember != null)
                loggedIn = true;

            return Json(new { enabled, loggedIn });
        }

        private void LogChat(string question, string answer, string sources)
        {
            try
            {
                // Try multiple paths - shared hosting may have different root
                var candidates = new[]
                {
                    Path.Combine(_env.ContentRootPath, "App_Data", "AiChatLogs"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "AiChatLogs"),
                };

                string? logDir = null;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        logDir = candidate;
                        break;
                    }
                    catch { }
                }

                if (logDir == null) return;

                var logFile = Path.Combine(logDir, $"chat-{DateTime.UtcNow:yyyy-MM}.log");
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                // "K:" = vilka stycken av kunskapsbasen frågan fick med sig. Står FÖRE "Q:", så
                // statistikens läsare (som läser frågan från "Q:" till "A:") hoppar över den.
                var entry = $"[{timestamp}]\nK: {sources.Replace('\n', ' ')}\nQ: {question}\nA: {answer}\n---\n";

                System.IO.File.AppendAllText(logFile, entry);
            }
            catch { }
        }

        private List<string> GetUserRoles(MemberIdentityUser member)
        {
            var roles = new List<string> { "public", "member" };

            var memberData = _memberService.GetByEmail(member.Email ?? "");
            if (memberData == null) return roles;

            var memberRoles = _memberService.GetAllRoles(memberData.Id)?.ToList() ?? new List<string>();

            if (memberRoles.Contains("Administrators"))
                roles.Add("admin");

            if (memberRoles.Any(r => r.StartsWith("RegionalAdmin_")))
                roles.Add("regional-admin");

            if (memberRoles.Any(r => r.StartsWith("ClubAdmin_")))
                roles.Add("club-admin");

            if (memberRoles.Any(r => r.StartsWith("Skjutledare_")))
                roles.Add("skjutledare");

            // Trainer and competition-manager roles are context-dependent
            // (specific to a group/competition), so we include their docs
            // for any authenticated member — the docs explain the prerequisites
            roles.Add("trainer");
            roles.Add("competition-manager");

            return roles;
        }
    }

    public class ChatRequest
    {
        public string Message { get; set; } = "";
        public List<ChatMessage>? History { get; set; }
    }
}
