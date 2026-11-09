namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Sidor under sidor (klubbsidorna): reglerna som ren kod, utan databas, så att varje regel går att prova. Två nivåer: en egen sida kan ligga under en annan egen sida som själv
    /// ligger på egen nivå. Bara egna (fria) sidor, hos samma förening. En undersida är aldrig öppnare än sin förälder (samma regel som för block: sidan är det strängaste av
    /// de två). Adresserna är platta och rörs aldrig av trädet.
    /// </summary>
    public static class SitePageTree
    {
        /// <summary>Högst så många undersidor under en sida (hela föreningen har dessutom taket <see cref="SitePageStore.MaxFreePagesPerOwner"/>).</summary>
        public const int MaxChildrenPerParent = 20;

        /// <summary>
        /// Får <paramref name="page"/> läggas under <paramref name="parent"/>? Null = ja, annars förklaringen på svenska. <paramref name="parentChildCount"/> = antal undersidor föräldern
        /// redan har (utan <paramref name="page"/> själv); <paramref name="pageHasChildren"/> = sidan har egna undersidor (då kan den inte själv bli en undersida: tre nivåer finns inte).
        /// </summary>
        public static string? ParentError(SitePageRow page, SitePageRow? parent, int parentChildCount, bool pageHasChildren)
        {
            if (!page.IsFree) return "Bara egna sidor kan ligga under en annan sida.";
            if (parent == null || parent.DeletedDate != null) return "Sidan du valde finns inte.";
            if (parent.OwnerType != page.OwnerType || parent.OwnerId != page.OwnerId) return "Sidan du valde finns inte hos den här föreningen.";
            if (parent.Id == page.Id) return "En sida kan inte ligga under sig själv.";
            if (!parent.IsFree) return "Bara en egen sida kan ha undersidor.";
            if (parent.ParentPageId != null) return "Det går bara att ha två nivåer: sidan du valde ligger redan under en annan sida.";
            if (pageHasChildren) return "Sidan har egna undersidor och kan inte själv bli en undersida. Flytta undersidorna först.";
            if (parentChildCount >= MaxChildrenPerParent && page.ParentPageId != parent.Id) return $"En sida kan ha högst {MaxChildrenPerParent} undersidor.";
            return null;
        }

        /// <summary>Samma prövning för en sida som ännu inte finns (skapas som undersida): bara föräldern prövas.</summary>
        public static string? NewChildError(string ownerType, int ownerId, SitePageRow? parent, int parentChildCount)
        {
            if (parent == null || parent.DeletedDate != null || parent.OwnerType != ownerType || parent.OwnerId != ownerId) return "Sidan du valde finns inte.";
            if (!parent.IsFree) return "Bara en egen sida kan ha undersidor.";
            if (parent.ParentPageId != null) return "Det går bara att ha två nivåer: sidan du valde ligger redan under en annan sida.";
            if (parentChildCount >= MaxChildrenPerParent) return $"En sida kan ha högst {MaxChildrenPerParent} undersidor.";
            return null;
        }

        /// <summary>Nivåerna räknas från öppen (0) till dold (4): en undersida får aldrig vara öppnare (lägre tal) än sin förälder.</summary>
        public static bool ChildLevelOk(SiteVisibility child, SiteVisibility parent) => child >= parent;

        /// <summary>
        /// Trädordningen för lista och meny: egna sidor på första nivån i sin ordning (SortOrder, sedan titel, sedan id), var och en följd av sina undersidor i deras ordning.
        /// En undersida vars förälder inte finns i listan (till exempel raderad) räknas som egen nivå, så ingen sida försvinner ur listan.
        /// </summary>
        public static IReadOnlyList<(T Item, int Depth)> Flatten<T>(IEnumerable<T> items, Func<T, int> id, Func<T, int?> parent, Func<T, int> sort, Func<T, string> title)
        {
            var all = items.ToList();
            var ids = all.Select(id).ToHashSet();
            // Bara en sida på egen nivå kan vara förälder. Skulle det finnas en tredje nivå (det ska aldrig hända) räknas den sidan som egen nivå i stället för att försvinna.
            var validParents = all.Where(x => parent(x) is not int p || !ids.Contains(p) || p == id(x)).Select(id).ToHashSet();
            IEnumerable<T> Ordered(IEnumerable<T> xs) => xs.OrderBy(sort).ThenBy(title, StringComparer.CurrentCultureIgnoreCase).ThenBy(id);
            var top = Ordered(all.Where(x => parent(x) is not int p || !validParents.Contains(p) || p == id(x)));
            var result = new List<(T, int)>();
            foreach (var t in top)
            {
                result.Add((t, 0));
                if (!validParents.Contains(id(t))) continue;
                foreach (var c in Ordered(all.Where(x => parent(x) == id(t) && id(x) != id(t)))) result.Add((c, 1));
            }
            // Skyddsnät: ingen sida får försvinna ur listan, hur trasig strukturen än är.
            var emitted = result.Select(r => id(r.Item1)).ToHashSet();
            foreach (var x in Ordered(all.Where(x => !emitted.Contains(id(x))))) result.Add((x, 0));
            return result;
        }
    }
}
