namespace HpskSite.Models
{
    /// <summary>
    /// Single source of truth for the current Personuppgiftsbiträdesavtal (Data Processing
    /// Agreement) version. The DPA page (Views/DataProcessingAgreement.cshtml) renders this
    /// version, and ClubDpaAcceptance rows are stamped with it. Bump <see cref="Version"/>
    /// only when the *main terms* change materially — appendix-only changes (e.g. a new
    /// sub-processor) are handled via the notice + objection flow described in the agreement
    /// and do NOT require clubs to re-accept.
    /// </summary>
    public static class DpaInfo
    {
        /// <summary>Current contract version. Bump on material changes to the main terms.</summary>
        public const string Version = "1.1";

        /// <summary>Effective date shown on the agreement (yyyy-MM-dd).</summary>
        public const string EffectiveDate = "2026-06-06";
    }
}
