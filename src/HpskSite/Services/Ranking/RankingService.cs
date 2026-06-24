using HpskSite.Models.Ranking;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Ranking
{
    /// <summary>
    /// Read-side of the Träningsmatch ranking. Reads the latest persisted snapshot, filters to a
    /// scope (club/region/national), ranks at read time (so multi-club shooters rank correctly in
    /// each of their clubs), computes ↑/↓ movement, and resolves each row's identity relative to the
    /// viewer (in-club → full name; otherwise per the shooter's chosen visibility level).
    /// </summary>
    public class RankingService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IConfiguration _config;

        public const int MinFieldSize = 4;
        public const int MovementCompareDays = 7;
        private const int DefaultTake = 50;

        public RankingService(IScopeProvider scopeProvider, IConfiguration config)
        {
            _scopeProvider = scopeProvider;
            _config = config;
        }

        public RankingResult GetRanking(
            string discipline, string weaponGroup, string scope, string? scopeKey, RankingBoard board,
            int viewerMemberId, IReadOnlyCollection<int> viewerClubIds, bool viewerIsAdmin, string? scopeLabel)
        {
            var result = new RankingResult
            {
                Discipline = discipline,
                WeaponGroup = weaponGroup,
                Scope = scope,
                ScopeLabel = scopeLabel,
                Board = board.ToString()
            };

            using var scopeDb = _scopeProvider.CreateScope();
            var db = scopeDb.Database;

            var latestDate = db.ExecuteScalar<DateTime?>(
                "SELECT MAX(SnapshotDate) FROM RankingSnapshot WHERE Discipline = @0 AND WeaponGroup = @1",
                discipline, weaponGroup);
            if (latestDate == null)
            {
                scopeDb.Complete();
                result.EmptyReason = "Ingen ranking har beräknats än.";
                return result;
            }
            result.SnapshotDate = latestDate;

            var latestRows = db.Fetch<RankingSnapshotRow>(
                "SELECT * FROM RankingSnapshot WHERE SnapshotDate = @0 AND Discipline = @1 AND WeaponGroup = @2",
                latestDate.Value, discipline, weaponGroup);

            var inScope = latestRows.Where(r => InScope(r, scope, scopeKey)).ToList();
            var ranked = OrderForBoard(inScope, board).ToList();

            result.TotalShooters = ranked.Count;
            var minField = _config.GetValue("RankingSettings:MinFieldSize", MinFieldSize);
            if (ranked.Count < minField)
            {
                scopeDb.Complete();
                if (board != RankingBoard.Index && inScope.Count >= minField)
                    result.EmptyReason = "Förbättringslistan visas så snart vi har några dagars historik att jämföra mot (uppdateras varje natt).";
                else
                    result.EmptyReason = "För få deltagare i den här klassen än.";
                return result;
            }

            // movement: rank the same scope/board on a prior snapshot
            var priorMovement = new Dictionary<int, int>();
            var priorDate = db.ExecuteScalar<DateTime?>(
                "SELECT MAX(SnapshotDate) FROM RankingSnapshot WHERE SnapshotDate <= @0 AND Discipline = @1 AND WeaponGroup = @2",
                latestDate.Value.AddDays(-MovementCompareDays), discipline, weaponGroup);
            if (priorDate != null && priorDate.Value != latestDate.Value)
            {
                var priorRows = db.Fetch<RankingSnapshotRow>(
                    "SELECT * FROM RankingSnapshot WHERE SnapshotDate = @0 AND Discipline = @1 AND WeaponGroup = @2",
                    priorDate.Value, discipline, weaponGroup);
                var priorRanked = OrderForBoard(priorRows.Where(r => InScope(r, scope, scopeKey)).ToList(), board).ToList();
                for (int i = 0; i < priorRanked.Count; i++)
                    priorMovement[priorRanked[i].MemberId] = i + 1;
            }

            scopeDb.Complete();

            // assemble entries
            RankingEntry? you = null;
            for (int i = 0; i < ranked.Count; i++)
            {
                var r = ranked[i];
                var rank = i + 1;
                var entry = ToEntry(r, rank, board, viewerMemberId, viewerClubIds, viewerIsAdmin, priorMovement);
                if (entry.IsYou) you = entry;
                if (rank <= DefaultTake) result.Entries.Add(entry);
            }

            // ensure the viewer sees their own row even if outside the top slice
            if (you != null && !result.Entries.Any(e => e.IsYou))
                result.Entries.Add(you);

            result.You = you;
            result.HasData = true;
            return result;
        }

        /// <summary>Distinct (discipline, weapon group) combos present in the latest snapshot — drives the dropdowns.</summary>
        public List<ClassCombo> GetAvailableClasses()
        {
            using var scopeDb = _scopeProvider.CreateScope();
            var db = scopeDb.Database;
            var latest = db.ExecuteScalar<DateTime?>("SELECT MAX(SnapshotDate) FROM RankingSnapshot");
            var list = new List<ClassCombo>();
            if (latest != null)
            {
                list = db.Fetch<ClassCombo>(
                    "SELECT Discipline, WeaponGroup, COUNT(*) AS Cnt FROM RankingSnapshot WHERE SnapshotDate = @0 GROUP BY Discipline, WeaponGroup ORDER BY Discipline, WeaponGroup",
                    latest.Value);
            }
            scopeDb.Complete();
            return list;
        }

        public List<MyRankingLine> GetMyRankingContext(int memberId)
        {
            var lines = new List<MyRankingLine>();

            using var scopeDb = _scopeProvider.CreateScope();
            var db = scopeDb.Database;

            var latestDate = db.ExecuteScalar<DateTime?>("SELECT MAX(SnapshotDate) FROM RankingSnapshot");
            if (latestDate == null) { scopeDb.Complete(); return lines; }

            var myRows = db.Fetch<RankingSnapshotRow>(
                "SELECT * FROM RankingSnapshot WHERE SnapshotDate = @0 AND MemberId = @1", latestDate.Value, memberId);
            if (myRows.Count == 0) { scopeDb.Complete(); return lines; }

            var priorDate = db.ExecuteScalar<DateTime?>(
                "SELECT MAX(SnapshotDate) FROM RankingSnapshot WHERE SnapshotDate <= @0", latestDate.Value.AddDays(-MovementCompareDays));

            foreach (var mine in myRows)
            {
                var primaryClubId = (mine.ClubIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                var groupRows = db.Fetch<RankingSnapshotRow>(
                    "SELECT * FROM RankingSnapshot WHERE SnapshotDate = @0 AND Discipline = @1 AND WeaponGroup = @2",
                    latestDate.Value, mine.Discipline, mine.WeaponGroup);

                var line = new MyRankingLine
                {
                    Discipline = mine.Discipline,
                    WeaponGroup = mine.WeaponGroup,
                    HandicapIndex = mine.HandicapIndex,
                    IsProvisional = mine.IsProvisional,
                    ClubName = mine.ClubName,
                    ImprovementDelta30 = mine.ImprovementDelta30
                };

                // national
                var nat = OrderForBoard(groupRows, RankingBoard.Index).ToList();
                line.NationalTotal = nat.Count;
                line.NationalRank = IndexOfMember(nat, memberId);

                // club
                if (!string.IsNullOrEmpty(primaryClubId))
                {
                    var club = OrderForBoard(groupRows.Where(r => InScope(r, "club", primaryClubId)).ToList(), RankingBoard.Index).ToList();
                    line.ClubTotal = club.Count;
                    line.ClubRank = IndexOfMember(club, memberId);

                    if (priorDate != null && priorDate.Value != latestDate.Value && line.ClubRank != null)
                    {
                        var priorGroup = db.Fetch<RankingSnapshotRow>(
                            "SELECT * FROM RankingSnapshot WHERE SnapshotDate = @0 AND Discipline = @1 AND WeaponGroup = @2",
                            priorDate.Value, mine.Discipline, mine.WeaponGroup);
                        var priorClub = OrderForBoard(priorGroup.Where(r => InScope(r, "club", primaryClubId)).ToList(), RankingBoard.Index).ToList();
                        var priorRank = IndexOfMember(priorClub, memberId);
                        if (priorRank != null) line.ClubMovement = priorRank.Value - line.ClubRank.Value;
                    }
                }

                lines.Add(line);
            }

            scopeDb.Complete();
            return lines;
        }

        // ---- helpers ----

        private static bool InScope(RankingSnapshotRow r, string scope, string? scopeKey)
        {
            switch (scope)
            {
                case "national": return true;
                case "club":
                    return !string.IsNullOrEmpty(scopeKey) && CsvContains(r.ClubIds, scopeKey);
                case "region":
                    return !string.IsNullOrEmpty(scopeKey) && CsvContains(r.RegionCodes, scopeKey);
                default: return false;
            }
        }

        private static bool CsvContains(string? csv, string value)
        {
            if (string.IsNullOrEmpty(csv)) return false;
            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (part.Trim().Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static IEnumerable<RankingSnapshotRow> OrderForBoard(List<RankingSnapshotRow> rows, RankingBoard board)
        {
            // Provisional shooters (low sample) always sink below established ones on every board,
            // so a fluke index/delta from a handful of series can't top the list.
            switch (board)
            {
                case RankingBoard.Improvement30:
                    return rows.Where(r => r.ImprovementDelta30 != null)
                               .OrderBy(r => r.IsProvisional)
                               .ThenByDescending(r => r.ImprovementDelta30!.Value)
                               .ThenBy(r => r.HandicapIndex);
                case RankingBoard.ImprovementSeason:
                    return rows.Where(r => r.ImprovementDeltaSeason != null)
                               .OrderBy(r => r.IsProvisional)
                               .ThenByDescending(r => r.ImprovementDeltaSeason!.Value)
                               .ThenBy(r => r.HandicapIndex);
                default: // Index — established shooters first (lower index = better), then provisional
                    return rows.OrderBy(r => r.IsProvisional)
                               .ThenBy(r => r.HandicapIndex)
                               .ThenByDescending(r => r.SessionCount);
            }
        }

        private static int? IndexOfMember(List<RankingSnapshotRow> ordered, int memberId)
        {
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].MemberId == memberId) return i + 1;
            return null;
        }

        private static RankingEntry ToEntry(
            RankingSnapshotRow r, int rank, RankingBoard board,
            int viewerMemberId, IReadOnlyCollection<int> viewerClubIds, bool viewerIsAdmin,
            Dictionary<int, int> priorMovement)
        {
            bool isYou = r.MemberId == viewerMemberId;
            bool inSameClub = ViewerSharesClub(r, viewerClubIds);
            var (name, club, avatar) = ResolveDisplay(r, isYou, viewerIsAdmin || inSameClub);

            int? movement = null;
            if (priorMovement.TryGetValue(r.MemberId, out var prevRank))
                movement = prevRank - rank;

            return new RankingEntry
            {
                Rank = rank,
                MemberId = r.MemberId,
                DisplayName = name,
                ClubName = club,
                AvatarUrl = avatar,
                HandicapIndex = r.HandicapIndex,
                ImprovementDelta = board == RankingBoard.ImprovementSeason ? r.ImprovementDeltaSeason : r.ImprovementDelta30,
                IsProvisional = r.IsProvisional,
                SessionCount = r.SessionCount,
                Movement = movement,
                IsYou = isYou
            };
        }

        private static bool ViewerSharesClub(RankingSnapshotRow r, IReadOnlyCollection<int> viewerClubIds)
        {
            if (viewerClubIds == null || viewerClubIds.Count == 0 || string.IsNullOrEmpty(r.ClubIds)) return false;
            foreach (var part in r.ClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), out var cid) && viewerClubIds.Contains(cid)) return true;
            return false;
        }

        /// <summary>(displayName, clubName?, avatarUrl?) honouring the shooter's visibility unless the viewer is them / a club-mate / admin.</summary>
        private static (string name, string? club, string? avatar) ResolveDisplay(RankingSnapshotRow r, bool isYou, bool fullAccess)
        {
            var full = string.IsNullOrEmpty(r.FullName) ? "Skytt" : r.FullName!;
            var initials = string.IsNullOrEmpty(r.Initials) ? "S" : r.Initials!;

            if (isYou) return ($"{full} (du)", r.ClubName, r.AvatarUrl);
            if (fullAccess) return (full, r.ClubName, r.AvatarUrl);

            switch (r.IdentityVisibility)
            {
                case "Halv":
                    return (initials, r.ShowClub ? r.ClubName : null, r.AvatarUrl);
                case "Anonym":
                    return (initials, r.ShowClub ? r.ClubName : null, null);
                default: // Full
                    return (full, r.ClubName, r.AvatarUrl);
            }
        }
    }
}
