using HpskSite.Models.Messaging;
using HpskSite.Services.Notifications;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Messaging
{
    /// <summary>
    /// Outward, shooter-facing competition notifications. Composes the three steps of a send:
    ///   1. resolve the audience from registrations (ParticipantAudienceResolver),
    ///   2. persist a Shooter-audience EventMessage row (the durable record + in-app inbox source),
    ///   3. fire web-push to each resolved member in the background (fire-and-forget).
    /// Shared by the manual organizer composer (ParticipantMessageController) and the opt-in
    /// auto-triggers on results/start-list publish.
    /// </summary>
    public class ParticipantNotificationService
    {
        private readonly EventMessageService _messages;
        private readonly ParticipantAudienceResolver _audience;
        private readonly WebPushService _webPush;
        private readonly IUmbracoContextFactory _ctxFactory;
        private readonly ILogger<ParticipantNotificationService> _logger;

        public ParticipantNotificationService(
            EventMessageService messages,
            ParticipantAudienceResolver audience,
            WebPushService webPush,
            IUmbracoContextFactory ctxFactory,
            ILogger<ParticipantNotificationService> logger)
        {
            _messages = messages;
            _audience = audience;
            _webPush = webPush;
            _ctxFactory = ctxFactory;
            _logger = logger;
        }

        /// <summary>
        /// Send a notification to the resolved audience. Returns the recipient count immediately
        /// (the inbox row is written synchronously so the message is never lost); pushes go out on a
        /// background task. Safe to call from a publish endpoint — never throws to the caller.
        /// </summary>
        public int Notify(int competitionId, string scopeType, string? scopeKey, string body, string? urgency,
            int fromMemberId, string fromName)
        {
            if (competitionId <= 0 || string.IsNullOrWhiteSpace(body)) return 0;

            var scope = string.IsNullOrWhiteSpace(scopeType) ? MessageScopeType.All : scopeType.Trim();
            var isAll = string.Equals(scope, MessageScopeType.All, StringComparison.OrdinalIgnoreCase);
            var key = isAll ? null : (string.IsNullOrWhiteSpace(scopeKey) ? null : scopeKey.Trim());

            var members = _audience.ResolveMemberIds(competitionId, scope, key);

            var text = body.Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);
            var urg = urgency switch
            {
                MessageUrgency.Urgent => MessageUrgency.Urgent,
                MessageUrgency.Safety => MessageUrgency.Safety,
                _ => MessageUrgency.Normal
            };

            var (compName, compUrl) = ResolveCompetition(competitionId);

            // 1+2: persist the durable/inbox row.
            try
            {
                _messages.Post(new EventMessage
                {
                    CompetitionId = competitionId,
                    Discipline = "",
                    Audience = MessageAudience.Shooter,
                    ScopeType = scope,
                    ScopeKey = key,
                    FromMemberId = fromMemberId,
                    FromName = string.IsNullOrWhiteSpace(fromName) ? "Arrangören" : fromName,
                    Body = text,
                    Urgency = urg,
                    CreatedDate = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Participant notification: failed to persist message for comp {Comp}", competitionId);
            }

            // 3: background push (fire-and-forget). WebPushService is off-request safe (own DB scope).
            if (members.Count > 0)
            {
                var title = string.IsNullOrWhiteSpace(compName) ? "pistol.nu" : compName;
                var url = string.IsNullOrWhiteSpace(compUrl) ? "/" : compUrl;
                var tag = "comp-" + competitionId;
                var recipients = members;
                _ = Task.Run(async () =>
                {
                    foreach (var mid in recipients)
                    {
                        try { await _webPush.SendToMemberAsync(mid, title, text, url, tag); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Participant push failed for member {Member}", mid); }
                    }
                });
            }

            return members.Count;
        }

        private (string Name, string Url) ResolveCompetition(int competitionId)
        {
            try
            {
                using var cref = _ctxFactory.EnsureUmbracoContext();
                var content = cref.UmbracoContext.Content?.GetById(competitionId);
                if (content == null) return ("pistol.nu", "/");
                var url = content.Url();
                return (content.Name ?? "Tävling", string.IsNullOrWhiteSpace(url) ? "/" : url);
            }
            catch
            {
                return ("pistol.nu", "/");
            }
        }
    }
}
