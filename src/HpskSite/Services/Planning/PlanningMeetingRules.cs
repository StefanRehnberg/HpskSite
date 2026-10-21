using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Vilken föreslagen tid som passar bäst — en regel som står på skärmen ("passar flest"), så att
    /// den som bokar inte behöver räkna själv.
    ///
    /// <para>Flest <i>Passar</i>; vid lika färst <i>Passar inte</i>; sedan flest <i>Kanske</i>; sist den
    /// tidigaste tiden. Ingen röst alls ger inget förslag.</para>
    /// </summary>
    public static class PlanningMeetingRules
    {
        public sealed record OptionTally(int OptionId, int Yes, int Maybe, int No);

        public static List<OptionTally> Tally(IEnumerable<PlanningMeetingOptionRow> options, IEnumerable<PlanningMeetingVoteRow> votes)
        {
            var v = votes.ToList();
            return options.Select(o => new OptionTally(o.Id,
                v.Count(x => x.PlanningMeetingOptionId == o.Id && x.Answer == PlanningVoteAnswer.Yes),
                v.Count(x => x.PlanningMeetingOptionId == o.Id && x.Answer == PlanningVoteAnswer.Maybe),
                v.Count(x => x.PlanningMeetingOptionId == o.Id && x.Answer == PlanningVoteAnswer.No))).ToList();
        }

        public static int? BestOption(IEnumerable<PlanningMeetingOptionRow> options, IEnumerable<PlanningMeetingVoteRow> votes)
        {
            var opts = options.ToList();
            var t = Tally(opts, votes);
            if (t.All(x => x.Yes + x.Maybe + x.No == 0)) return null;
            var start = opts.ToDictionary(o => o.Id, o => o.StartsAt);
            return t.OrderByDescending(x => x.Yes).ThenBy(x => x.No).ThenByDescending(x => x.Maybe)
                .ThenBy(x => start[x.OptionId]).First().OptionId;
        }
    }
}
