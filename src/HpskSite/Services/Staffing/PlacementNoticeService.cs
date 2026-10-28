using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Mail;
using HpskSite.Services.Notifications;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.DataProtection;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// <b>Meddela placeringarna</b> (Stefan 2026-10-28). En placering tilldelas — den begärs inte, och personen godkänner den inte. Att meddela är
    /// att berätta vilka placeringar personen har: ETT mejl (och en notis för medlemmar) per människa, med alla hens placeringar som ännu inte är
    /// meddelade och en knapp till hens tävlingsdagssida (<c>/mitt-schema?p=</c>). Raderna får <c>NotifiedAt</c>; statusen rörs inte.
    ///
    /// <para><b>Utan konto:</b> samma mejl, med uppgifterna i mejlet och en tokenlänk till en läsvy (<c>/mina-uppdrag/svar?t=</c>).</para>
    /// <para><b>Dubbelklick:</b> raderna tas med ett villkorat UPDATE (<see cref="StaffingService.ClaimNotified"/>) FÖRE utskicket. Två samtidiga
    /// anrop kan alltså aldrig båda få samma rad, och en person får aldrig två mejl om samma placering. Når inget utskick personen tas märket tillbaka.</para>
    /// <para><b>Reply-To</b> är den som meddelar — motparten (mail-reply-to-required-on-core-path). Högst <see cref="MailLimits.MaxRecipientsPerSend"/>
    /// mejl per anrop; fler vägras FÖRE första mejlet.</para>
    /// <para>Ingen ja/nej och ingen länk att svara: återkoppling ("jag kan inte") är inte byggd och kan läggas till senare — då behövs bara en ny kolumn.</para>
    /// </summary>
    public class PlacementNoticeService
    {
        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        private readonly IScopeProvider _scopes;
        private readonly StaffingService _staffing;
        private readonly StaffOwnerResolver _owners;
        private readonly RoleCatalogService _roles;
        private readonly PlanningService _planning;
        private readonly IMemberService _members;
        private readonly HpskSite.Services.EmailService _email;
        private readonly WebPushService _push;
        private readonly PlanningLinkService _links;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly ILogger<PlacementNoticeService> _logger;

        public PlacementNoticeService(IScopeProvider scopes, StaffingService staffing, StaffOwnerResolver owners, RoleCatalogService roles,
            PlanningService planning, IMemberService members, HpskSite.Services.EmailService email, WebPushService push, PlanningLinkService links,
            IDataProtectionProvider dataProtection, ILogger<PlacementNoticeService> logger)
        {
            _scopes = scopes; _staffing = staffing; _owners = owners; _roles = roles; _planning = planning; _members = members; _email = email;
            _push = push; _links = links; _dataProtection = dataProtection; _logger = logger;
        }

        /// <summary>Vad ett anrop gjorde. <see cref="Unreachable"/> = namn på dem som varken har konto eller e-post (eller som inget utskick nådde).</summary>
        public sealed record Result(bool Ok, string? Message, int People, int Placements, int Mailed, int Pushed, int AlreadyTold, List<string> Unreachable);

        // ------------------------------------------------------------------ rena regler (enhetstestade)

        /// <summary>Ska raden meddelas? En placering (inte självanmäld, inte bekräftad) som ingen har fått besked om.</summary>
        public static bool Due(StaffAssignment a) =>
            a.NotifiedAt == null && StaffAssignmentStatus.Normalize(a.Status) == StaffAssignmentStatus.Planned;

        /// <summary>Vem som är "samma människa": medlemmen, annars e-postadressen. Null = ingen väg att nå personen.</summary>
        public static string? PersonKey(StaffAssignment a) =>
            a.MemberId is > 0 ? "m:" + a.MemberId
            : !string.IsNullOrWhiteSpace(a.Email) ? "e:" + a.Email!.Trim().ToLowerInvariant() : null;

        public static string Subject(int placements, string compName) =>
            (placements == 1 ? "Du har fått en placering" : $"Du har fått {placements} placeringar") + ": " + compName;

        // ------------------------------------------------------------------ meddela

        /// <summary>
        /// Meddelar personerna bakom <paramref name="rows"/>. Anroparen har redan prövat att den som meddelar får röra varje rad. Rader som redan är
        /// meddelade (eller självanmälda/bekräftade) hoppas över och räknas i <c>AlreadyTold</c>.
        /// </summary>
        public async Task<Result> NotifyAsync(IReadOnlyList<StaffAssignment> rows, int byMemberId)
        {
            var due = rows.GroupBy(r => r.Id).Select(g => g.First()).Where(Due).ToList();
            var alreadyTold = rows.GroupBy(r => r.Id).Count() - due.Count;
            var unreachable = new List<string>();

            var roots = new Dictionary<int, int>();
            int RootOf(StaffAssignment a)
            {
                var pid = a.PlanningId ?? 0;
                if (!roots.TryGetValue(pid, out var root))
                {
                    var fam = pid > 0 ? _planning.Family(pid) : null;
                    roots[pid] = root = fam is { Count: > 0 } ? fam[0].Id : pid;
                }
                return root;
            }

            var groups = new List<(string Key, int Root, List<StaffAssignment> Rows)>();
            foreach (var g in due.Where(a => PersonKey(a) != null).GroupBy(a => (Key: PersonKey(a)!, Root: RootOf(a))))
                groups.Add((g.Key.Key, g.Key.Root, g.OrderBy(a => a.StartsAt ?? a.DayDate ?? DateTime.MaxValue).ThenBy(a => a.Id).ToList()));
            foreach (var a in due.Where(a => PersonKey(a) == null)) unreachable.Add(string.IsNullOrWhiteSpace(a.DisplayName) ? "Namnlös" : a.DisplayName);

            if (groups.Count > MailLimits.MaxRecipientsPerSend)
                return new Result(false, $"Det blir {groups.Count} mejl, och ett utskick via pistol.nu får ha högst {MailLimits.MaxRecipientsPerSend}. Meddela några åt gången.",
                    0, 0, 0, 0, alreadyTold, unreachable);

            var by = byMemberId > 0 ? _members.GetById(byMemberId) : null;
            var passes = LoadPasses(due);
            int people = 0, placements = 0, mailed = 0, pushed = 0;

            foreach (var (key, root, personRows) in groups)
            {
                List<int> claimed = new();
                try
                {
                    // Tas FÖRE utskicket: den som redan tagit raderna (ett annat klick) lämnar inget kvar åt oss.
                    claimed = _staffing.ClaimNotified(personRows.Select(r => r.Id).ToList());
                    if (claimed.Count == 0) { alreadyTold += personRows.Count; continue; }
                    var mine = personRows.Where(r => claimed.Contains(r.Id)).ToList();
                    alreadyTold += personRows.Count - mine.Count;

                    var (gotMail, gotPush) = await SendOneAsync(mine, root, passes, by);
                    if (!gotMail && gotPush == 0)
                    {
                        _staffing.UnclaimNotified(claimed);   // inget nådde personen: raden ska gå att meddela igen
                        unreachable.Add(mine[0].DisplayName);
                        continue;
                    }
                    people++; placements += mine.Count;
                    if (gotMail) mailed++;
                    pushed += gotPush;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Meddela: besked till {Person} föll.", key);
                    if (claimed.Count > 0) { try { _staffing.UnclaimNotified(claimed); } catch { /* raden står kvar som meddelad — hellre det än ett dubbelmejl */ } }
                    unreachable.Add(personRows[0].DisplayName);
                }
            }
            return new Result(true, null, people, placements, mailed, pushed, alreadyTold, unreachable);
        }

        private async Task<(bool Mail, int Push)> SendOneAsync(List<StaffAssignment> rows, int rootId, Dictionary<int, StaffPass> passes, Umbraco.Cms.Core.Models.IMember? by)
        {
            var first = rows[0];
            var owner = _owners.OfRow(first.PlanningId, first.CompetitionId);
            var root = rootId > 0 ? _planning.Get(rootId) : null;
            var compName = !string.IsNullOrWhiteSpace(root?.Name) ? root!.Name : owner != null ? _owners.Name(owner) : "tävlingen";
            var dayPage = $"/mitt-schema?p={rootId}";

            var lines = rows.Select(r =>
            {
                var o = _owners.OfRow(r.PlanningId, r.CompetitionId);
                var role = o == null ? r.RoleKey : _roles.NameFor(o, _owners.Discipline(o), r.RoleKey);
                return (Role: role, Where: StaffingService.BuildScopeLabel(r), When: WhenText(r, r.PassId is > 0 && passes.TryGetValue(r.PassId.Value, out var p) ? p : null));
            }).ToList();

            var subject = Subject(lines.Count, compName);
            string? email = null; var memberId = first.MemberId is > 0 ? first.MemberId!.Value : 0;
            if (memberId > 0) email = _members.GetById(memberId)?.Email;
            if (string.IsNullOrWhiteSpace(email)) email = rows.Select(r => r.Email).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));

            var replyTo = by != null && !string.IsNullOrWhiteSpace(by.Email)
                ? MailReplyTo.To(by.Email, by.Name ?? "", $"{compName} via Pistol.nu") : MailReplyTo.NoReply;

            var any = false; var push = 0;
            if (memberId > 0)
            {
                var body = string.Join("; ", lines.Take(3).Select(l => l.Role + (l.When.Length > 0 ? ", " + l.When : ""))) + (lines.Count > 3 ? $" och {lines.Count - 3} till" : "") + $" — {compName}.";
                try { push = await _push.SendToMemberAsync(memberId, lines.Count == 1 ? "Du har fått en placering" : $"Du har fått {lines.Count} placeringar", body, dayPage, $"placement-{memberId}-{rootId}"); }
                catch (Exception ex) { _logger.LogWarning(ex, "Meddela: pushen till medlem {Member} föll.", memberId); }
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                string url, buttonLabel, footer;
                if (memberId > 0)
                {
                    url = _links.SiteUrl + dayPage; buttonLabel = "Se din tävlingsdag";
                    footer = "Du får det här mejlet för att du har fått en placering. Du behöver inte svara — på sidan ser du dina placeringar och tider. Kan du inte, hör av dig till den som står som avsändare.";
                }
                else
                {
                    var token = StaffingInviteToken.Protect(_dataProtection, first);
                    url = $"{_links.SiteUrl}/mina-uppdrag/svar?t={Uri.EscapeDataString(token)}"; buttonLabel = "Se din placering";
                    footer = "Du får det här mejlet för att du har fått en placering. Du behöver inget konto och inte svara — uppgifterna står här och på sidan knappen leder till. Kan du inte, hör av dig till den som står som avsändare.";
                }
                var sb = new System.Text.StringBuilder();
                sb.Append($"<p>Du har fått {(lines.Count == 1 ? "en placering" : $"{lines.Count} placeringar")} i <b>{PlanningNotifier.H(compName)}</b>:</p>");
                sb.Append("<table style=\"border-collapse:collapse;width:100%;max-width:560px\">");
                foreach (var l in lines)
                    sb.Append("<tr style=\"border-top:1px solid #ddd\"><td style=\"padding:8px 0\"><b>" + PlanningNotifier.H(l.Role) + "</b>"
                        + (string.IsNullOrWhiteSpace(l.Where) ? "" : "<br><span style=\"color:#666\">" + PlanningNotifier.H(l.Where) + "</span>")
                        + (l.When.Length > 0 ? "<br>" + PlanningNotifier.H(l.When) : "") + "</td></tr>");
                sb.Append("</table>");
                sb.Append($"<p>{PlanningNotifier.Button(buttonLabel, url)}</p>");
                if (by != null)
                {
                    var phone = PhoneOf(by);
                    sb.Append($"<p style=\"color:#555\">Frågor om placeringen: <b>{PlanningNotifier.H(by.Name ?? "")}</b>"
                        + (phone != null ? $", <a href=\"tel:{PlanningNotifier.H(phone)}\">{PlanningNotifier.H(phone)}</a>" : "") + ". Du kan också svara på det här mejlet.</p>");
                }
                var name = first.DisplayName;
                if (await _email.SendPlanningMailAsync(email!, subject, PlanningNotifier.Page(name, compName, sb.ToString(), footer), replyTo)) any = true;
            }
            return (any, push);
        }

        private static string? PhoneOf(Umbraco.Cms.Core.Models.IMember m)
        {
            try { return m.HasProperty("phoneNumber") && m.GetValue<string>("phoneNumber") is { Length: > 0 } p ? p.Trim() : null; }
            catch { return null; }
        }

        /// <summary>"lördag 14 november 08:00–13:00" / "… hela dagen" / "" när raden saknar dag.</summary>
        public static string WhenText(StaffAssignment a, StaffPass? pass)
        {
            var (date, interval) = FamilyWeekService.WhenOf(a, pass);
            if (date == null) return "";
            var day = date.Value.ToString("dddd d MMMM", Sv);
            return interval is { AllDay: false } iv ? $"{day} {iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : $"{day} hela dagen";
        }

        private Dictionary<int, StaffPass> LoadPasses(IEnumerable<StaffAssignment> rows)
        {
            var ids = rows.Where(r => r.PassId is > 0).Select(r => r.PassId!.Value).Distinct().ToList();
            if (ids.Count == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE Id IN (@ids)", new { ids }).ToDictionary(p => p.Id);
        }
    }
}
