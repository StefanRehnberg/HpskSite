namespace HpskSite.Models
{
    /// <summary>
    /// Model for missing club request from registration form
    /// </summary>
    public class MissingClubRequest
    {
        public string ClubName { get; set; } = string.Empty;
        public string? ClubLocation { get; set; }
        public string? ContactPerson { get; set; }
        public string RequestorEmail { get; set; } = string.Empty;
        public string? AdditionalNotes { get; set; }
    }
}
