using HpskSite.Shared.DTOs;
using Plugin.Firebase.CloudMessaging;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Service for handling Firebase Cloud Messaging push notifications
/// </summary>
public class PushNotificationService : IPushNotificationService
{
    private readonly IApiService _apiService;
    private readonly ISecureStorageService _secureStorage;
    private string? _cachedToken;
    private bool _isInitialized;

    private const string DeviceTokenKey = "device_fcm_token";

    public PushNotificationService(IApiService apiService, ISecureStorageService secureStorage)
    {
        _apiService = apiService;
        _secureStorage = secureStorage;
    }

    public event EventHandler<string>? NotificationReceived;

    /// <summary>
    /// Initialize Firebase Cloud Messaging and subscribe to token refresh events
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        try
        {
            // Request permission first
            await RequestPermissionAsync();

            // Subscribe to token refresh events
            CrossFirebaseCloudMessaging.Current.TokenChanged += OnTokenChanged;

            // Get current token
            _cachedToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            if (!string.IsNullOrEmpty(_cachedToken))
            {
                Preferences.Default.Set(DeviceTokenKey, _cachedToken);
            }

            // Subscribe to notification received events
            CrossFirebaseCloudMessaging.Current.NotificationReceived += OnNotificationReceived;

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize FCM: {ex.Message}");
        }
    }

    /// <summary>
    /// Request notification permissions from the user
    /// </summary>
    public async Task<bool> RequestPermissionAsync()
    {
        try
        {
#if ANDROID
            // Android 13+ requires explicit permission
            if (DeviceInfo.Version.Major >= 13)
            {
                var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                if (status != PermissionStatus.Granted)
                {
                    status = await Permissions.RequestAsync<Permissions.PostNotifications>();
                }
                return status == PermissionStatus.Granted;
            }
            return true;
#elif IOS
            // iOS permission is requested by Firebase automatically
            await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();
            return true;
#else
            return false;
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to request permission: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get the current FCM device token
    /// </summary>
    public async Task<string?> GetTokenAsync()
    {
        if (!string.IsNullOrEmpty(_cachedToken))
            return _cachedToken;

        try
        {
            _cachedToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            if (!string.IsNullOrEmpty(_cachedToken))
            {
                Preferences.Default.Set(DeviceTokenKey, _cachedToken);
            }
            return _cachedToken;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get FCM token: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Register the device with the server for push notifications
    /// </summary>
    public async Task<bool> RegisterDeviceWithServerAsync()
    {
        try
        {
            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                System.Diagnostics.Debug.WriteLine("No FCM token available for registration");
                return false;
            }

            var platform = DeviceInfo.Platform == DevicePlatform.Android ? "Android" : "iOS";

            var response = await _apiService.PostAsync("api/notifications/register-device", new RegisterDeviceRequest
            {
                DeviceToken = token,
                Platform = platform
            });

            if (response.Success)
            {
                System.Diagnostics.Debug.WriteLine("Device registered for push notifications");
                return true;
            }

            System.Diagnostics.Debug.WriteLine($"Failed to register device: {response.Message}");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register device: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unregister the device from push notifications (call on logout)
    /// </summary>
    public async Task<bool> UnregisterDeviceAsync()
    {
        try
        {
            var token = Preferences.Default.Get(DeviceTokenKey, string.Empty);
            if (string.IsNullOrEmpty(token))
            {
                return true; // No token to unregister
            }

            var response = await _apiService.PostAsync("api/notifications/unregister-device", new UnregisterDeviceRequest
            {
                DeviceToken = token
            });

            if (response.Success)
            {
                Preferences.Default.Remove(DeviceTokenKey);
                System.Diagnostics.Debug.WriteLine("Device unregistered from push notifications");
                return true;
            }

            System.Diagnostics.Debug.WriteLine($"Failed to unregister device: {response.Message}");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to unregister device: {ex.Message}");
            return false;
        }
    }

    private void OnTokenChanged(object? sender, Plugin.Firebase.CloudMessaging.EventArgs.FCMTokenChangedEventArgs e)
    {
        _cachedToken = e.Token;
        Preferences.Default.Set(DeviceTokenKey, _cachedToken);

        // Re-register with new token
        _ = RegisterDeviceWithServerAsync();
    }

    private void OnNotificationReceived(object? sender, Plugin.Firebase.CloudMessaging.EventArgs.FCMNotificationReceivedEventArgs e)
    {
        var notification = e.Notification;
        var message = notification?.Body ?? "Ny notis";

        NotificationReceived?.Invoke(this, message);

        // Handle notification tap - navigate to match if match code is in data
        if (notification?.Data != null && notification.Data.TryGetValue("matchCode", out var matchCode))
        {
            // Navigate on main thread
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Shell.Current.GoToAsync($"//match?code={matchCode}");
            });
        }
    }
}

public interface IPushNotificationService
{
    event EventHandler<string>? NotificationReceived;
    Task InitializeAsync();
    Task<bool> RequestPermissionAsync();
    Task<string?> GetTokenAsync();
    Task<bool> RegisterDeviceWithServerAsync();
    Task<bool> UnregisterDeviceAsync();
}
