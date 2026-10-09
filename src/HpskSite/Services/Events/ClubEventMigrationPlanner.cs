using HpskSite.Models.Events;

namespace HpskSite.Services.Events
{
    public sealed class PlannedClubEvent
    {
        public ClubEventRecord Row { get; init; } = new();
        public int UrlYear { get; init; }
        public string Slug { get; init; } = "";
        /// <summary>Den gamla trädadressens sista del, eller tom när den inte gick att avgöra.</summary>
        public string LegacySlug { get; init; } = "";
    }

    public sealed class ClubEventMigrationPlan
    {
        public List<PlannedClubEvent> Rows { get; } = new();
        public int Nodes { get; set; }
        public int SkippedMovedToTraining { get; set; }
        public int SlugCollisions { get; set; }
        public int Unpublished => Rows.Count(r => !r.Row.IsPublished);
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// <b>Reglerna för flytten av händelserna</b> (sajtstrukturen fas 2d) — en ren funktion, så att
    /// torrkörningen och den skarpa körningen inte kan skilja sig och reglerna går att pröva utan databas.
    ///
    /// <list type="number">
    /// <item><b>Varje nod behåller sitt id</b> — deltagarrader, lånebokningar, liggarrader och redan
    /// utskrivna QR-affischer pekar på det.</item>
    /// <item><b>Händelser som redan flyttats till en träning hoppas över</b> (<c>ClubTraining.LegacyEventNodeId</c>).</item>
    /// <item><b>Årtalet</b> är startdatumets, annars skapandeårets. <b>Sluggen</b> görs av sidnamnet med
    /// samma regel som Umbracos segment; en krock inom samma ägare och år ger <c>-2</c>, <c>-3</c> …,
    /// i id-ordning så att resultatet är stabilt.</item>
    /// <item><b>Den gamla trädadressens</b> sista del sparas, så att gamla länkar 301:as.</item>
    /// <item><b>Opublicerade händelser flyttas också</b>, opublicerade.</item>
    /// </list>
    /// </summary>
    public static class ClubEventMigrationPlanner
    {
        public static ClubEventMigrationPlan Plan(IEnumerable<ClubEventRecord> nodes, ISet<int> movedToTraining, Func<string, string> slugify)
        {
            var plan = new ClubEventMigrationPlan();
            var taken = new HashSet<(int Owner, int Year, string Slug)>();
            foreach (var n in nodes.Where(n => n != null && n.Id > 0 && n.OwnerId > 0).OrderBy(n => n.Id))
            {
                plan.Nodes++;
                if (movedToTraining.Contains(n.Id))
                {
                    plan.SkippedMovedToTraining++;
                    continue;
                }

                var r = n.Clone();
                var year = r.StartsAt?.Year ?? (r.CreatedDate == default ? DateTime.Now : r.CreatedDate).Year;
                var source = string.IsNullOrWhiteSpace(r.PageName) ? r.Name : r.PageName;
                var baseSlug = slugify(source ?? "");
                if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "handelse";
                var slug = baseSlug;
                if (taken.Contains((r.OwnerId, year, slug)))
                {
                    plan.SlugCollisions++;
                    for (var i = 2; ; i++)
                        if (!taken.Contains((r.OwnerId, year, $"{baseSlug}-{i}"))) { slug = $"{baseSlug}-{i}"; break; }
                    plan.Notes.Add($"Händelse {r.Id} ({r.DisplayName}): adressen {year}/{baseSlug} var tagen — blir {year}/{slug}.");
                }
                taken.Add((r.OwnerId, year, slug));

                plan.Rows.Add(new PlannedClubEvent
                {
                    Row = r,
                    UrlYear = year,
                    Slug = slug,
                    LegacySlug = LastSegment(r.Url)
                });
            }
            return plan;
        }

        /// <summary>"/halland/klubbar/x/gasaskjutning/" → "gasaskjutning".</summary>
        public static string LastSegment(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var path = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var abs)) path = abs.AbsolutePath;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "" : parts[^1];
        }
    }
}
