namespace HpskSite.Models.ViewModels.Training
{
    /// <summary>
    /// Represents a training level in the Skyttetrappan system
    /// </summary>
    public class TrainingLevel
    {
        public int LevelId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Badge { get; set; } = string.Empty; // Emoji or icon identifier
        public string Goal { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public int Order { get; set; }
        public List<TrainingStep> Steps { get; set; } = new List<TrainingStep>();

        /// <summary>
        /// Check if this level is completed for a member
        /// </summary>
        public bool IsCompleted(MemberProgress progress)
        {
            if (progress.CurrentLevel > LevelId) return true;
            if (progress.CurrentLevel < LevelId) return false;

            // Same level - check if all steps are completed
            return progress.CurrentStep > Steps.Count;
        }

        /// <summary>
        /// Get the current step for a member in this level
        /// </summary>
        public TrainingStep? GetCurrentStep(MemberProgress progress)
        {
            if (progress.CurrentLevel != LevelId) return null;
            if (progress.CurrentStep > Steps.Count) return null;

            return Steps.FirstOrDefault(s => s.StepNumber == progress.CurrentStep);
        }

        /// <summary>
        /// Get completed steps for a member in this level
        /// </summary>
        public List<TrainingStep> GetCompletedSteps(MemberProgress progress)
        {
            if (progress.CurrentLevel > LevelId)
            {
                return Steps; // All steps completed if past this level
            }

            if (progress.CurrentLevel < LevelId)
            {
                return new List<TrainingStep>(); // No steps completed if not reached this level
            }

            // Same level - return steps up to current step
            return Steps.Where(s => s.StepNumber < progress.CurrentStep).ToList();
        }
    }
}