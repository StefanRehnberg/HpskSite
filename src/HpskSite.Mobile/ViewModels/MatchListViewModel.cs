using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;
using HpskSite.Shared.DTOs;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// ViewModel for match list page with 3 tabs: Current, Active, History
/// </summary>
public partial class MatchListViewModel : BaseViewModel
{
    private readonly IMatchService _matchService;
    private readonly ISignalRService _signalRService;
    private readonly IAuthService _authService;

    public MatchListViewModel(IMatchService matchService, ISignalRService signalRService, IAuthService authService)
    {
        _matchService = matchService;
        _signalRService = signalRService;
        _authService = authService;
        Title = "Matcher";

        _signalRService.MatchCreated += OnMatchCreated;
        _signalRService.MatchDeleted += OnMatchDeleted;
    }

    // Orientation tracking for landscape-specific UI (set by code-behind)
    [ObservableProperty]
    private bool _isLandscape;

    // Tab selection (0 = Current, 1 = Active, 2 = History)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsActiveTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTabSelected))]
    private int _selectedTabIndex = 0;

    public bool IsCurrentTabSelected => SelectedTabIndex == 0;
    public bool IsActiveTabSelected => SelectedTabIndex == 1;
    public bool IsHistoryTabSelected => SelectedTabIndex == 2;

    // Current match (user's active match if they have one)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentMatch))]
    [NotifyPropertyChangedFor(nameof(ShowNoMatchOptions))]
    private TrainingMatch? _currentMatch;

    public bool HasCurrentMatch => CurrentMatch != null;

