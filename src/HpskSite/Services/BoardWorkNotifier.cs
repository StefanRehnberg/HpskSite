using System.Net;
using HpskSite.Models;
using HpskSite.Services.Mail;
using HpskSite.Services.Notifications;

namespace HpskSite.Services
{
    /// <summary>
    /// Mejlen i ärendekön och motionerna (2026-10-08). Alla besked på ett ställe, så att vem som
    /// får vad och vart ett svar tar vägen går att läsa i en fil.
    ///
    /// <para><b>⚠️ Svarsadressen är MOTPARTEN.</b> Ett besked till sekreteraren om ett nytt ärende
    /// svarar till den som anmälde det; ett besked till den som anmälde svarar till sekreteraren.
    /// En motionär som svarar på kvittot når föreningen, inte sajtägaren.</para>
    ///
    /// <para><b>⚠️ Ett misslyckat mejl fäller aldrig handlingen.</b> Ärendet är anmält och motionen
    /// inlämnad även om SMTP svarar fel; metoderna sväljer sina fel och returnerar hur många som
    /// faktiskt nåddes, så att ytan kan säga det.</para>
    /// </summary>
    public class BoardWorkNotifier
    {
        private readonly EmailService _email;
        private readonly BoardRoleService _roles;
        private readonly BoardWorkService _work;
        private readonly ReplyContactResolver _reply;
        private readonly WebPushService _push;
        private readonly ILogger<BoardWorkNotifier> _logger;

        public BoardWorkNotifier(EmailService email, BoardRoleService roles, BoardWorkService work,
            ReplyContactResolver reply, WebPushService push, ILogger<BoardWorkNotifier> logger)
        {
            _email = email;
            _roles = roles;
            _work = work;
            _reply = reply;
            _push = push;
            _logger = logger;
        }

        private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

