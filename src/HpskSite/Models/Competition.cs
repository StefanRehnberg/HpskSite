using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using Newtonsoft.Json;

namespace HpskSite.Models
{
    public class Competition : BasePage
    {
        public Competition(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Basic competition properties
        public string CompetitionName => this.Value<string>("competitionName") ?? this.Name;
        public string Description => this.Value<string>("description") ?? "";
        public DateTime CompetitionDate => this.Value<DateTime>("competitionDate", fallback: Fallback.ToDefaultValue, defaultValue: DateTime.Today);
        public DateTime? CompetitionEndDate => this.Value<DateTime?>("competitionEndDate");
        public string Venue => this.Value<string>("venue") ?? "";
        public bool IsActive => this.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true);

        // Competition type reference
        public IPublishedContent? CompetitionTypeContent => this.Value<IPublishedContent>("competitionType");

        // Shooting class reference (supports multiple classes)
        public string ShootingClassIds
        {
            get
            {
                var raw = this.Value("shootingClassIds");
                if (raw is string[] stringArray)
                {
                    return string.Join(",", stringArray);
                }
                else if (raw is IEnumerable<string> enumerable)
                {
                    return string.Join(",", enumerable);
                }
                else if (raw is string stringValue)
                {
                    return stringValue ?? "";
                }
                else if (raw != null)
                {
                    // Try generic Value<T> approach for Flexible Dropdown
                    try
                    {
                        var typed = this.Value<IEnumerable<string>>("shootingClassIds");
                        if (typed != null)
                        {
                            return string.Join(",", typed);
                        }
                    }
                    catch
                    {
                        // Fallback to empty if all parsing fails
                    }
                }
                return "";
            }
        }
        public List<ShootingClass> ShootingClasses => GetShootingClassesList();
        public string ShootingClassNames => string.Join(", ", ShootingClasses.Select(sc => sc.Name));
        public bool HasShootingClasses => ShootingClasses.Any();


        private List<ShootingClass> GetShootingClassesList()
        {
            var classes = new List<ShootingClass>();
            var multiIds = ShootingClassIds;

            if (!string.IsNullOrEmpty(multiIds))
            {
                var ids = multiIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id));
                foreach (var id in ids)
                {
                    var shootingClass = Models.ShootingClasses.GetById(id);
                    if (shootingClass != null)
                    {
                        classes.Add(shootingClass);
                    }
                }
            }

