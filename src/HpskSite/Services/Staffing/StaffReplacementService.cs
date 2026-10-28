using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Mail;
using HpskSite.Services.Notifications;
using HpskSite.Services.Planning;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Steg 6b (B11): <b>Hitta ersättare</b> när någon hoppar av — ofta kvällen före. Föreslår de som anmält sig till dagen i någon del
    /// av familjen (reservpoolen, steg 6a), rankade efter om de är lediga den tiden och har rätt roll, och aviserar den som tar över.
    /// Själva överlämningen är <see cref="StaffingService.ReplaceHolder"/> — samma rad, ny person.
    /// </summary>
    public class StaffReplacementService
    {
        /// <summary>Så många i poolen granskas — varje kandidat kostar en veckoberäkning.</summary>
        public const int MaxChecked = 25;

        private readonly IScopeProvider _scopes;
        private readonly FamilyWeekService _week;
        private readonly PlanningService _planning;
        private readonly StaffOwnerResolver _owners;
        private readonly RoleCatalogService _roles;
        private readonly IMemberService _members;
        private readonly HpskSite.Services.EmailService _email;
        private readonly WebPushService _push;
        private readonly PlanningLinkService _links;
        private readonly ILogger<StaffReplacementService> _logger;

        public StaffReplacementService(IScopeProvider scopes, FamilyWeekService week, PlanningService planning, StaffOwnerResolver owners,
            RoleCatalogService roles, IMemberService members, HpskSite.Services.EmailService email, WebPushService push, PlanningLinkService links,
            ILogger<StaffReplacementService> logger)
        {
            _scopes = scopes; _week = week; _planning = planning; _owners = owners; _roles = roles; _members = members; _email = email;
            _push = push; _links = links; _logger = logger;
        }

        /// <summary>Uppdraget som ska lämnas över: vad det är och när. Null Date = uppdraget har ingen dag än.</summary>
        public sealed record Target(int AssignmentId, int? HolderMemberId, string HolderName, string RoleKey, string RoleName, DateTime? Date, StaffInterval? Interval, string When,
            bool HadAccess = false);

        /// <summary>
        /// <paramref name="canSeePart"/>: får den som frågar läsa bemanningen i den delen? Vid "Upptagen" visas annars bara "uppdrag i
        /// en annan del" — och en egen start visas aldrig med titel eller tid, bara som "egen start" (steg 4–6 M2: en del-redaktör ska
        /// inte kunna läsa andras scheman i delar hen inte får se).
        /// </summary>
        public (Target? Target, List<ReplacementCandidate> Candidates, string? Error) Candidates(int assignmentId, Func<int, bool>? canSeePart = null)
        {
            StaffAssignment? a; StaffPass? pass = null; List<StaffHelpSignup> signups; HashSet<int> slotIds = new();
            var empty = new List<ReplacementCandidate>();
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                a = db.SingleOrDefault<StaffAssignment>("SELECT * FROM StaffAssignment WHERE Id = @0", assignmentId);
                if (a?.PlanningId is not > 0) return (null, empty, "Uppdraget hittades inte.");
                if (a.PassId is > 0) pass = db.SingleOrDefault<StaffPass>("SELECT * FROM StaffPass WHERE Id = @0", a.PassId);
                var (date0, _) = FamilyWeekService.WhenOf(a, pass);
                if (date0 == null) return (null, empty, "Uppdraget har ingen dag än.");
                var fam = _planning.Family(a.PlanningId.Value);
                if (fam.Count == 0) return (null, empty, "Planeringen finns inte längre.");
                var ids = string.Join(",", fam.Select(f => f.Id));
                signups = db.Fetch<StaffHelpSignup>($"SELECT * FROM StaffHelpSignup WHERE PlanningId IN ({ids}) AND MemberId > 0");
                slotIds = db.Fetch<int>($"SELECT Id FROM StaffHelpSlot WHERE PlanningId IN ({ids}) AND CAST(SlotDate AS date) = @0", date0.Value.Date).ToHashSet();
            }

            var (date, interval) = FamilyWeekService.WhenOf(a, pass);
            var owner = _owners.ForPlanning(a.PlanningId.Value);
            var roleName = owner == null ? a.RoleKey : _roles.NameFor(owner, _owners.Discipline(owner), a.RoleKey);
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            var dayText = date!.Value.ToString("dddd d MMMM", sv);
            var when = interval is { AllDay: false } iv ? $"{dayText} {iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : $"{dayText} hela dagen";
            var target = new Target(a.Id, a.MemberId, a.DisplayName, a.RoleKey, roleName, date, interval, when, a.HasAdminAccess);
            var rootId = _planning.Family(a.PlanningId.Value)[0].Id;

            // Poolen: en person kan ha en anmälan per del — pröva alla hens val.
            var pool = new List<(int MemberId, string Name, bool RoleMatch, string? Comment)>();
            foreach (var g in signups.Where(x => x.MemberId != a.MemberId).GroupBy(x => x.MemberId))
            {
                var choices = g.SelectMany(x => ParseChoices(x.SlotsJson)).ToList();
                var (volunteers, role) = ReplacementRules.Matches(choices, date!.Value, a.RoleKey, slotIds);
                if (!volunteers) continue;
                var latest = g.OrderByDescending(x => x.ModifiedDate).First();
                pool.Add((g.Key, latest.MemberName, role, g.Select(x => x.Comment).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))));
            }

            var result = new List<ReplacementCandidate>();
            foreach (var p in pool.OrderBy(p => p.Name).Take(MaxChecked))
            {
                var mine = new List<(StaffInterval Iv, string Label)>();
                try
                {
                    var me = _week.Build(rootId, StaffPersonKey.For(p.MemberId, null)).FirstOrDefault();
                    foreach (var d in me?.Days.Where(d => d.Date.Date == date!.Value.Date) ?? Enumerable.Empty<HpskSite.Models.Staffing.PersonWeekDay>())
                        foreach (var it in d.Items)
                            if (it.Entry.Interval is { } x && it.Entry.Kind != PersonWeekKind.Intent)
                                mine.Add((x, BusyLabel(it.Entry, canSeePart)));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Ersättare: veckan för medlem {Member} gick inte att läsa.", p.MemberId); }
                var free = ReplacementRules.FreeFor(interval, mine.Select(m => m.Iv));
                var busy = free == false && interval is { } row ? mine.FirstOrDefault(m => m.Iv.Overlaps(row)).Label : null;
                result.Add(new ReplacementCandidate(p.MemberId, p.Name, p.RoleMatch, free, busy, p.Comment, PhoneOf(p.MemberId)));
            }
            return (target, ReplacementRules.Rank(result), null);
        }

        /// <summary>En rad i ledarens lista: en placering i delen som går att lämna över. <see cref="Open"/> = ingen har den.</summary>
        public sealed record PartRow(int AssignmentId, string Holder, string RoleName, string When, string Status, bool Open);

        /// <summary>
        /// Placeringarna i <paramref name="planningId"/> som en ledare där kan lämna över: kommande dagar, utan behörighet och inte ledarens egna
        /// (<see cref="ReplacementAccessRules.Refusal"/>). Tomma först — det är dem man letar ersättare till.
        /// </summary>
        public List<PartRow> PartAssignments(int planningId, int viewerMemberId)
        {
            List<StaffAssignment> rows; Dictionary<int, StaffPass> passes;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                rows = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE PlanningId = @0", planningId);
                var passIds = rows.Where(r => r.PassId is > 0).Select(r => r.PassId!.Value).Distinct().ToList();
                passes = passIds.Count == 0 ? new() : db.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE Id IN (@ids)", new { ids = passIds }).ToDictionary(p => p.Id);
            }
            var owner = _owners.ForPlanning(planningId);
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            var today = HpskSite.Services.Hosting.SwedishTime.Now.Date;
            var result = new List<(DateTime Date, PartRow Row)>();
            foreach (var a in rows)
            {
                if (ReplacementAccessRules.Refusal(true, a.RoleKey, a.HasAdminAccess, a.MemberId == viewerMemberId && viewerMemberId > 0, null) != null) continue;
                var (date, interval) = FamilyWeekService.WhenOf(a, a.PassId is > 0 && passes.TryGetValue(a.PassId.Value, out var ps) ? ps : null);
                if (date == null || date.Value.Date < today) continue;
                var dayText = date.Value.ToString("dddd d MMMM", sv);
                var when = interval is { AllDay: false } iv ? $"{dayText} {iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : $"{dayText} hela dagen";
                var roleName = owner == null ? a.RoleKey : _roles.NameFor(owner, _owners.Discipline(owner), a.RoleKey);
                var open = a.MemberId is not > 0;
                result.Add((date.Value.Date, new PartRow(a.Id, string.IsNullOrWhiteSpace(a.DisplayName) ? "Ingen utsedd" : a.DisplayName, roleName, when, StaffAssignmentStatus.Normalize(a.Status), open)));
            }
            return result.OrderByDescending(x => x.Row.Open).ThenBy(x => x.Date).ThenBy(x => x.Row.RoleName).Select(x => x.Row).Take(100).ToList();
        }

        /// <summary>
        /// Skiftschemat: en person som redan fått besked om sin placering har fått en ny tid (en gräns flyttades, ett skift delades). Push och mejl med gamla och nya tiden, svar
        /// till den som ändrade. Returnerar om något gick iväg. Anropas efter att ändringen sparats; ett fel här fäller aldrig sparningen.
        /// </summary>
        public async Task<bool> NotifyTimeChangedAsync(StaffAssignment row, string before, int byMemberId)
        {
            if (row.MemberId is not > 0) return false;
            try
            {
                var owner = row.PlanningId is > 0 ? _owners.ForPlanning(row.PlanningId.Value) : null;
                if (owner == null) return false;
                var member = _members.GetById(row.MemberId.Value);
                var by = _members.GetById(byMemberId);
                var roleName = _roles.NameFor(owner, _owners.Discipline(owner), row.RoleKey);
                var (date, interval) = FamilyWeekService.WhenOf(row, null);
                var day = date == null ? "" : date.Value.ToString("dddd d MMMM", CultureInfo.GetCultureInfo("sv-SE"));
                var now = interval is { AllDay: false } iv ? $"{iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : "hela dagen";
                var planningName = _owners.Name(owner);
                var title = "Din tid har ändrats";
                var body = $"{roleName}{(day.Length > 0 ? ", " + day : "")}: nu {now} (var {before}) — {planningName}.";
                var any = false;
                var dayPage = DayPagePath(row.PlanningId!.Value);
                try { any = await _push.SendToMemberAsync(row.MemberId.Value, title, body, dayPage, $"retime-{row.Id}-{now}") > 0; }
                catch (Exception ex) { _logger.LogWarning(ex, "Skiftschema: pushen till medlem {Member} föll.", row.MemberId); }
                if (member != null && !string.IsNullOrWhiteSpace(member.Email))
                {
                    var url = _links.SiteUrl + dayPage;
                    var inner = $"<p>Din tid i <b>{PlanningNotifier.H(planningName)}</b> har ändrats:</p>"
                        + $"<p style=\"font-size:18px\"><b>{PlanningNotifier.H(roleName)}</b>{(day.Length > 0 ? "<br>" + PlanningNotifier.H(day) : "")}<br><b>{PlanningNotifier.H(now)}</b> <span style=\"color:#666\">(var {PlanningNotifier.H(before)})</span></p>"
                        + $"<p>{PlanningNotifier.Button("Se din tid", url)}</p>"
                        + ContactHtml(by);
                    var replyTo = by != null && !string.IsNullOrWhiteSpace(by.Email)
                        ? MailReplyTo.To(by.Email, by.Name ?? "", $"{planningName} via Pistol.nu") : MailReplyTo.NoReply;
                    var html = PlanningNotifier.Page(member.Name ?? "", planningName, inner, "Du får det här mejlet för att din tid har ändrats efter att du fått besked om din placering. Det här är den som gäller.");
                    if (await _email.SendPlanningMailAsync(member.Email, $"Ny tid: {roleName} {day}".Trim(), html, replyTo)) any = true;
                }
                return any;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skiftschema: beskedet om ny tid för uppdrag {Id} föll.", row.Id);
                return false;
            }
        }

        /// <summary>Aviserar den som tar över (push och mejl). Returnerar om något gick iväg. Anropas efter att överlämningen sparats.</summary>
        public async Task<bool> NotifyAsync(StaffAssignment row, string? oldName, int? oldMemberId, int byMemberId)
        {
            if (row.MemberId is not > 0) return false;
            try
            {
                var owner = row.PlanningId is > 0 ? _owners.ForPlanning(row.PlanningId.Value) : null;
                if (owner == null) return false;
                var member = _members.GetById(row.MemberId.Value);
                var by = _members.GetById(byMemberId);
                var roleName = _roles.NameFor(owner, _owners.Discipline(owner), row.RoleKey);
                var (date, interval) = FamilyWeekService.WhenOf(row, null);
                var when = date == null ? "" : date.Value.ToString("dddd d MMMM", CultureInfo.GetCultureInfo("sv-SE"))
                    + (interval is { AllDay: false } iv ? $" {iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : " hela dagen");
                var dayPage = DayPagePath(row.PlanningId!.Value);
                var url = _links.SiteUrl + dayPage;
                var planningName = _owners.Name(owner);
                var title = "Du har fått en placering";
                var body = $"{roleName}{(when.Length > 0 ? ", " + when : "")} — {planningName}.";
                var any = false;
                try { any = await _push.SendToMemberAsync(row.MemberId.Value, title, body, dayPage, $"replace-{row.Id}") > 0; }
                catch (Exception ex) { _logger.LogWarning(ex, "Ersättare: pushen till medlem {Member} föll.", row.MemberId); }

                if (member != null && !string.IsNullOrWhiteSpace(member.Email))
                {
                    var inner = $"<p>Du har fått en placering i <b>{PlanningNotifier.H(planningName)}</b>:</p>"
                        + $"<p style=\"font-size:18px\"><b>{PlanningNotifier.H(roleName)}</b>{(when.Length > 0 ? "<br>" + PlanningNotifier.H(when) : "")}</p>"
                        + (string.IsNullOrWhiteSpace(oldName) ? "" : $"<p>{PlanningNotifier.H(oldName)} kunde inte. </p>")
                        + $"<p>{PlanningNotifier.Button("Se din placering", url)}</p>"
                        + ContactHtml(by);
                    // Svaret går till den som lämnade över (motparten) — inte till en postlåda.
                    var replyTo = by != null && !string.IsNullOrWhiteSpace(by.Email)
                        ? MailReplyTo.To(by.Email, by.Name ?? "", $"{planningName} via Pistol.nu") : MailReplyTo.NoReply;
                    var html = PlanningNotifier.Page(member.Name ?? "", planningName, inner,
                        "Du får det här mejlet för att du har fått en placering. Kan du inte, hör av dig till den som står som avsändare.");
                    if (await _email.SendPlanningMailAsync(member.Email, $"Du har fått en placering: {roleName} {when}".Trim(), html, replyTo)) any = true;
                }

                // Steg 4–6 K6: den som lämnade uppdraget ska få veta att hen inte ska komma — annars har hen kanske redan fått kvällsmejlet.
                if (oldMemberId is > 0 && oldMemberId != row.MemberId)
                    await NotifyReleasedAsync(oldMemberId.Value, roleName, when, planningName, row.DisplayName, by, dayPage);
                return any;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ersättare: avisering för uppdrag {Id} föll.", row.Id);
                return false;
            }
        }

        /// <summary>Tävlingsdagssidan (Mitt schema för hela familjen) — dit alla meddelanden om placeringar leder.</summary>
        private string DayPagePath(int planningId)
        {
            var fam = _planning.Family(planningId);
            return $"/mitt-schema?p={(fam.Count > 0 ? fam[0].Id : planningId)}";
        }

        private async Task NotifyReleasedAsync(int oldMemberId, string roleName, string when, string planningName, string newName,
            Umbraco.Cms.Core.Models.IMember? by, string signupPath)
        {
            try
            {
                var old = _members.GetById(oldMemberId);
                if (old == null) return;
                var body = $"{roleName}{(when.Length > 0 ? ", " + when : "")} i {planningName} har lämnats över till {newName}. Du behöver inte komma.";
                try { await _push.SendToMemberAsync(oldMemberId, "Din placering har lämnats över", body, signupPath, $"released-{oldMemberId}-{roleName}"); }
                catch (Exception ex) { _logger.LogWarning(ex, "Ersättare: pushen till avlöst medlem {Member} föll.", oldMemberId); }
                if (string.IsNullOrWhiteSpace(old.Email)) return;
                var inner = $"<p>Din placering i <b>{PlanningNotifier.H(planningName)}</b> har lämnats över till <b>{PlanningNotifier.H(newName)}</b>:</p>"
                    + $"<p style=\"font-size:18px\"><b>{PlanningNotifier.H(roleName)}</b>{(when.Length > 0 ? "<br>" + PlanningNotifier.H(when) : "")}</p>"
                    + "<p><b>Du behöver inte komma.</b> Har du redan fått ett mejl om dagen är det här det som gäller.</p>"
                    + ContactHtml(by);
                var replyTo = by != null && !string.IsNullOrWhiteSpace(by.Email)
                    ? MailReplyTo.To(by.Email, by.Name ?? "", $"{planningName} via Pistol.nu") : MailReplyTo.NoReply;
                await _email.SendPlanningMailAsync(old.Email, $"Placeringen är lämnad över: {roleName} {when}".Trim(),
                    PlanningNotifier.Page(old.Name ?? "", planningName, inner, "Du får det här mejlet för att placeringen låg på dig."), replyTo);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Ersättare: beskedet till avlöst medlem {Member} föll.", oldMemberId); }
        }

        /// <summary>"Frågor? Ring X" — den som lämnade över är den motpart mottagaren kan nå (Reply-To är samma person).</summary>
        private string ContactHtml(Umbraco.Cms.Core.Models.IMember? by)
        {
            if (by == null) return "";
            var phone = PhoneOf(by.Id);
            return $"<p style=\"color:#555\">Frågor om placeringen: <b>{PlanningNotifier.H(by.Name ?? "")}</b>"
                + (phone != null ? $", <a href=\"tel:{PlanningNotifier.H(phone)}\">{PlanningNotifier.H(phone)}</a>" : "") + ".</p>";
        }

        /// <summary>Vad som visas vid "Upptagen". Egen start och rader i delar den frågande inte läser avslöjas inte.</summary>
        public static string BusyLabel(PersonWeekEntry e, Func<int, bool>? canSeePart)
        {
            if (e.Kind == PersonWeekKind.Start) return "egen start";
            if (canSeePart != null && e.PlanningId is > 0 && !canSeePart(e.PlanningId.Value)) return "placering i en annan del";
            return e.Label;
        }

        /// <summary>Medlemmens telefonnummer ur profilen (det som står där, i ordningen medlemmen skrivit det) — det man ringer kl 06:30.</summary>
        private string? PhoneOf(int memberId)
        {
            try
            {
                var m = _members.GetById(memberId);
                return m != null && m.HasProperty("phoneNumber") && m.GetValue<string>("phoneNumber") is { Length: > 0 } p ? p.Trim() : null;
            }
            catch { return null; }
        }

        private static List<HelpSlotChoice> ParseChoices(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return Newtonsoft.Json.JsonConvert.DeserializeObject<List<HelpSlotChoice>>(json) ?? new(); }
            catch { return new(); }
        }
    }
}
