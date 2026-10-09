using HpskSite.Models;
using HpskSite.Models.Registrations;

namespace HpskSite.Services.Registrations
{
    /// <summary>En rad som ska skrivas, och de äldre noder som slogs ihop in i den.</summary>
    public sealed class PlannedRegistrationRow
    {
        public RegistrationRecord Row { get; init; } = new();
        public List<int> MergedNodeIds { get; init; } = new();
        /// <summary>Raden var aktiv men gjordes inaktiv: en annan aktiv anmälan för samma skytt vann.</summary>
        public bool Deactivated { get; init; }
    }

    public sealed class RegistrationMigrationPlan
    {
        public List<PlannedRegistrationRow> Rows { get; } = new();
        public int NodeCount { get; set; }
        public int MergedNodes => Rows.Sum(r => r.MergedNodeIds.Count);
        public int DeactivatedRows => Rows.Count(r => r.Deactivated);
        public int ClubFromLegacyText { get; set; }
        public HashSet<string> UnknownClasses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// <b>Reglerna för flytten av anmälningarna</b> (sajtstrukturen fas 1d) — en ren funktion, så att de
    /// går att pröva utan databas och så att torrkörningen och den skarpa körningen inte kan skilja sig.
    ///
    /// <list type="number">
    /// <item><b>Varje nod behåller sitt id.</b> Gamla fakturor, liggarrader och avgiftsval pekar på det.</item>
    /// <item><b>Äldre tävlingar har EN nod per klass för samma skytt.</b> Noder vars klasser inte redan
    /// finns på vinnaren slås ihop in i den; deras id:n hamnar i <c>CompetitionRegistrationLegacyId</c>.</item>
    /// <item><b>Vinnaren</b> är den nyaste aktiva (anmälningsdatum, sedan högst id). Aktiva noder som bär
    /// en klass vinnaren redan har är äkta dubbletter: de blir egna rader med <c>IsActive = 0</c> — ingenting
    /// raderas, och allt redovisas i rapporten.</item>
    /// <item><b>Inaktiva noder</b> flyttas som de är.</item>
    /// <item><b>En klubb som bara fanns som numerisk text</b> (<c>memberClub</c>) blir <c>ClubId</c>.</item>
    /// <item><b>Klasserna</b> normaliseras till klassens Id; okända strängar behålls och redovisas.</item>
    /// </list>
    /// </summary>
    public static class RegistrationMigrationPlanner
    {
        public static RegistrationMigrationPlan Plan(IEnumerable<RegistrationRecord> nodes)
        {
            var plan = new RegistrationMigrationPlan();
            var all = nodes.Where(n => n != null && n.Id > 0 && n.CompetitionId > 0).ToList();
            plan.NodeCount = all.Count;

            foreach (var group in all.GroupBy(n => (n.CompetitionId, n.MemberId)).OrderBy(g => g.Key.CompetitionId))
            {
                var active = group.Where(n => n.IsActive)
                    .OrderByDescending(n => n.RegistrationDate).ThenByDescending(n => n.Id).ToList();
                var inactive = group.Where(n => !n.IsActive).OrderBy(n => n.Id).ToList();

                // En skytt utan medlem (MemberId 0) kan inte jämföras med någon annan — flytta var och en.
                if (group.Key.MemberId <= 0)
                {
                    foreach (var n in group.OrderBy(n => n.Id)) plan.Rows.Add(new PlannedRegistrationRow { Row = Prepare(n, plan) });
                    continue;
                }

                if (active.Count > 0)
                {
                    var winner = Prepare(active[0], plan);
                    var merged = new List<int>();
                    foreach (var other in active.Skip(1).OrderBy(n => n.Id))
                    {
                        var prepared = Prepare(other, plan);
                        var winnerKeys = new HashSet<string>(winner.Classes.Select(c => c.Class), StringComparer.OrdinalIgnoreCase);
                        var overlaps = prepared.Classes.Count == 0 || prepared.Classes.Any(c => winnerKeys.Contains(c.Class));
                        if (!overlaps)
                        {
                            winner.Classes.AddRange(prepared.Classes);
                            if (winner.ClubId <= 0 && prepared.ClubId > 0) winner.ClubId = prepared.ClubId;
                            if (!winner.InvoiceId.HasValue && prepared.InvoiceId.HasValue) winner.InvoiceId = prepared.InvoiceId;
                            winner.IsSubCompetition |= prepared.IsSubCompetition;
                            winner.IsCheckedIn |= prepared.IsCheckedIn;
                            if (prepared.CreatedDate != default && (winner.CreatedDate == default || prepared.CreatedDate < winner.CreatedDate))
                                winner.CreatedDate = prepared.CreatedDate;
                            merged.Add(other.Id);
                        }
                        else
                        {
                            prepared.IsActive = false;
                            plan.Rows.Add(new PlannedRegistrationRow { Row = prepared, Deactivated = true });
                            plan.Notes.Add($"Tävling {other.CompetitionId}: {other.MemberName} hade två aktiva anmälningar med samma klass — {other.Id} görs inaktiv, {winner.Id} gäller.");
                        }
                    }
                    plan.Rows.Add(new PlannedRegistrationRow { Row = winner, MergedNodeIds = merged });
                }

                foreach (var n in inactive) plan.Rows.Add(new PlannedRegistrationRow { Row = Prepare(n, plan) });
            }

            return plan;
        }

        /// <summary>En kopia klar att skriva: klass-Id, klubb ur numerisk text, tabellformen för klasser.</summary>
        private static RegistrationRecord Prepare(RegistrationRecord node, RegistrationMigrationPlan plan)
        {
            var r = node.Clone();
            r.ClassesFromLegacyScalar = false;
            r.Classes = SqlRegistrationStore.NormalizeClasses(r.Classes);
            foreach (var c in r.Classes)
                if (ShootingClasses.Resolve(c.Class) == null) plan.UnknownClasses.Add(c.Class);

            if (r.ClubId <= 0 && int.TryParse((r.LegacyClubText ?? "").Trim(), out var legacyClub) && legacyClub > 0)
            {
                r.ClubId = legacyClub;
                r.LegacyClubText = "";
                plan.ClubFromLegacyText++;
            }
            if (r.CreatedDate == default) r.CreatedDate = r.RegistrationDate;
            return r;
        }
    }
}
