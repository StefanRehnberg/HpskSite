using HpskSite.Models.Registrations;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>En skyttes plats i en patrull, så som den ligger i databasen. DNS-platser räknas inte (de har ingen plats).</summary>
    public sealed record PairPlacement(int MemberId, string ShootingClass, int PatrolId, int PatrolNumber, bool IsDns);

    /// <summary>En flytt eller ny placering som är på väg att göras: skytten och klassen, och patrullen de hamnar i.</summary>
    public sealed record PairMove(int MemberId, string ShootingClass, int TargetPatrolId, int TargetPatrolNumber);

    /// <summary>
    /// "Måste gå i samma patrull som" när patrullerna ändras FÖR HAND (flytta, lägg till, efteranmälan, rullande start). Generatorn håller kravet själv
    /// (<c>FaltPatrolEngine</c>); den här klassen gör att de manuella vägarna inte tyst bryter det. Ren funktion, ingen databas, enhetstestbar.
    /// <para>Kravet gäller vapengruppen (alla C är samma grupp) och pekar åt ett håll: den som följer (<c>JoinMemberId</c>) ska ligga i samma patrull som kamraten.
    /// Går kamraten bort (avanmäld, DNS, byter klass) finns inget krav kvar, så de räknas inte.</para>
    /// </summary>
    public static class PatrolPairRules
    {
        static string GroupOf(string? cls) => RegistrationJoinRule.GroupOf(cls);

        /// <summary>Kamraten (medlems-id) som skytten ska gå med i vapengruppen, eller null.</summary>
        public static int? PartnerOf(IEnumerable<CompetitionRegistration> regs, int memberId, string shootingClass)
        {
            var g = GroupOf(shootingClass);
            return regs.Where(r => r.IsActive && r.MemberId == memberId && GroupOf(r.MemberClass) == g && r.JoinMemberId is > 0)
                .Select(r => r.JoinMemberId).FirstOrDefault();
        }

        /// <summary>
        /// Patrullen där skyttens kamrat ligger i samma vapengrupp, om kamraten är placerad och inte DNS. Null om skytten inte har någon kamrat, kamraten inte är placerad,
        /// eller kamraten inte längre har en aktiv anmälan i gruppen (då finns inget krav).
        /// </summary>
        public static PairPlacement? PartnerPlacement(IReadOnlyList<CompetitionRegistration> regs, IReadOnlyList<PairPlacement> placements, int memberId, string shootingClass)
        {
            var partner = PartnerOf(regs, memberId, shootingClass);
            if (partner is not { } pid) return null;
            var g = GroupOf(shootingClass);
            if (!regs.Any(r => r.IsActive && r.MemberId == pid && GroupOf(r.MemberClass) == g)) return null;
            return placements.FirstOrDefault(p => p.MemberId == pid && !p.IsDns && GroupOf(p.ShootingClass) == g);
        }

        /// <summary>
        /// Beskedet (svenska, med namn) när <paramref name="moves"/> skulle lägga ett par i olika patruller, eller null. Med i beskedet kommer bara par där flytten
        /// själv orsakar att de hamnar isär: de låg ihop förut, eller så är någon av dem ny i patrullen. Ett par som redan låg isär nämns inte.
        /// </summary>
        public static string? SeparationMessage(IReadOnlyList<CompetitionRegistration> regs, IReadOnlyList<PairPlacement> before, IReadOnlyList<PairMove> moves)
        {
            if (moves.Count == 0) return null;
            var beforeMap = new Dictionary<(int, string), PairPlacement>();
            foreach (var p in before.Where(p => !p.IsDns)) beforeMap[(p.MemberId, GroupOf(p.ShootingClass))] = p;
            var after = new Dictionary<(int, string), PairPlacement>(beforeMap);
            var moved = new HashSet<(int, string)>();
            foreach (var m in moves)
            {
                var key = (m.MemberId, GroupOf(m.ShootingClass));
                moved.Add(key);
                after[key] = new PairPlacement(m.MemberId, m.ShootingClass, m.TargetPatrolId, m.TargetPatrolNumber, false);
            }
            string NameOf(int id) => regs.FirstOrDefault(r => r.MemberId == id)?.MemberName is { Length: > 0 } n ? n : $"Medlem {id}";
            var lines = new List<string>();
            foreach (var r in regs.Where(r => r.IsActive && r.JoinMemberId is > 0).OrderBy(r => r.MemberId))
            {
                var g = GroupOf(r.MemberClass); var follower = (r.MemberId, g); var partner = (r.JoinMemberId!.Value, g);
                if (!moved.Contains(follower) && !moved.Contains(partner)) continue;
                if (!after.TryGetValue(follower, out var a) || !after.TryGetValue(partner, out var b)) continue;
                if (a.PatrolId == b.PatrolId) continue;
                var togetherBefore = beforeMap.TryGetValue(follower, out var fb) && beforeMap.TryGetValue(partner, out var pb) && fb.PatrolId == pb.PatrolId;
                var newlyPlaced = !beforeMap.ContainsKey(follower) || !beforeMap.ContainsKey(partner);
                if (!togetherBefore && !newlyPlaced) continue;
                lines.Add($"{NameOf(r.MemberId)} ska gå i samma patrull som {NameOf(r.JoinMemberId!.Value)} i klass {g}, men hamnar i patrull {a.PatrolNumber} medan {NameOf(r.JoinMemberId!.Value)} ligger i patrull {b.PatrolNumber}.");
            }
            return lines.Count == 0 ? null : string.Join("\n", lines.Distinct());
        }
    }
}
