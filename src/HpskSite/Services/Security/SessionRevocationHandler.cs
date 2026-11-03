using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Säkerhetsplanen 2026-11-02, paket 2 (P5b): en medlem som stängs av, låses eller förlorar en roll ska inte behålla sin
    /// öppna session. Inloggningskakan bär rollerna från inloggningstillfället och prövas mot databasen bara när säkerhetsstämpeln
    /// valideras — därför byter vi stämpeln när något av det här händer (vid VARJE sparning, så alla skrivvägar täcks).
    /// Hur snabbt ändringen slår igenom bestäms av <c>Security:Session:StampValidationMinutes</c> (se <see cref="MemberSecurityStampValidatorOptions"/>).
    /// Ett fel här får aldrig fälla sparningen; det loggas.
    /// </summary>
    public sealed class SessionRevocationHandler :
        INotificationHandler<MemberSavingNotification>,
        INotificationHandler<RemovedMemberRolesNotification>
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<SessionRevocationHandler> _logger;

        public SessionRevocationHandler(IScopeProvider scopeProvider, ILogger<SessionRevocationHandler> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>Avstängd (IsApproved blir falskt) eller låst: ny stämpel i samma sparning.</summary>
        public void Handle(MemberSavingNotification notification)
        {
            foreach (var member in notification.SavedEntities)
            {
                try
                {
                    if (member.Id <= 0) continue;
                    if (ShouldRevoke(member.IsApproved, member.IsPropertyDirty(nameof(IMember.IsApproved)),
                                     member.IsLockedOut, member.IsPropertyDirty(nameof(IMember.IsLockedOut))))
                    {
                        member.SecurityStamp = Guid.NewGuid().ToString();
                        _logger.LogInformation("SEC: Sessionerna för medlem {MemberId} ogiltigförklaras (avstängd eller låst)", member.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SEC: Kunde inte byta säkerhetsstämpel för medlem {MemberId}", member.Id);
                }
            }
        }

        /// <summary>En roll togs bort: byt stämpeln direkt i tabellen (ingen ny medlemssparning, inga följdhändelser).</summary>
        public void Handle(RemovedMemberRolesNotification notification)
        {
            try
            {
                var ids = notification.MemberIds?.Where(i => i > 0).Distinct().ToArray() ?? Array.Empty<int>();
                if (ids.Length == 0) return;
                using var scope = _scopeProvider.CreateScope();
                foreach (var chunk in ids.Chunk(500))
                {
                    scope.Database.Execute(
                        "UPDATE cmsMember SET securityStampToken = @0 WHERE nodeId IN (@ids)",
                        Guid.NewGuid().ToString(), new { ids = chunk });
                }
                scope.Complete();
                _logger.LogInformation("SEC: Sessionerna ogiltigförklaras för {Count} medlem(mar) som förlorat en roll", ids.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SEC: Kunde inte byta säkerhetsstämpel efter borttagen roll");
            }
        }

        /// <summary>Regeln: avstängning (IsApproved ändras till falskt) eller låsning (IsLockedOut ändras till sant).</summary>
        public static bool ShouldRevoke(bool isApproved, bool approvedDirty, bool isLockedOut, bool lockedDirty) =>
            (approvedDirty && !isApproved) || (lockedDirty && isLockedOut);
    }
}
