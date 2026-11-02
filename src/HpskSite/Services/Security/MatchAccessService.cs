using HpskSite.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Security
{
    /// <summary>Vem som anropar träningsmatchens hubb — bevisat på servern, aldrig ur parametrar.</summary>
    public sealed record HubCaller(int? MemberId, int? GuestId, string DisplayName, string PictureUrl, string ClubName)
    {
        public bool IsMember => MemberId.HasValue;
        public bool IsGuest => GuestId.HasValue;
    }

    /// <summary>
    /// EN regel för vem som får följa och arrangera en träningsmatch i realtid (säkerhetsplanen 2026-11-02, A7/H3).
    ///
    /// <para>En inloggad medlem (webbens kaka eller mobilens JWT) eller en gäst med giltig gästkaka
    /// <c>guest_session_{kod}</c> får följa matchen. <b>Bara matchens skapare</b> får lyssna på arrangörens
    /// förfrågningar. Anonyma anslutningar utan någondera släpps inte in — Stefans beslut: ingen följer en match med
    /// bara koden.</para>
    /// </summary>
    public class MatchAccessService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ClubService _clubService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MatchAccessService> _logger;

        public MatchAccessService(
            IScopeProvider scopeProvider, IMemberService memberService, IMemberManager memberManager,
            ClubService clubService, IConfiguration configuration, ILogger<MatchAccessService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Brytaren <c>Security:Hub:Enforce</c> (förval: på). Av = bara loggning ("SEC:"), ingen nekas — för att kunna backa
        /// utan ny deploy om något oväntat händer (t.ex. en mobilversion som inte identifierar sig).
        /// </summary>
        public bool Enforce => _configuration.GetValue("Security:Hub:Enforce", true);

        /// <summary>Vem är anroparen? Null = ingen inloggad medlem och ingen giltig gäst för den här matchen.</summary>
        public async Task<HubCaller?> ResolveAsync(HubCallerContext context, string matchCode)
        {
            var http = context.GetHttpContext();

            // 1. Mobilens JWT (access_token i adressen på /hubs, se Program.cs) eller kontots claim.
            int? memberId = null;
            var claim = context.User?.FindFirst("memberId")?.Value;
            if (int.TryParse(claim, out var fromClaim)) memberId = fromClaim;
            if (memberId == null && http != null)
            {
                try
                {
                    var auth = await http.AuthenticateAsync("JwtBearer");
                    if (auth.Succeeded && int.TryParse(auth.Principal?.FindFirst("memberId")?.Value, out var fromJwt)) memberId = fromJwt;
                }
                catch (InvalidOperationException) { /* schemat är inte registrerat — ingen JWT */ }
            }

            // 2. Webbens kaka.
            if (memberId == null)
            {
                try
                {
                    var current = await _memberManager.GetCurrentMemberAsync();
                    if (current != null) memberId = _memberService.GetByEmail(current.Email ?? "")?.Id;
                }
                catch { /* ingen kaka */ }
            }

            if (memberId.HasValue)
            {
                var member = _memberService.GetById(memberId.Value);
                if (member != null && member.IsApproved && !member.IsLockedOut)
                {
                    var clubId = member.GetValue<int>("primaryClubId");
                    return new HubCaller(
                        member.Id, null,
                        SpectatorRules.CleanName(member.Name),
                        SpectatorRules.CleanPictureUrl(member.GetValue<string>("profilePictureUrl")),
                        SpectatorRules.CleanClub(clubId > 0 ? _clubService.GetClubNameById(clubId) : null));
                }
            }

            // 3. Gästkakan för just den här matchen.
            if (http != null && !string.IsNullOrWhiteSpace(matchCode))
            {
                using var scope = _scopeProvider.CreateScope();
                var guest = await GuestMatchController.ValidateGuestSession(matchCode, http.Request, scope.Database);
                scope.Complete();
                if (guest != null)
                    return new HubCaller(null, guest.GuestId, SpectatorRules.CleanName(guest.DisplayName), "", "");
            }

            return null;
        }

        /// <summary>Är medlemmen matchens skapare (och matchen aktiv)? Bara skaparen lyssnar på arrangörens förfrågningar.</summary>
        public bool IsCreator(string matchCode, int memberId)
        {
            using var scope = _scopeProvider.CreateScope();
            var count = scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM TrainingMatches WHERE MatchCode = @0 AND CreatedByMemberId = @1",
                (matchCode ?? "").ToUpperInvariant(), memberId);
            scope.Complete();
            return count > 0;
        }

        /// <summary>Loggar en nekad åtgärd (alltid) och säger om den ska nekas på riktigt.</summary>
        public bool ShouldDeny(string what, string matchCode)
        {
            _logger.LogWarning("SEC: Hubben nekade {What} för match {Code} (enforce={Enforce})", what, matchCode, Enforce);
            return Enforce;
        }
    }

    public class HubSecurityComposer : IComposer
    {
        // Scoped: använder IMemberManager (scoped i Umbraco). Hubben skapas per anrop, så den får sin egen instans.
        public void Compose(IUmbracoBuilder builder) => builder.Services.AddScoped<MatchAccessService>();
    }
}
