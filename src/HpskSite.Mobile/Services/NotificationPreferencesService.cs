using HpskSite.Shared.DTOs;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Service for managing notification preferences locally and syncing with server
/// </summary>
public class NotificationPreferencesService : INotificationPreferencesService
{
    private readonly IApiService _apiService;

    private const string NotificationsEnabledKey = "notifications_enabled";
    private const string NotificationPreferenceKey = "notification_preference";

    public NotificationPreferencesService(IApiService apiService)
    {
        _apiService = apiService;
    }

    /// <summary>
    /// Check if notifications are enabled locally
    /// </summary>
    public bool AreNotificationsEnabled()
    {
        return Preferences.Default.Get(NotificationsEnabledKey, true);
    }

    /// <summary>
    /// Get notification preference ("OpenMatchesOnly" or "All")
    /// </summary>
    public string GetNotificationPreference()
    {
        return Preferences.Default.Get(NotificationPreferenceKey, "OpenMatchesOnly");
    }

    /// <summary>
    /// Set notifications enabled state locally and sync with server
    /// </summary>
    public async Task SetNotificationsEnabledAsync(bool enabled)
    {
        Preferences.Default.Set(NotificationsEnabledKey, enabled);
        await SyncPreferencesWithServerAsync();
    }

    /// <summary>
    /// Set notification preference ("OpenMatchesOnly" or "All") and sync with server
    /// </summary>
    public async Task SetNotificationPreferenceAsync(string preference)
    {
        if (preference != "OpenMatchesOnly" && preference != "All")
        {
            preference = "OpenMatchesOnly"; // Default fallback
        }

        Preferences.Default.Set(NotificationPreferenceKey, preference);
        await SyncPreferencesWithServerAsync();
    }

    /// <summary>
    /// Load preferences from server and update local storage
    /// </summary>
    public async Task LoadPreferencesFromServerAsync()
    {
        try
        {
            var response = await _apiService.GetAsync<NotificationPreferencesResponse>("api/notifications/preferences");
            if (response.Success && response.Data != null)
            {
                Preferences.Default.Set(NotificationsEnabledKey, response.Data.NotificationsEnabled);
                Preferences.Default.Set(NotificationPreferenceKey, response.Data.NotificationPreference);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load preferences from server: {ex.Message}");
        }
    }

    /// <summary>
    /// Sync local preferences to server
    /// </summary>
    public async Task SyncPreferencesWithServerAsync()
    {
        try
        {
            var request = new UpdateNotificationPreferencesRequest
            {
                NotificationsEnabled = AreNotificationsEnabled(),
                NotificationPreference = GetNotificationPreference()
            };

            var response = await _apiService.PutAsync<object>("api/notifications/preferences", request);
            if (!response.Success)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to sync preferences: {response.Message}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to sync preferences: {ex.Message}");
        }
    }
}

public interface INotificationPreferencesService
{
    bool AreNotificationsEnabled();
    string GetNotificationPreference();
    Task SetNotificationsEnabledAsync(bool enabled);
    Task SetNotificationPreferenceAsync(string preference);
    Task LoadPreferencesFromServerAsync();
    Task SyncPreferencesWithServerAsync();
}
