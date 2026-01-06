using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using HpskSite.Services;

namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class UmbracoStartListRepository
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly ILogger<UmbracoStartListRepository> _logger;
        private readonly ClubService _clubService;

        public UmbracoStartListRepository(IMemberService memberService, IContentService contentService, IContentTypeService contentTypeService, ILogger<UmbracoStartListRepository> logger, ClubService clubService)
        {
            _memberService = memberService;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _logger = logger;
            _clubService = clubService;
        }

        public async Task<List<CompetitionRegistration>> GetCompetitionRegistrations(int competitionId)
        {
            try
            {
                // PERFORMANCE FIX: Direct traversal from competition instead of loading entire site tree
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    _logger.LogWarning("Competition {CompetitionId} not found", competitionId);
                    return new List<CompetitionRegistration>();
                }

                // Get registrations hub (only load first 20 children, hub is usually near top)
                var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
                var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");

                if (hub == null)
                {
                    _logger.LogInformation("No registrations hub found for competition {CompetitionId}", competitionId);
                    return new List<CompetitionRegistration>();
                }

                // Get all registrations under hub
                var umbracoRegistrations = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .ToList();

                _logger.LogInformation($"Found {umbracoRegistrations.Count} registrations for competition {competitionId}");

                // Batch load all members (sequential - IMemberService is NOT thread-safe)
                var uniqueMemberIds = umbracoRegistrations
                    .Select(r => r.GetValue<int>("memberId"))
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                // NOTE: Cannot use Parallel.ForEach - causes SqlTransaction errors
                var memberDict = new Dictionary<int, IMember>();
                foreach (var memberId in uniqueMemberIds)
                {
                    try
                    {
                        var member = _memberService.GetById(memberId);
                        if (member != null)
                        {
                            memberDict[memberId] = member;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error loading member {MemberId}", memberId);
                    }
                }

                _logger.LogDebug("Loaded {Count} members for registrations", memberDict.Count);

                var registrations = new List<CompetitionRegistration>();
                int registrationId = 1;

                foreach (var content in umbracoRegistrations)
                {
                    var memberId = content.GetValue<int>("memberId");
                    var memberName = content.GetValue<string>("memberName") ?? "Unknown Member";
                    var registrationDate = content.GetValue<DateTime>("registrationDate");

                    // Get shooting classes - support both new JSON array format and legacy single-class format
                    var shootingClassesJson = content.GetValue<string>("shootingClasses");
                    var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                    // FALLBACK: If no classes found, try legacy 'shootingClass' property (single string)
                    if (!shootingClasses.Any())
                    {
                        var legacyShootingClass = content.GetValue<string>("shootingClass");
                        if (!string.IsNullOrWhiteSpace(legacyShootingClass))
                        {
                            shootingClasses = new List<ShootingClassEntry>
                            {
                                new ShootingClassEntry { Class = legacyShootingClass, StartPreference = "" }
                            };
                            _logger.LogDebug("Using legacy shootingClass format for member {MemberId}: {Class}", memberId, legacyShootingClass);
                        }
                    }

                    // Derive club name from clubId property
                    string clubName = "Okänd klubb";

                    var clubId = content.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        clubName = _clubService.GetClubNameById(clubId) ?? $"Club {clubId}";
                    }
                    else
                    {
                        // Fallback for legacy data - try memberClub property
                        var regClubValue = content.GetValue<string>("memberClub");
                        if (!string.IsNullOrWhiteSpace(regClubValue))
                        {
                            if (int.TryParse(regClubValue, out var legacyClubId))
                            {
                                clubName = _clubService.GetClubNameById(legacyClubId) ?? $"Club {legacyClubId}";
                            }
                            else
                            {
                                clubName = regClubValue.Trim();
                            }
                        }
                        else if (memberId > 0 && memberDict.TryGetValue(memberId, out var member))
                        {
                            // PERFORMANCE FIX: Use cached member data instead of individual lookup
                            var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                            {
                                clubName = _clubService.GetClubNameById(primaryClubId) ?? $"Club {primaryClubId}";
                            }
                        }
                    }

                    // NEW: Expand classes - create one CompetitionRegistration per class
                    foreach (var classEntry in shootingClasses)
                    {
                        registrations.Add(new CompetitionRegistration
                        {
                            Id = registrationId++,
                            MemberId = memberId,
                            CompetitionId = competitionId,
                            MemberName = memberName,
                            MemberClass = classEntry.Class,
                            MemberClub = clubName,
                            RegistrationDate = registrationDate,
                            IsActive = true
                        });

                        _logger.LogDebug($"Added registration: {memberName} ({classEntry.Class}) from {clubName}");
                    }
                }

                return registrations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving competition registrations for competition {CompetitionId}", competitionId);
                return new List<CompetitionRegistration>();
            }
        }

        private IEnumerable<IContent> GetAllDescendants(IContent content)
        {
            yield return content;
            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out var totalRecords);
            foreach (var child in children)
            {
                foreach (var descendant in GetAllDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        public List<IContent> GetStartListsForCompetition(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    _logger.LogWarning("Competition {CompetitionId} not found", competitionId);
                    return new List<IContent>();
                }

                // Get direct children of competition
                var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);

                // Find the correct alias for precision start list
                var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };
                string? correctAlias = null;

                foreach (var alias in possibleAliases)
                {
                    var contentType = _contentTypeService.Get(alias);
                    if (contentType != null)
                    {
                        correctAlias = alias;
                        break;
                    }
                }

                if (correctAlias == null)
                {
                    _logger.LogWarning("No precision start list content type found");
                    return new List<IContent>();
                }

                // NEW ARCHITECTURE: Look for start list as DIRECT child of competition (not under hub)
                var startList = children.FirstOrDefault(c => c.ContentType.Alias == correctAlias);

                if (startList == null)
                {
                    // BACKWARD COMPATIBILITY: Also check under hub during migration period
                    var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                    if (hub != null)
                    {
                        var hubChildren = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _);
                        startList = hubChildren
                            .Where(c => c.ContentType.Alias == correctAlias)
                            .OrderByDescending(sl => sl.GetValue<DateTime>("generatedDate"))
                            .FirstOrDefault();

                        if (startList != null)
                        {
                            _logger.LogInformation("Found start list {StartListId} under hub (legacy) for competition {CompetitionId}",
                                startList.Id, competitionId);
                        }
                    }
                }

                if (startList == null)
                {
                    _logger.LogInformation("No start list found for competition {CompetitionId}", competitionId);
                    return new List<IContent>();
                }

                _logger.LogInformation("Found start list {StartListId} for competition {CompetitionId}",
                    startList.Id, competitionId);

                // Return single-item list to maintain API compatibility
                return new List<IContent> { startList };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving start lists for competition {CompetitionId}", competitionId);
                return new List<IContent>();
            }
        }

        public int GetTeamCountFromContent(IContent startList)
        {
            try
            {
                var configData = startList.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configData)) return 0;

                var config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                return config?.Teams?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public int GetTotalShootersFromContent(IContent startList)
        {
            try
            {
                var configData = startList.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configData)) return 0;

                var config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                return config?.Teams?.Sum(t => t.Shooters?.Count ?? 0) ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private IEnumerable<IContent> GetDescendantsOfType(IContent content, string contentTypeAlias)
        {
            var result = new List<IContent>();

            if (content.ContentType.Alias == contentTypeAlias)
            {
                result.Add(content);
            }

            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out _);
            foreach (var child in children)
            {
                result.AddRange(GetDescendantsOfType(child, contentTypeAlias));
            }

            return result;
        }

        public IContent? GetOrCreateRegistrationsHub(IContent competition)
        {
            try
            {
                // First, check if a registrations hub already exists
                var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                var existingHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                if (existingHub != null)
                {
                    return existingHub;
                }

                // Check if hub document type exists
                var hubContentType = _contentTypeService.Get("competitionRegistrationsHub");
                if (hubContentType == null)
                {
                    // Create hub as a simple content page if specific type doesn't exist
                    _logger.LogWarning("Document type 'competitionRegistrationsHub' not found, using 'contentPage'");
                    hubContentType = _contentTypeService.Get("contentPage");

                    if (hubContentType == null)
                    {
                        _logger.LogError("No suitable document type found for registrations hub");
                        return competition; // Fall back to creating directly under competition
                    }
                }

                // Create the hub
                var hubName = "Anmälningar";
                var hub = _contentService.Create(hubName, competition, hubContentType.Alias);

                // Set properties if it's a content page
                if (hubContentType.Alias == "contentPage")
                {
                    hub.SetValue("pageTitle", "Anmälningar");
                    hub.SetValue("bodyText", "<p>Alla anmälningar för denna tävling.</p>");
                }
                else
                {
                    // Set hub-specific properties if using proper hub document type
                    hub.SetValue("description", "Alla anmälningar för denna tävling.");
                    hub.SetValue("registrationDeadline", DateTime.Now.AddDays(30)); // Example deadline
                    hub.SetValue("maxParticipants", 100); // Example limit
                }

                var saveResult = _contentService.Save(hub);
                if (saveResult.Success)
                {
                    var publishResult = _contentService.Publish(hub, Array.Empty<string>());
                    if (publishResult.Success)
                    {
                        _logger.LogInformation("Created registrations hub '{HubName}' for competition {CompetitionId}", hubName, competition.Id);
                        return hub;
                    }
                }

                _logger.LogError("Failed to create or publish registrations hub for competition {CompetitionId}", competition.Id);
                return competition; // Fall back to creating directly under competition
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating registrations hub for competition {CompetitionId}", competition.Id);
                return competition; // Fall back to creating directly under competition
            }
        }

        // NOTE: GetOrCreateStartListsHub() REMOVED (2025-11-24)
        // Start lists are now created as DIRECT children of competition (no hub)
        // See START_LIST_ARCHITECTURE_REFACTORING.md for details

        public string GetMemberClub(int memberId)
        {
            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return "Okänd klubb";
                }

                // primaryClubId is stored as a string on the member; parse safely
                var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
                if (!string.IsNullOrWhiteSpace(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId) && primaryClubId > 0)
                {
                    // PERFORMANCE FIX: Clubs are stored as Document Type nodes, use ClubService
                    // (NOT members - previous implementation was incorrect)
                    return _clubService.GetClubNameById(primaryClubId) ?? "Okänd klubb";
                }

                return "Okänd klubb";
            }
            catch
            {
                return "Okänd klubb";
            }
        }

        public static bool IsUnknownClub(string? club)
        {
            if (string.IsNullOrWhiteSpace(club)) return true;
            var normalized = club.Trim();
            return normalized.Equals("Unknown Club", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Okänd klubb", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Okand klubb", StringComparison.OrdinalIgnoreCase);
        }

        public List<CompetitionRegistration> AddClubNames(List<CompetitionRegistration> registrations)
        {
            foreach (var reg in registrations)
            {
                if (string.IsNullOrWhiteSpace(reg.MemberClub))
                {
                    reg.MemberClub = string.IsNullOrWhiteSpace(reg.MemberClub) || IsUnknownClub(reg.MemberClub)
                                ? GetMemberClub(reg.MemberId)
                                : reg.MemberClub;
                }
            }

            return registrations;
        }

        //public async Task<List<PrecisionResultEntry>> GetQualificationResults(int competitionId)
        //{
        //    var competition = _contentService.GetById(competitionId);
        //    var numberOfFinalSeries = competition?.GetValue<int>("numberOfFinalSeries") ?? 0;
        //    var numberOfSeries = competition?.GetValue<int>("numberOfSeriesOrStations") ?? 0;
        //    var qualSeriesCount = numberOfFinalSeries > 0 ? (numberOfSeries - numberOfFinalSeries) : numberOfSeries;

        //    var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
        //    var results = new List<PrecisionResultEntry>();

        //    var resultDocuments = allContent
        //        .Where(c => c.ContentType.Alias == "precisionResult")
        //        .Where(c =>
        //        {
        //            var parentId = c.ParentId;
        //            while (parentId > 0)
        //            {
        //                if (parentId == competitionId)
        //                    return true;
        //                var parent = _contentService.GetById(parentId);
        //                parentId = parent?.ParentId ?? -1;
        //            }
        //            return false;
        //        })
        //        .ToList();

        //    foreach (var doc in resultDocuments)
        //    {
        //        var shooterId = doc.GetValue<int>("shooterId");
        //        var shooterName = doc.GetValue<string>("shooterName") ?? "Unknown";
        //        var shootingClass = doc.GetValue<string>("shootingClass") ?? "Unknown";
        //        var clubName = doc.GetValue<string>("clubName") ?? "Unknown";

        //        var scores = new List<int>();
        //        for (int i = 1; i <= qualSeriesCount; i++)
        //        {
        //            var scoreKey = $"series{i}Score";
        //            var score = doc.GetValue<int?>(scoreKey);
        //            if (score.HasValue)
        //                scores.Add(score.Value);
        //        }

        //        results.Add(new PrecisionResultEntry
        //        {
        //            ShooterId = shooterId,
        //            ShooterName = shooterName,
        //            ShootingClass = shootingClass,
        //            ClubName = clubName,
        //            Scores = scores,
        //            TotalScore = scores.Sum()
        //        });
        //    }

        //    return results.OrderByDescending(r => r.TotalScore).ToList();
        //}

        //public async Task<Dictionary<int, ShooterInfo>> GetShooterInfoDictionary(int competitionId)
        //{
        //    var shooterInfo = new Dictionary<int, ShooterInfo>();
        //    var registrations = await GetCompetitionRegistrations(competitionId);

        //    foreach (var reg in registrations)
        //    {
        //        shooterInfo[reg.MemberId] = new ShooterInfo
        //        {
        //            ShooterId = reg.MemberId,
        //            ShooterName = reg.MemberName,
        //            ShootingClass = reg.MemberClass,
        //            ClubName = reg.MemberClub
        //        };
        //    }

        //    return shooterInfo;
        //}

    }
}
