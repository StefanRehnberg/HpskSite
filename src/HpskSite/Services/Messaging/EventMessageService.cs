using HpskSite.Models.Messaging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Messaging
{
    /// <summary>
    /// Data layer for in-app functionary messaging. Competition-scoped, append-style — mirrors the
    /// Springskytte time-adjustment ledger shape. Scope matching is done in memory (message volume
    /// per competition is tiny) so the query stays a single windowed fetch + one ack fetch, with no
    /// OR-tower SQL. Authorization is the controller's job; this service only reads/writes.
    /// </summary>
    public class EventMessageService
    {
        private readonly IScopeProvider _scopeProvider;

        // Only show recent traffic on the live poll; the full log is available to the console.
        private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(12);

        public EventMessageService(IScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        /// <summary>Insert a message; returns its new id.</summary>
        public int Post(EventMessage msg)
        {
            if (msg.CreatedDate == default) msg.CreatedDate = DateTime.UtcNow;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var id = Convert.ToInt32(scope.Database.Insert(msg));
            return id;
        }

        /// <summary>
        /// The feed a viewer sees on a staff screen: every message in the recency window that matches
        /// one of the viewer's declared scopes, plus every All broadcast and every Person:me direct
        /// message. Acks are attached; Mine/AckedByMe are stamped for the viewer.
        /// </summary>
        public EventMessageFeed GetFeed(int competitionId, IEnumerable<EventMessageScope> scopes, int viewerMemberId, TimeSpan? window = null)
        {
            var selectors = BuildSelectors(scopes, viewerMemberId);
            var all = FetchWindow(competitionId, window ?? DefaultWindow);
            var matched = all.Where(m => MatchesAny(m, selectors)).ToList();
            return BuildFeed(matched, viewerMemberId);
        }

        /// <summary>
        /// The tävlingsledning console feed: every message for the competition in the window,
        /// unfiltered by scope. Used by the aggregating view on /competitionmanagement.
        /// </summary>
        public EventMessageFeed GetAll(int competitionId, int viewerMemberId, TimeSpan? window = null)
        {
            var all = FetchWindow(competitionId, window ?? DefaultWindow);
            return BuildFeed(all, viewerMemberId);
        }

        /// <summary>Idempotent read-receipt. Re-acking (or acking your own message) is a safe no-op.</summary>
        public void Ack(int messageId, int memberId, string memberName)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                @"IF NOT EXISTS (SELECT 1 FROM EventMessageAck WHERE MessageId = @0 AND MemberId = @1)
                      INSERT INTO EventMessageAck (MessageId, MemberId, MemberName, AckDate)
                      VALUES (@0, @1, @2, @3)",
                messageId, memberId, memberName ?? "", DateTime.UtcNow);
        }

        /// <summary>Resolve the competition id a message belongs to (for the ack authorization check).</summary>
        public int? GetCompetitionIdForMessage(int messageId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int?>(
                "SELECT CompetitionId FROM EventMessage WHERE Id = @0", messageId);
        }

        // --- internals ---

        private List<EventMessage> FetchWindow(int competitionId, TimeSpan window)
        {
            var since = DateTime.UtcNow - window;   // CreatedDate is stored UTC
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<EventMessage>(
                "SELECT * FROM EventMessage WHERE CompetitionId = @0 AND CreatedDate >= @1 ORDER BY CreatedDate DESC, Id DESC",
                competitionId, since);
        }

        private static List<EventMessageScope> BuildSelectors(IEnumerable<EventMessageScope> scopes, int viewerMemberId)
        {
            var list = new List<EventMessageScope>
            {
                new EventMessageScope(MessageScopeType.All, null),
                new EventMessageScope(MessageScopeType.Person, viewerMemberId.ToString())
            };
            if (scopes != null)
            {
                foreach (var s in scopes)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.ScopeType)) continue;
                    list.Add(s);
                }
            }
            return list;
        }

        private static bool MatchesAny(EventMessage m, List<EventMessageScope> selectors)
        {
            foreach (var s in selectors)
            {
                if (!string.Equals(s.ScopeType, m.ScopeType, StringComparison.OrdinalIgnoreCase)) continue;

                // All ignores the key; everything else must match the key case-insensitively.
                if (string.Equals(m.ScopeType, MessageScopeType.All, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (string.Equals(s.ScopeKey ?? "", m.ScopeKey ?? "", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private EventMessageFeed BuildFeed(List<EventMessage> messages, int viewerMemberId)
        {
            var feed = new EventMessageFeed { ServerTime = DateTime.UtcNow };
            if (messages.Count == 0) return feed;

            var ids = messages.Select(m => m.Id).ToList();
            var acks = FetchAcks(ids);
            var acksByMsg = acks.GroupBy(a => a.MessageId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var m in messages)
            {
                acksByMsg.TryGetValue(m.Id, out var msgAcks);
                msgAcks ??= new List<EventMessageAck>();
                feed.Messages.Add(new EventMessageView
                {
                    Id = m.Id,
                    ScopeType = m.ScopeType,
                    ScopeKey = m.ScopeKey,
                    FromMemberId = m.FromMemberId,
                    FromName = m.FromName,
                    FromScopeType = m.FromScopeType,
                    FromScopeKey = m.FromScopeKey,
                    Body = m.Body,
                    Urgency = m.Urgency,
                    CreatedDate = m.CreatedDate,
                    Mine = m.FromMemberId == viewerMemberId,
                    AckedByMe = msgAcks.Any(a => a.MemberId == viewerMemberId),
                    AckCount = msgAcks.Count,
                    Acks = msgAcks
                        .OrderBy(a => a.AckDate)
                        .Select(a => new EventMessageAckView { MemberId = a.MemberId, MemberName = a.MemberName, AckDate = a.AckDate })
                        .ToList()
                });
            }
            return feed;
        }

        private List<EventMessageAck> FetchAcks(List<int> messageIds)
        {
            if (messageIds.Count == 0) return new List<EventMessageAck>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // NPoco expands a list parameter into an IN (...) clause.
            return scope.Database.Fetch<EventMessageAck>(
                "SELECT * FROM EventMessageAck WHERE MessageId IN (@0)", messageIds);
        }
    }
}
