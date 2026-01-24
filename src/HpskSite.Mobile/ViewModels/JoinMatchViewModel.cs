using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// ViewModel for joining a match by code or QR scan
/// </summary>
[QueryProperty(nameof(ScannedCode), "scannedCode")]
public partial class JoinMatchViewModel : BaseViewModel
{
    private readonly IMatchService _matchService;

    // Valid shooter classes (must match server)
    private static readonly string[] ShooterClasses = new[]
    {
        "Klass 1 - Nybörjare",
        "Klass 2 - Guldmärkesskytt",
        "Klass 3 - Riksmästare"
    };

    public JoinMatchViewModel(IMatchService matchService)
    {
        _matchService = matchService;
        Title = "Gå med i match";
        AvailableTeams = new ObservableCollection<TrainingMatchTeam>();

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

    [ObservableProperty]
    private string _matchCode = string.Empty;

    // Team selection properties
    [ObservableProperty]
    private bool _isTeamMatch;

    [ObservableProperty]
    private bool _showTeamSelection;

    [ObservableProperty]
    private bool _isOpenTeamMatch;

    [ObservableProperty]
    private ObservableCollection<TrainingMatchTeam> _availableTeams;

    [ObservableProperty]
    private TrainingMatchTeam? _selectedTeam;

    [ObservableProperty]
    private string _newTeamName = string.Empty;

    [ObservableProperty]
    private ClubPickerItem? _selectedNewTeamClub;

    public ObservableCollection<ClubPickerItem> AvailableClubs { get; }

    [ObservableProperty]
    private bool _isCreatingNewTeam;

    // Property for receiving scanned code from QR scanner
    private string? _scannedCode;
    public string? ScannedCode
    {
        get => _scannedCode;
        set
        {
            _scannedCode = value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                MatchCode = value.ToUpperInvariant();
                // Auto-join after scanning
                _ = JoinMatchAsync();
            }
        }
    }

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [RelayCommand]
    private async Task JoinMatchAsync()
    {
        if (string.IsNullOrWhiteSpace(MatchCode))
        {
            ErrorMessage = "Ange matchkod";
            HasError = true;
            return;
        }

        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            HasError = false;

            // First, get match info to check if it's a team match
            var matchResult = await _matchService.GetMatchAsync(MatchCode.ToUpper());

            if (!matchResult.Success || matchResult.Data == null)
            {
                ErrorMessage = matchResult.Message ?? "Kunde inte hitta matchen";
                HasError = true;
                return;
            }

            var match = matchResult.Data;

            // Check if this is a team match
            if (match.IsTeamMatch)
            {
                IsTeamMatch = true;
                IsOpenTeamMatch = match.IsOpen;
                AvailableTeams.Clear();

                if (match.Teams != null)
                {
                    foreach (var team in match.Teams)
                    {
                        AvailableTeams.Add(team);
                    }
                }

                // Show team selection UI
                ShowTeamSelection = true;
                IsBusy = false;
                return;
            }

            // Not a team match - join directly
            await JoinMatchDirectlyAsync(null);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Join match with selected team (or no team for non-team matches)
    /// </summary>
    [RelayCommand]
    private async Task JoinWithTeamAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            HasError = false;

            int? teamId = null;

            if (IsCreatingNewTeam)
            {
                // Create new team first
                if (string.IsNullOrWhiteSpace(NewTeamName))
                {
                    ErrorMessage = "Ange ett lagnamn";
                    HasError = true;
                    return;
                }

                var createTeamResult = await _matchService.CreateTeamAsync(MatchCode.ToUpper(), NewTeamName.Trim(), SelectedNewTeamClub?.Id);
                if (!createTeamResult.Success || createTeamResult.Data == null)
                {
                    ErrorMessage = createTeamResult.Message ?? "Kunde inte skapa laget";
                    HasError = true;
                    return;
                }
                teamId = createTeamResult.Data.Id;
            }
            else if (SelectedTeam != null)
            {
                teamId = SelectedTeam.Id;
            }
            else
            {
                ErrorMessage = "Välj ett lag eller skapa ett nytt";
                HasError = true;
                return;
            }

            await JoinMatchDirectlyAsync(teamId);
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

    /// <summary>
    /// Direct join without team selection UI
    /// </summary>
    private async Task JoinMatchDirectlyAsync(int? teamId)
    {
        IsBusy = true;
        HasError = false;

        try
        {
            var result = await _matchService.JoinMatchAsync(MatchCode.ToUpper(), teamId);

            if (result.Success)
            {
                ShowTeamSelection = false;
                await Shell.Current.GoToAsync($"//main/matches/activeMatch?code={MatchCode.ToUpper()}");
            }
            else if (result.NeedsShooterClass)
            {
                // User needs to set shooter class for handicap match
                await HandleShooterClassRequiredAsync(teamId);
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte gå med i matchen";
                HasError = true;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancel team selection and go back to code entry
    /// </summary>
    [RelayCommand]
    private void CancelTeamSelection()
    {
        ShowTeamSelection = false;
        IsTeamMatch = false;
        SelectedTeam = null;
        NewTeamName = string.Empty;
        IsCreatingNewTeam = false;
    }

    /// <summary>
    /// Toggle between selecting existing team and creating new team
    /// </summary>
    [RelayCommand]
    private void ToggleCreateNewTeam()
    {
        IsCreatingNewTeam = !IsCreatingNewTeam;
        if (IsCreatingNewTeam)
        {
            SelectedTeam = null;
        }
        else
        {
            NewTeamName = string.Empty;
        }
    }

    /// <summary>
    /// Handles the case where user needs to set shooter class before joining a handicap match
    /// </summary>
    private async Task HandleShooterClassRequiredAsync(int? teamId = null)
    {
        // Show picker dialog
        var selectedClass = await Application.Current!.MainPage!.DisplayActionSheet(
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
        IsBusy = true;
        HasError = false;

        var setClassResult = await _matchService.SetShooterClassAsync(selectedClass);

        if (!setClassResult.Success)
        {
            ErrorMessage = setClassResult.Message ?? "Kunde inte spara skytteklass";
            HasError = true;
            IsBusy = false;
            return;
        }

        // Now retry joining the match with team if specified
        var joinResult = await _matchService.JoinMatchAsync(MatchCode.ToUpper(), teamId);

        IsBusy = false;

        if (joinResult.Success)
        {
            ShowTeamSelection = false;
            await Shell.Current.GoToAsync($"//main/matches/activeMatch?code={MatchCode.ToUpper()}");
        }
        else
        {
            ErrorMessage = joinResult.Message ?? "Kunde inte gå med i matchen";
            HasError = true;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task ScanQrAsync()
    {
        await Shell.Current.GoToAsync("qrScanner");
    }
}
