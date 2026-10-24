using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Notifications;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// "Sök funktionärer" mail-out. Two modes, chosen to respect Simply's relay (websmtp.simply.com discourages
    /// bulk + risks deliverability): **Relay** = a few emails to the club/region admins who then distribute
    /// (the safe default, via SMTP); **Direct** = straight to members via web-push first + Brevo for email
    /// (bulk stays off the Simply relay). Every send is logged.
    /// </summary>
    public class StaffRequestService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly StaffingSignupService _signup;
        private readonly EmailService _email;
        private readonly WebPushService _webPush;
        private readonly BrevoEmailService _brevo;
        private readonly ILogger<StaffRequestService> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.ClubRosterService _roster;
        private readonly StaffOwnerResolver _owners;

        public StaffRequestService(IScopeProvider scopeProvider, IMemberService memberService, IContentService contentService,
            StaffingSignupService signup, EmailService email, WebPushService webPush, BrevoEmailService brevo, ILogger<StaffRequestService> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.ClubRosterService roster, StaffOwnerResolver owners)
        {
            _owners = owners;
            _roster = roster;
            _competitions = competitions;
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _contentService = contentService;
            _signup = signup;
            _email = email;
            _webPush = webPush;
            _brevo = brevo;
            _logger = logger;
        }

        public StaffRequestPreview Preview(StaffOwner owner)
        {
            var scopes = _signup.GetScopes(owner);
            var p = new StaffRequestPreview
            {
                HasScopes = scopes.Count > 0,
                AudienceLabels = scopes.Select(s => s.Label).ToList(),
                RelayCount = ResolveRelay(scopes).Count,
                DirectCount = ResolveDirectMemberIds(scopes).Count,
                DirectAvailable = !string.IsNullOrWhiteSpace(HostingClubBrevoKey(owner.Comp)),
            };
            var last = LatestLog(owner);
            if (last != null)
                p.LastSent = $"{(last.Mode == StaffRequestMode.Relay ? "Klubbansvariga" : "Direkt")} · {last.SentCount} skickade · {last.CreatedDate.ToLocalTime():yyyy-MM-dd HH:mm}";
            return p;
        }

        /// <summary>
        /// Den som frågar och vad som fattas (omgång 3, persona 2): relämejlet sa varken vem som frågade, vilka dagar, vad
        /// som behövdes eller vem man svarar — och ett svar hamnade hos sajten. <paramref name="publicBase"/> = sajtens
        /// adress för länken (tom = pistol.nu).
        /// </summary>
        public sealed record RequestContext(string? PublicBase, string? SenderName, string? SenderEmail, IReadOnlyList<NeedDayView>? Needs);

        public (bool Ok, string? Message, int Sent, int Push, int Recipients) Send(StaffOwner owner, string mode, string? message, int byMemberId,
            RequestContext? context = null)
        {
            var scopes = _signup.GetScopes(owner);
            if (scopes.Count == 0) return (false, "Öppna först för självanmälan (välj klubb/krets) — då vet vi vilka som ska få förfrågan.", 0, 0, 0);

            var competitionId = owner.Comp;
            var compName = _owners.Name(owner);
            var baseUrl = string.IsNullOrWhiteSpace(context?.PublicBase) ? "https://pistol.nu" : context!.PublicBase!.TrimEnd('/');
            var link = $"{baseUrl}{owner.SignupPath}";
            var extra = (string.IsNullOrWhiteSpace(message) ? "" : $"<p style='white-space:pre-wrap'>{System.Net.WebUtility.HtmlEncode(message!.Trim())}</p>")
                + NeedsHtml(context?.Needs) + SenderHtml(context);
            int sent = 0, push = 0, recipients;

            if (string.Equals(mode, StaffRequestMode.Direct, StringComparison.OrdinalIgnoreCase))
            {
                var memberIds = ResolveDirectMemberIds(scopes);
                recipients = memberIds.Count;
                var subject = $"{compName} söker funktionärer";
                var html = Html($"<strong>{System.Net.WebUtility.HtmlEncode(compName)}</strong> söker funktionärer. Vill du hjälpa till? Anmäl dig och ange när du kan:", extra, link, "Anmäl dig");
                // Push-first (free/instant for opted-in members).
                foreach (var mid in memberIds)
                    try { push += _webPush.SendToMemberAsync(mid, "Funktionärer sökes", $"{compName} söker funktionärer", link, $"staffreq-{owner.MemoKey}").GetAwaiter().GetResult(); } catch { }
                // Email via Brevo (bulk stays off the Simply relay).
                var key = HostingClubBrevoKey(competitionId);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var rcp = memberIds.Select(ResolveMember).Where(x => x.HasValue).Select(x => x!.Value).Where(x => !string.IsNullOrWhiteSpace(x.Email)).ToList();
                    try { sent = _brevo.SendBulkEmailAsync(key!, "admin@pistol.nu", "Pistol.nu", rcp, subject, html).GetAwaiter().GetResult().Sent; } catch (Exception ex) { _logger.LogWarning(ex, "StaffRequest: Brevo bulk failed for {CompetitionId}", competitionId); }
                }
            }
            else
            {
                // Relay: a handful of emails to club/region admins, via SMTP.
                var admins = ResolveRelay(scopes);
                recipients = admins.Count;
                var subject = $"Funktionärer sökes: {compName}";
                var html = Html($"Din klubb/krets ombeds hjälpa till att bemanna <strong>{System.Net.WebUtility.HtmlEncode(compName)}</strong>. Vidarebefordra gärna till era medlemmar, eller uppmana dem att anmäla sig:", extra, link, "Öppna bemanningssidan");
                // Svaret går till den som frågar, och mejlet bär hens namn ("NN via Pistol.nu").
                var replyTo = string.IsNullOrWhiteSpace(context?.SenderEmail) ? null : context!.SenderEmail;
                var fromName = string.IsNullOrWhiteSpace(context?.SenderName) ? null : $"{context!.SenderName} via Pistol.nu";
                foreach (var (emailAddr, _) in admins)
                {
                    try { if (_email.SendHtmlEmailAsync(emailAddr, subject, html, fromName, replyTo, context?.SenderName).GetAwaiter().GetResult()) sent++; } catch { }
                }
            }

            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                scope.Database.Insert(new StaffRequestLog
                {
                    PlanningId = owner.PlanningIdOrNull,
                    CompetitionId = owner.CompetitionIdOrNull,
                    Mode = string.Equals(mode, StaffRequestMode.Direct, StringComparison.OrdinalIgnoreCase) ? StaffRequestMode.Direct : StaffRequestMode.Relay,
                    RecipientCount = recipients,
                    SentCount = sent,
                    PushCount = push,
                    ByMemberId = byMemberId,
                    CreatedDate = DateTime.UtcNow,
                });

            return (true, null, sent, push, recipients);
        }

        // ---- recipient resolution ----

        private List<(string Email, string Name)> ResolveRelay(List<SourceScopeView> scopes)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<(string, string)>();
            foreach (var s in scopes)
            {
                var group = string.Equals(s.ScopeType, SourceScopeType.Region, StringComparison.OrdinalIgnoreCase)
                    ? $"RegionalAdmin_{s.ScopeKey}" : $"ClubAdmin_{s.ScopeKey}";
                try
                {
                    var members = _memberService.GetMembersByGroup(group) ?? Enumerable.Empty<Umbraco.Cms.Core.Models.IMember>();
                    foreach (var m in members)
                        if (!string.IsNullOrWhiteSpace(m.Email) && seen.Add(m.Email))
                            list.Add((m.Email, m.Name ?? m.Email));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "StaffRequest: relay resolve failed for group {Group}", group); }
            }
            return list;
        }

        // Direct mode targets specific clubs' members (region-wide direct mail is deliberately not offered —
        // that's what Relay is for). Members whose primaryClubId is one of the Club-scope clubs.
        private List<int> ResolveDirectMemberIds(List<SourceScopeView> scopes)
        {
            var clubKeys = scopes.Where(s => string.Equals(s.ScopeType, SourceScopeType.Club, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.ScopeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (clubKeys.Count == 0) return new();
            var ids = new List<int>();
            try
            {
                // Godkända medlemmar med en av klubbarna som HUVUDklubb, ur klubbregistret (skalplanen B).
                var clubIds = clubKeys.Select(k => int.TryParse(k, out var id) ? id : 0).Where(id => id > 0);
                ids.AddRange(_roster.ActiveInClubs(clubIds)
                    .Where(m => m.IsPrimary && m.IsApproved)
                    .Select(m => m.MemberId)
                    .Distinct());
            }
            catch (Exception ex) { _logger.LogWarning(ex, "StaffRequest: direct resolve failed"); }
            return ids;
        }

        private (string Email, string Name)? ResolveMember(int memberId)
        {
            try
            {
                var m = _memberService.GetById(memberId);
                if (m == null || string.IsNullOrWhiteSpace(m.Email)) return null;
                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                return (m.Email, string.IsNullOrEmpty(name) ? (m.Name ?? m.Email) : name);
            }
            catch { return null; }
        }

        private string? HostingClubBrevoKey(int competitionId)
        {
            if (competitionId <= 0) return null;   // P2b: en okopplad planering har ingen värdklubb
            try
            {
                var clubId = _competitions.GetSaved(competitionId)?.ClubId ?? 0; // C3-planen §5d
                if (clubId <= 0) return null;
                return _contentService.GetById(clubId)?.GetValue<string>("brevoApiKey");
            }
            catch { return null; }
        }

        private StaffRequestLog? LatestLog(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<StaffRequestLog>(
                "SELECT * FROM StaffRequestLog WHERE " + StaffOwner.Where(0) + " ORDER BY Id DESC", owner.P, owner.C);
        }

        private static readonly System.Globalization.CultureInfo Sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");

        /// <summary>Dagarna och rollerna där det fattas folk — högst tio dagar.</summary>
        private static string NeedsHtml(IReadOnlyList<NeedDayView>? needs)
        {
            var days = (needs ?? Array.Empty<NeedDayView>()).Where(d => d.Missing > 0).Take(10).ToList();
            if (days.Count == 0) return "";
            var sb = new System.Text.StringBuilder("<p><b>Det här behövs:</b></p><ul>");
            foreach (var d in days)
            {
                var when = DateTime.TryParse(d.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt)
                    ? dt.ToString("dddd d MMMM", Sv) : d.Date;
                var roles = string.Join(", ", d.Roles.Where(r => r.Missing > 0).Select(r => $"{r.RoleName} ({r.Missing})"));
                sb.Append($"<li>{System.Net.WebUtility.HtmlEncode(when)}: {System.Net.WebUtility.HtmlEncode(roles)}</li>");
            }
            return sb.Append("</ul>").ToString();
        }

        private static string SenderHtml(RequestContext? c) =>
            string.IsNullOrWhiteSpace(c?.SenderName) ? ""
            : $"<p>Hälsningar<br>{System.Net.WebUtility.HtmlEncode(c!.SenderName)}"
              + (string.IsNullOrWhiteSpace(c.SenderEmail) ? "" : $" — svara på det här mejlet så når svaret {System.Net.WebUtility.HtmlEncode(c.SenderName)}")
              + "</p>";

        private static string Html(string lead, string extra, string link, string cta) => $@"<div style='font-family:Arial,sans-serif;max-width:560px;margin:0 auto;color:#222'>
<p>Hej!</p><p>{lead}</p>{extra}
<p style='margin:24px 0'><a href='{link}' style='background:#0d6efd;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none'>{System.Net.WebUtility.HtmlEncode(cta)}</a></p>
<p style='color:#666;font-size:13px'>Skickat via pistol.nu tävlingsplanering. Vill du inte längre få dessa utskick, kontakta arrangören.</p></div>";
    }
}
