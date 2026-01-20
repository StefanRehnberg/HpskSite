using HpskSite.Shared.DTOs;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Service for training match operations
/// </summary>
public class MatchService : IMatchService
{
    private readonly IApiService _apiService;

    public MatchService(IApiService apiService)
    {
        _apiService = apiService;
    }

    public async Task<ApiResponse<TrainingMatch>> CreateMatchAsync(CreateMatchRequest request)
    {
        return await _apiService.PostAsync<TrainingMatch>("api/match", request);
    }

    public async Task<ApiResponse<TrainingMatch>> GetMatchAsync(string matchCode)
    {
        return await _apiService.GetAsync<TrainingMatch>($"api/match/{matchCode}");
    }

    public async Task<ApiResponse<List<TrainingMatch>>> GetActiveMatchesAsync()
    {
        // Returns ALL active matches (for "Active" tab display)
        return await _apiService.GetAsync<List<TrainingMatch>>("api/match/active");
    }

    public async Task<ApiResponse<List<TrainingMatch>>> GetMyMatchesAsync()
    {
        // Returns only matches where current user is a participant
        return await _apiService.GetAsync<List<TrainingMatch>>("api/match/ongoing");
    }

    public async Task<ApiResponse<TrainingMatch>> JoinMatchAsync(string matchCode)
    {
        return await _apiService.PostAsync<TrainingMatch>($"api/match/{matchCode}/join");
    }

    public async Task<ApiResponse> LeaveMatchAsync(string matchCode)
    {
        return await _apiService.PostAsync($"api/match/{matchCode}/leave");
    }

    public async Task<ApiResponse> CompleteMatchAsync(string matchCode)
    {
        return await _apiService.PostAsync($"api/match/{matchCode}/complete");
    }

    public async Task<ApiResponse> UpdateMatchSettingsAsync(string matchCode, int? maxSeriesCount)
    {
        return await _apiService.PostAsync($"api/match/{matchCode}/settings", new { MaxSeriesCount = maxSeriesCount });
    }

    public async Task<ApiResponse> DeleteMatchAsync(string matchCode)
    {
        return await _apiService.DeleteAsync($"api/match/{matchCode}");
    }

    public async Task<ApiResponse> SaveScoreAsync(string matchCode, SaveScoreRequest request)
    {
        return await _apiService.PostAsync($"api/match/{matchCode}/score", request);
    }

    public async Task<ApiResponse<PagedResponse<MatchHistoryItem>>> GetMatchHistoryAsync(
        int page = 1,
        int pageSize = 20,
        string? weaponClass = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? searchName = null,
        bool myMatchesOnly = false)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };

        if (!string.IsNullOrEmpty(weaponClass))
            queryParams.Add($"weaponClass={weaponClass}");
        if (dateFrom.HasValue)
            queryParams.Add($"dateFrom={dateFrom:yyyy-MM-dd}");
        if (dateTo.HasValue)
            queryParams.Add($"dateTo={dateTo:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(searchName))
            queryParams.Add($"searchName={Uri.EscapeDataString(searchName)}");
        if (myMatchesOnly)
            queryParams.Add("myMatchesOnly=true");

        return await _apiService.GetAsync<PagedResponse<MatchHistoryItem>>(
            $"api/match/history?{string.Join("&", queryParams)}");
    }

    public async Task<ApiResponse<List<TrainingMatch>>> GetAllActiveMatchesAsync()
    {
        return await _apiService.GetAsync<List<TrainingMatch>>("api/match/active");
    }

    public async Task<ApiResponse<SpectatorMatchResponse>> ViewMatchAsSpectatorAsync(string matchCode)
    {
        return await _apiService.GetAsync<SpectatorMatchResponse>($"api/match/{matchCode}/spectator");
    }

    public async Task<ApiResponse> RequestJoinMatchAsync(string matchCode)
    {
        return await _apiService.PostAsync($"api/match/{matchCode}/request-join");
    }

    public async Task<ApiResponse> RespondToJoinRequestAsync(int requestId, string action)
    {
        return await _apiService.PostAsync("api/match/respond-join", new { RequestId = requestId, Action = action });
    }

    public async Task<ApiResponse<SetShooterClassResponse>> SetShooterClassAsync(string shooterClass)
    {
        return await _apiService.PostAsync<SetShooterClassResponse>("api/match/set-shooter-class", new { ShooterClass = shooterClass });
    }

    public async Task<ApiResponse<SetShooterClassResponse>> GetShooterClassAsync()
    {
        return await _apiService.GetAsync<SetShooterClassResponse>("api/match/shooter-class");
    }

    public async Task<ApiResponse<UploadPhotoResponse>> UploadSeriesPhotoAsync(string matchCode, int seriesNumber, byte[] photoData)
    {
        var fileName = $"target_{DateTime.Now:yyyyMMddHHmmss}.jpg";
        return await _apiService.PostMultipartAsync<UploadPhotoResponse>(
            $"api/match/{matchCode}/series/{seriesNumber}/photo",
            photoData,
            fileName);
    }

    public async Task<ApiResponse<List<PhotoReaction>>> AddReactionAsync(string matchCode, int seriesNumber, int targetMemberId, string emoji)
    {
        return await _apiService.PostAsync<List<PhotoReaction>>(
            $"api/match/{matchCode}/series/{seriesNumber}/reaction",
            new { TargetMemberId = targetMemberId, Emoji = emoji });
    }

    public async Task<ApiResponse<AddGuestResponse>> AddGuestAsync(string matchCode, AddGuestRequest request)
    {
        return await _apiService.PostAsync<AddGuestResponse>($"api/match/{matchCode}/guest/add", request);
    }

    public async Task<ApiResponse> RemoveGuestAsync(string matchCode, int guestId)
    {
        return await _apiService.DeleteAsync($"api/match/{matchCode}/guest/{guestId}");
    }

    public async Task<ApiResponse<RegenerateGuestQrResponse>> RegenerateGuestQrAsync(string matchCode, int guestId)
    {
        return await _apiService.PostAsync<RegenerateGuestQrResponse>($"api/match/{matchCode}/guest/{guestId}/regenerate-qr");
    }

    public async Task<ApiResponse> UpdateMatchSettingsWithGuestsAsync(string matchCode, int? maxSeriesCount, bool? allowGuests)
    {
        return await _apiService.PostAsync($"api/match/{matchCode}/settings", new { MaxSeriesCount = maxSeriesCount, AllowGuests = allowGuests });
    }
}

