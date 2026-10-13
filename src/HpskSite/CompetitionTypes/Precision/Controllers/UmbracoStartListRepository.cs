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
        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;
        private readonly HpskSite.Services.StartLists.IStartListStore _startLists;

        public UmbracoStartListRepository(IMemberService memberService, IContentService contentService, IContentTypeService contentTypeService, ILogger<UmbracoStartListRepository> logger, ClubService clubService,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations,
            HpskSite.Services.StartLists.IStartListStore startLists)
        {
            _startLists = startLists;
            _memberService = memberService;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _logger = logger;
            _clubService = clubService;
            _registrations = registrations;
        }

        public async Task<List<CompetitionRegistration>> GetCompetitionRegistrations(int competitionId)
        {
            try
            {
                // Anmälningarna går via lagringen (sajtstrukturen fas 1a) — aldrig via noderna direkt.
                var stored = _registrations.List(competitionId);
                _logger.LogInformation($"Found {stored.Count} registrations for competition {competitionId}");

                // Batch load all members (sequential - IMemberService is NOT thread-safe).
                // Bara de som behövs: medlemmens huvudklubb är sista reserven för klubbnamnet.
                var memberDict = new Dictionary<int, IMember>();
                foreach (var memberId in stored
                    .Where(r => r.ClubId <= 0 && string.IsNullOrWhiteSpace(r.LegacyClubText) && r.MemberId > 0)
                    .Select(r => r.MemberId).Distinct())
                {
                    try
                    {
                        var member = _memberService.GetById(memberId);
                        if (member != null) memberDict[memberId] = member;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error loading member {MemberId}", memberId);
                    }
                }

                var registrations = new List<CompetitionRegistration>();
                int registrationId = 1;

                foreach (var reg in stored)
                {
                    var memberName = string.IsNullOrWhiteSpace(reg.MemberName) ? "Unknown Member" : reg.MemberName;

                    // Klubbnamnet: anmälans klubb, sedan den äldre memberClub-texten, sedan
                    // medlemmens huvudklubb.
                    string clubName = "Okänd klubb";
                    if (reg.ClubId > 0)
                    {
                        clubName = _clubService.GetClubNameById(reg.ClubId) ?? $"Club {reg.ClubId}";
                    }
                    else if (!string.IsNullOrWhiteSpace(reg.LegacyClubText))
                    {
                        clubName = int.TryParse(reg.LegacyClubText, out var legacyClubId)
                            ? _clubService.GetClubNameById(legacyClubId) ?? $"Club {legacyClubId}"
                            : reg.LegacyClubText.Trim();
                    }
                    else if (reg.MemberId > 0 && memberDict.TryGetValue(reg.MemberId, out var member))
                    {
                        var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                            clubName = _clubService.GetClubNameById(primaryClubId) ?? $"Club {primaryClubId}";
                    }

                    // One CompetitionRegistration per class.
                    foreach (var classEntry in reg.Classes)
                    {
                        registrations.Add(new CompetitionRegistration
                        {
                            Id = registrationId++,
                            MemberId = reg.MemberId,
                            CompetitionId = competitionId,
                            MemberName = memberName,
                            MemberClass = classEntry.Class,
                            MemberClub = clubName,
                            RegistrationDate = reg.RegistrationDate,
                            IsActive = true,
                            // Carried so start-list generators can honour an early/late wish.
                            // Per CLASS, which is how the wish is stored and how it is asked for.
                            StartPreference = classEntry.StartPreference ?? "",
                            IsSubCompetition = reg.IsSubCompetition
                        });
                    }
                }

                return await Task.FromResult(registrations);
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

        /// <summary>
        /// Precisionsfamiljens grundomgångslista: tävlingens första direkta, annars (äldre tävlingar
        /// under hubben) den senast genererade. En lista eller ingen — samma regel som alltid, nu ur
        /// <c>IStartListStore</c> (skalplanen C2).
        /// </summary>
        public List<HpskSite.Models.StartLists.StartListRecord> GetStartListsForCompetition(int competitionId)
        {
            try
            {
                var primary = _startLists.Primary(competitionId);
                return primary == null ? new List<HpskSite.Models.StartLists.StartListRecord>() : new List<HpskSite.Models.StartLists.StartListRecord> { primary };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving start lists for competition {CompetitionId}", competitionId);
                return new List<HpskSite.Models.StartLists.StartListRecord>();
            }
        }

        public int GetTeamCountFromContent(HpskSite.Models.StartLists.StartListRecord startList)
        {
            try
            {
                var configData = startList.ConfigurationData;
                if (string.IsNullOrEmpty(configData)) return 0;

                var config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                return config?.Teams?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public int GetTotalShootersFromContent(HpskSite.Models.StartLists.StartListRecord startList)
        {
            try
            {
                var configData = startList.ConfigurationData;
                if (string.IsNullOrEmpty(configData)) return 0;

                var config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                return config?.Teams?.Sum(t => t.Shooters?.Count ?? 0) ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public int GetUniqueShootersFromContent(HpskSite.Models.StartLists.StartListRecord startList)
        {
            try
            {
                var configData = startList.ConfigurationData;
                if (string.IsNullOrEmpty(configData)) return 0;

                var config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                return config?.Teams?
                    .SelectMany(t => t.Shooters ?? Enumerable.Empty<StartListShooter>())
                    .Select(s => s.MemberId)
                    .Where(id => id > 0)
                    .Distinct()
                    .Count() ?? 0;
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

        // GetOrCreateRegistrationsHub() BORTTAGEN 2026-10-09 (hade inga anropare): anmälningshubben
        // ägs av NodeRegistrationStore (sajtstrukturen fas 1a).

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
