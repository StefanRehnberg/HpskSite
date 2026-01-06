using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;

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
    }

    [ObservableProperty]
    private string _matchCode = string.Empty;

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

            var result = await _matchService.JoinMatchAsync(MatchCode.ToUpper());

            if (result.Success)
            {
                await Shell.Current.GoToAsync($"//main/matches/activeMatch?code={MatchCode.ToUpper()}");
            }
            else if (result.NeedsShooterClass)
            {
                // User needs to set shooter class for handicap match
                await HandleShooterClassRequiredAsync();
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte gå med i matchen";
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

    /// <summary>
    /// Handles the case where user needs to set shooter class before joining a handicap match
    /// </summary>
    private async Task HandleShooterClassRequiredAsync()
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

        // Now retry joining the match
        var joinResult = await _matchService.JoinMatchAsync(MatchCode.ToUpper());

        IsBusy = false;

        if (joinResult.Success)
        {
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
