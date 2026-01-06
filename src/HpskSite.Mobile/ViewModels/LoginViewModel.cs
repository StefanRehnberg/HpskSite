using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// ViewModel for login page
/// </summary>
public partial class LoginViewModel : BaseViewModel
{
    private readonly IAuthService _authService;

    public LoginViewModel(IAuthService authService)
    {
        _authService = authService;
        Title = "Logga in";
    }

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Ange e-post och lösenord";
            HasError = true;
            return;
        }

        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            HasError = false;

            var result = await _authService.LoginAsync(Email, Password, true);

            if (result.Success)
            {
                // Navigate to main page
                await Shell.Current.GoToAsync("//main");
            }
            else
            {
                ErrorMessage = result.Message ?? "Inloggning misslyckades";
                HasError = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ett fel uppstod: {ex.Message}";
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
