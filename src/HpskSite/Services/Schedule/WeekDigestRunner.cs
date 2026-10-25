using HpskSite.Models.Schedule;
using HpskSite.Services.Mail;

namespace HpskSite.Services.Schedule
{
    public sealed record DigestRecipient(string Email, string Name);

    /// <summary>Den som bemannar: den man vänder sig till i mejlets fotnot, och som svaret går till.</summary>
    public sealed record DigestContact(string Name, string? Phone, string? Email);

    /// <summary>
    /// Allt veckomejlet behöver av omvärlden — databasen (urval, spärr, försöksräknare), veckan, mottagaren, kontaktpersonen och själva
    /// sändningen. Ett gränssnitt så att <see cref="WeekDigestRunner"/> kan enhetstestas med falska portar (steg 4–6: själva sändvägen
    /// var den enda helt otestade delen).
    /// </summary>
    public interface IWeekDigestPorts
    {
        IReadOnlyList<(int MemberId, int RootId)> Candidates(DateTime day);
        /// <summary>Tar spärren för personen och dagen. False = någon annan har den (eller kan inte tas): skicka inte.</summary>
        bool TryClaim(int memberId, DateTime day);
        void Release(int memberId, DateTime day);
        int Failures(int memberId, DateTime day);
        void RecordFailure(int memberId, DateTime day);
        /// <summary>Personens vecka den dagen i familjen. Null = inget den dagen. Ett fel KASTAS (och är inte "inget att skicka").</summary>
        MySchedule? Week(int memberId, int rootId, DateTime day);
        DigestRecipient? Recipient(int memberId);
        DigestContact? Contact(int rootId);
        Task<bool> SendAsync(string toEmail, string subject, string html, MailReplyTo replyTo);
    }

    public sealed record WeekDigestResult(int Sent, int Skipped, int Failed, bool Aborted);

    /// <summary>
    /// Steg 4 (B9), omarbetad efter Expertgruppens granskning (K3, C7, C8): <b>ett mejl per person och dag</b> över alla familjer personen
    /// har uppdrag i; spärren tas FÖRE sändningen (claim-then-send) och släpps vid misslyckande; högst
    /// <see cref="StaffWeekDigest.MaxAttempts"/> försök per person och dag; efter <see cref="StaffWeekDigest.MaxConsecutiveFailures"/>
    /// misslyckanden i rad avbryts svepet (SMTP är nere). Ett fel när veckan byggs räknas som ett misslyckande — det tolkas inte som
    /// "inget att skicka". Inga databaser, ingen SMTP: allt går genom <see cref="IWeekDigestPorts"/>.
    /// </summary>
    public static class WeekDigestRunner
    {
        public static async Task<WeekDigestResult> RunAsync(DateTime now, IWeekDigestPorts ports, string siteUrl, CancellationToken ct)
        {
            if (!StaffWeekDigest.InSendWindow(now)) return new WeekDigestResult(0, 0, 0, false);
            var day = StaffWeekDigest.DayFor(now);
            int sent = 0, skipped = 0, failed = 0, consecutive = 0;
            foreach (var person in ports.Candidates(day).GroupBy(c => c.MemberId))
            {
                if (ct.IsCancellationRequested) break;
                var memberId = person.Key;
                if (ports.Failures(memberId, day) >= StaffWeekDigest.MaxAttempts) { skipped++; continue; }
                if (!ports.TryClaim(memberId, day)) continue;

                bool ok, mailed = false;
                try { (ok, mailed) = await SendOneAsync(memberId, person.Select(p => p.RootId).Distinct().ToList(), day, ports, siteUrl); }
                catch { ok = false; }

                if (ok)
                {
                    consecutive = 0;
                    if (mailed) sent++;
                    continue;
                }
                ports.Release(memberId, day);
                ports.RecordFailure(memberId, day);
                failed++;
                if (++consecutive >= StaffWeekDigest.MaxConsecutiveFailures) return new WeekDigestResult(sent, skipped, failed, true);
            }
            return new WeekDigestResult(sent, skipped, failed, false);
        }

        /// <summary>(Ok, Mailed): Ok = klart (skickat, eller inget att skicka); Mailed = ett mejl gick iväg.</summary>
        private static async Task<(bool Ok, bool Mailed)> SendOneAsync(int memberId, List<int> roots, DateTime day, IWeekDigestPorts ports, string siteUrl)
        {
            var weeks = new List<MySchedule>();
            foreach (var root in roots.OrderBy(r => r))
                if (ports.Week(memberId, root, day) is { HasAny: true } w) weeks.Add(w);
            if (weeks.Count == 0) return (true, false);   // inget kvar den dagen (borttaget, avböjt) — spärren får stå

            var to = ports.Recipient(memberId);
            if (to == null || string.IsNullOrWhiteSpace(to.Email)) return (true, false);   // ingen adress: inget att göra, och inget att försöka om

            var inner = string.Concat(weeks.Select(w => StaffWeekDigest.Inner(w, day, $"{siteUrl}/mitt-schema?p={w.PlanningId}", siteUrl, heading: weeks.Count > 1)));
            var contact = ports.Contact(weeks[0].PlanningId);
            var replyTo = contact?.Email is { Length: > 0 } e
                ? MailReplyTo.To(e, contact.Name, $"{weeks[0].CompName} via Pistol.nu") : MailReplyTo.NoReply;
            var html = HpskSite.Services.Planning.PlanningNotifier.Page(to.Name, weeks[0].CompName, inner, StaffWeekDigest.Footer(contact?.Name, contact?.Phone));
            var ok = await ports.SendAsync(to.Email, StaffWeekDigest.Subject(weeks.Select(w => w.CompName).ToList(), day), html, replyTo);
            return (ok, ok);
        }
    }
}
