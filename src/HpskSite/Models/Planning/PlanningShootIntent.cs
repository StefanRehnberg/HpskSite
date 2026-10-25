using System;
using NPoco;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Omgång 3 steg 9 (Stefan 2026-10-24): en funktionär säger vilka av veckans tävlingar hen tänker tävla i — långt innan
    /// anmälan öppnar — så att planeringen kan ta hänsyn till det. Tävlingen är familjens PLANERING (en delplanering = en
    /// tävling, <see cref="TargetPlanningId"/>), så avsikten står kvar när planeringen kopplas till tävlingen.
    /// <para>En person är en medlem (<see cref="MemberId"/>) eller, för ett handskrivet namn i bemanningen, bara namnet.</para>
    /// </summary>
    [TableName("PlanningShootIntent")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningShootIntent
    {
        public int Id { get; set; }
        public int RootPlanningId { get; set; }
        public int TargetPlanningId { get; set; }
        public int? MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        /// <summary>Klasserna med skyttens egna ord ("C1, A3"); null = inte angivet.</summary>
        public string? Classes { get; set; }
        /// <summary><see cref="ShootIntentSource"/>.</summary>
        public string Source { get; set; } = ShootIntentSource.Self;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UpdatedBy { get; set; }

        /// <summary>Samma personnyckel som bemanningen: "m:{id}" eller "n:{namn i gemener}".</summary>
        [Ignore]
        public string PersonKey => HpskSite.Models.Staffing.StaffPersonKey.For(MemberId, DisplayName);
    }

    public static class ShootIntentSource
    {
        /// <summary>Funktionären själv, på anmälningssidan.</summary>
        public const string Self = "Self";
        /// <summary>Den som bemannar, i personens panel.</summary>
        public const string Organiser = "Organiser";
    }
}