    // Track if we've completed loading current match data
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoMatchOptions))]
    private bool _hasLoadedCurrentMatch;

    // Show no-match options only after loading completes and no match found
    public bool ShowNoMatchOptions => HasLoadedCurrentMatch && !HasCurrentMatch;

    // Active matches (ongoing + upcoming)
    [ObservableProperty]
    private ObservableCollection<TrainingMatch> _activeMatches = new();

    // Ongoing matches (HasStarted = true)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOngoingMatches))]
    private ObservableCollection<TrainingMatch> _ongoingMatches = new();

    public bool HasOngoingMatches => OngoingMatches.Count > 0;

    // Upcoming matches (HasStarted = false)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpcomingMatches))]
    private ObservableCollection<TrainingMatch> _upcomingMatches = new();

    public bool HasUpcomingMatches => UpcomingMatches.Count > 0;

    // Legacy - keeping for compatibility
    [ObservableProperty]
    private ObservableCollection<TrainingMatch> _myMatches = new();

    // Match history
    [ObservableProperty]
    private ObservableCollection<MatchHistoryItem> _matchHistory = new();

    [ObservableProperty]
    private bool _hasMoreHistory = true;

    [ObservableProperty]
    private bool _isLoadingHistory;

    private int _historyPage = 1;
    private const int HistoryPageSize = 20;

    // History filter properties
    [ObservableProperty]
    private bool _isFilterPanelOpen;

    [ObservableProperty]
    private string? _filterWeaponClass;

    [ObservableProperty]
    private bool _filterMyMatchesOnly;

    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddMonths(-3);

    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today;

    [ObservableProperty]
    private string _filterSearchName = string.Empty;

    // Weapon class options for picker
    public List<string> WeaponClassOptions { get; } = new() { "", "A", "B", "C", "R", "P" };

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [RelayCommand]
    private async Task LoadMatchesAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            IsRefreshing = true;
            HasError = false;

            // Connect to SignalR if not already connected (non-blocking - don't fail if SignalR is unavailable)
            if (!_signalRService.IsConnected)
            {
                try
                {
                    await _signalRService.ConnectAsync();
                }
                catch (Exception signalREx)
                {
                    // SignalR connection failed - continue without real-time updates
                    System.Diagnostics.Debug.WriteLine($"SignalR connection failed: {signalREx.Message}");
                }
            }

            // Load active matches (ongoing + upcoming)
            var activeResult = await _matchService.GetActiveMatchesAsync();
            if (activeResult.Success && activeResult.Data != null)
            {
                ActiveMatches.Clear();
                OngoingMatches.Clear();
                UpcomingMatches.Clear();

                foreach (var match in activeResult.Data)
                {
                    ActiveMatches.Add(match);

                    // Separate into ongoing and upcoming
                    if (match.HasStarted)
                    {
                        OngoingMatches.Add(match);
                    }
                    else
                    {
                        UpcomingMatches.Add(match);
                    }
                }

                // Notify that the counts have changed
                OnPropertyChanged(nameof(HasOngoingMatches));
                OnPropertyChanged(nameof(HasUpcomingMatches));
            }

            // Load my matches and find current active match
            var myResult = await _matchService.GetMyMatchesAsync();
            if (myResult.Success && myResult.Data != null)
            {
                MyMatches.Clear();
                CurrentMatch = null;

                foreach (var match in myResult.Data)
                {
                    MyMatches.Add(match);
                    // Set first match as current (user's active match)
                    if (CurrentMatch == null)
                    {
                        CurrentMatch = match;
                    }
                }
            }

            // Load initial history if on history tab
            if (IsHistoryTabSelected && MatchHistory.Count == 0)
            {
                await LoadHistoryAsync();
            }
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
            HasLoadedCurrentMatch = true;
        }
    }

    [RelayCommand]
    private void SelectTab(int tabIndex)
    {
        SelectedTabIndex = tabIndex;

        // Load history when switching to history tab for the first time
        if (tabIndex == 2 && MatchHistory.Count == 0 && !IsLoadingHistory)
        {
            _ = LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (IsLoadingHistory || !HasMoreHistory)
            return;

        try
        {
            IsLoadingHistory = true;

            var result = await _matchService.GetMatchHistoryAsync(
                _historyPage,
                HistoryPageSize,
                string.IsNullOrEmpty(FilterWeaponClass) ? null : FilterWeaponClass,
                FilterDateFrom,
                FilterDateTo,
                string.IsNullOrEmpty(FilterSearchName) ? null : FilterSearchName,
                FilterMyMatchesOnly);

            if (result.Success && result.Data != null)
            {
                // Check admin status and set CanDelete for each match
                var isAdmin = _authService.CurrentUser?.IsAdmin ?? false;
                foreach (var match in result.Data.Items)
                {
                    match.CanDelete = match.IsCreator || isAdmin;
                    MatchHistory.Add(match);
                }

                HasMoreHistory = result.Data.HasNextPage;
                _historyPage++;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    [RelayCommand]
    private void ToggleFilterPanel()
    {
        IsFilterPanelOpen = !IsFilterPanelOpen;
    }

    [RelayCommand]
    private async Task ApplyFiltersAsync()
    {
        _historyPage = 1;
        MatchHistory.Clear();
        HasMoreHistory = true;
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        FilterWeaponClass = null;
        FilterMyMatchesOnly = false;
        FilterSearchName = string.Empty;
        // Reset to default 3-month range
        FilterDateTo = DateTime.Today;
        FilterDateFrom = DateTime.Today.AddMonths(-3);
        await ApplyFiltersAsync();
    }

    [RelayCommand]
    private async Task ViewHistoryMatchAsync(MatchHistoryItem match)
    {
        if (match == null)
            return;

        // Use existing spectator mode
        await Shell.Current.GoToAsync($"activeMatch?code={match.MatchCode}&spectator=true");
    }

    [RelayCommand]
    private async Task DeleteHistoryMatchAsync(MatchHistoryItem match)
    {
        if (match == null || !match.CanDelete)
            return;

        var confirm = await Shell.Current.DisplayAlert(
            "Radera match",
            $"Vill du verkligen radera matchen \"{match.DisplayName}\"?",
            "Ja, radera",
            "Avbryt");

        if (!confirm)
            return;

        try
        {
            var result = await _matchService.DeleteMatchAsync(match.MatchCode);
            if (result.Success)
            {
                MatchHistory.Remove(match);
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte radera match";
                HasError = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
    }

    [RelayCommand]
    private async Task ContinueMatchAsync()
    {
        if (CurrentMatch == null)
            return;

        await Shell.Current.GoToAsync($"activeMatch?code={CurrentMatch.MatchCode}");
    }

    [RelayCommand]
    private async Task NavigateToCreateMatchAsync()
    {
        await Shell.Current.GoToAsync("createMatch");
    }

    [RelayCommand]
    private async Task NavigateToJoinMatchAsync()
    {
        await Shell.Current.GoToAsync("joinMatch");
    }

    [RelayCommand]
    private async Task NavigateToMatchAsync(TrainingMatch match)
    {
        if (match == null)
            return;

        await Shell.Current.GoToAsync($"activeMatch?code={match.MatchCode}");
    }

    [RelayCommand]
    private async Task ViewMatchAsync(TrainingMatch match)
    {
        if (match == null)
            return;

        // Navigate to match in spectator mode
        await Shell.Current.GoToAsync($"activeMatch?code={match.MatchCode}&spectator=true");
    }

    // Valid shooter classes (must match server)
    private static readonly string[] ShooterClasses = new[]
    {
        "Klass 1 - Nybörjare",
        "Klass 2 - Guldmärkesskytt",
        "Klass 3 - Riksmästare"
    };

    [RelayCommand]
    private async Task JoinMatchAsync(TrainingMatch match)
    {
        if (match == null)
            return;

        try
        {
            HasError = false;
            var userId = _authService.CurrentUser?.MemberId;

            // Check if user is already a participant
            var isParticipant = userId.HasValue &&
                match.Participants.Any(p => p.MemberId == userId.Value);

            if (isParticipant)
            {
                // Already a participant - go directly to match (like "Fortsätt match")
                await Shell.Current.GoToAsync($"activeMatch?code={match.MatchCode}");
                return;
            }

            // Not a participant - check if match is open
            if (match.IsOpen)
            {
                // Open match - join directly
                var result = await _matchService.JoinMatchAsync(match.MatchCode);
                if (result.Success)
                {
                    await Shell.Current.GoToAsync($"activeMatch?code={match.MatchCode}");
                }
                else if (result.NeedsShooterClass)
                {
                    // User needs to set shooter class for handicap match
                    await HandleShooterClassRequiredAsync(match.MatchCode);
                }
                else
                {
                    ErrorMessage = result.Message ?? "Kunde inte gå med i matchen";
                    HasError = true;
                }
            }
            else
            {
                // Private match - send join request
                var result = await _matchService.RequestJoinMatchAsync(match.MatchCode);
                if (result.Success)
                {
                    await Shell.Current.DisplayAlert("Förfrågan skickad",
                        "Din förfrågan har skickats till matchvärden", "OK");
                }
                else
                {
                    ErrorMessage = result.Message ?? "Kunde inte skicka förfrågan";
                    HasError = true;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
    }

    /// <summary>
    /// Handles the case where user needs to set shooter class before joining a handicap match
    /// </summary>
    private async Task HandleShooterClassRequiredAsync(string matchCode)
    {
        // Show picker dialog
        var selectedClass = await Shell.Current.DisplayActionSheet(
            "Välj din skytteklass",
            "Avbryt",
            null,
            ShooterClasses);

        if (string.IsNullOrEmpty(selectedClass) || selectedClass == "Avbryt")
        {
            ErrorMessage = "Du måste välja en skytteklass för att gå med i en handicapmatch";
            HasError = true;
            return;
        }

        // Save the shooter class
        var setClassResult = await _matchService.SetShooterClassAsync(selectedClass);

        if (!setClassResult.Success)
        {
            ErrorMessage = setClassResult.Message ?? "Kunde inte spara skytteklass";
            HasError = true;
            return;
        }

        // Now retry joining the match
        var joinResult = await _matchService.JoinMatchAsync(matchCode);

        if (joinResult.Success)
        {
            await Shell.Current.GoToAsync($"activeMatch?code={matchCode}");
        }
        else
        {
            ErrorMessage = joinResult.Message ?? "Kunde inte gå med i matchen";
            HasError = true;
        }
    }

    private void OnMatchCreated(object? sender, TrainingMatch match)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!ActiveMatches.Any(m => m.MatchCode == match.MatchCode))
            {
                ActiveMatches.Insert(0, match);

                // Add to appropriate collection
                if (match.HasStarted)
                {
                    if (!OngoingMatches.Any(m => m.MatchCode == match.MatchCode))
                    {
                        OngoingMatches.Insert(0, match);
                        OnPropertyChanged(nameof(HasOngoingMatches));
                    }
                }
                else
                {
                    if (!UpcomingMatches.Any(m => m.MatchCode == match.MatchCode))
                    {
                        UpcomingMatches.Insert(0, match);
                        OnPropertyChanged(nameof(HasUpcomingMatches));
                    }
                }
            }
        });
    }

    private void OnMatchDeleted(object? sender, string matchCode)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var match = ActiveMatches.FirstOrDefault(m => m.MatchCode == matchCode);
            if (match != null)
            {
                ActiveMatches.Remove(match);
            }

            // Remove from ongoing
            var ongoingMatch = OngoingMatches.FirstOrDefault(m => m.MatchCode == matchCode);
            if (ongoingMatch != null)
            {
                OngoingMatches.Remove(ongoingMatch);
                OnPropertyChanged(nameof(HasOngoingMatches));
            }

            // Remove from upcoming
            var upcomingMatch = UpcomingMatches.FirstOrDefault(m => m.MatchCode == matchCode);
            if (upcomingMatch != null)
            {
                UpcomingMatches.Remove(upcomingMatch);
                OnPropertyChanged(nameof(HasUpcomingMatches));
            }
        });
    }
}
