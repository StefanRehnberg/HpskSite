namespace HpskSite.Models.ViewModels.Training
{
    /// <summary>
    /// Where a <see cref="StepCompletion"/> came from. Three genuinely different things - a
    /// functionary's sign-off, the shooter's own note, and a credit for a marke earned before
    /// pistol.nu existed - so they get one field rather than a pile of booleans.
    /// </summary>
    public static class StepCompletionSources
    {
        /// <summary>Approved by a trainer / skjutledare / club admin / site admin.</summary>
        public const string Functionary = "Functionary";

        /// <summary>Ticked by the shooter (levels 4+ only).</summary>
        public const string SelfReported = "SelfReported";

        /// <summary>Credited from a held Pistolskyttemarke valor, not shot here.</summary>
        public const string Badge = "Badge";
    }

    /// <summary>
    /// Represents a completed training step with metadata
    /// </summary>
    public class StepCompletion
    {
        public int LevelId { get; set; }
        public int StepNumber { get; set; }
        public DateTime CompletedDate { get; set; }
        public string? InstructorName { get; set; }
        public string? Notes { get; set; }

        /// <summary>
        /// Where this completion came from - see <see cref="StepCompletionSources"/>.
        /// Null in older stored JSON, which reads as Functionary: correct, since everything recorded
        /// before self-service existed was functionary-approved.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// The shooter ticked this step themselves. Only possible from
        /// <see cref="TrainingDefinitions.SelfServiceMinLevel"/> and up.
        /// Derived - the client reads this off the JSON payload.
        /// </summary>
        public bool SelfReported => Source == StepCompletionSources.SelfReported;

        /// <summary>
        /// Credited from a Pistolskyttemarke the member already held (a veteran joining pistol.nu with
        /// a decades-old guldmarke). Never shot on this site, so it must never mint a marke back.
        /// </summary>
        public bool FromBadge => Source == StepCompletionSources.Badge;

        /// <summary>
        /// Get unique identifier for this step
        /// </summary>
        public string StepId => $"{LevelId}-{StepNumber}";

        /// <summary>
        /// Get display name for this completion
        /// </summary>
        public string GetDisplayName()
        {
            var level = TrainingDefinitions.GetLevel(LevelId);
            var step = TrainingDefinitions.GetStep(LevelId, StepNumber);

            if (level == null || step == null)
                return $"Level {LevelId}, Step {StepNumber}";

            return $"{level.Name} - Step {StepNumber}";
        }

        /// <summary>
        /// Get step description
        /// </summary>
        public string GetStepDescription()
        {
            var step = TrainingDefinitions.GetStep(LevelId, StepNumber);
            return step?.Description ?? "Unknown step";
        }
    }
}