            return classes;
        }

        // Registration settings
        public int MaxParticipants => this.Value<int>("maxParticipants", fallback: Fallback.ToDefaultValue, defaultValue: 50);
        public DateTime? RegistrationOpenDate => this.Value<DateTime?>("registrationOpenDate");
        public DateTime? RegistrationCloseDate => this.Value<DateTime?>("registrationCloseDate");

        public decimal RegistrationFee => this.Value<decimal>("registrationFee", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        public string SwishNumber => this.Value<string>("swishNumber") ?? "";
        public bool HasSwishPayment => !string.IsNullOrEmpty(SwishNumber) && RegistrationFee > 0;

        // Competition management
        public string CompetitionDirector => this.Value<string>("competitionDirector") ?? "";
        public string ContactEmail => this.Value<string>("contactEmail") ?? "";
        public string ContactPhone => this.Value<string>("contactPhone") ?? "";
        public string SpecialInstructions => this.Value<string>("specialInstructions") ?? "";

        // Results
        public bool ShowLiveResults => this.Value<bool>("showLiveResults", fallback: Fallback.ToDefaultValue, defaultValue: true);

        // Status calculation
        public CompetitionStatus Status
        {
            get
            {
                var now = DateTime.Now;
                var today = now.Date;

                if (!IsActive) return CompetitionStatus.Inactive;

                if (CompetitionDate.Date < today)
                {
                    return CompetitionEndDate?.Date >= today ? CompetitionStatus.InProgress : CompetitionStatus.Completed;
                }

                if (CompetitionDate.Date == today) return CompetitionStatus.InProgress;

                // Future competition
                if (RegistrationCloseDate.HasValue && RegistrationCloseDate.Value.Date < today)
                    return CompetitionStatus.RegistrationClosed;

                if (RegistrationOpenDate.HasValue && RegistrationOpenDate.Value.Date > today)
                    return CompetitionStatus.RegistrationNotOpen;

                return CompetitionStatus.RegistrationOpen;
            }
        }

        // Registration helpers
        public bool IsRegistrationOpen
        {
            get
            {
                var status = Status;
                return status == CompetitionStatus.RegistrationOpen;
            }
        }

        public bool CanRegister(int currentParticipants = 0)
        {
            return IsRegistrationOpen && currentParticipants < MaxParticipants;
        }

        // Display helpers
        public string GetStatusDisplay()
        {
            return Status switch
            {
                CompetitionStatus.RegistrationNotOpen => "Anmälan öppnar snart",
                CompetitionStatus.RegistrationOpen => "Öppen för anmälan",
                CompetitionStatus.RegistrationClosed => "Anmälan stängd",
                CompetitionStatus.InProgress => "Pågår",
                CompetitionStatus.Completed => "Avslutad",
                CompetitionStatus.Inactive => "Inaktiv",
                _ => "Okänd status"
            };
        }

        public string GetStatusColor()
        {
            return Status switch
            {
                CompetitionStatus.RegistrationOpen => "#4CAF50",      // Green
                CompetitionStatus.InProgress => "#FF5722",            // Red-orange
                CompetitionStatus.Completed => "#9E9E9E",             // Gray
                CompetitionStatus.RegistrationClosed => "#FF9800",    // Orange
                CompetitionStatus.RegistrationNotOpen => "#2196F3",   // Blue
                CompetitionStatus.Inactive => "#757575",              // Dark gray
                _ => "#9E9E9E"
            };
        }

        // Date range properties and helpers
        private bool HasValidEndDate => CompetitionEndDate.HasValue &&
                                        CompetitionEndDate.Value > DateTime.MinValue &&
                                        CompetitionEndDate.Value.Year > 1900 &&
                                        CompetitionEndDate.Value.Date >= CompetitionDate.Date;

        public bool IsMultiDay => HasValidEndDate && CompetitionEndDate!.Value.Date != CompetitionDate.Date;
        public bool IsSingleDay => !IsMultiDay;
        public int DurationDays => IsMultiDay ? (CompetitionEndDate!.Value.Date - CompetitionDate.Date).Days + 1 : 1;

        public string GetDateDisplay()
        {
            if (IsMultiDay)
            {
                return $"{CompetitionDate:yyyy-MM-dd} - {CompetitionEndDate!.Value:yyyy-MM-dd}";
            }
            return CompetitionDate.ToString("yyyy-MM-dd");
        }

        public string GetDateDisplayWithIcon()
        {
            if (IsMultiDay)
            {
                return $"📅 {CompetitionDate:MMM dd} - {CompetitionEndDate!.Value:MMM dd, yyyy}";
            }
            return $"📅 {CompetitionDate:MMM dd, yyyy}";
        }

        public string GetDurationDisplay()
        {
            if (IsMultiDay)
            {
                return $"{DurationDays} dagar";
            }
            return "1 dag";
        }

        public string GetTypeIcon()
        {
            return IsMultiDay ? "📅" : "🕐";
        }

        public string GetTypeDescription()
        {
            return IsMultiDay ? "Flerdagstävling" : "Endagstävling";
        }

        // Get scoring configuration (defaults based on common shooting class rules)
        public int MaxSeries => GetShootingClassSetting("maxSeries", 3);
        public int ShotsPerSeries => GetShootingClassSetting("shotsPerSeries", 10);
        public decimal MaxScorePerShot => GetShootingClassSetting("maxScorePerShot", 10.9m);
        public bool AllowPrecisionScoring => GetShootingClassSetting("allowPrecisionScoring", true);
        public decimal MaxPossibleScore => MaxSeries * ShotsPerSeries * MaxScorePerShot;

        // Finals/Championship configuration
        public int NumberOfFinalSeries => this.Value<int>("numberOfFinalSeries", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        public bool IsChampionship => NumberOfFinalSeries > 0;
        public bool HasFinalsRound => NumberOfFinalSeries > 0;
        public int QualificationSeriesCount => HasFinalsRound ? (this.Value<int>("numberOfSeriesOrStations") - NumberOfFinalSeries) : this.Value<int>("numberOfSeriesOrStations");

        // Helper method to get shooting class settings with fallbacks
        private T GetShootingClassSetting<T>(string settingName, T defaultValue)
        {
            // For now, use default values. Could be extended later to have class-specific rules
            // if needed, but since they're static, we can keep it simple
            return defaultValue;
        }

        // Club-only competition properties
        public bool IsClubOnly => this.Value<bool>("isClubOnly", fallback: Fallback.ToDefaultValue, defaultValue: false);
        public int ClubId => this.Value<int>("clubId", fallback: Fallback.ToDefaultValue, defaultValue: 0);

        // Competition Series relationship properties
        public IPublishedContent? ParentSeriesContent
        {
            get
            {
                var parent = this.Parent();
                return parent?.ContentType.Alias == "competitionSeries" ? parent : null;
            }
        }

        public bool IsPartOfSeries => ParentSeriesContent != null;

        public string SeriesName => ParentSeriesContent?.Value<string>("seriesName") ?? ParentSeriesContent?.Name ?? "";

        public string SeriesDescription => ParentSeriesContent?.Value<string>("seriesDescription") ?? "";

        public string SeriesShortDescription => ParentSeriesContent?.Value<string>("seriesShortDescription") ?? SeriesDescription;

        public DateTime? SeriesStartDate => ParentSeriesContent?.Value<DateTime>("seriesStartDate");

        public DateTime? SeriesEndDate => ParentSeriesContent?.Value<DateTime>("seriesEndDate");

        public bool SeriesIsActive => ParentSeriesContent?.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true) ?? false;

        public int SeriesRound
        {
            get
            {
                if (!IsPartOfSeries || ParentSeriesContent == null) return 0;

                var seriesCompetitions = ParentSeriesContent.Children()
                    .Where(x => x.ContentType.Alias == "competition")
                    .OrderBy(x => x.Value<int?>("seriesSortOrder") ?? int.MaxValue)
                    .ThenBy(x => x.Value<DateTime>("competitionDate"))
                    .ToList();

                return seriesCompetitions.IndexOf(this) + 1;
            }
        }

        public int TotalSeriesRounds => ParentSeriesContent?.Children().Count(x => x.ContentType.Alias == "competition") ?? 0;

        // Navigation helpers for series
        public IPublishedContent? PreviousInSeries
        {
            get
            {
                if (!IsPartOfSeries) return null;
                var siblings = GetSeriesSiblings();
                var currentIndex = siblings.IndexOf(this);
                return currentIndex > 0 ? siblings[currentIndex - 1] : null;
            }
        }

        public IPublishedContent? NextInSeries
        {
            get
            {
                if (!IsPartOfSeries) return null;
                var siblings = GetSeriesSiblings();
                var currentIndex = siblings.IndexOf(this);
                return currentIndex < siblings.Count - 1 ? siblings[currentIndex + 1] : null;
            }
        }

        private List<IPublishedContent> GetSeriesSiblings()
        {
            return ParentSeriesContent?.Children()
                .Where(x => x.ContentType.Alias == "competition")
                .OrderBy(x => x.Value<int?>("seriesSortOrder") ?? int.MaxValue)
                .ThenBy(x => x.Value<DateTime>("competitionDate"))
                .ToList() ?? new List<IPublishedContent>();
        }

        // Get allowed member classes
        public List<string> GetAllowedMemberClasses()
        {
            var allowAllClasses = CompetitionTypeContent?.Value<bool>("allowAllClasses") ?? true;
            var memberClassRequirements = CompetitionTypeContent?.Value<string>("memberClassRequirements") ?? "";

            if (allowAllClasses || string.IsNullOrWhiteSpace(memberClassRequirements))
            {
                return new List<string>
                {
                    "Träningsklass",
                    "Nybörjarklass",
                    "Medelklass",
                    "Avancerad",
                    "Expertklass",
                    "Mästarklass"
                };
            }

            return memberClassRequirements.Split(',')
                                        .Select(c => c.Trim())
                                        .Where(c => !string.IsNullOrEmpty(c))
                                        .ToList();
        }

        // Competition Management properties - handles JSON array storing member IDs
        public List<int> CompetitionManagerIds
        {
            get
            {
                var json = this.Value<string>("competitionManagers") ?? "[]";
                try
                {
                    var managerIds = JsonConvert.DeserializeObject<int[]>(json) ?? Array.Empty<int>();
                    return managerIds.ToList();
                }
                catch
                {
                    return new List<int>();
                }
            }
        }

        public bool HasCompetitionManagers => CompetitionManagerIds.Any();

        // Helper method for permission checking - checks if member ID is in the manager IDs list
        public bool IsManagerForCompetition(int memberId)
        {
            return CompetitionManagerIds.Contains(memberId);
        }
    }

    public enum CompetitionStatus
    {
        RegistrationNotOpen,
        RegistrationOpen,
        RegistrationClosed,
        InProgress,
        Completed,
        Inactive
    }
}