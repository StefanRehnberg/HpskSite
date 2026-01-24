using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// ViewModel for creating a new match
/// </summary>
public partial class CreateMatchViewModel : BaseViewModel
{
    private readonly IMatchService _matchService;

    public CreateMatchViewModel(IMatchService matchService)
    {
        _matchService = matchService;
        Title = "Skapa match";

        // Initialize weapon classes
        WeaponClasses = new ObservableCollection<string> { "A", "B", "C", "R", "M", "L" };
        SelectedWeaponClass = "C";

        // Initialize team options
        MaxShootersOptions = new ObservableCollection<int> { 2, 3, 4, 5, 6, 8, 10 };
        TeamCountOptions = new ObservableCollection<int> { 2, 3, 4 };

        // Initialize club list with "No club" option
        AvailableClubs = new ObservableCollection<ClubPickerItem>
        {
            new ClubPickerItem { Id = null, Name = "Ingen klubb" }
        };

        // Load clubs asynchronously
        _ = LoadClubsAsync();
    }

    private async Task LoadClubsAsync()
    {
        try
        {
            var result = await _matchService.GetClubsAsync();
            if (result.Success && result.Data != null)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var club in result.Data.OrderBy(c => c.Name))
                    {
                        AvailableClubs.Add(new ClubPickerItem { Id = club.Id, Name = club.Name });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load clubs: {ex.Message}");
        }
    }

    // Options for pickers
    public ObservableCollection<int> MaxShootersOptions { get; }
    public ObservableCollection<int> TeamCountOptions { get; }
    public ObservableCollection<ClubPickerItem> AvailableClubs { get; }

    // Computed properties for team name visibility
    public bool ShowTeam3 => TeamCount >= 3;
    public bool ShowTeam4 => TeamCount >= 4;

    [ObservableProperty]
    private string? _matchName;

    [ObservableProperty]
    private ObservableCollection<string> _weaponClasses;

    [ObservableProperty]
    private string _selectedWeaponClass;

    [ObservableProperty]
    private DateTime? _startDate;

    [ObservableProperty]
    private TimeSpan? _startTime;

    [ObservableProperty]
    private bool _isOpen = true;

    [ObservableProperty]
    private bool _hasHandicap = true;

    [ObservableProperty]
    private bool _isTeamMatch;

    [ObservableProperty]
    private int _maxShootersPerTeam = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTeam3))]
    [NotifyPropertyChangedFor(nameof(ShowTeam4))]
    private int _teamCount = 2;

    [ObservableProperty]
    private string _team1Name = string.Empty;

    [ObservableProperty]
    private ClubPickerItem? _team1Club;

    [ObservableProperty]
    private string _team2Name = string.Empty;

    [ObservableProperty]
    private ClubPickerItem? _team2Club;

    [ObservableProperty]
    private string _team3Name = string.Empty;

    [ObservableProperty]
    private ClubPickerItem? _team3Club;

    [ObservableProperty]
    private string _team4Name = string.Empty;

    [ObservableProperty]
    private ClubPickerItem? _team4Club;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [RelayCommand]
    private async Task CreateMatchAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            HasError = false;

            // Combine date and time if both are set, otherwise use Now
            DateTime combinedStartDate;
            if (StartDate.HasValue)
            {
                combinedStartDate = StartDate.Value.Date;
                if (StartTime.HasValue)
                {
                    combinedStartDate = combinedStartDate.Add(StartTime.Value);
                }
            }
            else
            {
                // No start date set - start immediately
                combinedStartDate = DateTime.Now;
            }

            var request = new CreateMatchRequest
            {
                MatchName = MatchName,
                WeaponClass = SelectedWeaponClass,
                StartDate = combinedStartDate,
                IsOpen = IsOpen,
                HasHandicap = HasHandicap,
                IsTeamMatch = IsTeamMatch,
                MaxShootersPerTeam = IsTeamMatch ? MaxShootersPerTeam : null
            };

            // Add team definitions for closed team matches
            if (IsTeamMatch && !IsOpen)
            {
                request.Teams = new List<TeamDefinition>();
                if (!string.IsNullOrWhiteSpace(Team1Name))
                    request.Teams.Add(new TeamDefinition { TeamNumber = 1, TeamName = Team1Name.Trim(), ClubId = Team1Club?.Id });
                if (!string.IsNullOrWhiteSpace(Team2Name))
                    request.Teams.Add(new TeamDefinition { TeamNumber = 2, TeamName = Team2Name.Trim(), ClubId = Team2Club?.Id });
                if (TeamCount >= 3 && !string.IsNullOrWhiteSpace(Team3Name))
                    request.Teams.Add(new TeamDefinition { TeamNumber = 3, TeamName = Team3Name.Trim(), ClubId = Team3Club?.Id });
                if (TeamCount >= 4 && !string.IsNullOrWhiteSpace(Team4Name))
                    request.Teams.Add(new TeamDefinition { TeamNumber = 4, TeamName = Team4Name.Trim(), ClubId = Team4Club?.Id });

                // Validate team names for closed matches
                if (request.Teams.Count < TeamCount)
                {
                    ErrorMessage = "Ange namn för alla lag";
                    HasError = true;
                    return;
                }
            }

            var result = await _matchService.CreateMatchAsync(request);

            if (result.Success && result.Data != null)
            {
                // Navigate to the active match page
                await Shell.Current.GoToAsync($"//main/matches/activeMatch?code={result.Data.MatchCode}");
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte skapa match";
                HasError = true;
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
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}

/// <summary>
/// Club item for picker display
/// </summary>
public class ClubPickerItem
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public override string ToString() => Name;
}
