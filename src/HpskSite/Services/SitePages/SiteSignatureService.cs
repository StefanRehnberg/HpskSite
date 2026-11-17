using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Slår upp en signatur (<see cref="SiteSignatures"/>) för en medlem hos en ägare (klubb eller krets). EN plats för regeln om vem som
    /// visas, så att sidan, nyheten, förhandsvisningen och redigeraren ger samma svar.
    ///
    /// <para><b>Bara den som hör till föreningen visas</b>: aktiv medlem i klubben (för en krets: i någon av kretsens klubbar) eller
    /// innehavare av en aktiv roll hos ägaren. En medlem som lämnat, som aldrig godkänts eller som inte finns ger null, och signaturen
    /// försvinner ur texten utan spår.</para>
    ///
    /// <para><b>Rollen</b> är medlemmens aktiva uppdrag hos just den här ägaren (<c>BoardRoles</c>; ett uppdrag vars mandat gått ut räknas inte),
    /// i styrelsens ordning, åtskilda med kommatecken. Har hen ingen roll visas bara namnet. En roll hos en annan förening visas aldrig.</para>
    /// </summary>
    public sealed class SiteSignatureService
    {
        private readonly IMemberService _members;
        private readonly IScopeProvider _scopes;
        private readonly ClubRosterService _roster;

        public SiteSignatureService(IMemberService members, IScopeProvider scopes, ClubRosterService roster)
        { _members = members; _scopes = scopes; _roster = roster; }

        /// <summary>Signaturen för <paramref name="memberId"/> hos ägaren, eller null när medlemmen inte får visas.</summary>
        public SiteSignature? Find(int memberId, string ownerType, int ownerId)
        {
            if (memberId <= 0 || ownerId <= 0) return null;
            var member = _members.GetById(memberId);
            if (member == null || !member.IsApproved) return null;

            var roles = Roles(memberId, ownerType, ownerId);
            if (roles.Count == 0 && !Belongs(memberId, ownerType, ownerId)) return null;

            var first = member.GetValue<string>("firstName") ?? "";
            var last = member.GetValue<string>("lastName") ?? "";
            var name = $"{first} {last}".Trim();
            if (name.Length == 0) name = member.Name ?? "";
            if (name.Length == 0) return null;

            var picture = member.GetValue<string>("profilePictureUrl");
            return new SiteSignature(memberId, name, SiteSignatures.IsOwnPicture(picture) ? picture : null,
                roles.Count == 0 ? null : string.Join(", ", roles));
        }

        /// <summary>Hör medlemmen till ägaren (aktiv i klubben, respektive i någon av kretsens klubbar)?</summary>
        public bool Belongs(int memberId, string ownerType, int ownerId) =>
            ownerType == SiteTemplates.Region ? _roster.IsActiveInRegion(memberId, ownerId) : _roster.IsActiveInClub(memberId, ownerId);

        /// <summary>Medlemmens rolltitlar hos ägaren, i styrelsens ordning. Tom lista = ingen roll.</summary>
        public List<string> Roles(int memberId, string ownerType, int ownerId)
        {
            var type = ownerType == SiteTemplates.Region ? DocumentOwnerType.Region : DocumentOwnerType.Club;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<BoardRole>(
                "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2 AND IsActive = 1 ORDER BY SortOrder, RoleKey",
                type, ownerId, memberId);
            return rows.Where(r => !r.IsTermExpired).Select(r => r.DisplayTitle).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        }

        /// <summary>
        /// Svaret redigeraren behöver: skribentens egen signatur (<c>me</c>, null med en förklaring när hen inte hör till föreningen) och
        /// förhandsvisningen av de signaturer som redan står i texten (<c>items</c>, bara de som får visas).
        /// </summary>
        public object ForEditor(int? writerId, string ownerType, int ownerId, IEnumerable<int> ids)
        {
            var me = writerId is int w ? Find(w, ownerType, ownerId) : null;
            var items = ids.Where(i => i > 0).Distinct().Take(50).Select(i => Find(i, ownerType, ownerId)).Where(s => s != null).Select(s => Dto(s!)).ToList();
            var where = ownerType == SiteTemplates.Region ? "kretsen" : "klubben";
            return new
            {
                success = true,
                me = me == null ? null : Dto(me),
                reason = me == null ? $"Du är inte medlem i {where} och har ingen roll där, så du kan inte skriva under här." : null,
                items,
            };
        }

        private static object Dto(SiteSignature s) => new { memberId = s.MemberId, name = s.Name, picture = s.PictureUrl, role = s.Role, initials = SiteSignatures.Initials(s.Name) };

        /// <summary>Texten med varje signatur utritad (för sidor och nyheter). Ren uppslagning per medlem, en gång per medlem och text.</summary>
        public string Resolve(string? html, string ownerType, int ownerId) =>
            SiteSignatures.Resolve(html, id => Find(id, ownerType, ownerId));

        /// <summary>Texten med signaturen som en enkel rad (för mejl, där en avatar inte ritas).</summary>
        public string ResolvePlain(string? html, string ownerType, int ownerId) =>
            SiteSignatures.ToPlainLine(html, id => Find(id, ownerType, ownerId));
    }
}
