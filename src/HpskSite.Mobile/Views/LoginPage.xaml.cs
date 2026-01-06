using HpskSite.Mobile.Services;
using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class LoginPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly IApiService _apiService;
    private bool _sessionCheckCompleted;

    public LoginPage(LoginViewModel viewModel, IAuthService authService, IApiService apiService)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _authService = authService;
        _apiService = apiService;

        // Hide content initially while checking session
        MainContent.IsVisible = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Only check session once
        if (_sessionCheckCompleted)
        {
            MainContent.IsVisible = true;
            return;
        }
        _sessionCheckCompleted = true;

        // Try to restore session from stored tokens
        var sessionRestored = await _authService.TryRestoreSessionAsync();
        if (sessionRestored)
        {
            // User has valid session, go to main
            await Shell.Current.GoToAsync("//main");
        }
        else
        {
            // No valid session, show login form
            MainContent.IsVisible = true;
        }
    }

    private async void OnRegisterLinkTapped(object sender, TappedEventArgs e)
    {
        var registerUrl = $"{_apiService.BaseUrl}/login-register#register";
        await Browser.Default.OpenAsync(registerUrl, BrowserLaunchMode.SystemPreferred);
    }
}
