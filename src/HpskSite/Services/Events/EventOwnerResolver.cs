using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// Vem som äger en händelse: en klubb eller en krets.
    /// </summary>
    /// <param name="IsRegion">Ägaren är en krets (<c>regionalPage</c>), annars en klubb.</param>
    /// <param name="OwnerId">Ägarens nod-id. Klubbar och kretsar stannar i Umbraco.</param>
    /// <param name="Name">Ägarnodens namn.</param>
    /// <param name="RegionCode">
    /// Kretsens kod: för en klubb <c>regionalFederation</c>, för en krets <c>regionCode</c>.
    /// <b>Råvärdet</b>, utan att falla tillbaka på namnet — behörigheten jämför koden exakt med
    /// gruppen <c>RegionalAdmin_{kod}</c>, och ett namn där hade bara gett ett tyst nej.
    /// </param>
    /// <param name="SwishNumber">Ägarens Swish-nummer, som händelsen ärver när den saknar eget.</param>
    public sealed record EventOwner(bool IsRegion, int OwnerId, string Name, string RegionCode, string SwishNumber)
    {
        public bool IsClub => !IsRegion;
    }

    /// <summary>
    /// <b>ENDA stället som svarar på "vem äger händelsen?"</b> (sajtstrukturen, fas 0 —
    /// <c>notes/sajtstruktur-plan-2026-10-09.md</c>).
    ///
    /// <para>Händelsen kommer ur <see cref="IClubEventStore"/> (fas 2a), och posten bär ägaren som
    /// (typ, id) — oavsett om händelsen är en nod eller en tabellrad. Läs aldrig <c>ParentId</c>/
    /// <c>.Parent</c> på en händelse för att ta reda på ägaren; gå hit, eller till postens
    /// <c>OwnerId</c>.</para>
    /// </summary>
    public class EventOwnerResolver
    {
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;
        private readonly IClubEventStore _events;

        public EventOwnerResolver(IContentService contentService, AdminAuthorizationService auth, IClubEventStore events)
        {
            _contentService = contentService;
            _auth = auth;
            _events = events;
        }

        /// <summary>Ägaren till händelsen med det id:t, eller null om den inte går att avgöra.
        /// Händelsen läses via <see cref="IClubEventStore"/> (sajtstrukturen fas 2a): posten bär ägaren.</summary>
        public EventOwner? ForEvent(int eventId)
        {
            var e = eventId > 0 ? _events.Get(eventId) : null;
            return e is null ? null : ForOwnerId(e.OwnerId);
        }

        /// <summary>Ägaren till en redan inläst händelse.</summary>
        public EventOwner? ForEventRecord(HpskSite.Models.Events.ClubEventRecord e) => ForOwnerId(e.OwnerId);

        /// <summary>
        /// En ägarnod (klubb eller krets) som <see cref="EventOwner"/>, eller null om noden är
        /// något annat. Används också när en NY händelse skapas under en given förälder.
        /// </summary>
        public static EventOwner? FromOwnerNode(IContent owner)
        {
            var alias = owner.ContentType.Alias;
            if (alias == ClubEvents.OwnerClubAlias)
                return new EventOwner(false, owner.Id, owner.Name ?? "",
                    owner.GetValue<string>("regionalFederation") ?? "",
                    (owner.GetValue<string>(ClubEvents.SwishProperty) ?? "").Trim());
            if (alias == ClubEvents.OwnerRegionAlias)
                return new EventOwner(true, owner.Id, owner.Name ?? "",
                    owner.GetValue<string>("regionCode") ?? "",
                    (owner.GetValue<string>(ClubEvents.SwishProperty) ?? "").Trim());
            return null;
        }

        /// <summary>Ägarnoden (klubb eller krets) som id, eller null.</summary>
        public EventOwner? ForOwnerId(int ownerId)
        {
            if (ownerId <= 0) return null;
            var node = _contentService.GetById(ownerId);
            return node is null ? null : FromOwnerNode(node);
        }

        /// <summary>
        /// Får den inloggade administrera händelser som ägs av <paramref name="owner"/>?
        /// Krets: kretsens administratörer. Klubb: klubbadmin, som också viker in klubbens
        /// kretsadministratörer och sajtadmin (se <c>IsClubAdminForClub</c>).
        /// </summary>
        public async Task<bool> CanManageAsync(EventOwner? owner)
        {
            if (owner is null) return false;
            if (owner.IsRegion)
                return !string.IsNullOrEmpty(owner.RegionCode)
                    && await _auth.IsRegionalAdminForRegion(owner.RegionCode);
            return await _auth.IsClubAdminForClub(owner.OwnerId);
        }
    }
}
