using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Mail;
using HpskSite.Services.Notifications;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Planeringens mejl och notiser. Varje utskick registreras i <c>PlanningNotifyLog</c> FÖRE
    /// utskicket (claim-then-send, unikt index), och raden får <c>Succeeded</c> = om det FAKTISKT gick
    /// iväg. En krasch mellan registrering och utskick kostar en missad notis, aldrig en dubbel.
    ///
    /// <para><b>⚠️ Anropas EFTER att skrivningens scope stängts</b>, aldrig inifrån det, och aldrig i en
    /// <c>Task.Run</c> utan <c>SuppressFlow</c>.</para>
    ///
    /// <para><b>⚠️ Mejlet är kanalen, inte en avisering</b> (Stefan 2026-10-21): varje mejl har en
    /// knapp som svarar eller röstar UTAN inloggning, så att en äldre representant — eller en gäst som
    /// inte är skytt — kan göra det direkt ur mejlet.</para>
    /// </summary>
    public class PlanningNotifier
    {
        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        private readonly PlanningService _planning;
        private readonly PlanningLinkService _links;
        private readonly EmailService _email;
        private readonly WebPushService _push;
        private readonly IMemberService _members;
        private readonly IScopeProvider _scopes;
        private readonly ILogger<PlanningNotifier> _logger;

        public PlanningNotifier(PlanningService planning, PlanningLinkService links, EmailService email, WebPushService push,
            IMemberService members, IScopeProvider scopes, ILogger<PlanningNotifier> logger)
        {
            _planning = planning;
            _links = links;
            _email = email;
            _push = push;
            _members = members;
            _scopes = scopes;
            _logger = logger;
        }

        // =====================================================================================
        //  Registrering (claim-then-send)
        // =====================================================================================

        /// <summary>Registrerar utskicket. Null = redan registrerat (eller kunde inte registreras) — skicka inte.</summary>
        internal int? Claim(int planningId, int participantId, string kind, string channel, string refKind, int refId,
            DateTime? dueAt = null, int? upToMessageId = null)
        {
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                return s.Database.ExecuteScalar<int?>(@"
                    INSERT INTO PlanningNotifyLog (PlanningId, ParticipantId, Kind, RefKind, RefId, DueAt, UpToMessageId, Channel, ClaimedAt, Succeeded)
                    SELECT @0, @1, @2, @4, @5, @6, @7, @3, @8, 0
                     WHERE NOT EXISTS (SELECT 1 FROM PlanningNotifyLog
                                        WHERE Kind = @2 AND Channel = @3 AND ParticipantId = @1 AND RefKind = @4 AND RefId = @5
                                          AND ((DueAt IS NULL AND @6 IS NULL) OR DueAt = @6));
                    SELECT CAST(SCOPE_IDENTITY() AS INT);",
                    planningId, participantId, kind, channel, refKind, refId, dueAt, upToMessageId, DateTime.Now);
            }
            catch (Exception ex)
            {
                // Unikt index slog till i en kapplöpning, eller tabellen saknas: skicka inte.
                _logger.LogWarning(ex, "Planering: notisen {Kind} till deltagare {Pid} kunde inte registreras.", kind, participantId);
                return null;
            }
        }

        internal void Done(int logId, bool succeeded)
        {
            if (!succeeded) return; // raden står redan på 0
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                s.Database.Execute("UPDATE PlanningNotifyLog SET Succeeded = 1 WHERE Id = @0", logId);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering: kunde inte markera notis {Id} som skickad.", logId); }
        }

        // =====================================================================================
        //  Mottagarna
        // =====================================================================================

        internal sealed record Recipient(PlanningMemberRow Row, string Email, string Name)
        {
            public int Pid => Row.Id;
            public int MemberId => Row.MemberId ?? 0;
        }

        internal Recipient? ResolveRecipient(PlanningMemberRow p)
        {
            if (!p.IsActive) return null;
            if (p.IsGuest) return string.IsNullOrWhiteSpace(p.GuestEmail) ? null : new Recipient(p, p.GuestEmail!, p.DisplayName);
            try
            {
                var m = _members.GetById(p.MemberId!.Value);
                if (m == null || string.IsNullOrWhiteSpace(m.Email)) return null;
                return new Recipient(p, m.Email, p.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Planering: kunde inte slå upp medlem {MemberId}.", p.MemberId);
                return null;
            }
        }

        internal MailReplyTo ReplyToFor(PlanningMemberRow? p, string planningName)
        {
            if (p == null) return MailReplyTo.NoReply;
            var r = ResolveRecipient(p);
            return r == null ? MailReplyTo.NoReply : MailReplyTo.To(r.Email, r.Name, $"{planningName} via Pistol.nu");
        }

        /// <summary>Planeringens sida för den här mottagaren: medlemmen loggar in, gästen kommer in med sin länk.</summary>
        internal string OpenUrl(Recipient r, int planningId, string view = "oversikt") =>
            r.Row.IsGuest
                ? _links.MailUrl(r.Pid, planningId, "oppna")
                : $"{_links.SiteUrl}/tavlingsplanering/{planningId}#vy={view}";

        // =====================================================================================
        //  Mejlets form
        // =====================================================================================

        internal static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        internal static string Button(string label, string url, bool primary = true) =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\" style=\"display:inline-block;margin:4px 6px 4px 0;"
            + (primary ? "background:#0d6efd;color:#fff;" : "background:#fff;color:#0d6efd;border:1px solid #0d6efd;")
            + "padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:600;\">" + H(label) + "</a>";

        internal static string Page(string toName, string planningName, string inner, string footerNote)
        {
            var sb = new StringBuilder();
            sb.Append("<html><body style=\"font-family:Arial,sans-serif;line-height:1.55;color:#222;font-size:16px;\">");
            sb.Append($"<p style=\"color:#666;font-size:13px;margin:0 0 4px\">Planeringen <b>{H(planningName)}</b> på pistol.nu</p>");
            sb.Append($"<h2 style=\"margin:4px 0 12px\">Hej {H(string.IsNullOrWhiteSpace(toName) ? "" : toName.Split(' ')[0])},</h2>");
            sb.Append(inner);
            if (!string.IsNullOrEmpty(footerNote))
                sb.Append($"<p style=\"color:#666;font-size:13px;margin-top:22px\">{footerNote}</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        internal static string When(DateTime d) => d.ToString("dddd d MMMM 'kl.' HH:mm", Sv);
        internal static string Span(DateTime a, DateTime b) =>
            a.Date == b.Date ? $"{When(a)}–{b:HH:mm}" : $"{When(a)} – {When(b)}";

        private string NotifyFooter(Recipient r, int planningId) =>
            $"Du får det här mejlet för att du är med i planeringen. Läget nu: <b>{H(PlanningNotifyMode.Label(r.Row.NotifyMode))}</b>. "
            + $"<a href=\"{H(r.Row.IsGuest ? _links.MailUrl(r.Pid, planningId, "notiser") : $"{_links.SiteUrl}/tavlingsplanering/{planningId}#vy=notiser")}\">Ändra hur du får notiser</a>.";

        private async Task<bool> SendMail(Recipient r, int planningId, string kind, string refKind, int refId, DateTime? dueAt,
            string subject, string html, MailReplyTo replyTo, int? upTo = null)
        {
            var id = Claim(planningId, r.Pid, kind, "mail", refKind, refId, dueAt, upTo);
            if (id == null) return false;
            var ok = await _email.SendPlanningMailAsync(r.Email, subject, html, replyTo);
            Done(id.Value, ok);
            if (!ok) _logger.LogWarning("Planering: mejlet {Kind} till deltagare {Pid} gick inte iväg.", kind, r.Pid);
            return ok;
        }

        private async Task<bool> SendPush(Recipient r, int planningId, string kind, string refKind, int refId, DateTime? dueAt,
            string title, string body, string url)
        {
            if (r.MemberId <= 0 || !_push.IsConfigured) return false;
            var id = Claim(planningId, r.Pid, kind, "push", refKind, refId, dueAt);
            if (id == null) return false;
            int sent = 0;
            try { sent = await _push.SendToMemberAsync(r.MemberId, title, body, url, tag: $"planning-{planningId}"); }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering: pushen {Kind} till medlem {MemberId} föll.", kind, r.MemberId); }
            Done(id.Value, sent > 0);
            return sent > 0;
        }

        // =====================================================================================
        //  Händelser som aviseras direkt
        // =====================================================================================

        /// <summary>Inbjudan till en ny deltagare. Returnerar om mejlet gick iväg.</summary>
        public async Task<bool> InviteAsync(int planningId, int participantId, PlanningMemberRow? invitedBy, bool resend)
        {
            var p = _planning.Get(planningId);
            var who = _planning.Participant(planningId, participantId);
            if (p == null || who == null) return false;
            var r = ResolveRecipient(who);
            if (r == null) return false;
            var byName = invitedBy?.DisplayName ?? "Någon";
            var inner = new StringBuilder();
            inner.Append($"<p>{H(byName)} har bjudit in dig till planeringen <b>{H(p.Name)}</b>"
                + (string.IsNullOrWhiteSpace(p.TargetText) ? "" : $" ({H(p.TargetText)})") + ".</p>");
            inner.Append("<p>Där skriver ni till varandra, hittar en tid för möten och håller ordning på vem som gör vad — i stället för långa mejltrådar.</p>");
            inner.Append(r.Row.IsGuest
                ? "<p>Du behöver inget konto. Knappen nedan tar dig direkt in; spara gärna mejlet.</p>"
                : "<p>Logga in med ditt vanliga konto på pistol.nu.</p>");
            inner.Append($"<p>{Button("Öppna planeringen", OpenUrl(r, planningId))}</p>");
            inner.Append($"<p style=\"color:#555\">Du får <b>ett mejl om dagen</b> med det som är nytt. Det kan du ändra i planeringen.</p>");
            var html = Page(r.Name, p.Name, inner.ToString(), "");
            var ok = await SendMail(r, planningId, "invite", "participant", participantId, resend ? DateTime.Now : null,
                $"Du är inbjuden till planeringen {p.Name}", html, ReplyToFor(invitedBy, p.Name));
            if (ok) _planning.MarkInviteSent(participantId);
            return ok;
        }

        /// <summary>Den som nämns får det direkt (i lägena Allt direkt och Bara när någon nämner mig).</summary>
        public async Task MentionsAsync(int planningId, int messageId, IEnumerable<int> mentionedParticipantIds)
        {
            var p = _planning.Get(planningId);
            var msg = _planning.Message(planningId, messageId);
            if (p == null || msg == null || msg.ParticipantId == null) return;
            var author = _planning.Participant(planningId, msg.ParticipantId.Value);
            foreach (var pid in mentionedParticipantIds.Distinct())
            {
                var who = _planning.Participant(planningId, pid);
                if (who == null || !who.IsActive || !PlanningNotifyRules.MailOnMention(who.NotifyMode)) continue;
                var r = ResolveRecipient(who);
                if (r == null) continue;
                var inner = $"<p><b>{H(author?.DisplayName)}</b> nämnde dig:</p>"
                    + $"<blockquote style=\"border-left:3px solid #ccc;margin:8px 0;padding:4px 12px;white-space:pre-wrap\">{H(msg.Body)}</blockquote>"
                    + $"<p>{Button("Svara", _links.MailUrl(r.Pid, planningId, "svara", messageId))}{Button("Öppna planeringen", OpenUrl(r, planningId, "meddelanden"), false)}</p>";
                await SendMail(r, planningId, "mention", "message", messageId, null,
                    $"{author?.DisplayName} nämnde dig i {p.Name}", Page(r.Name, p.Name, inner, NotifyFooter(r, planningId)), ReplyToFor(author, p.Name));
                await SendPush(r, planningId, "mention", "message", messageId, null,
                    p.Name, $"{author?.DisplayName} nämnde dig", OpenUrl(r, planningId, "meddelanden"));
            }
        }

        /// <summary>"Vilka tider passar?" — till alla som är med, utom den som frågar.</summary>
        public async Task MeetingProposedAsync(int planningId, int meetingId)
        {
            var p = _planning.Get(planningId);
            var m = _planning.Meeting(planningId, meetingId);
            if (p == null || m == null) return;
            var options = _planning.Options(new[] { meetingId });
            var creator = _planning.Participant(planningId, m.CreatedBy);
            foreach (var who in _planning.Participants(planningId))
            {
                if (who.Id == m.CreatedBy || !PlanningAccess.IsCalledToMeetings(who) || !PlanningNotifyRules.GetsMeetingMail(who.NotifyMode)) continue;
                var r = ResolveRecipient(who);
                if (r == null) continue;
                var list = string.Join("", options.Select(o => $"<li>{H(Span(o.StartsAt, o.EndsAt))}</li>"));
                var inner = $"<p><b>{H(creator?.DisplayName)}</b> föreslår ett möte: <b>{H(m.Title)}</b>.</p>"
                    + $"<p>Vilka av de här tiderna passar dig?</p><ul>{list}</ul>"
                    + (m.VoteDeadline.HasValue ? $"<p>Svara gärna senast {H(m.VoteDeadline.Value.ToString("dddd d MMMM", Sv))}.</p>" : "")
                    + $"<p>{Button("Svara på tiderna", _links.MailUrl(r.Pid, planningId, "rosta", meetingId))}</p>"
                    + "<p style=\"color:#555\">Du behöver inte logga in — knappen tar dig direkt till tiderna.</p>";
                await SendMail(r, planningId, "meeting-proposed", "meeting", meetingId, null,
                    $"Vilka tider passar? {m.Title}", Page(r.Name, p.Name, inner, NotifyFooter(r, planningId)), ReplyToFor(creator, p.Name));
            }
        }

        /// <summary>Påminnelse till dem som inte svarat på tiderna. Returnerar antalet som nåddes.</summary>
        public async Task<int> RemindNonVotersAsync(int planningId, int meetingId, bool manual)
        {
            var p = _planning.Get(planningId);
            var m = _planning.Meeting(planningId, meetingId);
            if (p == null || m == null || m.Status != PlanningMeetingStatus.Proposed) return 0;
            var voted = _planning.Votes(new[] { meetingId }).Select(v => v.ParticipantId).ToHashSet();
            var creator = _planning.Participant(planningId, m.CreatedBy);
            var reached = 0;
            foreach (var who in _planning.Participants(planningId))
            {
                if (voted.Contains(who.Id) || !PlanningAccess.IsCalledToMeetings(who) || !PlanningNotifyRules.GetsMeetingMail(who.NotifyMode)) continue;
                var r = ResolveRecipient(who);
                if (r == null) continue;
                var inner = $"<p>Vi väntar på ditt svar om mötet <b>{H(m.Title)}</b> — vilka tider passar dig?</p>"
                    + $"<p>{Button("Svara på tiderna", _links.MailUrl(r.Pid, planningId, "rosta", meetingId))}</p>";
                // Manuell påminnelse: en per dag. Automatisk: en per svarsfrist.
                var due = manual ? DateTime.Today : m.VoteDeadline;
                if (await SendMail(r, planningId, manual ? "vote-reminder-manual" : "vote-reminder", "meeting", meetingId, due,
                        $"Påminnelse: vilka tider passar? {m.Title}", Page(r.Name, p.Name, inner, NotifyFooter(r, planningId)), ReplyToFor(creator, p.Name)))
                    reached++;
            }
            return reached;
        }

        /// <summary>Mötet är bokat (eller flyttat): tid, plats, länk och kalendern, till alla.</summary>
        public async Task MeetingBookedAsync(int planningId, int meetingId, bool rebooked)
        {
            var p = _planning.Get(planningId);
            var m = _planning.Meeting(planningId, meetingId);
            if (p == null || m == null || m.StartsAt == null || m.EndsAt == null) return;
            var booker = m.BookedBy.HasValue ? _planning.Participant(planningId, m.BookedBy.Value) : null;
            foreach (var who in _planning.Participants(planningId))
            {
                if (!PlanningAccess.IsCalledToMeetings(who) || !PlanningNotifyRules.GetsMeetingMail(who.NotifyMode)) continue;
                var r = ResolveRecipient(who);
                if (r == null) continue;
                var inner = new StringBuilder();
                inner.Append($"<p>{(rebooked ? "Mötet har <b>flyttats</b>" : "Mötet är <b>bokat</b>")}: <b>{H(m.Title)}</b></p>");
                inner.Append($"<p style=\"font-size:18px\"><b>{H(Span(m.StartsAt.Value, m.EndsAt.Value))}</b></p>");
                if (!string.IsNullOrWhiteSpace(m.Place)) inner.Append($"<p>Plats: {H(m.Place)}</p>");
                if (!string.IsNullOrWhiteSpace(m.OnlineUrl)) inner.Append($"<p>{Button("Gå med i mötet", m.OnlineUrl!)}</p><p style=\"color:#555;font-size:13px\">Länken: {H(m.OnlineUrl)}</p>");
                inner.Append($"<p>{Button("Lägg i min kalender", _links.IcsUrl(r.Pid, planningId), false)}{Button("Öppna planeringen", OpenUrl(r, planningId, "moten"), false)}</p>");
                inner.Append("<p style=\"color:#555\">Kalenderknappen lägger in planeringens möten i din kalender och håller dem uppdaterade om de flyttas. Du får en påminnelse dagen innan.</p>");
                await SendMail(r, planningId, rebooked ? "meeting-rebooked" : "meeting-booked", "meeting", meetingId, m.StartsAt,
                    $"{(rebooked ? "Flyttat" : "Bokat")}: {m.Title}, {m.StartsAt.Value.ToString("d MMM HH:mm", Sv)}",
                    Page(r.Name, p.Name, inner.ToString(), NotifyFooter(r, planningId)), ReplyToFor(booker, p.Name));
                if (PlanningNotifyRules.GetsHourlyPush(who.NotifyMode))
                    await SendPush(r, planningId, "meeting-booked", "meeting", meetingId, m.StartsAt, p.Name,
                        $"Möte {m.StartsAt.Value.ToString("d MMM HH:mm", Sv)}", OpenUrl(r, planningId, "moten"));
            }
        }

        public async Task MeetingCancelledAsync(int planningId, int meetingId)
        {
            var p = _planning.Get(planningId);
            var m = _planning.Meeting(planningId, meetingId);
            if (p == null || m == null || m.StartsAt == null) return; // ett möte som aldrig bokades behöver ingen avbokning
            foreach (var who in _planning.Participants(planningId))
            {
                if (!PlanningAccess.IsCalledToMeetings(who) || !PlanningNotifyRules.GetsMeetingMail(who.NotifyMode)) continue;
                var r = ResolveRecipient(who);
                if (r == null) continue;
                var inner = $"<p>Mötet <b>{H(m.Title)}</b> {H(When(m.StartsAt.Value))} är <b>inställt</b>.</p>"
                    + "<p>Har du lagt in planeringens kalender uppdateras den av sig själv.</p>"
                    + $"<p>{Button("Öppna planeringen", OpenUrl(r, planningId, "moten"), false)}</p>";
                await SendMail(r, planningId, "meeting-cancelled", "meeting", meetingId, m.StartsAt,
                    $"Inställt: {m.Title}", Page(r.Name, p.Name, inner, NotifyFooter(r, planningId)), MailReplyTo.NoReply);
            }
        }

        // =====================================================================================
        //  Svepet (körs av PlanningNotificationHostedService)
        // =====================================================================================

        public sealed record SweepResult(int Digests, int Pushes, int MeetingReminders, int VoteReminders);

        public async Task<SweepResult> SweepAsync(DateTime now)
        {
            int digests = 0, pushes = 0, meetingRem = 0, voteRem = 0;
            List<int> active;
            using (var s = _scopes.CreateScope(autoComplete: true))
                active = s.Database.Fetch<int>("SELECT Id FROM Planning WHERE DeletedAt IS NULL AND Status = N'Active'");

            foreach (var planningId in active)
            {
                try
                {
                    var p = _planning.Get(planningId);
                    if (p == null) continue;
                    var people = _planning.Participants(planningId);
                    var reads = _planning.ReadRows(planningId).ToDictionary(r => r.ParticipantId);
                    var recent = _planning.Messages(planningId, 0, 200);

                    foreach (var who in people)
                    {
                        if (!reads.TryGetValue(who.Id, out var rr)) continue;
                        var unread = recent.Where(m => m.Id > Math.Max(rr.LastReadMessageId, 0) && m.DeletedAt == null
                                                       && m.ParticipantId != who.Id).ToList();
                        // Kvällsmejlet
                        if (PlanningNotifyRules.GetsDigest(who.NotifyMode) && PlanningNotifyRules.DigestDue(now))
                        {
                            var fresh = unread.Where(m => m.Id > rr.LastDigestMessageId && m.Kind == "Message").ToList();
                            if (fresh.Count > 0 && await DigestAsync(p, who, fresh, people, now)) digests++;
                        }
                        // Pushen högst en gång i timmen
                        if (PlanningNotifyRules.GetsHourlyPush(who.NotifyMode) && who.MemberId > 0
                            && unread.Any(m => m.Kind == "Message" && (rr.LastPushAt == null || m.CreatedAt > rr.LastPushAt))
                            && PlanningNotifyRules.HourlyPushDue(rr.LastPushAt, now) && ClaimHourlyPush(rr.Id, now))
                        {
                            var r = ResolveRecipient(who);
                            if (r != null && _push.IsConfigured)
                            {
                                var n = unread.Count(m => m.Kind == "Message");
                                try
                                {
                                    if (await _push.SendToMemberAsync(r.MemberId, p.Name, n == 1 ? "1 nytt meddelande" : $"{n} nya meddelanden",
                                            OpenUrl(r, planningId, "meddelanden"), tag: $"planning-{planningId}") > 0) pushes++;
                                }
                                catch (Exception ex) { _logger.LogWarning(ex, "Planering: timpushen föll."); }
                            }
                        }
                    }

                    // Mötena: påminnelse dagen innan och en timme före; svarsfristen
                    foreach (var m in _planning.Meetings(planningId))
                    {
                        if (m.Status == PlanningMeetingStatus.Scheduled && m.StartsAt.HasValue)
                        {
                            var step = PlanningNotifyRules.MeetingReminderStep(m.StartsAt.Value, now);
                            if (step != null) meetingRem += await MeetingReminderAsync(p, m, step, people);
                        }
                        else if (m.Status == PlanningMeetingStatus.Proposed && PlanningNotifyRules.VoteReminderDue(m.VoteDeadline, now))
                        {
                            voteRem += await RemindNonVotersAsync(planningId, m.Id, manual: false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Planering {PlanningId}: svepet föll för planeringen.", planningId);
                }
            }
            return new SweepResult(digests, pushes, meetingRem, voteRem);
        }

        /// <summary>Spärren för timpushen: villkorad UPDATE, 1 rad = vi fick den.</summary>
        private bool ClaimHourlyPush(int readRowId, DateTime now)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Execute(
                "UPDATE PlanningMessageRead SET LastPushAt = @1 WHERE Id = @0 AND (LastPushAt IS NULL OR LastPushAt < DATEADD(hour, -1, @1))",
                readRowId, now) == 1;
        }

        private async Task<bool> DigestAsync(PlanningRow p, PlanningMemberRow who, List<PlanningMessageRow> fresh,
            List<PlanningMemberRow> people, DateTime now)
        {
            var r = ResolveRecipient(who);
            if (r == null) return false;
            var names = people.ToDictionary(x => x.Id, x => x.DisplayName);
            var upTo = fresh.Max(m => m.Id);
            var inner = new StringBuilder();
            inner.Append($"<p>{(fresh.Count == 1 ? "Ett nytt meddelande" : $"{fresh.Count} nya meddelanden")} sedan sist:</p>");
            foreach (var m in fresh.Take(30))
            {
                var name = m.ParticipantId.HasValue && names.TryGetValue(m.ParticipantId.Value, out var n) ? n : "Någon";
                inner.Append($"<div style=\"border-left:3px solid #ccc;margin:10px 0;padding:2px 12px\"><div style=\"color:#555;font-size:13px\"><b>{H(name)}</b> · {H(m.CreatedAt.ToString("ddd d MMM HH:mm", Sv))}</div>"
                    + $"<div style=\"white-space:pre-wrap\">{H(m.Body)}</div></div>");
            }
            if (fresh.Count > 30) inner.Append($"<p>… och {fresh.Count - 30} till.</p>");
            inner.Append($"<p>{Button("Svara", _links.MailUrl(r.Pid, p.Id, "svara"))}{Button("Öppna planeringen", OpenUrl(r, p.Id, "meddelanden"), false)}</p>");
            inner.Append("<p style=\"color:#555\">Du kan svara direkt med knappen, utan att logga in. Svarar du på mejlet i stället når det den som skapade planeringen.</p>");
            var owner = people.FirstOrDefault(x => x.Access == PlanningAccessLevel.Owner);
            var ok = await SendMail(r, p.Id, "digest", "day", PlanningNotifyRules.DayKey(now), null,
                $"{p.Name}: {(fresh.Count == 1 ? "1 nytt meddelande" : $"{fresh.Count} nya meddelanden")}",
                Page(r.Name, p.Name, inner.ToString(), NotifyFooter(r, p.Id)), ReplyToFor(owner, p.Name), upTo);
            // Märket flyttas oavsett utfall: registreringen spärrar ändå ett andra utskick i dag,
            // och i morgon ska bara det nya komma med.
            using (var s = _scopes.CreateScope(autoComplete: true))
                s.Database.Execute(@"UPDATE PlanningMessageRead SET LastDigestMessageId = @2, LastDigestAt = @3
                                      WHERE PlanningId = @0 AND ParticipantId = @1 AND LastDigestMessageId < @2", p.Id, who.Id, upTo, now);
            return ok;
        }

        private async Task<int> MeetingReminderAsync(PlanningRow p, PlanningMeetingRow m, string step, List<PlanningMemberRow> people)
        {
            var n = 0;
            foreach (var who in people)
            {
                if (!PlanningAccess.IsCalledToMeetings(who) || !PlanningNotifyRules.GetsMeetingMail(who.NotifyMode)) continue;
                var r = ResolveRecipient(who);
                if (r == null) continue;
                var when = Span(m.StartsAt!.Value, m.EndsAt ?? m.StartsAt.Value.AddHours(1));
                if (step == "d1")
                {
                    var inner = $"<p>Påminnelse: mötet <b>{H(m.Title)}</b> är <b>{H(when)}</b>.</p>"
                        + (string.IsNullOrWhiteSpace(m.Place) ? "" : $"<p>Plats: {H(m.Place)}</p>")
                        + (string.IsNullOrWhiteSpace(m.OnlineUrl) ? "" : $"<p>{Button("Gå med i mötet", m.OnlineUrl!)}</p>")
                        + $"<p>{Button("Öppna planeringen", OpenUrl(r, p.Id, "moten"), false)}</p>";
                    if (await SendMail(r, p.Id, "meeting-d1", "meeting", m.Id, m.StartsAt,
                            $"I morgon: {m.Title}", Page(r.Name, p.Name, inner, NotifyFooter(r, p.Id)), MailReplyTo.NoReply)) n++;
                }
                if (PlanningNotifyRules.GetsMeetingPush(who.NotifyMode))
                    await SendPush(r, p.Id, "meeting-" + step, "meeting", m.Id, m.StartsAt, p.Name,
                        step == "h1" ? $"Mötet börjar {m.StartsAt.Value:HH:mm}" : $"I morgon {m.StartsAt.Value:HH:mm}: {m.Title}",
                        string.IsNullOrWhiteSpace(m.OnlineUrl) ? OpenUrl(r, p.Id, "moten") : m.OnlineUrl!);
            }
            return n;
        }
    }
}
