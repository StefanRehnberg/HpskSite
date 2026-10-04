using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Writes Pistolskyttemärkets brons/silver derived from verified series (fas A4). The rule itself
    /// is the pure <see cref="MarkenBaseValor.Derive"/>; this service only gathers the evidence and
    /// inserts what it returns. Add-only: nothing is downgraded, a rejected level is never recreated.
    ///
    /// <para>Lazy, like the guldfodring: run when a member's märken are read, after every series
    /// validation, and for a whole club before its order list is built — otherwise the list would miss
    /// the brons of every member nobody happened to open.</para>
    /// </summary>
    public class MarkenBaseValorService
    {
        private const string Family = Marken.FamilyPistolskytte;

        private readonly MarkenLedgerService _ledger;
        private readonly StandardMedalLedgerService _standardMedals;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly ILogger<MarkenBaseValorService> _logger;

        public MarkenBaseValorService(MarkenLedgerService ledger, StandardMedalLedgerService standardMedals,
            IUmbracoDatabaseFactory databaseFactory, IMemberService memberService, ILogger<MarkenBaseValorService> logger)
        {
            _ledger = ledger;
            _standardMedals = standardMedals;
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _logger = logger;
        }

        /// <summary>Recompute one member. Returns the number of badges added. Never throws.</summary>
        public async Task<int> RecomputeAsync(int memberId)
        {
            try
            {
                var series = (await _ledger.GetAllVerifiedSeriesAsync(memberId))
                    .Where(s => s.BadgeFamily == Family).ToList();
                var medalYears = (await _standardMedals.GetAwardsForMemberAsync(memberId))
                    .Where(a => string.Equals(a.Discipline, StandardMedals.Faltskytte, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(a.Discipline, StandardMedals.MagnumFalt, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Year).ToHashSet();
                var guldYears = (await _ledger.GetQualificationsForMemberAsync(memberId, Family))
                    .Where(q => q.Fulfilled && q.Status == Marken.StatusVerified).Select(q => q.Year).ToHashSet();

                static int Ord(MarkenSeries s) => Marken.LevelOrdinal(s.ClaimedLevel ?? "");
                var evidence = series.Select(s => s.Year).Concat(medalYears).Concat(guldYears).Distinct()
                    .Select(y =>
                    {
                        var prec = series.Where(s => s.Year == y && s.SeriesType == Marken.SeriesTypePrecision
                                                     && s.CountsTowardGuldfodring).ToList();
                        var till = series.Where(s => s.Year == y
                            && Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplineTillampning).ToList();
                        return new MarkenBaseValor.YearEvidence(y,
                            prec.Count(s => Ord(s) >= 1), prec.Count(s => Ord(s) >= 2),
                            till.Count(s => Ord(s) >= 1), till.Count(s => Ord(s) >= 2),
                            medalYears.Contains(y), guldYears.Contains(y));
                    }).ToList();

                var held = (await _ledger.GetBadgesForMemberAsync(memberId, Family, includeRejected: true))
                    .Where(b => b.Level is Marken.LevelBrons or Marken.LevelSilver or Marken.LevelGuld)
                    .Select(b => new MarkenBaseValor.HeldBadge(b.Level, b.AchievedYear, b.Status));

                int added = 0;
                foreach (var award in MarkenBaseValor.Derive(evidence, held))
                {
                    await _ledger.InsertBadgeAsync(new MemberBadge
                    {
                        MemberId = memberId,
                        BadgeFamily = Family,
                        Level = award.Level,
                        LevelOrdinal = Marken.LevelOrdinal(award.Level),
                        Discipline = "Precision",
                        AchievedYear = award.Year,
                        Source = Marken.SourceSeries,
                        Status = Marken.StatusVerified,
                        Notes = $"Automatiskt ur godkända serier {award.Year} (3 precisionsserier + 3 tillämpningsserier eller standardmedalj i fält)",
                        EnteredByMemberId = 0
                    });
                    added++;
                    _logger.LogInformation("Märken: {Level} {Year} registrerat ur serier för medlem {MemberId}",
                        award.Level, award.Year, memberId);
                }
                return added;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Märken: brons/silver ur serier kunde inte räknas om för medlem {MemberId}", memberId);
                return 0;
            }
        }

        /// <summary>
        /// Recompute every member whose PRIMARY club is <paramref name="clubId"/> and who has a verified
        /// Pistolskyttemärke series in <paramref name="year"/> — the set the order list can be missing.
        /// </summary>
        public async Task RecomputeForClubYearAsync(int clubId, int year)
        {
            List<int> ids;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                ids = await db.FetchAsync<int>(
                    "SELECT DISTINCT MemberId FROM MarkenSeries WHERE BadgeFamily = @0 AND Status = @1 AND [Year] = @2",
                    Family, Marken.StatusVerified, year);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Märken: kunde inte läsa medlemmar för omräkning av brons/silver ({ClubId}, {Year})", clubId, year);
                return;
            }

            foreach (var id in ids)
            {
                var m = _memberService.GetById(id);
                if (m == null) continue;
                if (!int.TryParse(m.GetValue("primaryClubId")?.ToString(), out var pc) || pc != clubId) continue;
                await RecomputeAsync(id);
            }
        }
    }
}
