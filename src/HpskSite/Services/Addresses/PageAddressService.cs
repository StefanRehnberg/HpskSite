using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Addresses
{
    /// <summary>Sidornas sorter i <c>PageAddress</c> (id-lagen: ett id följs alltid av sin sort).</summary>
    public static class PageAddressKinds
    {
        public const string Event = "event";
        public const string Competition = "competition"; // competitionstore-ok: adressens sort, inte en doktyp
        public const string Series = "series";
    }

    /// <summary>
    /// Ägaren i en adress. En klubb eller krets bär sitt nod-id; förbundets mästerskap har ingen nod och
    /// bär id 0 med ägartypen som adressens första segment (<c>/sm/</c>, <c>/ssm/</c> …).
    /// </summary>
    public readonly record struct PageAddressOwner(string Type, int Id)
    {
        public const string Club = "Club";
        public const string Region = "Region";

        /// <summary>Förbundets mästerskap: SM och de fyra landsdelarna, i den ordning adresserna skrivs.</summary>
        public static readonly string[] National = { "sm", "ssm", "vsm", "osm", "nsm" };

        public static bool IsNationalType(string? type)
            => type != null && National.Contains(type, StringComparer.OrdinalIgnoreCase);

        public bool IsNational => IsNationalType(Type);

        public static PageAddressOwner ForNational(string segment) => new(segment.ToLowerInvariant(), 0);
    }

    public sealed class PageAddressRow
    {
        public int Id { get; set; }
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public int? UrlYear { get; set; }
        public string Slug { get; set; } = "";
        public string TargetKind { get; set; } = "";
        public int TargetId { get; set; }
        public bool IsCurrent { get; set; }

        public PageAddressOwner Owner => new(OwnerType, OwnerId);
    }

    /// <summary>
    /// C3-planen S6: EN adresstabell för sidorna utanför trädet — händelser, tävlingar och serier,
    /// <c>/{ägarens adress}/{sort}/{år}/{slug}/</c> (sorten: <c>tavlingar</c>, <c>handelser</c>, <c>serier</c>).
    ///
    /// <para><b>Adressen lagras relativt ägaren</b> (ägare, år, slug, sort), aldrig som en hel sökväg. Byter en
    /// klubb adress följer alla dess sidor med av sig själva, och den gamla klubbadressen tas av Umbracos
    /// egen omdirigering.</para>
    ///
    /// <para><b>Ny aktuell adress bara när ägare, år eller slug ändras;</b> den gamla står kvar och 301:as.
    /// <b>Varje sort har sin egen namnrymd</b> (2026-10-18): en tävling och en händelse med samma namn samma år
    /// krockar inte, eftersom sorten står i adressen. En krock inom samma sort ger <c>-2</c>, <c>-3</c> ….
    /// Undantaget är förbundets mästerskap (<c>/sm/2026/x/</c>), som saknar sortsegment — där delar sorterna
    /// namnrymd (<see cref="PageAddressRules.SharesNamespace"/>). Det unika indexet
    /// <c>UX_PageAddress_Address</c> (ägare, år, slug, sort) gör en dubblett inom sorten omöjlig.</para>
    /// </summary>
    public class PageAddressService
    {
        public const string CurrentCacheKey = "page_address_current_v1";
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        private readonly IScopeProvider _scopeProvider;
        private readonly AppCaches _caches;

        public PageAddressService(IScopeProvider scopeProvider, AppCaches caches)
        {
            _scopeProvider = scopeProvider;
            _caches = caches;
        }

        private const string Columns = "Id, OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent";

        /// <summary>Adressen (ägare, sort, år, slug), eller null. År null = händelsernas gamla trädadress.</summary>
        public PageAddressRow? Find(PageAddressOwner owner, string kind, int? urlYear, string slug)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // Två frågor i stället för "(@2 IS NULL AND …) OR …", så att indexet kan sökas.
            return urlYear == null
                ? scope.Database.FirstOrDefault<PageAddressRow>($"SELECT TOP 1 {Columns} FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL AND Slug = @2 AND TargetKind = @3",
                    owner.Type, owner.Id, slug, kind)
                : scope.Database.FirstOrDefault<PageAddressRow>($"SELECT TOP 1 {Columns} FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2 AND Slug = @3 AND TargetKind = @4",
                    owner.Type, owner.Id, urlYear.Value, slug, kind);
        }

        /// <summary>
        /// Adressen (ägare, år, slug) av vilken sort som helst — för adresser UTAN sortsegment: förbundets
        /// mästerskap och de gamla formerna från före 2026-10-18 (<c>/halland/2026/x/</c>). Finns flera sorter
        /// tas den äldsta raden: den gamla adressen kan bara ha pekat på den som fanns först, eftersom sorterna
        /// delade namnrymd då.
        /// </summary>
        public PageAddressRow? FindAnyKind(PageAddressOwner owner, int? urlYear, string slug)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return urlYear == null
                ? scope.Database.FirstOrDefault<PageAddressRow>($"SELECT TOP 1 {Columns} FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL AND Slug = @2 ORDER BY Id",
                    owner.Type, owner.Id, slug)
                : scope.Database.FirstOrDefault<PageAddressRow>($"SELECT TOP 1 {Columns} FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2 AND Slug = @3 ORDER BY Id",
                    owner.Type, owner.Id, urlYear.Value, slug);
        }

        /// <summary>Sidans aktuella adress, ur en cache över alla aktuella rader.</summary>
        public PageAddressRow? Current(string kind, int id)
            => id != 0 && CurrentIndex().TryGetValue(Key(kind, id), out var row) ? row : null;

        /// <summary>Alla sidans adresser, aktuell först.</summary>
        public IReadOnlyList<PageAddressRow> AllFor(string kind, int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<PageAddressRow>($"SELECT {Columns} FROM dbo.PageAddress WHERE TargetKind = @0 AND TargetId = @1 ORDER BY IsCurrent DESC, Id DESC", kind, id);
        }

        public IReadOnlyList<PageAddressRow> ListCurrent(string kind)
            => CurrentIndex().Values.Where(r => r.TargetKind == kind).ToList();

        public void Invalidate() => InvalidateLocal(_caches);

        /// <summary>
        /// Töm cachen och räkna upp generationen. ⚠️ Generationen är det som gör tömningen säker: en läsning som
        /// började FÖRE en skrivning och blir klar EFTER tömningen hade annars lagt tillbaka den gamla adressen i
        /// fem minuter (sett 2026-10-17: tävlingen pekade på sin gamla adress efter ett datumbyte).
        /// </summary>
        public static void InvalidateLocal(AppCaches caches)
        {
            Interlocked.Increment(ref _generation);
            caches.RuntimeCache.ClearByKey(CurrentCacheKey);
        }

        private static long _generation;

        private sealed record Snapshot(long Generation, Dictionary<string, PageAddressRow> Map);

        private static string Key(string kind, int id) => kind + "|" + id;

        private Dictionary<string, PageAddressRow> CurrentIndex()
        {
            var gen = Interlocked.Read(ref _generation);
            if (_caches.RuntimeCache.Get(CurrentCacheKey) is Snapshot s && s.Generation == gen) return s.Map;
            Dictionary<string, PageAddressRow> map;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                map = scope.Database.Fetch<PageAddressRow>($"SELECT {Columns} FROM dbo.PageAddress WHERE IsCurrent = 1")
                    .GroupBy(r => Key(r.TargetKind, r.TargetId))
                    .ToDictionary(g => g.Key, g => g.First());
            }
            // Bara om ingen skrivning skett under läsningen — annars kan svaret vara inaktuellt.
            if (Interlocked.Read(ref _generation) == gen)
                _caches.RuntimeCache.Insert(CurrentCacheKey, () => new Snapshot(gen, map), CacheFor);
            return map;
        }

        /// <summary>
        /// Gör (ägare, år, slug) till sidans aktuella adress och returnerar sluggen som blev det.
        /// Oförändrad ägare, år och grundslug = ingenting skrivs. Annars blir den gamla en historikrad, en
        /// gammal adress som sidan själv haft återanvänds, och en adress som en ANNAN sida har ger <c>-2</c> ….
        /// Körs i anroparens databas, så att adressen och sidan skrivs i samma transaktion.
        /// </summary>
        public static (string Slug, bool Changed) SetCurrent(IUmbracoDatabase db, string kind, int id,
            PageAddressOwner owner, int year, string baseSlug)
        {
            if (string.IsNullOrWhiteSpace(baseSlug)) throw new ArgumentException("Sidan saknar slug.", nameof(baseSlug));
            var current = db.FirstOrDefault<PageAddressRow>(
                $"SELECT TOP 1 {Columns} FROM dbo.PageAddress WITH (UPDLOCK, HOLDLOCK) WHERE TargetKind = @0 AND TargetId = @1 AND IsCurrent = 1", kind, id);
            // Sluggen jämförs både rakt och utan krocksuffix: "ssm-2026" är en egen slug (BaseOf ger "ssm"),
            // "x-2" är "x" som krockade.
            if (current != null && current.OwnerType == owner.Type && current.OwnerId == owner.Id && current.UrlYear == year
                && (string.Equals(current.Slug, baseSlug, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(BaseOf(current.Slug), baseSlug, StringComparison.OrdinalIgnoreCase)))
                return (current.Slug, false);

            var rows = db.Fetch<PageAddressRow>(
                $"SELECT {Columns} FROM dbo.PageAddress WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2",
                owner.Type, owner.Id, year);
            // Bara sortens egen namnrymd räknas (utom hos förbundets mästerskap, där adressen saknar sortsegment).
            var slug = PageAddressRules.UniqueSlug(baseSlug, rows
                .Where(r => PageAddressRules.SharesNamespace(owner, r.TargetKind, kind))
                .Select(r => (r.Slug, Mine: r.TargetKind == kind && r.TargetId == id)));

            db.Execute("UPDATE dbo.PageAddress SET IsCurrent = 0 WHERE TargetKind = @0 AND TargetId = @1 AND IsCurrent = 1", kind, id);
            var reused = db.Execute(
                "UPDATE dbo.PageAddress SET IsCurrent = 1 WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2 AND Slug = @3 AND TargetKind = @4 AND TargetId = @5",
                owner.Type, owner.Id, year, slug, kind, id);
            if (reused == 0)
                db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES (@0, @1, @2, @3, @4, @5, 1)",
                    owner.Type, owner.Id, year, slug, kind, id);
            return (slug, true);
        }

        /// <summary>Sätter (ägare, år, slug) som sidans aktuella adress i en egen transaktion.</summary>
        public (string Slug, bool Changed) SetCurrent(string kind, int id, PageAddressOwner owner, int year, string baseSlug)
        {
            (string Slug, bool Changed) result;
            // ⚠️ Scopet stängs (= transaktionen skrivs) FÖRE tömningen. Complete() skriver ingenting själv; tömdes
            // cachen innan dess hann en samtidig läsning lägga tillbaka den gamla adressen.
            using (var scope = _scopeProvider.CreateScope())
            {
                result = SetCurrent(scope.Database, kind, id, owner, year, baseSlug);
                scope.Complete();
            }
            if (result.Changed) Invalidate();
            return result;
        }

        /// <summary>En historikrad (inte aktuell) — t.ex. händelsens gamla trädadress. Finns adressen redan rörs inget.</summary>
        public static void AddHistory(IUmbracoDatabase db, string kind, int id, PageAddressOwner owner, int? year, string slug)
        {
            var exists = year == null
                ? db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL AND Slug = @2 AND TargetKind = @3", owner.Type, owner.Id, slug, kind)
                : db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2 AND Slug = @3 AND TargetKind = @4", owner.Type, owner.Id, year.Value, slug, kind);
            if (exists == 0)
                db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES (@0, @1, @2, @3, @4, @5, 0)",
                    owner.Type, owner.Id, year, slug, kind, id);
        }

        /// <summary>Sidans alla adresser bort (en raderad händelse). Tävlingar och serier behåller sina — de raderas mjukt.</summary>
        public static void DeleteFor(IUmbracoDatabase db, string kind, int id)
            => db.Execute("DELETE FROM dbo.PageAddress WHERE TargetKind = @0 AND TargetId = @1", kind, id);

        /// <summary>"gasaskjutning-2" → "gasaskjutning".</summary>
        public static string BaseOf(string slug) => PageAddressRules.BaseOf(slug);
    }

    /// <summary>Adressreglerna som rena funktioner (testade i <c>PageAddressRulesTests</c>).</summary>
    public static class PageAddressRules
    {
        /// <summary>"gasaskjutning-2" → "gasaskjutning". En slug som själv slutar på ett tal ≥ 2 tolkas som en krock.</summary>
        public static string BaseOf(string slug)
        {
            var i = slug.LastIndexOf('-');
            return i > 0 && int.TryParse(slug[(i + 1)..], out var n) && n >= 2 ? slug[..i] : slug;
        }

        /// <summary>
        /// Första lediga av <paramref name="baseSlug"/>, <c>-2</c>, <c>-3</c> … bland ägarens adresser det året.
        /// En adress som sidan själv haft (<c>Mine</c>) räknas som ledig — den återanvänds.
        /// </summary>
        public static string UniqueSlug(string baseSlug, IEnumerable<(string Slug, bool Mine)> existing)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s, m) in existing) (m ? mine : taken).Add(s);
            if (!taken.Contains(baseSlug)) return baseSlug;
            for (var n = 2; ; n++)
            {
                var candidate = $"{baseSlug}-{n}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>Ett årtal i en adress: fyra siffror, 1990–2199.</summary>
        public static bool IsYearSegment(string segment, out int year)
            => int.TryParse(segment, out year) && segment.Length == 4 && year is > 1990 and < 2200;

        /// <summary>Toppnivåsegment som adresserna äger — ingen nod får heta så (spärren i PageAddressReservedNames).</summary>
        public static readonly string[] ReservedTopSegments = { "sm", "ssm", "vsm", "osm", "nsm", "tavlingar" };

        /// <summary>Sortens segment i adressen: <c>tavlingar</c>, <c>handelser</c>, <c>serier</c>.</summary>
        public const string CompetitionsSegment = "tavlingar";
        public const string EventsSegment = "handelser";
        public const string SeriesSegment = "serier";

        public static string KindSegment(string kind) => kind switch
        {
            PageAddressKinds.Competition => CompetitionsSegment,
            PageAddressKinds.Event => EventsSegment,
            PageAddressKinds.Series => SeriesSegment,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Okänd sort."),
        };

        /// <summary>Sorten bakom ett segment, eller null.</summary>
        public static string? KindOfSegment(string? segment) => segment?.ToLowerInvariant() switch
        {
            CompetitionsSegment => PageAddressKinds.Competition,
            EventsSegment => PageAddressKinds.Event,
            SeriesSegment => PageAddressKinds.Series,
            _ => null,
        };

        /// <summary>
        /// Segment som en klubb och en nod direkt under en krets inte får heta: sortsegmenten och klubbkatalogen.
        /// En klubbs adress är <c>/{krets}/{klubb}/</c>, så klubben delar namnrymd med kretsens egna sidor.
        /// </summary>
        public static readonly string[] ReservedOwnerChildSegments = { "klubbar", CompetitionsSegment, EventsSegment, SeriesSegment };

        /// <summary>Får <paramref name="segment"/> användas för en klubb eller en sida direkt under en krets?</summary>
        public static bool IsReservedOwnerChildSegment(string segment)
            => ReservedOwnerChildSegments.Contains(segment, StringComparer.OrdinalIgnoreCase) || IsYearSegment(segment, out _);

        /// <summary>
        /// Delar två sorter namnrymd hos ägaren? Bara hos förbundets mästerskap, vars adress saknar sortsegment.
        /// </summary>
        public static bool SharesNamespace(PageAddressOwner owner, string kindA, string kindB)
            => owner.IsNational || string.Equals(kindA, kindB, StringComparison.Ordinal);

        /// <summary>
        /// Adressen ur ägarens adress (med snedstreck sist), sort, år och slug. År null = händelsens gamla
        /// trädadress <c>{ägare}{slug}/</c>. Förbundets mästerskap: <c>/sm/{år}/{slug}/</c>, utan sortsegment.
        /// </summary>
        public static string Compose(string ownerPrefix, bool national, string kind, int? year, string slug)
        {
            var prefix = ownerPrefix.EndsWith('/') ? ownerPrefix : ownerPrefix + "/";
            if (year == null) return $"{prefix}{slug}/";
            return national ? $"{prefix}{year}/{slug}/" : $"{prefix}{KindSegment(kind)}/{year}/{slug}/";
        }

        /// <summary>
        /// Delar en sökväg i ägarens segment, år, slug och resten (barnsegmentet, t.ex. <c>resultat</c>).
        /// Årtalet är det FÖRSTA segmentet (efter minst ett) som ser ut som ett år. Ingen träff = null.
        /// </summary>
        public static (string[] OwnerSegments, int Year, string? Slug, string[] Remaining)? Parse(string[] segments)
        {
            for (var i = 1; i < segments.Length; i++)
            {
                if (!IsYearSegment(segments[i], out var year)) continue;
                var slug = i + 1 < segments.Length ? segments[i + 1] : null;
                var rest = i + 2 < segments.Length ? segments[(i + 2)..] : Array.Empty<string>();
                return (segments[..i], year, slug, rest);
            }
            return null;
        }
    }
}
