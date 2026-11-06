using HpskSite.Models;
using HpskSite.Services.Results;
using HpskSite.Models.StartLists;
using HpskSite.Services.StartLists;
using Newtonsoft.Json.Linq;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>Varför vilka som står utanför mästerskapet INTE får ändras just nu.</summary>
    public enum OutOfCompetitionLockReason
    {
        /// <summary>Ändringen är tillåten.</summary>
        None,
        /// <summary>Resultatlistan är publicerad (officiell). Ändras bara via Rätta-flödet.</summary>
        ResultsPublished,
        /// <summary>Finalstartlistan är byggd. En gäst får inte stå i finalen, och finalisterna är redan uttagna.</summary>
        FinalsBuilt,
        /// <summary>En kvalgrupp är låst inför finalen. Finalisterna räknas ur den frusna rankningen.</summary>
        QualifyingFrozen,
    }

    /// <summary>
    /// När får vilka som står utanför mästerskapet ändras? EN regel för alla grenar (expertgruppen 2026-11-06, K3/K6): fältets spärr frågade
    /// <c>FaltskytteResultsOfficial</c>, som aldrig sätts för precision — efter att precisionsfamiljen släppts in hade flaggan varit fritt
    /// ändringsbar på en publicerad lista, och efter att en finalstartlista byggts hade en ändring tyst gjort att en gäst (eller en saknad
    /// tävlande) stod fel i finalen. Rena regeln finns här så att den kan prövas utan databas.
    /// </summary>
    public static class OutOfCompetitionLock
    {
        /// <param name="faltResultsOfficial">Fält/MagnumFält: tävlingens egen publiceringsflagga (<c>FaltskytteResultsOfficial</c>).</param>
        /// <param name="isPrecisionFamily">Tävlingen är en gren i precisionsfamiljen (tom typ räknas som Precision).</param>
        /// <param name="resultListOfficial">Precisionsfamiljen: resultatlistan är officiell.</param>
        /// <param name="finalsListExists">Precisionsfamiljen: det finns minst en finalstartlista.</param>
        /// <param name="qualifyingGroupFrozen">Precisionsfamiljen: minst en kvalgrupp är låst (finns i kvalstartlistans ögonblicksbild).</param>
        public static OutOfCompetitionLockReason Evaluate(
            bool faltResultsOfficial, bool isPrecisionFamily, bool resultListOfficial, bool finalsListExists, bool qualifyingGroupFrozen)
        {
            if (faltResultsOfficial) return OutOfCompetitionLockReason.ResultsPublished;
            if (!isPrecisionFamily) return OutOfCompetitionLockReason.None;
            if (resultListOfficial) return OutOfCompetitionLockReason.ResultsPublished;
            if (finalsListExists) return OutOfCompetitionLockReason.FinalsBuilt;
            if (qualifyingGroupFrozen) return OutOfCompetitionLockReason.QualifyingFrozen;
            return OutOfCompetitionLockReason.None;
        }

        public static bool IsPrecisionFamily(string? competitionTypeId) =>
            string.IsNullOrWhiteSpace(competitionTypeId)
            || PrecisionFamilyResultReader.SupportedTypes.Contains(competitionTypeId.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Läser tävlingens läge ur lagren och frågar <see cref="OutOfCompetitionLock"/>. Läser bara; skriver aldrig.</summary>
    public class OutOfCompetitionLockReader
    {
        private readonly ICompetitionResultStore _results;
        private readonly IStartListStore _startLists;

        public OutOfCompetitionLockReader(ICompetitionResultStore results, IStartListStore startLists)
        {
            _results = results;
            _startLists = startLists;
        }

        public OutOfCompetitionLockReason Reason(HpskSite.Models.Competitions.CompetitionInfo competition)
        {
            var precision = OutOfCompetitionLock.IsPrecisionFamily(competition.TypeId);
            if (!precision)
                return OutOfCompetitionLock.Evaluate(competition.FaltskytteResultsOfficial, false, false, false, false);

            // Billigast först: den publicerade listan avgör utan att startlistorna läses.
            var official = _results.Get(competition.Id)?.IsOfficial == true;
            if (official || competition.FaltskytteResultsOfficial)
                return OutOfCompetitionLock.Evaluate(competition.FaltskytteResultsOfficial, true, official, false, false);

            var finals = _startLists.List(competition.Id, StartListKind.Finals).Count > 0;
            if (finals)
                return OutOfCompetitionLock.Evaluate(false, true, false, true, false);

            var frozen = _startLists.List(competition.Id, StartListKind.Qualifying).Any(r => HasFrozenGroup(r.QualifyingResultsSnapshot));
            return OutOfCompetitionLock.Evaluate(false, true, false, false, frozen);
        }

        /// <summary>Innehåller kvalstartlistans ögonblicksbild minst en låst grupp? Trasig JSON räknas som ingen (samma som GetSnapshot).</summary>
        public static bool HasFrozenGroup(string? snapshotJson)
        {
            if (string.IsNullOrWhiteSpace(snapshotJson)) return false;
            try
            {
                var groups = JObject.Parse(snapshotJson)["ClassSnapshots"] as JObject;
                return groups != null && groups.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