        /// <summary>
        /// Sekreteraren och ordföranden — de som placerar ärenden och bereder motioner. Saknas båda
        /// går beskedet till föreningens kontaktadress, för det är just då ingen annan får veta det.
        /// </summary>
        private List<(string Name, string Email)> PlacerRecipients(int ownerType, int ownerId, int exceptMemberId = 0)
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _roles.GetActiveRoleHolders(ownerType, ownerId, BoardWorkAccess.PlacerRoleKeys))
            {
                if (r.MemberId == exceptMemberId) continue;
                var (name, email) = _work.MemberContact(r.MemberId);
                if (email != null && seen.Add(email)) list.Add((name, email));
            }
            if (list.Count == 0)
            {
                var (orgName, orgEmail) = _work.OwnerContact(ownerType, ownerId);
                if (orgEmail != null) list.Add((orgName, orgEmail));
            }
            return list;
        }

        private async Task<int> SendAll(IEnumerable<(string Name, string Email)> to, string subject,
            List<string> paragraphs, string? link, string? linkLabel, MailReplyTo replyTo)
        {
            int sent = 0;
            foreach (var (name, email) in to)
            {
                try
                {
                    if (await _email.SendBoardWorkNoticeAsync(email, name, subject, paragraphs, link, linkLabel, replyTo)) sent++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Styrelsebesked till {Email} gick inte iväg.", email); }
            }
            return sent;
        }

        /// <summary>
        /// Push till medlemmarnas webbläsare, utöver mejlet (2026-10-08). Bara till den som själv
        /// slagit på notiser på pistol.nu; en push som inte går fram är aldrig ett fel — mejlet bär
        /// beskedet ändå. Adressen är relativ, som i övriga push på sajten.
        /// </summary>
        private async Task Push(IEnumerable<int> memberIds, string title, string body, string url, string tag)
        {
            if (!_push.IsConfigured) return;
            foreach (var id in memberIds.Where(i => i > 0).Distinct())
            {
                try { await _push.SendToMemberAsync(id, title, body, url, tag); }
                catch (Exception ex) { _logger.LogWarning(ex, "Push om styrelsearbete till medlem {MemberId} gick inte iväg.", id); }
            }
        }

        private IEnumerable<int> PlacerMemberIds(int ownerType, int ownerId, int exceptMemberId) =>
            _roles.GetActiveRoleHolders(ownerType, ownerId, BoardWorkAccess.PlacerRoleKeys)
                .Select(r => r.MemberId).Where(id => id != exceptMemberId);

        private static string BoardPath(int ownerType, int ownerId, string view) =>
            $"/styrelse?type={ownerType}&id={ownerId}#vy={view}";

        private static string BoardLink(string baseUrl, int ownerType, int ownerId, string view) =>
            $"{baseUrl}/styrelse?type={ownerType}&id={ownerId}#vy={view}";

        public static string MotionPageLink(string baseUrl, int ownerType, int ownerId) =>
            $"{baseUrl}/motion?{(ownerType == DocumentOwnerType.Region ? "krets" : "klubb")}={ownerId}";

        // ---- Ärenden ---------------------------------------------------------------------------

        public async Task<int> IssueSubmittedAsync(BoardIssue i, string baseUrl)
        {
            await Push(PlacerMemberIds(i.OwnerType, i.OwnerId, i.SubmittedByMemberId),
                "Nytt ärende till styrelsen", i.Title, BoardPath(i.OwnerType, i.OwnerId, "arenden"), $"board-issue-{i.Id}");
            return await IssueSubmittedMailAsync(i, baseUrl);
        }

        private Task<int> IssueSubmittedMailAsync(BoardIssue i, string baseUrl)
        {
            var (who, _) = _work.MemberContact(i.SubmittedByMemberId);
            var (org, _) = _work.OwnerContact(i.OwnerType, i.OwnerId);
            var p = new List<string>
            {
                $"<strong>{H(who)}</strong> har anmält ett ärende till styrelsen i {H(org)}: <strong>{H(i.Title)}</strong> ({H(BoardIssueKinds.Label(i.Kind))}).",
                "Ärendet ligger i kön under <em>Ärenden</em> i Styrelsearbete. Där placerar du det under en punkt på ett kommande möte."
            };
            if (!string.IsNullOrWhiteSpace(i.Body)) p.Insert(1, $"<em>{H(i.Body)}</em>");
            return SendAll(PlacerRecipients(i.OwnerType, i.OwnerId, i.SubmittedByMemberId),
                $"Nytt ärende till styrelsen: {i.Title}", p,
                BoardLink(baseUrl, i.OwnerType, i.OwnerId, "arenden"), "Öppna ärendekön",
                _reply.ForMember(i.SubmittedByMemberId));
        }

        public async Task<bool> IssuePlacedAsync(BoardIssue i, BoardWorkService.Placement pl, int byMemberId, string baseUrl)
        {
            if (i.SubmittedByMemberId != byMemberId)
                await Push(new[] { i.SubmittedByMemberId }, "Ditt ärende är placerat",
                    $"{i.Title} — {pl.MeetingTitle} {pl.MeetingDate:d MMM}, {pl.Paragraph}", BoardPath(i.OwnerType, i.OwnerId, "arenden"), $"board-issue-{i.Id}");
            var (name, email) = _work.MemberContact(i.SubmittedByMemberId);
            if (email == null || i.SubmittedByMemberId == byMemberId) return false;
            var p = new List<string>
            {
                $"Ditt ärende <strong>{H(i.Title)}</strong> ligger nu på dagordningen för <strong>{H(pl.MeetingTitle)}</strong> {pl.MeetingDate:d MMMM yyyy}, som {H(pl.Paragraph)}.",
                "Du får besked om beslutet när protokollet är justerat."
            };
            return await SendAll(new[] { (name, email) }, $"Ditt ärende är placerat: {i.Title}", p,
                BoardLink(baseUrl, i.OwnerType, i.OwnerId, "arenden"), "Visa ärendet", _reply.ForMember(byMemberId)) > 0;
        }

        public async Task<bool> IssueRejectedAsync(BoardIssue i, int byMemberId, string baseUrl)
        {
            if (i.SubmittedByMemberId != byMemberId)
                await Push(new[] { i.SubmittedByMemberId }, "Ditt ärende tas inte upp", i.Title,
                    BoardPath(i.OwnerType, i.OwnerId, "arenden"), $"board-issue-{i.Id}");
            var (name, email) = _work.MemberContact(i.SubmittedByMemberId);
            if (email == null || i.SubmittedByMemberId == byMemberId) return false;
            var p = new List<string>
            {
                $"Ditt ärende <strong>{H(i.Title)}</strong> tas inte upp på något möte.",
                string.IsNullOrWhiteSpace(i.ClosedReason) ? "" : $"Skäl: {H(i.ClosedReason)}",
                "Svara på det här mejlet om du har frågor."
            }.Where(s => s.Length > 0).ToList();
            return await SendAll(new[] { (name, email) }, $"Ditt ärende: {i.Title}", p,
                BoardLink(baseUrl, i.OwnerType, i.OwnerId, "arenden"), "Visa ärendet", _reply.ForMember(byMemberId)) > 0;
        }

        // ---- Motioner --------------------------------------------------------------------------

        /// <summary>Kvitto till motionären och besked till sekreteraren/ordföranden.</summary>
        public async Task<(bool Receipt, int Board)> MotionSubmittedAsync(BoardMotion m, string baseUrl)
        {
            var (org, _) = _work.OwnerContact(m.OwnerType, m.OwnerId);
            var link = MotionPageLink(baseUrl, m.OwnerType, m.OwnerId);

            bool receipt = false;
            if (m.MotionerKind == BoardMotionerKinds.Member && m.MotionerMemberId.HasValue)
            {
                var (name, email) = _work.MemberContact(m.MotionerMemberId.Value);
                if (email != null)
                {
                    receipt = await SendAll(new[] { (name, email) }, $"Kvitto: din motion {m.NumberLabel} till {org}",
                        new List<string>
                        {
                            $"{H(org)} har tagit emot din motion <strong>{H(m.NumberLabel)} {H(m.Title)}</strong> den {m.SubmittedDate:d MMMM yyyy}.",
                            "Styrelsen skriver ett yttrande, och motionen behandlas på årsmötet. Du får besked om beslutet.",
                            "Du kan följa motionen och återkalla den fram till årsmötet på sidan nedan."
                        }, link, "Visa motionerna", (m.OwnerType == DocumentOwnerType.Club ? _reply.ForClub(m.OwnerId) : _reply.ForRegion(m.OwnerId))) > 0;
                }
            }

            await Push(PlacerMemberIds(m.OwnerType, m.OwnerId, m.SubmittedByMemberId),
                "Ny motion till årsmötet", $"{m.NumberLabel} {m.Title} — {m.MotionerName}",
                BoardPath(m.OwnerType, m.OwnerId, "motioner"), $"board-motion-{m.Id}");
            var board = await SendAll(PlacerRecipients(m.OwnerType, m.OwnerId, m.SubmittedByMemberId),
                $"Ny motion till årsmötet: {m.NumberLabel} {m.Title}",
                new List<string>
                {
                    $"<strong>{H(m.MotionerName)}</strong> har lämnat motionen <strong>{H(m.NumberLabel)} {H(m.Title)}</strong> till {H(org)}.",
                    string.IsNullOrWhiteSpace(m.SourceReference) ? "" : $"Hänvisning till styrelsens beslut: {H(m.SourceReference)}.",
                    "Styrelsens yttrande skrivs under <em>Motioner</em> i Styrelsearbete."
                }.Where(s => s.Length > 0).ToList(),
                BoardLink(baseUrl, m.OwnerType, m.OwnerId, "motioner"), "Öppna motionerna",
                m.MotionerMemberId.HasValue ? _reply.ForMember(m.MotionerMemberId.Value)
                    : (m.MotionerClubId.HasValue ? _reply.ForClub(m.MotionerClubId.Value) : MailReplyTo.SiteAdmin));
            return (receipt, board);
        }

        // ---- Besked efter justering ------------------------------------------------------------

        /// <summary>
        /// När ett protokoll blir justerat: den som anmälde ett ärende och motionären får beslutet.
        /// Spärren mot dubbla besked ligger i <see cref="BoardWorkService.ClaimDecisionNotices"/>.
        /// </summary>
        public async Task<int> DecisionsAsync(int meetingId, string baseUrl)
        {
            int sent = 0;
            try
            {
                var (issues, motions) = _work.ClaimDecisionNotices(meetingId);
                foreach (var (i, para, decision) in issues)
                {
                    await Push(new[] { i.SubmittedByMemberId }, "Beslut i ditt ärende",
                        string.IsNullOrWhiteSpace(decision) ? i.Title : $"{i.Title}: {decision}",
                        BoardPath(i.OwnerType, i.OwnerId, "arenden"), $"board-issue-{i.Id}");
                    var (name, email) = _work.MemberContact(i.SubmittedByMemberId);
                    if (email == null) continue;
                    var (org, orgEmail) = _work.OwnerContact(i.OwnerType, i.OwnerId);
                    sent += await SendAll(new[] { (name, email) }, $"Beslut i ditt ärende: {i.Title}",
                        new List<string>
                        {
                            $"Ditt ärende <strong>{H(i.Title)}</strong> har behandlats som {H(para)}, och protokollet är justerat.",
                            string.IsNullOrWhiteSpace(decision) ? "Inget beslut är antecknat under punkten." : $"<strong>Beslut:</strong> {H(decision)}"
                        }, BoardLink(baseUrl, i.OwnerType, i.OwnerId, "arenden"), "Visa ärendet",
                        MailReplyTo.FromClub(org, orgEmail));
                }
                foreach (var (m, para, decision) in motions)
                {
                    var (org, orgEmail) = _work.OwnerContact(m.OwnerType, m.OwnerId);
                    var to = new List<(string, string)>();
                    var pushTo = new List<int>();
                    if (m.MotionerKind == BoardMotionerKinds.Member && m.MotionerMemberId.HasValue)
                    {
                        pushTo.Add(m.MotionerMemberId.Value);
                        var (name, email) = _work.MemberContact(m.MotionerMemberId.Value);
                        if (email != null) to.Add((name, email));
                    }
                    else if (m.MotionerClubId.HasValue)
                    {
                        pushTo.AddRange(PlacerMemberIds(DocumentOwnerType.Club, m.MotionerClubId.Value, 0));
                        var (clubName, clubEmail) = _work.OwnerContact(DocumentOwnerType.Club, m.MotionerClubId.Value);
                        if (clubEmail != null) to.Add((clubName, clubEmail));
                        foreach (var r in _roles.GetActiveRoleHolders(DocumentOwnerType.Club, m.MotionerClubId.Value, BoardWorkAccess.PlacerRoleKeys))
                        {
                            var (n, e) = _work.MemberContact(r.MemberId);
                            if (e != null && !to.Any(x => string.Equals(x.Item2, e, StringComparison.OrdinalIgnoreCase))) to.Add((n, e));
                        }
                    }
                    await Push(pushTo, $"Årsmötets beslut om motion {m.NumberLabel}",
                        string.IsNullOrWhiteSpace(decision) ? m.Title : $"{m.Title}: {decision}",
                        MotionPageLink("", m.OwnerType, m.OwnerId), $"board-motion-{m.Id}");
                    sent += await SendAll(to, $"Årsmötets beslut om motion {m.NumberLabel}",
                        new List<string>
                        {
                            $"Motionen <strong>{H(m.NumberLabel)} {H(m.Title)}</strong> har behandlats av årsmötet i {H(org)} som {H(para)}, och protokollet är justerat.",
                            string.IsNullOrWhiteSpace(decision) ? "Inget beslut är antecknat under punkten." : $"<strong>Beslut:</strong> {H(decision)}"
                        }, MotionPageLink(baseUrl, m.OwnerType, m.OwnerId), "Visa motionerna",
                        MailReplyTo.FromClub(org, orgEmail));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Beskeden efter justeringen av möte {MeetingId} kunde inte skickas.", meetingId);
            }
            return sent;
        }

    }
}
