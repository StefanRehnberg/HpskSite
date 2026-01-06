using HpskSite.Shared.DTOs;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Service for statistics and dashboard data
/// </summary>
public class StatsService : IStatsService
{
    private readonly IApiService _apiService;

    public StatsService(IApiService apiService)
    {
        _apiService = apiService;
    }

    public async Task<ApiResponse<DashboardStatistics>> GetDashboardStatisticsAsync(int? year = null)
    {
        var endpoint = "api/stats/dashboard";
        if (year.HasValue)
        {
            endpoint += $"?year={year}";
        }
        return await _apiService.GetAsync<DashboardStatistics>(endpoint);
    }

    public async Task<ApiResponse<List<PersonalBestsByClass>>> GetPersonalBestsAsync()
    {
        return await _apiService.GetAsync<List<PersonalBestsByClass>>("api/stats/personal-bests");
    }

    public async Task<ApiResponse<PagedResponse<UnifiedResultEntry>>> GetResultsAsync(
        int page = 1,
        int pageSize = 20,
        string? weaponClass = null,
        string? sourceType = null)
    {
        var endpoint = $"api/stats/results?page={page}&pageSize={pageSize}";

        if (!string.IsNullOrEmpty(weaponClass))
        {
            endpoint += $"&weaponClass={Uri.EscapeDataString(weaponClass)}";
        }

        if (!string.IsNullOrEmpty(sourceType))
        {
            endpoint += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        }

        return await _apiService.GetAsync<PagedResponse<UnifiedResultEntry>>(endpoint);
    }

    public async Task<ApiResponse<ProgressChartData>> GetProgressChartAsync(int? year = null, string? weaponClass = null)
    {
        var endpoint = "api/stats/progress-chart";
        var queryParams = new List<string>();

        if (year.HasValue)
        {
            queryParams.Add($"year={year}");
        }

        if (!string.IsNullOrEmpty(weaponClass))
        {
            queryParams.Add($"weaponClass={Uri.EscapeDataString(weaponClass)}");
        }

        if (queryParams.Any())
        {
            endpoint += "?" + string.Join("&", queryParams);
        }

        return await _apiService.GetAsync<ProgressChartData>(endpoint);
    }

    public async Task<ApiResponse<HandicapProfile>> GetHandicapProfileAsync()
    {
        return await _apiService.GetAsync<HandicapProfile>("api/stats/handicap");
    }
}

public interface IStatsService
{
    Task<ApiResponse<DashboardStatistics>> GetDashboardStatisticsAsync(int? year = null);
    Task<ApiResponse<List<PersonalBestsByClass>>> GetPersonalBestsAsync();
    Task<ApiResponse<PagedResponse<UnifiedResultEntry>>> GetResultsAsync(
        int page = 1,
        int pageSize = 20,
        string? weaponClass = null,
        string? sourceType = null);
    Task<ApiResponse<ProgressChartData>> GetProgressChartAsync(int? year = null, string? weaponClass = null);
    Task<ApiResponse<HandicapProfile>> GetHandicapProfileAsync();
}
