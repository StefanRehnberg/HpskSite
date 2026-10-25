using System;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Kopiera planeringen (P3 steg C, 2026-10-23). Det som följer med är planeringens FORM — områden, uppgifter
    /// och milstolpar, delar, bemanningens roller, dagar och behov — aldrig personerna, meddelandena, mötena,
    /// dokumenten, förslagen eller uppdragen.
    ///
    /// <para><b>⚠️ Ett fast datum flyttas lika många dagar som planeringens första dag.</b> Ett datum som inte
    /// går att flytta (ingen första dag i källan eller i kopian) följer INTE med: förra årets datum i ett nytt
    /// år ser rätt ut och är fel. "N dagar före" följer alltid med — den räknas om från kopians dag.</para>
    /// </summary>
    public static class PlanningCopyRules
    {
        /// <summary>Hur många dagar allt flyttas: från källans dag (tävlingens eller planeringens första) till kopians första dag.</summary>
        public static int? Offset(DateTime? sourceAnchor, DateTime? newStart) =>
            sourceAnchor.HasValue && newStart.HasValue ? (int)(newStart.Value.Date - sourceAnchor.Value.Date).TotalDays : null;

        /// <summary>Det flyttade datumet, eller null när det inte går att flytta.</summary>
        public static DateTime? Shift(DateTime? date, int? offset) =>
            date.HasValue && offset.HasValue ? date.Value.Date.AddDays(offset.Value) : null;

        /// <summary>
        /// En uppgifts fasta datum. ⚠️ Ligger kopian ungefär ett helt antal år senare (±7 dagar — "samma helg nästa
        /// år") flyttas datumet i HELA ÅR, så att förbundets frist 1 oktober förblir 1 oktober; annars lika många
        /// dagar som första dagen. Bemanningens dagar flyttas alltid i dagar (<see cref="Shift"/>) — där är veckodagen det viktiga.
        /// </summary>
        public static DateTime? ShiftDeadline(DateTime? date, DateTime? sourceAnchor, DateTime? newStart)
        {
            var offset = Offset(sourceAnchor, newStart);
            if (!date.HasValue || !offset.HasValue) return null;
            var years = (int)Math.Round(offset.Value / 365.25);
            if (years != 0 && Math.Abs((newStart!.Value.Date - sourceAnchor!.Value.Date.AddYears(years)).TotalDays) <= 7)
                return date.Value.Date.AddYears(years);
            return date.Value.Date.AddDays(offset.Value);
        }

        public static string DefaultName(string sourceName) =>
            (sourceName.Length > 190 ? sourceName[..190] : sourceName) + " (kopia)";
    }

    /// <summary>Vad kopian fick med sig — svaret säger det, så att ingenting försvinner tyst.</summary>
    public sealed record PlanningCopyReport(int PlanningId, int Areas, int Tasks, int Roles, int Days, int Needs,
        int DatesDropped, int DaysDropped, int SubPlannings = 0);
}
