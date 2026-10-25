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
        public sealed record Target(int AssignmentId, int? HolderMemberId, string HolderName, string RoleKey, string RoleName, DateTime? Date, StaffInterval? Interval, string When);

        public (Target? Target, List<ReplacementCandidate> Candidates, string? Error) Candidates(int assignmentId)
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
            var when = interval is { AllDay: false } iv ? $"{date:yyyy-MM-dd} {iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : $"{date:yyyy-MM-dd} hela dagen";
            var target = new Target(a.Id, a.MemberId, a.DisplayName, a.RoleKey, roleName, date, interval, when);
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
                            if (it.Entry.Interval is { } x && it.Entry.Kind != PersonWeekKind.Intent) mine.Add((x, it.Entry.Label));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Ersättare: veckan för medlem {Member} gick inte att läsa.", p.MemberId); }
                var free = ReplacementRules.FreeFor(interval, mine.Select(m => m.Iv));
                var busy = free == false && interval is { } row ? mine.FirstOrDefault(m => m.Iv.Overlaps(row)).Label : null;
                result.Add(new ReplacementCandidate(p.MemberId, p.Name, p.RoleMatch, free, busy, p.Comment));
            }
            return (target, ReplacementRules.Rank(result), null);
        }

        /// <summary>Aviserar den som tar över (push och mejl). Returnerar om något gick iväg. Anropas efter att överlämningen sparats.</summary>
        public async Task<bool> NotifyAsync(StaffAssignment row, string? oldName, int byMemberId)
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
                var url = _links.SiteUrl + owner.SignupPath;
                var planningName = _owners.Name(owner);
                var title = "Du har fått ett uppdrag";
                var body = $"{roleName}{(when.Length > 0 ? ", " + when : "")} — {planningName}. Svara om du kan.";
                var any = false;
                try { any = await _push.SendToMemberAsync(row.MemberId.Value, title, body, owner.SignupPath, $"replace-{row.Id}") > 0; }
                catch (Exception ex) { _logger.LogWarning(ex, "Ersättare: pushen till medlem {Member} föll.", row.MemberId); }

                if (member != null && !string.IsNullOrWhiteSpace(member.Email))
                {
                    var inner = $"<p>Du har fått ett uppdrag i <b>{PlanningNotifier.H(planningName)}</b>:</p>"
                        + $"<p style=\"font-size:18px\"><b>{PlanningNotifier.H(roleName)}</b>{(when.Length > 0 ? "<br>" + PlanningNotifier.H(when) : "")}</p>"
                        + (string.IsNullOrWhiteSpace(oldName) ? "" : $"<p>{PlanningNotifier.H(oldName)} kunde inte. </p>")
                        + $"<p>{PlanningNotifier.Button("Svara på uppdraget", url)}</p>";
                    // Svaret går till den som lämnade över (motparten) — inte till en postlåda.
                    var replyTo = by != null && !string.IsNullOrWhiteSpace(by.Email)
                        ? MailReplyTo.To(by.Email, by.Name ?? "", $"{planningName} via Pistol.nu") : MailReplyTo.NoReply;
                    var html = PlanningNotifier.Page(member.Name ?? "", planningName, inner,
                        "Du får det här mejlet för att någon har lagt ett uppdrag på dig. Tackar du nej går det vidare till någon annan.");
                    if (await _email.SendPlanningMailAsync(member.Email, $"Uppdrag: {roleName} {when}".Trim(), html, replyTo)) any = true;
                }
                return any;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ersättare: avisering för uppdrag {Id} föll.", row.Id);
                return false;
            }
        }

        private static List<HelpSlotChoice> ParseChoices(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return Newtonsoft.Json.JsonConvert.DeserializeObject<List<HelpSlotChoice>>(json) ?? new(); }
            catch { return new(); }
        }
    }
}
