using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>En rad i förslagslistan för kassören. Bär ålder och födelseårets status — aldrig personnummer eller födelsedatum.</summary>
    public sealed record AgeProposalView(
        int MemberId, string Name, AgeProposalKind Kind,
        string? FromKey, string? FromName, string? ToKey, string? ToName,
        int? Age, BirthYearStatus BirthStatus, string? Reason,
        decimal? FeeBefore, decimal? FeeAfter, bool NoCategory);

    public sealed class AgeProposalResult
    {
        public int Year { get; init; }

        /// <summary>Klubben har åldersgränser. Utan dem är regeln av och listan tom.</summary>
        public bool Enabled { get; init; }

        /// <summary>Kassören kan godkänna (avgiftsåret är i år eller senare).</summary>
        public bool CanApply { get; init; }

        public List<AgeProposalView> Rows { get; init; } = new();
    }

    public sealed record AgeSkip(int MemberId, string Reason);

    public sealed class AgeApplyResult
    {
        public string? Error { get; init; }
        public int Applied { get; init; }
        public List<AgeSkip> Skipped { get; init; } = new();

        /// <summary>Oskickade avgifter som togs bort för de bytta medlemmarna (de skapas om vid utskicket).</summary>
        public int ChargesCleared { get; init; }
    }

    /// <summary>
    /// Förslagslistan, kassörens godkännande, undantaget "Behåll typen" och ångra (steg 3b). Reglerna är rena
    /// (<see cref="AgeTypeProposal"/>, <see cref="AgeTypeRules"/>); den här klassen läser underlaget och skriver bytena.
    ///
    /// <para><b>⚠️⚠️ Godkännandet är compare-and-swap.</b> Klienten skickar bara (medlem, typen hen hade när listan visades) —
    /// aldrig målet. Servern räknar om förslaget och behåller bara de par som fortfarande stämmer; varje byte är ett villkorat
    /// UPDATE (rätt gammal typ, aktiv, ingen Familj/hushåll, inget undantag) och 0 rader betyder "ändrades redan".
    /// Det är en ENDA skrivväg för åldersbyten; <c>MembershipType</c> skrivs annars av klubbens massåtgärd och medlemmens eget val.</para>
    ///
    /// <para><b>⚠️ Läs före skrivscopet.</b> Underlaget läses på egna anslutningar (rostern, födelseår, avgiftsraderna); gjort inuti
    /// det skrivande scopet väntar de på dess ocommittade rader tills "Execution Timeout Expired". Därför läses allt först, låset
    /// <c>fee-generate:{klubb}:{år}</c> hålls hela vägen (samma som skapandet av avgifter), och bara själva byten sker i scopet.</para>
    /// </summary>
    public class MembershipAgeProposalService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly MembershipAgeLimits _limits;
        private readonly MembershipTypeCatalog _types;
        private readonly ClubFeeInputService _feeInput;
        private readonly MembershipFeeService _fee;
        private readonly Hosting.DatabaseLock _lock;

        public MembershipAgeProposalService(IScopeProvider scopeProvider, MembershipAgeLimits limits, MembershipTypeCatalog types,
            ClubFeeInputService feeInput, MembershipFeeService fee, Hosting.DatabaseLock dbLock)
        {
            _scopeProvider = scopeProvider;
            _limits = limits;
            _types = types;
            _feeInput = feeInput;
            _fee = fee;
            _lock = dbLock;
        }

        private sealed record Basis(List<AgeStep> Steps, List<MembershipTypeEntry> Types, List<MemberFeeInput> Members, List<AgeProposalRow> Rows);

        /// <summary>Underlaget och de råa förslagsraderna. Kastar vid läsfel (ingen tyst tom lista).</summary>
        private Basis ComputeRows(int clubId, int year)
        {
            var steps = _limits.Steps(clubId);
            var types = _types.Types(clubId);
            var members = _feeInput.ActiveFeeMembers(clubId);
            if (steps.Count == 0) return new Basis(steps, types, members, new List<AgeProposalRow>());

            var optOuts = OptOuts(clubId);
            var birth = _limits.BirthYears(members.Select(m => m.MemberId));
            var inputs = members.Select(m => new AgeMemberInput(
                m.MemberId, m.MembershipType, IsActive: true, InHousehold: !string.IsNullOrWhiteSpace(m.HouseholdId),
                KeptByTreasurer: optOuts.Contains(m.MemberId),
                BirthYear: birth.GetValueOrDefault(m.MemberId, new BirthYearResult(null, BirthYearStatus.Missing))));
            var rows = AgeTypeProposal.Build(year, inputs, steps, types.Where(t => t.IsActive).Select(t => t.Key).ToList());
            return new Basis(steps, types, members, rows);
        }

        private HashSet<int> OptOuts(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>("SELECT MemberId FROM ClubMemberAgeRuleOptOut WHERE ClubId = @0", clubId).ToHashSet();
        }

        private static bool IsProposal(AgeProposalRow r) => r.Kind is AgeProposalKind.Change or AgeProposalKind.Untyped && r.ToKey is not null;

        /// <summary>Antal medlemmar med ett väntande förslag (byte eller typlös) — för varningen i skicka-modalen. Ingen avgiftsberäkning.</summary>
        public int CountPending(int clubId, int year) => ComputeRows(clubId, year).Rows.Count(IsProposal);

        /// <summary>Förslagslistan med avgiften före och efter (samma beräkning som skapandet av avgifter).</summary>
        public AgeProposalResult GetProposals(int clubId, int year)
        {
            var b = ComputeRows(clubId, year);
            var canApply = year >= MembershipAgeLimits.CurrentYear;
            if (b.Steps.Count == 0) return new AgeProposalResult { Year = year, Enabled = false, CanApply = canApply };

            var categories = _fee.GetCategories(clubId, year).Where(c => c.IssuerType == 0).ToList();
            var (parts, partTypes) = _fee.GetParts(clubId, year);

            var changes = b.Rows.Where(IsProposal).ToDictionary(r => r.MemberId, r => r.ToKey!);
            var before = ClubFeeProposal.Build(categories, b.Members);
            var beforeFee = ClubFeeCalculator.ForProposal(before, categories, b.Members, parts, partTypes);
            // Alla byten på en gång: de bytta ingår aldrig i ett hushåll, så de påverkar inte varandra.
            var afterMembers = b.Members.Select(m => changes.TryGetValue(m.MemberId, out var to)
                ? new MemberFeeInput
                {
                    MemberId = m.MemberId, MembershipType = to, HouseholdId = m.HouseholdId, HouseholdPrimary = m.HouseholdPrimary,
                    HomeClub = m.HomeClub, OptionIds = m.OptionIds
                }
                : m).ToList();
            var after = ClubFeeProposal.Build(categories, afterMembers);
            var afterFee = ClubFeeCalculator.ForProposal(after, categories, afterMembers, parts, partTypes);

            var info = _feeInput.MemberInfo(b.Rows.Select(r => r.MemberId));
            string Label(string? key) => string.IsNullOrWhiteSpace(key) ? "" : MembershipTypeNames.LabelFor(b.Types, key);

            var rows = b.Rows.Select(r =>
            {
                var isProposal = IsProposal(r);
                var noCategory = isProposal && after.TryGetValue(r.MemberId, out var a) && a.Kind == ClubFeeKind.NoCategory;
                decimal? feeBefore = null, feeAfter = null;
                if (isProposal)
                {
                    // Före: en typlös medlem har ingen avgift än (null, inte 0 kr). Efter: utan taxa för typen finns ingen avgift (null).
                    if (before.TryGetValue(r.MemberId, out var bp) && bp.Kind is ClubFeeKind.Individual or ClubFeeKind.HouseholdPrimary)
                        feeBefore = beforeFee.GetValueOrDefault(r.MemberId, FeeCalculation.None).Total;
                    if (!noCategory) feeAfter = afterFee.GetValueOrDefault(r.MemberId, FeeCalculation.None).Total;
                }
                info.TryGetValue(r.MemberId, out var mi);
                return new AgeProposalView(r.MemberId, mi.Name ?? $"Medlem {r.MemberId}", r.Kind,
                    r.FromKey, string.IsNullOrEmpty(r.FromKey) ? null : Label(r.FromKey),
                    r.ToKey, string.IsNullOrEmpty(r.ToKey) ? null : Label(r.ToKey),
                    r.Age, r.BirthStatus, noCategory ? $"Klubben har ingen avgift för {Label(r.ToKey)} {year}. Lägg till den först." : r.Reason,
                    feeBefore, feeAfter, noCategory);
            })
            .OrderBy(v => v.Kind).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

            return new AgeProposalResult { Year = year, Enabled = true, CanApply = canApply, Rows = rows };
        }

        // ── Godkänn ───────────────────────────────────────────────────

        /// <summary>
        /// Godkänner de valda byten. Servern räknar om förslaget och behåller bara de par som fortfarande stämmer (compare-and-swap);
        /// resten kommer tillbaka som <see cref="AgeApplyResult.Skipped"/> med skäl. Bara för avgiftsår från och med i år.
        /// Kastar <see cref="InvalidOperationException"/> när någon annan skapar avgifter just nu (låset).
        /// </summary>
        public AgeApplyResult Apply(int clubId, int year, IReadOnlyList<AgeApprovalItem> items, int? byMemberId)
        {
            if (year < MembershipAgeLimits.CurrentYear)
                return new AgeApplyResult { Error = "Typbyten godkänns bara för i år och kommande år. Äldre års avgifter ändras inte." };
            if (items.Count == 0) return new AgeApplyResult();

            using var lease = _lock.TryAcquire($"fee-generate:{clubId}:{year}", TimeSpan.FromSeconds(30));
            if (lease is null)
                throw new InvalidOperationException("Avgifterna skapas just nu av någon annan. Försök igen om en stund.");

            // Underlaget läses FÖRE skrivscopet.
            var b = ComputeRows(clubId, year);
            var categories = _fee.GetCategories(clubId, year).Where(c => c.IssuerType == 0).ToList();
            var current = b.Rows.Where(IsProposal).ToDictionary(r => r.MemberId);

            var skipped = new List<AgeSkip>();
            var candidates = new List<(AgeProposalRow Row, int RuleFromAge)>();
            foreach (var item in items)
            {
                if (!current.TryGetValue(item.MemberId, out var row))
                { skipped.Add(new AgeSkip(item.MemberId, "Ändrades redan, eller finns inte längre i listan.")); continue; }
                if (!string.Equals((row.FromKey ?? "").Trim(), item.ExpectedFrom.Trim(), StringComparison.OrdinalIgnoreCase))
                { skipped.Add(new AgeSkip(item.MemberId, "Typen hade ändrats sedan listan visades. Öppna listan igen.")); continue; }
                if (!categories.Any(c => MembershipTypeKeys.Same(c.MembershipType, row.ToKey)))
                { skipped.Add(new AgeSkip(item.MemberId, $"Klubben har ingen avgift för {MembershipTypeNames.LabelFor(b.Types, row.ToKey)} {year}.")); continue; }
                var step = b.Steps.First(s => MembershipTypeKeys.Same(s.Key, row.ToKey));
                candidates.Add((row, step.FromAge));
            }

            var applied = new List<int>();
            if (candidates.Count > 0)
            {
                // ⚠️ INGEN autoComplete: allt eller inget, Complete() sist.
                using var scope = _scopeProvider.CreateScope(autoComplete: false);
                var db = scope.Database;
                foreach (var (row, ruleFrom) in candidates)
                {
                    var from = string.IsNullOrWhiteSpace(row.FromKey) ? null : row.FromKey!.Trim();
                    // Loggraden först. Ett godkänt byte per medlem, klubb och år (filtrerat unikt index) — NOT EXISTS i stället för ett undantag.
                    var logged = db.Execute(
                        @"INSERT INTO ClubMembershipTypeChange (ClubId, MemberId, [Year], Action, FromType, ToType, RuleFromAge, ByMemberId, ChangedUtc)
                          SELECT @0, @1, @2, N'Applied', @3, @4, @5, @6, SYSUTCDATETIME()
                          WHERE NOT EXISTS (SELECT 1 FROM ClubMembershipTypeChange WHERE ClubId = @0 AND MemberId = @1 AND [Year] = @2 AND Action = N'Applied')",
                        clubId, row.MemberId, year, from, row.ToKey, ruleFrom, byMemberId);
                    if (logged == 0) { skipped.Add(new AgeSkip(row.MemberId, "Har redan fått ett typbyte för det här året. Ångra det först.")); continue; }

                    var updated = db.Execute(
                        @"UPDATE ClubMembership SET MembershipType = @2
                           WHERE ClubId = @0 AND MemberId = @1
                             AND LTRIM(RTRIM(ISNULL(MembershipType, N''))) = @3
                             AND ISNULL(MembershipStatus, N'') NOT IN (N'Utträdd', N'Avliden')
                             AND LTRIM(RTRIM(ISNULL(HouseholdId, N''))) = N''
                             AND LTRIM(RTRIM(ISNULL(MembershipType, N''))) <> N'Familj'
                             AND NOT EXISTS (SELECT 1 FROM ClubMemberAgeRuleOptOut o WHERE o.ClubId = @0 AND o.MemberId = @1)",
                        clubId, row.MemberId, row.ToKey, from ?? "");
                    if (updated == 0)
                    {
                        db.Execute("DELETE FROM ClubMembershipTypeChange WHERE ClubId = @0 AND MemberId = @1 AND [Year] = @2 AND Action = N'Applied'",
                            clubId, row.MemberId, year);
                        skipped.Add(new AgeSkip(row.MemberId, "Ändrades redan."));
                        continue;
                    }
                    applied.Add(row.MemberId);
                }
                scope.Complete();
            }

            // Efter scopet: oskickade avgifter för de bytta räknas om vid utskicket. Skickade och betalda rörs aldrig.
            var cleared = applied.Count == 0 ? 0 : _fee.ClearUnsentClubChargesForMembers(clubId, year, applied);
            return new AgeApplyResult { Applied = applied.Count, Skipped = skipped, ChargesCleared = cleared };
        }

        // ── Ångra ─────────────────────────────────────────────────────

        /// <summary>
        /// Ångrar godkända byten för de angivna medlemmarna och året: typen sätts tillbaka till den gamla, men bara om medlemmen
        /// fortfarande har den typ bytet gav (annars har någon ändrat sedan dess). Loggraden blir <c>Undone</c>, så året kan godkännas igen.
        /// </summary>
        public AgeApplyResult Undo(int clubId, int year, IReadOnlyCollection<int> memberIds)
        {
            if (memberIds.Count == 0) return new AgeApplyResult();
            using var lease = _lock.TryAcquire($"fee-generate:{clubId}:{year}", TimeSpan.FromSeconds(30));
            if (lease is null)
                throw new InvalidOperationException("Avgifterna skapas just nu av någon annan. Försök igen om en stund.");

            var skipped = new List<AgeSkip>();
            var undone = new List<int>();
            using (var scope = _scopeProvider.CreateScope(autoComplete: false))
            {
                var db = scope.Database;
                foreach (var memberId in memberIds.Distinct())
                {
                    var log = db.FirstOrDefault<ClubMembershipTypeChange>(
                        "SELECT * FROM ClubMembershipTypeChange WHERE ClubId = @0 AND MemberId = @1 AND [Year] = @2 AND Action = N'Applied'",
                        clubId, memberId, year);
                    if (log is null) { skipped.Add(new AgeSkip(memberId, "Inget godkänt byte att ångra.")); continue; }

                    var restored = db.Execute(
                        @"UPDATE ClubMembership SET MembershipType = NULLIF(@2, N'')
                           WHERE ClubId = @0 AND MemberId = @1 AND LTRIM(RTRIM(ISNULL(MembershipType, N''))) = @3",
                        clubId, memberId, log.FromType ?? "", (log.ToType ?? "").Trim());
                    if (restored == 0) { skipped.Add(new AgeSkip(memberId, "Typen har ändrats sedan bytet. Rör inte.")); continue; }

                    db.Execute("UPDATE ClubMembershipTypeChange SET Action = N'Undone' WHERE Id = @0", log.Id);
                    undone.Add(memberId);
                }
                scope.Complete();
            }

            var cleared = undone.Count == 0 ? 0 : _fee.ClearUnsentClubChargesForMembers(clubId, year, undone);
            return new AgeApplyResult { Applied = undone.Count, Skipped = skipped, ChargesCleared = cleared };
        }

        // ── Behåll typen ──────────────────────────────────────────────

        /// <summary>
        /// "Behåll typen" (keep) sätter ett undantag så att åldersreglerna aldrig föreslår något för medlemmen; "Lås upp" tar bort det.
        /// Loggas som <c>Kept</c>/<c>Unlocked</c>. Medlemmens eget val på betalsidan påverkas inte av undantaget.
        /// Returnerar ett felmeddelande eller null.
        /// </summary>
        public string? SetOptOut(int clubId, int year, int memberId, bool keep, int? byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            var type = db.FirstOrDefault<string>(
                @"SELECT ISNULL(MembershipType, N'') FROM ClubMembership
                   WHERE ClubId = @0 AND MemberId = @1 AND ISNULL(MembershipStatus, N'') NOT IN (N'Utträdd', N'Avliden')", clubId, memberId);
            if (type is null) return "Medlemmen finns inte i klubbens register.";

            int changed;
            string action;
            if (keep)
            {
                changed = db.Execute(
                    @"INSERT INTO ClubMemberAgeRuleOptOut (ClubId, MemberId, ByMemberId, CreatedUtc)
                      SELECT @0, @1, @2, SYSUTCDATETIME()
                      WHERE NOT EXISTS (SELECT 1 FROM ClubMemberAgeRuleOptOut WHERE ClubId = @0 AND MemberId = @1)", clubId, memberId, byMemberId);
                action = MembershipTypeChangeAction.Kept;
            }
            else
            {
                changed = db.Execute("DELETE FROM ClubMemberAgeRuleOptOut WHERE ClubId = @0 AND MemberId = @1", clubId, memberId);
                action = MembershipTypeChangeAction.Unlocked;
            }
            if (changed > 0)
                db.Insert(new ClubMembershipTypeChange
                {
                    ClubId = clubId, MemberId = memberId, Year = year, Action = action,
                    FromType = string.IsNullOrWhiteSpace(type) ? null : type.Trim(), ToType = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
                    ByMemberId = byMemberId, ChangedUtc = DateTime.UtcNow
                });
            scope.Complete();
            return null;
        }
    }
}
