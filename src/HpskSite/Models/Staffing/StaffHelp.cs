using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>A day/shift the organiser needs help with, on the sign-up page. Several per date allowed.</summary>
    [TableName("StaffHelpSlot")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffHelpSlot
    {
        public int Id { get; set; }
        public int? CompetitionId { get; set; }
        /// <summary>Planeringen raden hör till (P2b). Null = en tävlings rad som ännu inte har en planering.</summary>
        public int? PlanningId { get; set; }
        public DateTime SlotDate { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Headline { get; set; } = "";
        public string? Description { get; set; }
        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>A member's sign-up for a competition: free-text comment + the checked slots (with optional
    /// per-slot times), stored as SlotsJson = [{slotId, times:[{from,to}]}]. One row per (comp, member).</summary>
    [TableName("StaffHelpSignup")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffHelpSignup
    {
        public int Id { get; set; }
        public int? CompetitionId { get; set; }
        /// <summary>Planeringen raden hör till (P2b). Null = en tävlings rad som ännu inte har en planering.</summary>
        public int? PlanningId { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string? Comment { get; set; }
        public string SlotsJson { get; set; } = "[]";
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    // --- JSON shape (SlotsJson) ---
    /// <summary>
    /// Ett val på anmälningssidan. Antingen ett av arrangörens egna tillfällen ("Något annat vi behöver hjälp
    /// med", <see cref="SlotId"/> &gt; 0), eller en dag ur planen (<see cref="SlotId"/> = 0, <see cref="Date"/>)
    /// med en roll som behövs den dagen — tom <see cref="RoleKey"/> = "var det behövs".
    /// </summary>
    public class HelpSlotChoice
    {
        public int SlotId { get; set; }
        public string? Date { get; set; }
        public string? RoleKey { get; set; }
        public List<HelpTimeWindow> Times { get; set; } = new();
        public bool IsDayChoice => SlotId <= 0 && !string.IsNullOrWhiteSpace(Date);
    }

    /// <summary>En dag i planen med rollerna som behövs (Stefan 2026-10-23: anmälan bygger på behoven).</summary>
    public class NeedDayView
    {
        public string Date { get; set; } = "";       // "yyyy-MM-dd"
        public string Kind { get; set; } = "";
        public string KindLabel { get; set; } = "";
        public string? DayLabel { get; set; }
        public List<NeedRoleView> Roles { get; set; } = new();
        public int Missing => Roles.Sum(r => r.Missing);
    }

    public class NeedRoleView
    {
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public int Needed { get; set; }
        public int Missing { get; set; }
    }
    public class HelpTimeWindow
    {
        public string? From { get; set; }
        public string? To { get; set; }
    }

    // --- Output DTOs ---
    public class HelpSlotView
    {
        public int Id { get; set; }
        public string Date { get; set; } = "";        // "yyyy-MM-dd"
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Headline { get; set; } = "";
        public string? Description { get; set; }
    }

    /// <summary>A member's own sign-up, for prefilling the page.</summary>
    public class MyHelpSignupView
    {
        public string? Comment { get; set; }
        public List<HelpSlotChoice> Slots { get; set; } = new();
    }

    /// <summary>A volunteer in the manager review panel.</summary>
    public class HelpSignupReviewView
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string? Comment { get; set; }
        public string Updated { get; set; } = "";
        public List<HelpSignupReviewSlot> Slots { get; set; } = new();
    }
    public class HelpSignupReviewSlot
    {
        public int SlotId { get; set; }
        /// <summary>En dag ur planen (SlotId 0): dagen och rollen (tom = var det behövs).</summary>
        public string? Date { get; set; }
        public string? RoleKey { get; set; }
        public string Label { get; set; } = "";        // "2026-08-02 · Tävlingsdag 1 · Kansliet (06:00–22:00)"
        public string TimesText { get; set; } = "";     // "07:00–11:00, 18:00–22:00" or "" = hela passet
    }

    // --- Request DTOs ---
    public class SaveHelpSlotRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string? Date { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Headline { get; set; } = "";
        public string? Description { get; set; }
    }

    public class SaveHelpSignupRequest
    {
        public int CompetitionId { get; set; }
        /// <summary>P2b: planeringens bemanning (0 = tävlingens).</summary>
        public int PlanningId { get; set; }
        public string? Comment { get; set; }
        public List<HelpSlotChoice> Slots { get; set; } = new();
        /// <summary>Omgång 3 steg 9: veckans tävlingar funktionären tänker tävla i. Null = rör inte (en äldre sida).</summary>
        public List<ShootIntentChoice>? ShootIntents { get; set; }
    }

    public class ShootIntentChoice
    {
        public int TargetPlanningId { get; set; }
        public string? Classes { get; set; }
    }
}
