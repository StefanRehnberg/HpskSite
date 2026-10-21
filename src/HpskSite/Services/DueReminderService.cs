using System.Net;
using HpskSite.Models;
using HpskSite.Services.Mail;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Mejlpåminnelser 90 dagar före (2026-10-21, plan notes/arshjul-aterkommande-plan-2026-10-14.md del C):
    /// årshjulets tillfällen till föreningens styrelse, och banornas krav (besiktning, tillstånd, dokument) till
    /// banans förvaltare och styrelsen i klubbarna som äger, hyr eller är huvudanvändare.
    ///
    /// <para><b>Vilka tillfällen:</b> ej avklarade, med datum, inom <see cref="DueReminderRules.InWindow"/> — och
    /// för årshjulet bara de som har påminnelse på (<c>BoardWheelRule.Remind</c>; en engångspunkt alltid). Ett
    /// krav från en bana påminns det om EN gång, via banan — aldrig också som en punkt i varje klubbs årshjul.</para>
    ///
    /// <para><b>Claim-then-send</b> mot <see cref="DueReminder"/>; <c>Succeeded</c> bara när minst ett mejl gick.
    /// Demoklubben Ankeborg (och kretsen Ankeland) påminns aldrig.</para>
    ///
    /// <para><b>Länkarna</b> byggs ur <c>SiteUrl</c>-inställningen — en bakgrundstjänst har ingen förfrågan att
    /// läsa adressen ur (skalplanen D4).</para>
    /// </summary>
    public class DueReminderService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly BoardGovernanceService _gov;
        private readonly ShootingRangeService _ranges;
        private readonly BoardRoleService _roles;
        private readonly BoardWorkService _work;
        private readonly ReplyContactResolver _reply;
        private readonly EmailService _email;
        private readonly IConfiguration _config;
        private readonly ILogger<DueReminderService> _logger;

        public DueReminderService(IScopeProvider scopeProvider, BoardGovernanceService gov, ShootingRangeService ranges,
            BoardRoleService roles, BoardWorkService work, ReplyContactResolver reply, EmailService email,
            IConfiguration config, ILogger<DueReminderService> logger)
        {
            _scopeProvider = scopeProvider;
            _gov = gov;
            _ranges = ranges;
            _roles = roles;
            _work = work;
            _reply = reply;
            _email = email;
            _config = config;
            _logger = logger;
        }

        public sealed class RunResult
        {
            public int Due { get; set; }
            public int Claimed { get; set; }
            public int Sent { get; set; }
            public int NotSent { get; set; }
            public List<string> Lines { get; } = new();
        }

        private string SiteUrl => (_config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');

        private static bool IsDemo(string? name) =>
            !string.IsNullOrEmpty(name) && (name.Contains("ankeborg", StringComparison.OrdinalIgnoreCase)
                                            || name.Contains("ankeland", StringComparison.OrdinalIgnoreCase));

        public async Task<RunResult> RunAsync(DateTime today, CancellationToken ct = default)
        {
            var result = new RunResult();
            await RunWheelAsync(today, result, ct);
            await RunRangesAsync(today, result, ct);
            return result;
        }

        // ---- Årshjulet --------------------------------------------------------------------

        private async Task RunWheelAsync(DateTime today, RunResult result, CancellationToken ct)
        {
            var fromYear = today.AddDays(-DueReminderRules.MaxDaysOverdue).Year;
            var toYear = today.AddDays(DueReminderRules.DaysBefore).Year;
            foreach (var (ownerType, ownerId) in _gov.WheelReminderOwners(fromYear, toYear))
            {
                if (ct.IsCancellationRequested) return;
                var (orgName, orgEmail) = _work.OwnerContact(ownerType, ownerId);
                if (IsDemo(orgName)) continue;

                var due = _gov.GetOwnOccurrences(ownerType, ownerId, fromYear, toYear)
                    .Where(o => o.Remind && !o.Done && !o.FromRange && o.TargetDate.HasValue
                                && DueReminderRules.InWindow(o.TargetDate.Value, today))
                    .ToList();
                if (due.Count == 0) continue;

                // Registrera varje tillfälle för sig (spärren gäller per tillfälle) — skicka sedan ETT mejl med alla
                // nyregistrerade för föreningen.
                var claimed = new List<(int ClaimId, WheelOccurrence O)>();
                foreach (var o in due)
                {
                    result.Due++;
                    var (source, key) = o.RuleId.HasValue
                        ? (DueReminderSource.WheelRule, $"{o.RuleId}:{o.Year}")
                        : (DueReminderSource.WheelItem, o.ItemId?.ToString() ?? "");
                    var claimId = TryClaim(source, key, ownerType, ownerId, o.TargetDate!.Value);
                    if (claimId == 0) continue;
                    result.Claimed++;
                    claimed.Add((claimId, o));
                }
                if (claimed.Count == 0) continue;

                var recipients = BoardRecipients(ownerType, ownerId, orgName, orgEmail);
                var replyTo = ownerType == DocumentOwnerType.Club ? _reply.ForClub(ownerId) : _reply.ForRegion(ownerId);
                var link = $"{SiteUrl}/styrelse?type={ownerType}&id={ownerId}#vy=arshjul";
                var items = claimed.Select(c => (c.O.Title, c.O.TargetDate!.Value)).ToList();
                var sent = await SendAll(recipients, items, today, orgName,
                    "årshjulet för " + WebUtility.HtmlEncode(orgName), false, link, "Öppna årshjulet", replyTo);
                foreach (var c in claimed) Finish(c.ClaimId, sent);
                Tally(result, sent, $"{orgName}: {string.Join("; ", claimed.Select(c => $"{c.O.Title} {c.O.TargetDate:yyyy-MM-dd}"))} → {sent} av {recipients.Count}");
            }
        }

        /// <summary>Styrelsens ledamöter med e-post; finns ingen går mejlet till föreningens kontaktadress.</summary>
        private List<(string Name, string Email)> BoardRecipients(int ownerType, int ownerId, string orgName, string? orgEmail)
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _roles.GetBoardMembers(ownerType, ownerId, boardOnly: true))
            {
                var (name, email) = _work.MemberContact(r.MemberId);
                if (email != null && seen.Add(email)) list.Add((name, email));
            }
            if (list.Count == 0 && orgEmail != null) list.Add((orgName, orgEmail));
            return list;
        }

        // ---- Banorna ----------------------------------------------------------------------

        private async Task RunRangesAsync(DateTime today, RunResult result, CancellationToken ct)
        {
            List<RangeRequirement> reqs;
            try { reqs = _ranges.GetAllRangeRequirements(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Påminnelser: banornas krav kunde inte läsas."); return; }

            foreach (var group in reqs.Where(r => DueReminderRules.InWindow(r.Due, today)).GroupBy(r => r.RangeId))
            {
                if (ct.IsCancellationRequested) return;
                var rangeName = group.First().RangeName;
                if (IsDemo(rangeName)) continue;
                var claimed = new List<(int ClaimId, RangeRequirement Req)>();
                foreach (var req in group)
                {
                    result.Due++;
                    var claimId = TryClaim(DueReminderSource.ForRangeKind(req.Kind), req.SourceId.ToString(),
                        DueReminderSource.RangeOwner, req.RangeId, req.Due);
                    if (claimId == 0) continue;
                    result.Claimed++;
                    claimed.Add((claimId, req));
                }
                if (claimed.Count == 0) continue;

                var (stewards, clubs) = _ranges.GetRequirementRecipients(group.Key);
                var recipients = new List<(string Name, string Email)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in stewards)
                {
                    var (name, email) = _work.MemberContact(s);
                    if (email != null && seen.Add(email)) recipients.Add((name, email));
                }
                foreach (var clubId in clubs)
                {
                    var (clubName, clubEmail) = _work.OwnerContact(DocumentOwnerType.Club, clubId);
                    if (IsDemo(clubName)) continue;
                    foreach (var r in BoardRecipients(DocumentOwnerType.Club, clubId, clubName, clubEmail))
                        if (seen.Add(r.Email)) recipients.Add(r);
                }
                var replyTo = stewards.Count > 0 ? _reply.ForMember(stewards[0]) : MailReplyTo.SiteAdmin;
                var items = claimed.Select(c => (c.Req.Title, c.Req.Due)).ToList();
                var sent = await SendAll(recipients, items, today, rangeName,
                    "skjutbanan " + WebUtility.HtmlEncode(rangeName), true, $"{SiteUrl}{claimed[0].Req.Url}", "Öppna banan", replyTo);
                foreach (var c in claimed) Finish(c.ClaimId, sent);
                Tally(result, sent, $"{rangeName}: {string.Join("; ", claimed.Select(c => $"{c.Req.Title} {c.Req.Due:yyyy-MM-dd}"))} → {sent} av {recipients.Count}");
            }
        }

        // ---- Gemensamt --------------------------------------------------------------------

        private async Task<int> SendAll(List<(string Name, string Email)> to, List<(string Title, DateTime Due)> items,
            DateTime today, string where, string fromHtml, bool fromRange, string link, string linkLabel, MailReplyTo replyTo)
        {
            var subject = DueReminderRules.Subject(items, today, where);
            var paragraphs = DueReminderRules.Paragraphs(
                items.Select(i => (WebUtility.HtmlEncode(i.Title), i.Due)).ToList(), today, fromHtml, fromRange);
            int sent = 0;
            foreach (var (name, email) in to)
            {
                try { if (await _email.SendBoardWorkNoticeAsync(email, name, subject, paragraphs, link, linkLabel, replyTo)) sent++; }
                catch (Exception ex) { _logger.LogWarning(ex, "Påminnelsen {Subject} till {Email} gick inte iväg.", subject, email); }
            }
            return sent;
        }

        private static void Tally(RunResult result, int sent, string line)
        {
            if (sent > 0) result.Sent++; else result.NotSent++;
            result.Lines.Add(line);
        }

        /// <summary>
        /// Registrera påminnelsen. 0 = redan registrerad (eller en samtidig körning hann först) — skicka inte.
        /// <c>WHERE NOT EXISTS</c> avgör, inte att det unika indexet kastar (Umbraco loggar varje SqlException på
        /// Error innan en catch nås — samma lärdom som licenspåminnelserna). Indexet är spärren mot en äkta kapplöpning.
        /// </summary>
        private int TryClaim(string source, string key, int ownerType, int ownerId, DateTime due)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                var n = db.Execute(
                    @"INSERT INTO DueReminder (Source, SourceKey, OwnerType, OwnerId, DueDate, SentAt, Succeeded, RecipientCount)
                      SELECT @0, @1, @2, @3, @4, @5, 0, 0
                      WHERE NOT EXISTS (SELECT 1 FROM DueReminder WHERE Source = @0 AND SourceKey = @1
                                        AND OwnerType = @2 AND OwnerId = @3 AND DueDate = @4)",
                    source, key, ownerType, ownerId, due.Date, DateTime.Now);
                if (n == 0) return 0;
                return db.ExecuteScalar<int>(
                    "SELECT Id FROM DueReminder WHERE Source = @0 AND SourceKey = @1 AND OwnerType = @2 AND OwnerId = @3 AND DueDate = @4",
                    source, key, ownerType, ownerId, due.Date);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Påminnelsen kunde inte registreras ({Source} {Key}) — skickas inte.", source, key);
                return 0;
            }
        }

        private void Finish(int claimId, int sent)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                scope.Database.Execute("UPDATE DueReminder SET Succeeded = @1, RecipientCount = @2 WHERE Id = @0",
                    claimId, sent > 0, sent);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Påminnelse {Id}: utfallet kunde inte sparas.", claimId); }
        }

        /// <summary>Saknade tabeller (startkontrollen).</summary>
        public List<string> MissingSchema()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.DueReminder', 'U') IS NULL THEN 0 ELSE 1 END") == 0
                ? new List<string> { "DueReminder" } : new List<string>();
        }
    }
}
