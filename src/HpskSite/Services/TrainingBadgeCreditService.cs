using HpskSite.Models;
using HpskSite.Models.ViewModels.Training;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services
{
    /// <summary>
    /// Credits the beginner ladder (Skyttetrappan levels 1-3) from a Pistolskyttemärke the member
    /// ALREADY HOLDS, so a veteran who has carried a guldmärke for twenty years does not have to
    /// re-shoot Nybörjartrappa Brons to reach Guldmärkesskytt 1. The märke IS the proof.
    ///
    /// <para><b>The credit is DERIVED, never stored.</b> It is re-applied on every read and
    /// <see cref="MemberProgress.SaveToMember"/> strips it back out again. So a märke that is later
    /// makulerad, rejected or corrected takes its credit with it, instead of leaving a beginner ladder
    /// standing on nothing. It also means no read has to write.</para>
    ///
    /// <para>This is the exact inverse of <see cref="MarkenLedgerService.SyncTrappaBadgesAsync"/>
    /// (trappa → märke). The two cannot ping-pong: that one refuses to mint a valör for a level whose
    /// steps came from here (<see cref="StepCompletion.FromBadge"/>) — a märke must never be derived
    /// from steps that were themselves derived from a märke, or a member holding only Guld would have
    /// Brons and Silver manufactured for them with no functionary behind either.</para>
    ///
    /// <para>⚠️ Only a <b>Verified</b> valör counts. Crediting an unvalidated self-reported claim would
    /// turn the functionary gate on levels 1-3 into self-service by the back door.</para>
    /// </summary>
    public class TrainingBadgeCreditService
    {
        private readonly MarkenLedgerService _ledger;
        private readonly ILogger<TrainingBadgeCreditService> _logger;

        /// <summary>Skyttetrappan level → the Pistolskyttemärket valör it ends with.</summary>
        private static readonly (int LevelId, string Valor)[] LevelToValor =
        {
            (1, Marken.LevelBrons),
            (2, Marken.LevelSilver),
            (3, Marken.LevelGuld)
        };

        public TrainingBadgeCreditService(MarkenLedgerService ledger, ILogger<TrainingBadgeCreditService> logger)
        {
            _ledger = ledger;
            _logger = logger;
        }

        /// <summary>
        /// Apply the credit to one member's in-memory progress. Returns the number of steps credited.
        /// Safe to call on every read: it only fills gaps and recalculates the position.
        /// </summary>
        public async Task<int> ApplyAsync(int memberId, MemberProgress progress)
        {
            var held = await SafeLookupAsync(new[] { memberId });
            return held.TryGetValue(memberId, out var badge) ? Credit(progress, badge) : 0;
        }

        /// <summary>
        /// Apply the credit across a whole roster with ONE badge query instead of one per member —
        /// used by the participant list and the leaderboard, which materialize every member's progress.
        /// Without this the credited veterans would show up at Nybörjartrappa Brons in those lists
        /// while their own page said Guldmärkesskytt 1.
        /// </summary>
        public async Task ApplyManyAsync(IEnumerable<MemberProgress> progresses)
        {
            var list = progresses.ToList();
            if (list.Count == 0) return;

            var held = await SafeLookupAsync(list.Select(p => p.MemberId));
            if (held.Count == 0) return;

            foreach (var progress in list)
            {
                if (held.TryGetValue(progress.MemberId, out var badge))
                    Credit(progress, badge);
            }
        }

        /// <summary>
        /// Fill every beginner-ladder step covered by the held valör, then recalculate the position.
        /// A shooter holding Guld necessarily passed Brons and Silver, so EVERY level up to the held
        /// valör is credited — not only the one it maps to.
        /// </summary>
        private static int Credit(MemberProgress progress, MemberBadge badge)
        {
            var heldOrdinal = Marken.LevelOrdinal(badge.Level);
            if (heldOrdinal <= 0) return 0;

            // ⚠️ AchievedYear wins over AchievedDate when they disagree. `AwardBadge` stamps
            // AchievedDate = DateTime.Now even for a märke earned in 1998, so the date is a bookkeeping
            // timestamp and the YEAR is the fact. Trusting the date would file a veteran's whole
            // beginner ladder under today.
            var hasYear = badge.AchievedYear > 1900;
            var achievedAt =
                badge.AchievedDate is { } d && (!hasYear || d.Year == badge.AchievedYear) ? d
                : hasYear ? new DateTime(badge.AchievedYear, 1, 1)
                : badge.CreatedAt;
            var note = $"Tillgodoräknat från Pistolskyttemärket i {badge.Level.ToLowerInvariant()}"
                     + (badge.AchievedYear > 1900 ? $" ({badge.AchievedYear})" : "");

            int credited = 0;
            foreach (var (levelId, valor) in LevelToValor)
            {
                if (Marken.LevelOrdinal(valor) > heldOrdinal) continue;

                var def = TrainingDefinitions.GetLevel(levelId);
                if (def == null) continue;

                foreach (var step in def.Steps)
                {
                    if (progress.IsStepCompleted(levelId, step.StepNumber)) continue;
                    progress.CreditStepFromBadge(levelId, step.StepNumber, achievedAt, note);
                    credited++;
                }
            }

            if (credited > 0) progress.CalculateCurrentPosition();
            return credited;
        }

        /// <summary>
        /// An un-migrated environment (no MemberBadge table) must degrade to "no credit", never take
        /// the Skyttetrappan page down.
        /// </summary>
        private async Task<Dictionary<int, MemberBadge>> SafeLookupAsync(IEnumerable<int> memberIds)
        {
            try
            {
                return await _ledger.GetHighestBaseValorForMembersAsync(memberIds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Pistolskyttemärken; skipping Skyttetrappan credit");
                return new Dictionary<int, MemberBadge>();
            }
        }
    }
}
