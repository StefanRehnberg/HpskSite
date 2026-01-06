using System.Text.Json;
using HpskSite.Shared.DTOs;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Authentication service for managing user login state
/// </summary>
public class AuthService : IAuthService
{
    private readonly IApiService _apiService;
    private readonly ISecureStorageService _secureStorage;
    private readonly Lazy<IPushNotificationService> _pushNotificationService;
    private readonly Lazy<INotificationPreferencesService> _preferencesService;

    private UserInfo? _currentUser;

    public AuthService(
        IApiService apiService,
        ISecureStorageService secureStorage,
        Lazy<IPushNotificationService> pushNotificationService,
        Lazy<INotificationPreferencesService> preferencesService)
    {
        _apiService = apiService;
        _secureStorage = secureStorage;
        _pushNotificationService = pushNotificationService;
        _preferencesService = preferencesService;
    }

    public event EventHandler<bool>? AuthStateChanged;

    public bool IsLoggedIn => _currentUser != null;

    public UserInfo? CurrentUser => _currentUser;

    public async Task<bool> TryRestoreSessionAsync()
    {
        var isValid = await _secureStorage.IsAccessTokenValidAsync();
        if (!isValid)
        {
            // Try to get refresh token and restore session
            var refreshToken = await _secureStorage.GetRefreshTokenAsync();
            if (string.IsNullOrEmpty(refreshToken))
            {
                return false;
            }

            // Try to refresh
            var response = await _apiService.PostWithoutAuthAsync<LoginResponse>("api/auth/refresh", new RefreshTokenRequest
            {
                RefreshToken = refreshToken
            });

            if (!response.Success || response.Data == null)
            {
                _secureStorage.ClearAll();
                return false;
            }

            await SaveLoginResponseAsync(response.Data);
        }

        // Load user info from storage
        var userInfoJson = await _secureStorage.GetUserInfoAsync();
        if (!string.IsNullOrEmpty(userInfoJson))
        {
            _currentUser = JsonSerializer.Deserialize<UserInfo>(userInfoJson);
            AuthStateChanged?.Invoke(this, true);
            return true;
        }

        // Fetch user info from API
        var meResponse = await _apiService.GetAsync<UserInfo>("api/auth/me");
        if (meResponse.Success && meResponse.Data != null)
        {
            _currentUser = meResponse.Data;
            await _secureStorage.SetUserInfoAsync(JsonSerializer.Serialize(_currentUser));
            AuthStateChanged?.Invoke(this, true);

            // Report mobile app activity
            _ = ReportActivityAsync();

            // Initialize push notifications and request camera permission
            _ = InitializePushNotificationsAsync();
            _ = RequestCameraPermissionAsync();

            return true;
        }

        return false;
    }

    public async Task<ApiResponse<LoginResponse>> LoginAsync(string email, string password, bool rememberMe = false)
    {
        var response = await _apiService.PostWithoutAuthAsync<LoginResponse>("api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = rememberMe
        });

        if (response.Success && response.Data != null)
        {
            await SaveLoginResponseAsync(response.Data);
            _currentUser = response.Data.User;
            AuthStateChanged?.Invoke(this, true);

            // Report mobile app activity
            _ = ReportActivityAsync();

            // Initialize push notifications and request camera permission
            _ = InitializePushNotificationsAsync();
            _ = RequestCameraPermissionAsync();
        }

        return response;
    }

    public async Task LogoutAsync()
    {
        var refreshToken = await _secureStorage.GetRefreshTokenAsync();
        if (!string.IsNullOrEmpty(refreshToken))
        {
            // Notify server to revoke refresh token
            await _apiService.PostAsync("api/auth/logout", new RefreshTokenRequest
            {
                RefreshToken = refreshToken
            });
        }

        _secureStorage.ClearAll();
        _currentUser = null;
        AuthStateChanged?.Invoke(this, false);
    }

    public async Task<UserInfo?> GetCurrentUserAsync()
    {
        if (_currentUser != null)
            return _currentUser;

        var response = await _apiService.GetAsync<UserInfo>("api/auth/me");
        if (response.Success && response.Data != null)
        {
            _currentUser = response.Data;
            await _secureStorage.SetUserInfoAsync(JsonSerializer.Serialize(_currentUser));
        }

        return _currentUser;
    }

    private async Task SaveLoginResponseAsync(LoginResponse response)
    {
        await _secureStorage.SetAccessTokenAsync(response.AccessToken);
        await _secureStorage.SetRefreshTokenAsync(response.RefreshToken);
        await _secureStorage.SetAccessTokenExpirationAsync(response.AccessTokenExpires);

        if (response.User != null)
        {
            await _secureStorage.SetUserInfoAsync(JsonSerializer.Serialize(response.User));
        }
    }

    /// <summary>
    /// Report mobile app activity to the server (fire-and-forget, non-blocking)
    /// </summary>
    private async Task ReportActivityAsync()
    {
        try
        {
            await _apiService.PostAsync("api/auth/activity");
        }
        catch
        {
            // Ignore errors - activity tracking should not affect user experience
        }
    }

    /// <summary>
    /// Initialize push notifications and register device with server
    /// </summary>
    private async Task InitializePushNotificationsAsync()
    {
        try
        {
            var pushService = _pushNotificationService.Value;
            await pushService.InitializeAsync();
            await pushService.RegisterDeviceWithServerAsync();

            // Load notification preferences from server
            var prefsService = _preferencesService.Value;
            await prefsService.LoadPreferencesFromServerAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize push notifications: {ex.Message}");
            // Don't fail login if push notifications fail
        }
    }

    /// <summary>
    /// Request camera permission after login (needed for target photos and QR scanning)
    /// </summary>
    private async Task RequestCameraPermissionAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Camera>();
            }
            System.Diagnostics.Debug.WriteLine($"Camera permission status: {status}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to request camera permission: {ex.Message}");
            // Don't fail login if permission request fails
        }
    }
}

public interface IAuthService
{
    event EventHandler<bool>? AuthStateChanged;
    bool IsLoggedIn { get; }
    UserInfo? CurrentUser { get; }
    Task<bool> TryRestoreSessionAsync();
    Task<ApiResponse<LoginResponse>> LoginAsync(string email, string password, bool rememberMe = false);
    Task LogoutAsync();
    Task<UserInfo?> GetCurrentUserAsync();
}
