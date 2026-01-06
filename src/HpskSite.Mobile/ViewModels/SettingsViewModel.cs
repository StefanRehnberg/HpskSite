using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Services;

namespace HpskSite.Mobile.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private readonly IThemeService _themeService;
    private readonly IAuthService _authService;
    private readonly IPushNotificationService _pushNotificationService;
    private readonly INotificationPreferencesService _preferencesService;

    public SettingsViewModel(
        IThemeService themeService,
        IAuthService authService,
        IPushNotificationService pushNotificationService,
        INotificationPreferencesService preferencesService)
    {
        _themeService = themeService;
        _authService = authService;
        _pushNotificationService = pushNotificationService;
        _preferencesService = preferencesService;
        Title = "Installningar";

        // Initialize selected theme
        _selectedThemeIndex = (int)_themeService.CurrentTheme;

        // Initialize notification settings
        _notificationsEnabled = _preferencesService.AreNotificationsEnabled();
        _selectedNotificationIndex = _preferencesService.GetNotificationPreference() == "All" ? 1 : 0;
    }

    /// <summary>
    /// App version string (e.g., "v1.2 (3)")
    /// </summary>
    public string AppVersion => $"v{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    #region Theme Settings

    public List<string> ThemeOptions { get; } = new()
    {
        "Folj systemet",
        "Ljust",
        "Morkt"
    };

    [ObservableProperty]
    private int _selectedThemeIndex;

    partial void OnSelectedThemeIndexChanged(int value)
    {
        _themeService.CurrentTheme = (ThemeMode)value;
    }

    #endregion

    #region Notification Settings

    public List<string> NotificationOptions { get; } = new()
    {
        "Endast oppna matcher",
        "Alla matcher"
    };

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private int _selectedNotificationIndex;

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _ = SaveNotificationSettingsAsync();
    }

    partial void OnSelectedNotificationIndexChanged(int value)
    {
        _ = SaveNotificationSettingsAsync();
    }

    private async Task SaveNotificationSettingsAsync()
    {
        var preference = SelectedNotificationIndex == 1 ? "All" : "OpenMatchesOnly";
        await _preferencesService.SetNotificationsEnabledAsync(NotificationsEnabled);
        await _preferencesService.SetNotificationPreferenceAsync(preference);
    }

    #endregion

    [RelayCommand]
    private async Task LogoutAsync()
    {
        // Unregister device from push notifications before logout
        await _pushNotificationService.UnregisterDeviceAsync();

        await _authService.LogoutAsync();
        await Shell.Current.GoToAsync("//login");
    }
}
