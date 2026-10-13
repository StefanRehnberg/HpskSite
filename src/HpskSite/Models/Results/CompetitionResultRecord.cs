namespace HpskSite.Models.Results
{
    /// <summary>
    /// Tävlingens resultatlista som DATA — det som i dag ligger på <c>competitionResult</c>-noden under
    /// tävlingen (skalplanen fas C1, <c>notes/skalplan-2026-10-12.md</c>).
    ///
    /// <para>En tävling har högst EN resultatlista. Deltävlingen är inte en egen lista utan bär sina
    /// egna fält (<c>Sub…</c>) på samma rad, precis som noden gör.</para>
    ///
    /// <para><b>Läs och skriv aldrig noden direkt</b> — gå genom <c>ICompetitionResultStore</c>. Varje
    /// direktläsning är ett ställe som slutar fungera när listan flyttar till en tabell.</para>
    /// </summary>
    public class CompetitionResultRecord
    {
        /// <summary>Radens id. I nodlagringen = nodens id, och id:t bevaras vid flytten.</summary>
        public int Id { get; set; }

        public int CompetitionId { get; set; }

        /// <summary>Historiskt alltid "Final Results". Läses av ingenting, men bevaras.</summary>
        public string ResultType { get; set; } = CompetitionResultFields.DefaultResultType;

        /// <summary>Den beräknade resultatlistan (JSON). Tom = ingen lista räknad ännu.</summary>
        public string ResultData { get; set; } = "";

        /// <summary>Huvudlistan publicerad (officiell). Fältskyttet bär huvudlistans läge på tävlingen.</summary>
        public bool IsOfficial { get; set; }

        /// <summary>Deltävlingens lista publicerad.</summary>
        public bool SubIsOfficial { get; set; }

        /// <summary>Klassammanslagningen (JSON). Fält- och springskyttet bär huvudlistans på tävlingen.</summary>
        public string MergeConfig { get; set; } = "";
        public string SubMergeConfig { get; set; } = "";

        /// <summary>Arrangörens egna klassnamn (JSON-ordbok).</summary>
        public string ClassNameOverrides { get; set; } = "";
        public string SubClassNameOverrides { get; set; } = "";

        /// <summary>Fältskyttets särskjutningsstation (JSON).</summary>
        public string ShootOffConfig { get; set; } = "";
        public string SubShootOffConfig { get; set; } = "";

        /// <summary>Arrangörens fördelning av hederspriser (JSON).</summary>
        public string HonoraryAwardConfig { get; set; } = "";

        /// <summary>När listan senast räknades eller ändrades. Null = aldrig.</summary>
        public DateTime? LastUpdated { get; set; }

        /// <summary>Om listan finns publikt (nodlagringen: publicerad nod). Sätts av lagringen.</summary>
        public bool IsPublished { get; set; }

        public bool HasResultData => !string.IsNullOrWhiteSpace(ResultData);

        public bool OfficialFor(bool subCompetition) => subCompetition ? SubIsOfficial : IsOfficial;
        public string MergeConfigFor(bool subCompetition) => subCompetition ? SubMergeConfig : MergeConfig;
        public string ClassNameOverridesFor(bool subCompetition) => subCompetition ? SubClassNameOverrides : ClassNameOverrides;
        public string ShootOffConfigFor(bool subCompetition) => subCompetition ? SubShootOffConfig : ShootOffConfig;

        public CompetitionResultRecord Clone() => (CompetitionResultRecord)MemberwiseClone();
    }

    /// <summary>Fälten som kan saknas på en äldre doctype (nodlagringen). Tabellen bär alla.</summary>
    public enum CompetitionResultField
    {
        ResultData,
        IsOfficial,
        SubIsOfficial,
        MergeConfig,
        SubMergeConfig,
        ClassNameOverrides,
        SubClassNameOverrides,
        ShootOffConfig,
        SubShootOffConfig,
        HonoraryAwardConfig,
        LastUpdated,
    }

    /// <summary>Nodens egenskapsalias — en plats, delad av nodlagringen och flytten.</summary>
    public static class CompetitionResultFields
    {
        public const string NodeAlias = "competitionResult";
        public const string NodeName = "Resultat";
        public const string UrlSegment = "resultat";
        public const string DefaultResultType = "Final Results";

        public static string Alias(CompetitionResultField field) => field switch
        {
            CompetitionResultField.ResultData => "resultData",
            CompetitionResultField.IsOfficial => "isOfficial",
            CompetitionResultField.SubIsOfficial => "subCompetitionIsOfficial",
            CompetitionResultField.MergeConfig => "mergeConfig",
            CompetitionResultField.SubMergeConfig => "subCompetitionMergeConfig",
            CompetitionResultField.ClassNameOverrides => "classNameOverrides",
            CompetitionResultField.SubClassNameOverrides => "subCompetitionClassNameOverrides",
            CompetitionResultField.ShootOffConfig => "faltskytteShootOffConfig",
            CompetitionResultField.SubShootOffConfig => "subCompetitionFaltskytteShootOffConfig",
            CompetitionResultField.HonoraryAwardConfig => "honoraryAwardConfig",
            CompetitionResultField.LastUpdated => "lastUpdated",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        public static CompetitionResultField MergeConfigFor(bool sub) =>
            sub ? CompetitionResultField.SubMergeConfig : CompetitionResultField.MergeConfig;
        public static CompetitionResultField ClassNameOverridesFor(bool sub) =>
            sub ? CompetitionResultField.SubClassNameOverrides : CompetitionResultField.ClassNameOverrides;
        public static CompetitionResultField ShootOffConfigFor(bool sub) =>
            sub ? CompetitionResultField.SubShootOffConfig : CompetitionResultField.ShootOffConfig;
        public static CompetitionResultField OfficialFor(bool sub) =>
            sub ? CompetitionResultField.SubIsOfficial : CompetitionResultField.IsOfficial;
    }
}
