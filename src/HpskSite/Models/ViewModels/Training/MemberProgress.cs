using Umbraco.Cms.Core.Models;

namespace HpskSite.Models.ViewModels.Training
{
    /// <summary>
    /// Represents a member's progress in the Skyttetrappan training system
    /// </summary>
    public class MemberProgress
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = string.Empty;
        public string MemberEmail { get; set; } = string.Empty;
        public string? PrimaryClubName { get; set; }

        // Current position in training
        public int CurrentLevel { get; set; } = 1; // Start at level 1 (Brons)
        public int CurrentStep { get; set; } = 1;  // Start at step 1

        // Training history
        public DateTime? TrainingStartDate { get; set; }
        public DateTime? LastActivityDate { get; set; }
        public List<StepCompletion> CompletedSteps { get; set; } = new List<StepCompletion>();
        public string? Notes { get; set; }

        // Derived properties
        public bool IsActive => TrainingStartDate.HasValue;
        public string CurrentLevelName => TrainingDefinitions.GetLevel(CurrentLevel)?.Name ?? "Unknown";
        public string CurrentStepDescription => TrainingDefinitions.GetStep(CurrentLevel, CurrentStep)?.Description ?? "";

        /// <summary>
        /// Create MemberProgress from Umbraco member data
        /// </summary>
        public static MemberProgress FromMember(IMember member, string? clubName = null)
        {
            var progress = new MemberProgress
            {
                MemberId = member.Id,
                MemberName = member.Name,
                MemberEmail = member.Email,
                PrimaryClubName = clubName
            };

            // Load training data from member properties
            progress.CurrentLevel = GetIntProperty(member, "currentTrainingLevel", 1);
            progress.CurrentStep = GetIntProperty(member, "currentTrainingStep", 1);

            if (DateTime.TryParse(member.GetValue("trainingStartDate")?.ToString(), out var startDate))
            {
                progress.TrainingStartDate = startDate;
            }

            if (DateTime.TryParse(member.GetValue("lastTrainingActivity")?.ToString(), out var lastActivity))
            {
                progress.LastActivityDate = lastActivity;
            }

            progress.Notes = member.GetValue("trainingNotes")?.ToString();

            // Parse completed steps from JSON
            var completedStepsJson = member.GetValue("completedTrainingSteps")?.ToString();
            if (!string.IsNullOrEmpty(completedStepsJson))
            {
                try
                {
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    progress.CompletedSteps = System.Text.Json.JsonSerializer.Deserialize<List<StepCompletion>>(completedStepsJson, options) ?? new List<StepCompletion>();
                }
                catch
                {
                    progress.CompletedSteps = new List<StepCompletion>();
                }
            }

            // Calculate current position from completed steps to ensure accuracy
            progress.CalculateCurrentPosition();

            return progress;
        }

        /// <summary>
        /// Calculate current level and step from completed steps
        /// This ensures CurrentLevel/CurrentStep are accurate based on actual progress
        /// </summary>
        public void CalculateCurrentPosition()
        {
            if (CompletedSteps.Count == 0)
            {
                CurrentLevel = 1;
                CurrentStep = 1;
                return;
            }

            // Find the highest completed level and step
            var highestCompleted = CompletedSteps
                .OrderByDescending(s => s.LevelId)
                .ThenByDescending(s => s.StepNumber)
                .FirstOrDefault();

            if (highestCompleted == null)
            {
                CurrentLevel = 1;
                CurrentStep = 1;
                return;
            }

            var level = TrainingDefinitions.GetLevel(highestCompleted.LevelId);
            if (level == null)
            {
                CurrentLevel = 1;
                CurrentStep = 1;
                return;
            }

            // Check if we've completed all steps in this level
            if (highestCompleted.StepNumber >= level.Steps.Count)
            {
                // Move to next level
                CurrentLevel = highestCompleted.LevelId + 1;
                CurrentStep = 1;
            }
            else
            {
                // Stay in same level, advance to next step
                CurrentLevel = highestCompleted.LevelId;
                CurrentStep = highestCompleted.StepNumber + 1;
            }
        }

        /// <summary>
        /// Save progress back to Umbraco member
        /// </summary>
        public void SaveToMember(IMember member)
        {
            member.SetValue("currentTrainingLevel", CurrentLevel);
            member.SetValue("currentTrainingStep", CurrentStep);
            member.SetValue("lastTrainingActivity", DateTime.Now);

            if (TrainingStartDate.HasValue)
            {
                member.SetValue("trainingStartDate", TrainingStartDate.Value);
            }

            if (!string.IsNullOrEmpty(Notes))
            {
                member.SetValue("trainingNotes", Notes);
            }

            // Serialize completed steps to JSON
            var completedStepsJson = System.Text.Json.JsonSerializer.Serialize(CompletedSteps);
            member.SetValue("completedTrainingSteps", completedStepsJson);
        }

        /// <summary>
        /// Mark a step as completed and advance progress
        /// </summary>
        public void CompleteStep(int levelId, int stepNumber, string? instructorName = null, string? notes = null)
        {
            // Add completion record
            var completion = new StepCompletion
            {
                LevelId = levelId,
                StepNumber = stepNumber,
                CompletedDate = DateTime.Now,
                InstructorName = instructorName,
                Notes = notes
            };

            CompletedSteps.Add(completion);

            // Update current position
            if (levelId == CurrentLevel && stepNumber == CurrentStep)
            {
                var currentLevel = TrainingDefinitions.GetLevel(CurrentLevel);
                if (currentLevel != null)
                {
                    if (CurrentStep >= currentLevel.Steps.Count)
                    {
                        // Level completed - advance to next level
                        CurrentLevel++;
                        CurrentStep = 1;
                    }
                    else
                    {
                        // Advance to next step in same level
                        CurrentStep++;
                    }
                }
            }

            LastActivityDate = DateTime.Now;

            // Set start date if this is the first step
            if (!TrainingStartDate.HasValue)
            {
                TrainingStartDate = DateTime.Now;
            }
        }

        /// <summary>
        /// Check if a specific step is completed
        /// </summary>
        public bool IsStepCompleted(int levelId, int stepNumber)
        {
            return CompletedSteps.Any(c => c.LevelId == levelId && c.StepNumber == stepNumber);
        }

        /// <summary>
        /// Get completion percentage for current level
        /// </summary>
        public double GetLevelCompletionPercentage()
        {
            var level = TrainingDefinitions.GetLevel(CurrentLevel);
            if (level == null) return 0;

            var completedInLevel = CompletedSteps.Count(c => c.LevelId == CurrentLevel);
            return (double)completedInLevel / level.Steps.Count * 100;
        }

        /// <summary>
        /// Get overall completion percentage across all levels
        /// </summary>
        public double GetOverallCompletionPercentage()
        {
            var totalSteps = TrainingDefinitions.GetAllLevels().Sum(l => l.Steps.Count);
            return (double)CompletedSteps.Count / totalSteps * 100;
        }

        private static int GetIntProperty(IMember member, string propertyName, int defaultValue)
        {
            var value = member.GetValue(propertyName)?.ToString();
            return int.TryParse(value, out var result) ? result : defaultValue;
        }
    }
}