/// <summary>
/// Response for target photo upload
/// </summary>
public class UploadPhotoResponse
{
    public string PhotoUrl { get; set; } = string.Empty;
}

public class CreateMatchRequest
{
    public string? MatchName { get; set; }
    public string WeaponClass { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public bool IsOpen { get; set; } = true;
    public bool HasHandicap { get; set; }
}

public class SaveScoreRequest
{
    public int SeriesNumber { get; set; }
    public int Total { get; set; }
    public int XCount { get; set; }
    public List<string>? Shots { get; set; }
    public string? EntryMethod { get; set; }
}

public interface IMatchService
{
    Task<ApiResponse<TrainingMatch>> CreateMatchAsync(CreateMatchRequest request);
    Task<ApiResponse<TrainingMatch>> GetMatchAsync(string matchCode);
    Task<ApiResponse<List<TrainingMatch>>> GetActiveMatchesAsync();
    Task<ApiResponse<List<TrainingMatch>>> GetMyMatchesAsync();
    Task<ApiResponse<TrainingMatch>> JoinMatchAsync(string matchCode);
    Task<ApiResponse> LeaveMatchAsync(string matchCode);
    Task<ApiResponse> CompleteMatchAsync(string matchCode);
    Task<ApiResponse> UpdateMatchSettingsAsync(string matchCode, int? maxSeriesCount);
    Task<ApiResponse> DeleteMatchAsync(string matchCode);
    Task<ApiResponse> SaveScoreAsync(string matchCode, SaveScoreRequest request);
    Task<ApiResponse<PagedResponse<MatchHistoryItem>>> GetMatchHistoryAsync(
        int page = 1,
        int pageSize = 20,
        string? weaponClass = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? searchName = null,
        bool myMatchesOnly = false);
    Task<ApiResponse<List<TrainingMatch>>> GetAllActiveMatchesAsync();
    Task<ApiResponse<SpectatorMatchResponse>> ViewMatchAsSpectatorAsync(string matchCode);
    Task<ApiResponse> RequestJoinMatchAsync(string matchCode);
    Task<ApiResponse> RespondToJoinRequestAsync(int requestId, string action);
    Task<ApiResponse<SetShooterClassResponse>> SetShooterClassAsync(string shooterClass);
    Task<ApiResponse<SetShooterClassResponse>> GetShooterClassAsync();
    Task<ApiResponse<UploadPhotoResponse>> UploadSeriesPhotoAsync(string matchCode, int seriesNumber, byte[] photoData);
    Task<ApiResponse<List<PhotoReaction>>> AddReactionAsync(string matchCode, int seriesNumber, int targetMemberId, string emoji);
    Task<ApiResponse<AddGuestResponse>> AddGuestAsync(string matchCode, AddGuestRequest request);
    Task<ApiResponse> RemoveGuestAsync(string matchCode, int guestId);
    Task<ApiResponse<RegenerateGuestQrResponse>> RegenerateGuestQrAsync(string matchCode, int guestId);
    Task<ApiResponse> UpdateMatchSettingsWithGuestsAsync(string matchCode, int? maxSeriesCount, bool? allowGuests);
}

/// <summary>
/// Response for shooter class operations
/// </summary>
public class SetShooterClassResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? ShooterClass { get; set; }
}

