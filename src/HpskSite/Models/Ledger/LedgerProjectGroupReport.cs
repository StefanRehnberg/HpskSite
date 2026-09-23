namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En grupps utfall, räknat ur medlemsprojektens. <b>Ren funktion</b> — gruppens siffror är
    /// en summering, aldrig ett lagrat värde, och den enda fällan ligger här.
    ///
    /// <para><b>⚠️⚠️ DEN ENDA FÄLLAN ÄR DUBBELRÄKNING.</b> Ett projekt kan ingå i både
    /// "Klubbtävlingar" och "Fältskytte". Läggs de två gruppernas summor ihop får man ett tal som
    /// inte finns. Åtgärden är STRUKTURELL, inte en varningstext: det här lagret producerar
    /// <b>aldrig</b> en totalsumma över grupper, och varje grupp som delar projekt med en annan
    /// säger vilken och hur många — så ingen adderar i huvudet.</para>
    /// </summary>
    public static class LedgerProjectGroupReport
    {
        public static List<ProjectGroupResult> Build(
            IEnumerable<LedgerProjectGroup> groups,
            IEnumerable<LedgerProjectGroupMember> members,
            IEnumerable<ProjectFigures> projects)
        {
            var byProject = projects.ToDictionary(p => p.ProjectId);
            var groupList = groups.ToList();

            // Bara medlemskap vars projekt finns hos föreningen räknas. En rad som pekar på något
            // annat vore en bugg någon annanstans — den får inte bli ett tal här.
            var memberSets = groupList.ToDictionary(
                g => g.Id,
                g => members.Where(m => m.GroupId == g.Id && byProject.ContainsKey(m.ProjectId))
                            .Select(m => m.ProjectId)
                            .ToHashSet());

            var result = new List<ProjectGroupResult>();

            foreach (var g in groupList.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var set = memberSets[g.Id];
                var figures = set.Select(id => byProject[id]).ToList();

                var overlaps = groupList
                    .Where(o => o.Id != g.Id)
                    .Select(o => new ProjectGroupOverlap
                    {
                        GroupId = o.Id,
                        GroupName = o.Name,
                        SharedProjects = memberSets[o.Id].Count(set.Contains)
                    })
                    .Where(o => o.SharedProjects > 0)
                    .OrderBy(o => o.GroupName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                result.Add(new ProjectGroupResult
                {
                    GroupId = g.Id,
                    Name = g.Name,
                    Description = g.Description,
                    IsFromSeries = g.SourceType == LedgerProjectSource.Series,
                    ProjectIds = figures.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                                        .Select(f => f.ProjectId).ToList(),
                    Income = figures.Sum(f => f.Income),
                    Costs = figures.Sum(f => f.Costs),
                    Overlaps = overlaps
                });
            }

            return result;
        }
    }

    /// <summary>Det lilla av ett projekt summeringen behöver.</summary>
    public sealed class ProjectFigures
    {
        public int ProjectId { get; set; }
        public string Name { get; set; } = "";
        public decimal Income { get; set; }
        public decimal Costs { get; set; }
    }

    public sealed class ProjectGroupResult
    {
        public int GroupId { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }

        /// <summary>Gruppen skapades av systemet ur en tävlingsserie.</summary>
        public bool IsFromSeries { get; set; }

        public List<int> ProjectIds { get; set; } = new();

        public decimal Income { get; set; }
        public decimal Costs { get; set; }

        /// <summary>Plus = gruppens projekt gick ihop tillsammans.</summary>
        public decimal Net => Income - Costs;

        /// <summary>
        /// Andra grupper som delar minst ett projekt med den här. Tom = gruppens siffra går att
        /// jämföra med de andra utan att något räknas två gånger.
        /// </summary>
        public List<ProjectGroupOverlap> Overlaps { get; set; } = new();
    }

    public sealed class ProjectGroupOverlap
    {
        public int GroupId { get; set; }
        public string GroupName { get; set; } = "";
        public int SharedProjects { get; set; }
    }
}
