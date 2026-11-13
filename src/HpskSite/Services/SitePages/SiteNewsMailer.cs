using HpskSite.Models;
using HpskSite.Services.Mail;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>Vilka som får nyheten i mejlet, och varför ingen om den inte går.</summary>
    public sealed record SiteNewsMailPlan(bool CanMail, string? Reason, IReadOnlyList<(string Name, string Email)> Recipients);

    /// <summary>Utfallet av ett utskick.</summary>
    public sealed record SiteNewsMailResult(bool Started, string? Message, int Sent, int Failed);

    /// <summary>
    /// "Mejla till medlemmarna" (Stefan 2026-11-13: kryssrutan kommer direkt efter nyheterna). <b>Ett uttryckligt val vid publicering, aldrig automatiskt, och högst EN gång per nyhet</b>
    /// (<c>MailedDate</c> tas FÖRE utskicket; ett misslyckat utskick skickas inte om). Bara en publicerad nyhet som alla medlemmar får läsa (nivå Alla, Inloggade eller Klubbmedlemmar) mejlas:
    /// en Styrelse- eller Dold-nyhet kan aldrig nå fler än den får läsas av. Mottagare = ägarens aktiva medlemmar med e-post (kretsens klubbars medlemmar för en krets, en gång per person).
    /// Taket 250 per utskick är Simply websmtp:s gräns; fler mottagare vägras med besked (ett halvt utskick vore värre). Svaren går till föreningens kontaktadress.
    /// </summary>
    public sealed class SiteNewsMailer
    {
        public const int MaxRecipients = 250;
        private readonly ClubRosterService _roster;
        private readonly BoardWorkService _work;
        private readonly EmailService _email;
        private readonly IScopeProvider _scopes;
        private readonly ILogger<SiteNewsMailer> _logger;

        public SiteNewsMailer(ClubRosterService roster, BoardWorkService work, EmailService email, IScopeProvider scopes, ILogger<SiteNewsMailer> logger)
        {
            _roster = roster; _work = work; _email = email; _scopes = scopes; _logger = logger;
        }

        /// <summary>Kan nyheten mejlas till en publik av den här storleken? Ren funktion (testad): nivån, publiceringen och att den inte redan mejlats.</summary>
        public static string? Refusal(SitePostRow post, int recipientCount)
        {
            if (!post.IsPublished) return "Publicera nyheten först.";
            if (post.MailedDate != null) return "Nyheten har redan mejlats.";
            if (post.Visibility > SiteVisibility.Klubbmedlemmar) return "En nyhet som bara styrelsen eller administratören får läsa mejlas inte.";
            if (recipientCount == 0) return "Det finns inga medlemmar med e-postadress att mejla.";
            if (recipientCount > MaxRecipients) return $"Det är {recipientCount} mottagare och ett utskick får vara högst {MaxRecipients}. Kontakta pistol.nu om ni behöver skicka till fler.";
            return null;
        }

        /// <summary>Mottagarna, en gång per e-postadress, aktiva medlemmar med en adress.</summary>
        public IReadOnlyList<(string Name, string Email)> Recipients(string ownerType, int ownerId, IEnumerable<int> regionClubIds)
        {
            var rows = ownerType == SiteTemplates.Region ? _roster.ActiveInClubs(regionClubIds) : _roster.Active(ownerId);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<(string, string)>();
            foreach (var r in rows)
                if (!string.IsNullOrWhiteSpace(r.Email) && seen.Add(r.Email.Trim())) list.Add((string.IsNullOrWhiteSpace(r.FirstName) ? r.Name : r.FirstName, r.Email.Trim()));
            return list;
        }

        public SiteNewsMailPlan Plan(SitePostRow post, string ownerType, int ownerId, IEnumerable<int> regionClubIds)
        {
            var recipients = post.Visibility > SiteVisibility.Klubbmedlemmar ? Array.Empty<(string, string)>() : Recipients(ownerType, ownerId, regionClubIds);
            var reason = Refusal(post, recipients.Count);
            return new SiteNewsMailPlan(reason == null, reason, recipients);
        }

        public async Task<SiteNewsMailResult> SendAsync(string ownerType, int ownerId, SitePostRow post, string ownerName, string postUrl, IEnumerable<int> regionClubIds)
        {
            var plan = Plan(post, ownerType, ownerId, regionClubIds);
            if (!plan.CanMail) return new SiteNewsMailResult(false, plan.Reason, 0, 0);

            bool claimed;
            using (var scope = _scopes.CreateScope())
            {
                claimed = SitePosts.ClaimMail(scope.Database, post.Id, ownerType, ownerId, DateTime.Now);
                if (claimed) scope.Complete();
            }
            if (!claimed) return new SiteNewsMailResult(false, "Nyheten har redan mejlats.", 0, 0);

            var docType = ownerType == SiteTemplates.Region ? DocumentOwnerType.Region : DocumentOwnerType.Club;
            var (orgName, orgEmail) = _work.OwnerContact(docType, ownerId);
            var replyTo = orgEmail != null ? MailReplyTo.FromClub(orgName, orgEmail) : MailReplyTo.SiteAdmin;
            int sent = 0, failed = 0;
            foreach (var (name, email) in plan.Recipients)
            {
                try { if (await _email.SendSiteNewsAsync(email, name, ownerName, post.Title, post.Excerpt, postUrl, replyTo)) sent++; else failed++; }
                catch (Exception ex) { failed++; _logger.LogWarning(ex, "Nyhetsmejl till {Email} gick inte iväg.", email); }
            }
            using (var scope = _scopes.CreateScope())
            {
                SitePosts.SetMailedCount(scope.Database, post.Id, ownerType, ownerId, sent);
                scope.Complete();
            }
            return new SiteNewsMailResult(true, null, sent, failed);
        }
    }
}
