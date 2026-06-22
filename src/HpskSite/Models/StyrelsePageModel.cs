namespace HpskSite.Models
{
    /// <summary>One club or region the current member can do board work for.</summary>
    public class StyrelseScope
    {
        public int OwnerType { get; set; }   // 0=Club, 1=Region
        public int OwnerId { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "Klubb";   // "Klubb" / "Krets" (display)
        public bool CanManageRoles { get; set; }       // admin for this scope (role assignment)
    }

    /// <summary>View data for the /styrelse page (passed via ViewData; layout Model stays the site root).</summary>
    public class StyrelsePageModel
    {
        public List<StyrelseScope> Scopes { get; set; } = new();
        public StyrelseScope? Selected { get; set; }
        public string MemberName { get; set; } = "";
    }

    /// <summary>Model for the formal print views (dagordning / protokoll). Chromeless, Layout=null.</summary>
    public class StyrelsePrintModel
    {
        public string Mode { get; set; } = "protokoll";   // "dagordning" or "protokoll"
        public BoardMeeting Meeting { get; set; } = new();
        public List<BoardMeetingAgendaItem> Agenda { get; set; } = new();
        public List<BoardMeetingAttendee> Attendees { get; set; } = new();
        public List<BoardMeetingAgendaLink> Links { get; set; } = new();
        public string OrgName { get; set; } = "";
        public string? ChairmanName { get; set; }
        public string? SecretaryName { get; set; }
        public string? AdjusterName { get; set; }
    }

    /// <summary>Model for the formal "Valberedningens förslag" print. Chromeless, Layout=null.</summary>
    public class StyrelseValforslagModel
    {
        public string OrgName { get; set; } = "";
        public int Year { get; set; }
        public List<BoardNomination> Nominations { get; set; } = new();
    }
}
