namespace HpskSite.CompetitionTypes.Springskytte.Models
{
    /// <summary>Deltagarantal per (vapengrupp, åldersklass) — indata till analysen.</summary>
    public class SpringskytteClassCount
    {
        public string WeaponClass { get; set; } = "";
        public string AgeGenderClass { get; set; } = "";
        public int ParticipantCount { get; set; }
    }

    public class SpringskytteClassInfo
    {
        public string WeaponClass { get; set; } = "";
        public string AgeGenderClass { get; set; } = "";

        /// <summary>"vapengrupp|åldersklass" — samma nyckel resultatvägarna grupperar på.</summary>
        public string Key { get; set; } = "";

        public int ParticipantCount { get; set; }
        public bool BelowThreshold { get; set; }

        /// <summary>
        /// Varför klassen ligger under fem och ÄNDÅ inte kan slås samman. Tom när klassen är
        /// över gränsen eller har ett förslag — "inga förslag" och "inga klasser under fem" är
        /// två olika svar, och en yta som blandar ihop dem skickar arrangören på jakt efter en
        /// knapp som inte finns.
        /// </summary>
        public string MergeBlockReason { get; set; } = "";
    }

    public class SpringskytteMergeSuggestion
    {
        public string SourceKey { get; set; } = "";
        public string SourceClass { get; set; } = "";
        public string WeaponClass { get; set; } = "";
        public int SourceCount { get; set; }

        /// <summary>Tillåtna målklasser som FINNS i tävlingen, som nycklar.</summary>
        public List<string> PossibleTargets { get; set; } = new();

        /// <summary>Alltid true: SHB L.2.3.1 ger beslutet till tävlingsledningen.</summary>
        public bool RequiresAdminChoice { get; set; } = true;

        public string Reason { get; set; } = "";
    }

    public class SpringskytteMergeAnalysis
    {
        public List<SpringskytteClassInfo> Classes { get; set; } = new();
        public List<SpringskytteMergeSuggestion> Suggestions { get; set; } = new();
    }

    /// <summary>
    /// En sparad sammanslagning. Nycklar, inte klassnamn: springskyttets klass är ett PAR
    /// (vapengrupp, åldersklass), och "H 50" ensamt skulle träffa både A och C.
    /// </summary>
    public class SpringskytteClassMergeAction
    {
        public string SourceKey { get; set; } = "";
        public string TargetKey { get; set; } = "";
    }

    public class SaveSpringskytteMergeConfigRequest
    {
        public int CompetitionId { get; set; }

        /// <summary>JSON-array av <see cref="SpringskytteClassMergeAction"/>. Tom sträng rensar.</summary>
        public string? MergeConfig { get; set; }
    }
}
