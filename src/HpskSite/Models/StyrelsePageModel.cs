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
}
