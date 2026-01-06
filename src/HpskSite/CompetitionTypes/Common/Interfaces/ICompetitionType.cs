namespace HpskSite.CompetitionTypes.Common.Interfaces
{
    /// <summary>
    /// Base interface for all competition types.
    /// This defines the common contract that all competition types must implement.
    /// </summary>
    public interface ICompetitionType
    {
        /// <summary>
        /// Unique identifier for this competition type (e.g., "Precision", "Rapid Fire", etc.)
        /// </summary>
        string TypeName { get; }

        /// <summary>
        /// Display name for this competition type
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Description of this competition type
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Whether this competition type is currently active/available
        /// </summary>
        bool IsActive { get; }
    }
}
