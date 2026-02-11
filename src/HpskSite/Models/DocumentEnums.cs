namespace HpskSite.Models
{
    /// <summary>
    /// Owner types for document archive entities
    /// </summary>
    public static class DocumentOwnerType
    {
        public const int Club = 0;
        public const int Region = 1;
    }

    /// <summary>
    /// Access levels for documents
    /// </summary>
    public static class DocumentAccessLevel
    {
        public const int Public = 0;
        public const int Authenticated = 1;
        public const int ClubMembers = 2;
        public const int ClubAdmins = 3;

        public static string GetLabel(int level)
        {
            return level switch
            {
                Public => "Publik",
                Authenticated => "Inloggade",
                ClubMembers => "Klubbmedlemmar",
                ClubAdmins => "Klubbadministratörer",
                _ => "Okänd"
            };
        }
    }
}
