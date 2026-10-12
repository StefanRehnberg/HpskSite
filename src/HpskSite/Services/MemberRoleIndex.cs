using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Alla medlemmars medlemsgrupper i EN fråga (skalplanen B2, 2026-10-12).
    ///
    /// <para><b>⚠️ Använd den här i listor, rapporter och statistik — aldrig
    /// <c>_memberService.GetAllRoles(m.Id)</c> i en loop.</b> Det är en fråga per medlem: vid 20 000
    /// medlemmar 20 000 frågor för en enda lista. För EN medlem (behörighetskontroller) duger
    /// <c>GetAllRoles</c> fortfarande.</para>
    ///
    /// <para>Läser Umbracos egen kopplingstabell (<c>cmsMember2MemberGroup</c>) med en egen anslutning,
    /// utan cache — en rolltilldelning syns direkt. En medlem utan grupper saknas i ordboken;
    /// <see cref="Of"/> ger då en tom lista.</para>
    /// </summary>
    public sealed class MemberRoleIndex
    {
        private readonly Dictionary<int, string[]> _roles;

        private MemberRoleIndex(Dictionary<int, string[]> roles) => _roles = roles;

        public static MemberRoleIndex Load(IUmbracoDatabaseFactory databaseFactory)
        {
            using var db = databaseFactory.CreateDatabase();
            var rows = db.Fetch<Row>(
                @"SELECT m2g.Member AS MemberId, n.text AS GroupName
                    FROM cmsMember2MemberGroup m2g
                    JOIN umbracoNode n ON n.id = m2g.MemberGroup");
            return new MemberRoleIndex(rows
                .GroupBy(r => r.MemberId)
                .ToDictionary(g => g.Key, g => g.Select(r => r.GroupName).ToArray()));
        }

        public string[] Of(int memberId) => _roles.TryGetValue(memberId, out var r) ? r : Array.Empty<string>();

        public bool Has(int memberId, string group)
            => Of(memberId).Contains(group, StringComparer.OrdinalIgnoreCase);

        private sealed class Row
        {
            public int MemberId { get; set; }
            public string GroupName { get; set; } = "";
        }
    }
}
