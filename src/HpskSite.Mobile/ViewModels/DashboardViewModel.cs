using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;
using HpskSite.Shared.DTOs;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// ViewModel for dashboard/statistics page
/// </summary>
public partial class DashboardViewModel : BaseViewModel
{
    private readonly IStatsService _statsService;
    private readonly IAuthService _authService;

    public DashboardViewModel(IStatsService statsService, IAuthService authService)
    {
        _statsService = statsService;
        _authService = authService;
        Title = "Dashboard";

        // Initialize years (current year and last 5 years)
        var currentYear = DateTime.Now.Year;
        Years = new ObservableCollection<int>(Enumerable.Range(currentYear - 5, 6).Reverse());
        SelectedYear = currentYear;
    }

    [ObservableProperty]
    private DashboardStatistics? _statistics;

    [ObservableProperty]
    private ObservableCollection<int> _years;

    [ObservableProperty]
    private int _selectedYear;

    [ObservableProperty]
    private ObservableCollection<PersonalBestsByClass> _personalBests = new();

    [ObservableProperty]
    private ObservableCollection<ActivityEntry> _recentActivity = new();

    [ObservableProperty]
    private ObservableCollection<WeaponClassHandicap> _handicaps = new();

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string? _profilePictureUrl;

    [ObservableProperty]
    private string _userInitials = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isAveragesPopupOpen;

    [ObservableProperty]
    private bool _isBestScoresPopupOpen;

    /// <summary>
    /// Gets the weapon class with the highest average score
    /// </summary>
    public WeaponClassStat? BestAverageWeaponClass => Statistics?.WeaponClassStats
        ?.Where(w => w.Average > 0)
        .OrderByDescending(w => w.Average)
        .FirstOrDefault();

    /// <summary>
    /// Gets the weapon class with the highest best score
    /// </summary>
    public WeaponClassStat? BestScoreWeaponClass => Statistics?.WeaponClassStats
        ?.Where(w => w.BestScore > 0)
        .OrderByDescending(w => w.BestScore)
        .FirstOrDefault();

    partial void OnSelectedYearChanged(int value)
    {
        // Use Task.Run to avoid blocking the UI thread while still handling the async call properly
        Task.Run(async () => await LoadStatisticsAsync());
    }

    partial void OnStatisticsChanged(DashboardStatistics? value)
    {
        // Notify computed properties that depend on Statistics
        OnPropertyChanged(nameof(BestAverageWeaponClass));
        OnPropertyChanged(nameof(BestScoreWeaponClass));
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            IsRefreshing = true;
            HasError = false;

            // Get user info
            var user = _authService.CurrentUser;
            if (user != null)
            {
                UserName = user.FirstName ?? string.Empty;
                ProfilePictureUrl = user.ProfilePictureUrl;

                // Generate initials
                var firstInitial = !string.IsNullOrEmpty(user.FirstName) ? user.FirstName[0].ToString().ToUpper() : "";
                var lastInitial = !string.IsNullOrEmpty(user.LastName) ? user.LastName[0].ToString().ToUpper() : "";
                UserInitials = $"{firstInitial}{lastInitial}";
            }

            await LoadStatisticsAsync();
            await LoadPersonalBestsAsync();
            await LoadHandicapsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsBusy = false;
            IsRefreshing = false;
        }
    }

    private async Task LoadStatisticsAsync()
    {
        try
        {
            var result = await _statsService.GetDashboardStatisticsAsync(SelectedYear);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (result.Success && result.Data != null)
                {
                    Statistics = result.Data;

                    RecentActivity.Clear();
                    foreach (var activity in result.Data.RecentActivity)
                    {
                        RecentActivity.Add(activity);
                    }
                }
                else
                {
                    // Clear data if API returns no results
                    Statistics = null;
                    RecentActivity.Clear();
                }
            });
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ErrorMessage = $"Kunde inte ladda statistik: {ex.Message}";
                HasError = true;
            });
        }
    }

    private async Task LoadPersonalBestsAsync()
    {
        var result = await _statsService.GetPersonalBestsAsync();

        if (result.Success && result.Data != null)
        {
            PersonalBests.Clear();
            foreach (var pb in result.Data)
            {
                PersonalBests.Add(pb);
            }
        }
    }

    private async Task LoadHandicapsAsync()
    {
        try
        {
            var result = await _statsService.GetHandicapProfileAsync();

            if (result.Success && result.Data?.WeaponClasses != null)
            {
                Handicaps.Clear();
                foreach (var h in result.Data.WeaponClasses)
                {
                    Handicaps.Add(h);
                }
            }
        }
        catch
        {
            // Don't let handicap loading failure break the dashboard
        }
    }

    [RelayCommand]
    private async Task NavigateToMatchesAsync()
    {
        await Shell.Current.GoToAsync("//main/matches");
    }

    [RelayCommand]
    private async Task NavigateToHistoryAsync()
    {
        await Shell.Current.GoToAsync("history");
    }

    [RelayCommand]
    private void ShowAveragesPopup()
    {
        IsAveragesPopupOpen = true;
    }

    [RelayCommand]
    private void CloseAveragesPopup()
    {
        IsAveragesPopupOpen = false;
    }

    [RelayCommand]
    private void ShowBestScoresPopup()
    {
        IsBestScoresPopupOpen = true;
    }

    [RelayCommand]
    private void CloseBestScoresPopup()
    {
        IsBestScoresPopupOpen = false;
    }
}
