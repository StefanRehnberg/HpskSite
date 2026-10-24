using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Omgång 3 (Stefan 2026-10-24): familjen = huvudplaneringen och dess delplaneringar. SM-veckan är en familj med en
    /// tävling per delplanering (fält, precision, milsnabb). Rena regler, så att de går att pröva utan databas.
    /// </summary>
    public static class PlanningFamilyRules
    {
        /// <summary>
        /// Den tävling planeringens bemanning gäller: en egen koppling vinner; annars det val planeringen gjort bland
        /// familjens kopplade tävlingar ("Bemannar för"); annars huvudplaneringens tävling; annars ingen.
        /// <para><paramref name="linkByPlanning"/> = familjens kopplingar, planering → tävling.</para>
        /// <para>Ett val som inte längre är en av familjens tävlingar (kopplingen togs bort) räknas inte — då gäller
        /// huvudplaneringens.</para>
        /// </summary>
        public static int? StaffingCompetition(PlanningRow p, IReadOnlyDictionary<int, int> linkByPlanning)
        {
            if (linkByPlanning.TryGetValue(p.Id, out var own) && own > 0) return own;
            if (p.StaffsForCompetitionId is int chosen && chosen > 0 && linkByPlanning.Values.Contains(chosen)) return chosen;
            if (p.ParentPlanningId is int parent && linkByPlanning.TryGetValue(parent, out var pc) && pc > 0) return pc;
            return null;
        }
    }
}
