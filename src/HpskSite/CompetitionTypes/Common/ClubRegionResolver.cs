using HpskSite.Models.Competitions;
using HpskSite.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>Var en klubb och en tävling hör hemma (krets, landsdel). Gränssnitt så att reglerna kan provas utan Umbraco.</summary>
    public interface IClubRegionLookup
    {
        ClubOrigin? OriginOf(int clubId);
        CompetitionOrigin OriginOf(CompetitionInfo competition);
    }

    /// <summary>Medlemmarnas klubbar (alla, huvudklubben först) i EN läsning.</summary>
    public interface IMemberClubsLookup
    {
        IReadOnlyDictionary<int, IReadOnlyList<int>> ClubsOf(IEnumerable<int> memberIds);
    }

    /// <summary>
    /// Klubb → krets → landsdel ur den publicerade innehållscachen (klubbens förälders förälder är kretssidan <c>regionalPage</c>), med
    /// aliaskontroll på varje nod enligt id-reglerna. Minnesmärker per instans (en förfrågan) — klubbarnas kretstillhörighet ändras sällan
    /// och en förfrågan ska aldrig se två olika svar.
    ///
    /// <para>Ger null/tomt när sidan inte kan lösas upp (ingen Umbraco-kontext, klubb utan krets) — och det betyder INGET FÖRSLAG, aldrig
    /// "utanför" (se <see cref="OutOfCompetitionRules"/>).</para>
    /// </summary>
    public class ClubRegionResolver : IClubRegionLookup
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly ClubService _clubService;
        private readonly Dictionary<int, ClubOrigin?> _clubs = new();

        public ClubRegionResolver(IUmbracoContextAccessor ctx, ClubService clubService)
        {
            _ctx = ctx;
            _clubService = clubService;
        }

        public ClubOrigin? OriginOf(int clubId)
        {
            if (clubId <= 0) return null;
            if (_clubs.TryGetValue(clubId, out var cached)) return cached;
            return _clubs[clubId] = Resolve(clubId);
        }

        private ClubOrigin? Resolve(int clubId)
        {
            if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return null;
            var club = uc.Content.GetById(clubId);
            if (club == null || club.ContentType.Alias != "club") return null;
            var region = club.Parent?.Parent;
            if (region == null || region.ContentType.Alias != "regionalPage") return null;
            var code = region.Value<string>("regionCode") ?? "";
            if (string.IsNullOrWhiteSpace(code)) return null;
            var name = _clubService.GetClubNameById(clubId);
            return new ClubOrigin(clubId, string.IsNullOrWhiteSpace(name) ? club.Name : name!, code, region.Name, region.Value<string>("area"));
        }

        public CompetitionOrigin OriginOf(CompetitionInfo competition)
        {
            // Värdklubbens krets; en tävling utan klubb (kretsvärd, SM) har kretsens kod i RegionalFederation.
            if (competition.ClubId > 0)
            {
                var host = OriginOf(competition.ClubId);
                return host == null
                    ? new CompetitionOrigin(competition.ClubId, null, null, null)
                    : new CompetitionOrigin(competition.ClubId, host.RegionCode, host.RegionName, host.Area);
            }

            var wanted = competition.RegionalFederation;
            if (string.IsNullOrWhiteSpace(wanted) || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null)
                return new CompetitionOrigin(0, null, null, null);
            var region = uc.Content.GetAtRoot().FirstOrDefault()?.Children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage"
                && string.Equals(c.Value<string>("regionCode") ?? "", wanted, StringComparison.OrdinalIgnoreCase));
            return region == null
                ? new CompetitionOrigin(0, null, null, null)
                : new CompetitionOrigin(0, region.Value<string>("regionCode"), region.Name, region.Value<string>("area"));
        }
    }

    /// <summary>Medlemmarnas klubbar via <see cref="MemberPropertyReader"/> (en SQL-läsning för alla).</summary>
    public class MemberClubsLookup : IMemberClubsLookup
    {
        private readonly MemberPropertyReader _reader;
        public MemberClubsLookup(MemberPropertyReader reader) => _reader = reader;

        public IReadOnlyDictionary<int, IReadOnlyList<int>> ClubsOf(IEnumerable<int> memberIds)
        {
            var ids = memberIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, IReadOnlyList<int>>();
            // ⚠️ Aliaset heter memberClubIDs i databasen (CLAUDE.md, skalreglerna).
            return _reader.Read(new[] { "primaryClubId", "memberClubIDs" }, ids)
                .ToDictionary(r => r.Id, r => (IReadOnlyList<int>)r.AllClubIds);
        }
    }
}
