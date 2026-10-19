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

        // Laddas och sparas av TrainingProgressStore (skalplanen B5, 2026-10-19) - aldrig ur/till
        // medlemsegenskaper. FromMember/SaveToMember ar borttagna med flit.

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
        /// Mark a step as completed and advance progress
        /// </summary>
        public void CompleteStep(int levelId, int stepNumber, string? instructorName = null, string? notes = null, string? source = null)
        {
            // Add completion record
            var completion = new StepCompletion
            {
                LevelId = levelId,
                StepNumber = stepNumber,
                CompletedDate = DateTime.Now,
                InstructorName = instructorName,
                Notes = notes,
                Source = source ?? StepCompletionSources.Functionary
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
        /// Credit a step the member never shot here, because they already hold the marke that step
        /// leads up to. Dated from the marke, not today, so the history reads honestly.
        /// Does NOT recalculate the position - the caller credits a whole batch and then calls
        /// <see cref="CalculateCurrentPosition"/> once.
        /// </summary>
        public void CreditStepFromBadge(int levelId, int stepNumber, DateTime achievedAt, string? notes = null)
        {
            if (IsStepCompleted(levelId, stepNumber)) return;

            CompletedSteps.Add(new StepCompletion
            {
                LevelId = levelId,
                StepNumber = stepNumber,
                CompletedDate = achievedAt,
                InstructorName = null,
                Notes = notes,
                Source = StepCompletionSources.Badge
            });

            LastActivityDate = DateTime.Now;
            TrainingStartDate ??= achievedAt;
        }

        /// <summary>
        /// Remove a completion (mis-click undo) and recalculate the current position.
        /// Returns true when something was actually removed.
        /// </summary>
        public bool UncompleteStep(int levelId, int stepNumber)
        {
            var removed = CompletedSteps.RemoveAll(c => c.LevelId == levelId && c.StepNumber == stepNumber);
            if (removed == 0) return false;

            CalculateCurrentPosition();
            LastActivityDate = DateTime.Now;
            return true;
        }

        /// <summary>
        /// Get a stored completion, or null if the step is not completed.
        /// </summary>
        public StepCompletion? GetCompletion(int levelId, int stepNumber)
        {
            return CompletedSteps.FirstOrDefault(c => c.LevelId == levelId && c.StepNumber == stepNumber);
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

    }
}