using HpskSite.Models.Events;
using HpskSite.Services.SitePages;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// <b>Vem som läser händelser</b> (klubbsidorna 2c). Varje läsning ur <see cref="IClubEventStore"/> kräver en <see cref="EventViewer"/>:
    /// en anropare som glömmer synligheten kompilerar inte. Synligheten är <see cref="ClubEventRecord.ReadVisibility"/> (samma modell som
    /// sidorna, <see cref="SiteAccess.CanSee"/>) och prövas på ETT ställe, i <see cref="SwitchingClubEventStore"/>.
    ///
    /// <para>Fyra sorter: <see cref="Anonymous"/>, en riktig tittare per begäran (<see cref="EventViewerFactory"/>), en fast tittare för en ägare
    /// (<see cref="ForOwner"/>, när en sida renderas för en känd <see cref="SiteViewer"/>) och <see cref="System"/> — den som inte är en besökare:
    /// adminytor som redan prövat behörighet, bakgrundsjobb, flytt, statistik. <b>Varje användning av <see cref="System"/> måste stå i listan i
    /// <c>EventViewerSystemUseTests</c></b>; en ny användning kräver ett medvetet val där.</para>
    /// </summary>
    public sealed class EventViewer
    {
        private readonly Func<string, int, SiteViewer>? _forOwner;

        private EventViewer(bool isSystem, Func<string, int, SiteViewer>? forOwner)
        {
            IsSystem = isSystem;
            _forOwner = forOwner;
        }

        /// <summary>Ingen besökare: ser allt. Bara för kod som redan prövat behörighet på annat sätt (se klassens text).</summary>
        public static readonly EventViewer System = new(true, null);

        /// <summary>Den som inte är inloggad: ser bara händelser med synligheten Alla.</summary>
        public static readonly EventViewer Anonymous = new(false, (_, _) => SiteViewer.Anonymous);

        /// <summary>Samma tittare för varje händelse, avsedd för EN ägares händelser (en renderad sida).</summary>
        public static EventViewer ForOwner(SiteViewer viewer) => new(false, (_, _) => viewer);

        /// <summary>En tittare som räknas fram per ägare (en inloggad medlem är medlem i vissa klubbar och inte andra).</summary>
        public static EventViewer Of(Func<string, int, SiteViewer> perOwner) => new(false, perOwner);

        public bool IsSystem { get; }

        public SiteViewer For(string ownerType, int ownerId) =>
            _forOwner == null ? SiteViewer.Anonymous : _forOwner(ownerType, ownerId);

        /// <summary>Får tittaren se händelsen? System ser allt; annars avgör händelsens egen synlighet (okänt värde = aldrig).</summary>
        public bool CanSee(ClubEventRecord e) =>
            IsSystem || SiteAccess.CanSee(e.ReadVisibility, For(e.OwnerType, e.OwnerId));
    }

    /// <summary>
    /// Bygger <see cref="EventViewer"/> för den inloggade i den här begäran. Medlemskap, styrelse och admin räknas per ägare och sparas under
    /// begäran. Adminrättigheten speglar <c>IsClubAdminForClub</c> (klubbens admin, kretsens admin i klubbens krets, sajtadmin) och
    /// <c>IsRegionalAdminForRegion</c>; den är avgjord FÖRE läsningen (läsningen är synkron). Misslyckas något svarar fabriken med den
    /// snävaste tittaren, aldrig den bredaste.
    /// </summary>
    public sealed class EventViewerFactory
    {
        private readonly Umbraco.Cms.Core.Security.IMemberManager _members;
        private readonly Umbraco.Cms.Core.Services.IMemberService _memberService;
        private readonly Umbraco.Cms.Core.Services.IContentService _content;
        private readonly AdminAuthorizationService _auth;
        private readonly ClubRosterService _roster;
        private readonly BoardRoleService _board;
        private readonly ILogger<EventViewerFactory> _logger;
        private EventViewer? _cached;

        public EventViewerFactory(Umbraco.Cms.Core.Security.IMemberManager members, Umbraco.Cms.Core.Services.IMemberService memberService,
            Umbraco.Cms.Core.Services.IContentService content, AdminAuthorizationService auth, ClubRosterService roster, BoardRoleService board,
            ILogger<EventViewerFactory> logger)
        {
            _members = members;
            _memberService = memberService;
            _content = content;
            _auth = auth;
            _roster = roster;
            _board = board;
            _logger = logger;
        }

        public async Task<EventViewer> CurrentAsync()
        {
            if (_cached != null) return _cached;
            try
            {
                var current = await _members.GetCurrentMemberAsync();
                if (current == null) return _cached = EventViewer.Anonymous;
                var member = _memberService.GetByEmail(current.Email ?? string.Empty);
                var siteAdmin = await _auth.IsCurrentUserAdminAsync();
                var roles = member == null ? Array.Empty<string>() : _memberService.GetAllRoles(member.Id).ToArray();
                var memberId = member?.Id ?? 0;
                var perOwner = new Dictionary<(string, int), SiteViewer>();
                return _cached = EventViewer.Of((ownerType, ownerId) =>
                {
                    if (perOwner.TryGetValue((ownerType, ownerId), out var known)) return known;
                    SiteViewer v;
                    try
                    {
                        if (ownerType == ClubEventRecord.OwnerClub)
                        {
                            var region = _auth.GetClubRegionCode(ownerId);
                            var admin = siteAdmin || roles.Contains($"ClubAdmin_{ownerId}") || (!string.IsNullOrEmpty(region) && roles.Contains($"RegionalAdmin_{region}"));
                            var isMember = memberId > 0 && _roster.ActiveMemberIds(ownerId).Contains(memberId);
                            var isBoard = memberId > 0 && _board.IsBoardMemberOf(HpskSite.Models.DocumentOwnerType.Club, ownerId, memberId);
                            v = new SiteViewer(true, isMember, isBoard, admin);
                        }
                        else
                        {
                            var code = _content.GetById(ownerId)?.GetValue<string>("regionCode");
                            var admin = siteAdmin || (!string.IsNullOrEmpty(code) && roles.Contains($"RegionalAdmin_{code}"));
                            var isBoard = memberId > 0 && _board.IsBoardMemberOf(HpskSite.Models.DocumentOwnerType.Region, ownerId, memberId);
                            var isRegionMember = memberId > 0 && _roster.IsActiveInRegion(memberId, ownerId);   // kretsmedlem (2d): samma regel som sidmotorn
                            v = new SiteViewer(true, isRegionMember, isBoard, admin);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Tittaren för {OwnerType} {OwnerId} kunde inte räknas fram; inloggad utan fler rättigheter.", ownerType, ownerId);
                        v = new SiteViewer(true, false, false, false);
                    }
                    perOwner[(ownerType, ownerId)] = v;
                    return v;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tittaren kunde inte räknas fram; behandlas som utloggad.");
                return EventViewer.Anonymous;
            }
        }
    }
}
