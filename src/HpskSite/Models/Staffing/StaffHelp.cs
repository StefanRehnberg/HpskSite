using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>A day/shift the organiser needs help with, on the sign-up page. Several per date allowed.</summary>
    [TableName("StaffHelpSlot")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffHelpSlot
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
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
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string? Comment { get; set; }
        public string SlotsJson { get; set; } = "[]";
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    // --- JSON shape (SlotsJson) ---
    public class HelpSlotChoice
    {
        public int SlotId { get; set; }
        public List<HelpTimeWindow> Times { get; set; } = new();
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
        public string? Comment { get; set; }
        public List<HelpSlotChoice> Slots { get; set; } = new();
    }
}
