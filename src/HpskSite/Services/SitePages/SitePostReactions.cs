using Microsoft.Data.SqlClient;
using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Reaktionerna på en nyhet: samma fem som Träningsmatcherna (hjärta, tumme upp, eld, ledsen, prickskytt) — men lagrade som NYCKLAR, aldrig som själva emojin
    /// (❤️ bär ett osynligt variationstecken som ger jämförelsefel). Hjärtat är "gilla": ett tryck. Listan står här på ETT ställe; vyn, kontrollern och databasens CHECK läser den.
    /// </summary>
    public static class SitePostReactionKinds
    {
        public const string Heart = "heart";
        public const string Thumbs = "thumbs";
        public const string Fire = "fire";
        public const string Sad = "sad";
        public const string Target = "target";

        /// <summary>Visningsordningen. Samma ordning och emojis som Träningsmatchernas väljare (MatchApiController.AddReaction).</summary>
        public static readonly (string Key, string Emoji, string Label)[] All =
        {
            (Heart, "❤️", "Hjärta"), (Thumbs, "👍", "Tumme upp"), (Fire, "🔥", "Eld"), (Sad, "😢", "Ledsen"), (Target, "🎯", "Prickskytt"),
        };

        public static bool IsValid(string? key) => key != null && All.Any(k => k.Key == key);
        public static string Emoji(string key) => All.FirstOrDefault(k => k.Key == key).Emoji ?? "";
        public static string Label(string key) => All.FirstOrDefault(k => k.Key == key).Label ?? "";
    }

    /// <summary>Antalet reaktioner på en nyhet, totalt och per sort.</summary>
    public sealed class SitePostReactionSummary
    {
        public Dictionary<string, int> ByKind { get; } = new();
        public int Total => ByKind.Values.Sum();
        public int Of(string key) => ByKind.GetValueOrDefault(key);
        /// <summary>Den vanligaste sorten (vid lika: den som står först i <see cref="SitePostReactionKinds.All"/>), eller null utan reaktioner.</summary>
        public string? Top => SitePostReactionKinds.All.Select(k => k.Key).Where(k => Of(k) > 0).OrderByDescending(Of).FirstOrDefault();
    }

    /// <summary>
    /// Vem som får reagera: inloggad medlem i ägaren (för en krets: en aktiv medlem i någon av kretsens klubbar — det ligger redan i <see cref="SiteViewer.IsOwnerMember"/>), styrelsen eller
    /// ägarens admin (inklusive sajtadmin). Aldrig en utloggad besökare och aldrig en inloggad medlem i en helt annan klubb — en öppen nyhet ska inte kunna reageras på av vem som helst.
    /// Läsrätten till nyheten prövas separat (<see cref="SitePosts.CanRead"/>): man reagerar bara på det man får läsa. Ren funktion.
    /// </summary>
    public static class SitePostReactionRules
    {
        public static bool CanReact(SiteViewer v) => v.LoggedIn && (v.IsOwnerMember || v.IsOwnerBoard || v.IsOwnerAdmin);
        /// <summary>Högst så här många reaktionsändringar per medlem och minut (egen räknare: en medlems tryck får aldrig stoppa skribenternas sparningar).</summary>
        public const int PerMinute = 30;
    }

    /// <summary>
    /// Reaktionerna i SQL. Varje fråga bär ägaren nyheten hör till (id-lagen). EN reaktion per medlem och nyhet — det UNIKA INDEXET är domaren, inte koden: två flikar eller ett dubbelklick
    /// ger aldrig två rader. Skrivningarna är explicita ("sätt" och "ta bort"), aldrig en växling, så ett dubbelklick inte vänder fel. En reaktion rör aldrig <c>SitePost.Revision</c>
    /// (skribentens konfliktkontroll) och antalen räknas med COUNT, inte en kolumn på nyhetsraden.
    /// </summary>
    public static class SitePostReactions
    {
        /// <summary>
        /// Sätter medlemmens reaktion (byter om hen redan har en). Bara på en publicerad, icke raderad nyhet hos just den ägaren — villkoret sitter i SAMMA sats som skrivningen,
        /// så ingen kan reagera på en nyhet som dragits tillbaka mellan kontrollen och skrivningen. False = nyheten finns inte (för reaktioner) eller sorten är ogiltig.
        /// </summary>
        public static bool Set(IDatabase db, int postId, string ownerType, int ownerId, int memberId, string kind, DateTime now)
        {
            if (!SitePostReactionKinds.IsValid(kind) || memberId <= 0) return false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var updated = db.Execute(
                    @"UPDATE r SET Kind = @3 FROM dbo.SitePostReaction r JOIN dbo.SitePost p ON p.Id = r.PostId AND p.OwnerType = r.OwnerType AND p.OwnerId = r.OwnerId
                      WHERE r.PostId = @0 AND r.OwnerType = @1 AND r.OwnerId = @2 AND r.MemberId = @4 AND p.Status = 1 AND p.DeletedDate IS NULL",
                    postId, ownerType, ownerId, kind, memberId);
                if (updated == 1) return true;
                try
                {
                    var inserted = db.Execute(
                        @"INSERT INTO dbo.SitePostReaction (PostId, OwnerType, OwnerId, MemberId, Kind, CreatedDate)
                          SELECT Id, OwnerType, OwnerId, @3, @4, @5 FROM dbo.SitePost WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Status = 1 AND DeletedDate IS NULL",
                        postId, ownerType, ownerId, memberId, kind, now);
                    return inserted == 1;
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    // En samtidig skrivning hann före: raden finns nu. Gå ett varv till och uppdatera den.
                }
            }
            return true;
        }

        /// <summary>Tar bort medlemmens reaktion. Idempotent: ingen reaktion = inget händer.</summary>
        public static void Clear(IDatabase db, int postId, string ownerType, int ownerId, int memberId) =>
            db.Execute("DELETE FROM dbo.SitePostReaction WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2 AND MemberId = @3", postId, ownerType, ownerId, memberId);

        /// <summary>Antalen per nyhet för de angivna nyheterna, i EN fråga (listsidans tio rader). Saknas tabellen: inga reaktioner.</summary>
        public static Dictionary<int, SitePostReactionSummary> Summaries(IDatabase db, string ownerType, int ownerId, IReadOnlyCollection<int> postIds)
        {
            var result = new Dictionary<int, SitePostReactionSummary>();
            if (postIds.Count == 0 || !SitePosts.SocialTablesExist(db)) return result;
            var ids = string.Join(",", postIds.Where(i => i > 0).Distinct());   // heltal ur våra egna rader: aldrig text utifrån
            if (ids.Length == 0) return result;
            var rows = db.Fetch<CountRow>(
                $@"SELECT PostId, Kind, COUNT(*) AS N FROM dbo.SitePostReaction WHERE OwnerType = @0 AND OwnerId = @1 AND PostId IN ({ids}) GROUP BY PostId, Kind", ownerType, ownerId);
            foreach (var r in rows)
            {
                if (!result.TryGetValue(r.PostId, out var s)) result[r.PostId] = s = new SitePostReactionSummary();
                s.ByKind[r.Kind] = r.N;
            }
            return result;
        }

        /// <summary>Medlemmens egen reaktion på nyheten (nyckeln), eller null.</summary>
        public static string? Mine(IDatabase db, int postId, string ownerType, int ownerId, int memberId) =>
            memberId <= 0 || !SitePosts.SocialTablesExist(db) ? null :
                db.FirstOrDefault<string>("SELECT Kind FROM dbo.SitePostReaction WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2 AND MemberId = @3", postId, ownerType, ownerId, memberId);

        private sealed class CountRow { public int PostId { get; set; } public string Kind { get; set; } = ""; public int N { get; set; } }
    }
}
