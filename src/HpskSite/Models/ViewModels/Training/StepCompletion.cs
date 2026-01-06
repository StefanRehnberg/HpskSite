namespace HpskSite.Models.ViewModels.Training
{
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