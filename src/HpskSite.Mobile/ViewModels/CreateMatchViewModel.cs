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
    }

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
                HasHandicap = HasHandicap
            };

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