/// <summary>
/// Response for viewing a match as spectator
/// </summary>
public class SpectatorMatchResponse
{
    public TrainingMatch Match { get; set; } = null!;
    public bool IsSpectator { get; set; }
    public bool IsParticipant { get; set; }
    public bool CanJoin { get; set; }
}

/// <summary>
/// DTO for match history items with user-specific data
/// </summary>
public class MatchHistoryItem
{
    public int Id { get; set; }
    public string MatchCode { get; set; } = string.Empty;
    public string? MatchName { get; set; }
    public string WeaponClass { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public int ParticipantCount { get; set; }
    public List<MatchHistoryParticipant> Participants { get; set; } = new();

    // User-specific fields
    public bool IsCreator { get; set; }
    public bool IsParticipant { get; set; }
    public int? UserScore { get; set; }
    public int? UserSeriesCount { get; set; }
    public int? UserRanking { get; set; }

    // Computed properties
    public string DisplayName => !string.IsNullOrWhiteSpace(MatchName) ? MatchName : MatchCode;
    public string PlacementBadge => UserRanking switch
    {
        1 => "🥇",
        2 => "🥈",
        3 => "🥉",
        int n => $"{n}:e",
        _ => ""
    };

    // Avatar display properties (max 4 avatars + overflow badge)
    public List<MatchHistoryParticipant> DisplayedParticipants => Participants.Take(4).ToList();
    public int ExtraParticipantCount => Math.Max(0, ParticipantCount - 4);
    public bool HasExtraParticipants => ExtraParticipantCount > 0;
    public string ExtraParticipantsBadge => $"+{ExtraParticipantCount}";

    // For delete button visibility (set by ViewModel)
    public bool CanDelete { get; set; }
}

/// <summary>
/// Request to add a guest participant to a training match
/// </summary>
public class AddGuestRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? HandicapClass { get; set; }
    public string? Email { get; set; }
    public int? ClubId { get; set; }
}

/// <summary>
/// Response after adding a guest participant to a training match
/// </summary>
public class AddGuestResponse
{
    public int GuestId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ClaimUrl { get; set; } = string.Empty;
    public DateTime ClaimExpiresAt { get; set; }
    public bool InviteSent { get; set; }
    public int? PendingMemberId { get; set; }
    public int ParticipantId { get; set; }
}

/// <summary>
/// Response for regenerating a guest's QR code (claim token)
/// </summary>
public class RegenerateGuestQrResponse
{
    public int GuestId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ClaimUrl { get; set; } = string.Empty;
    public DateTime ClaimExpiresAt { get; set; }
}

/// <summary>
/// Participant info for match history display
/// </summary>
public class MatchHistoryParticipant
{
    public int MemberId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string Initials => $"{FirstName?.FirstOrDefault()}{LastName?.FirstOrDefault()}".ToUpper();

    /// <summary>
    /// Platform-adjusted URL for loading images (fixes localhost and SSL for Android emulator)
    /// </summary>
    public string? AdjustedProfilePictureUrl
    {
        get
        {
            if (string.IsNullOrEmpty(ProfilePictureUrl))
                return null;

#if DEBUG
            if (DeviceInfo.Platform == DevicePlatform.Android)
            {
                // Replace localhost with 10.0.2.2 and use HTTP (Android Image control can't bypass SSL)
                return ProfilePictureUrl
                    .Replace("https://localhost:", "http://10.0.2.2:")
                    .Replace("http://localhost:", "http://10.0.2.2:")
                    .Replace("https://127.0.0.1:", "http://10.0.2.2:")
                    .Replace("http://127.0.0.1:", "http://10.0.2.2:");
            }
#endif
            return ProfilePictureUrl;
        }
    }
}
