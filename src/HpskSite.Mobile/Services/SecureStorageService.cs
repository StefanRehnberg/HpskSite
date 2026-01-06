namespace HpskSite.Mobile.Services;

/// <summary>
/// Service for secure storage of sensitive data like tokens
/// </summary>
public class SecureStorageService : ISecureStorageService
{
    private const string AccessTokenKey = "access_token";
    private const string RefreshTokenKey = "refresh_token";
    private const string AccessTokenExpiresKey = "access_token_expires";
    private const string UserInfoKey = "user_info";

    public async Task<string?> GetAccessTokenAsync()
    {
        return await SecureStorage.Default.GetAsync(AccessTokenKey);
    }

    public async Task SetAccessTokenAsync(string token)
    {
        await SecureStorage.Default.SetAsync(AccessTokenKey, token);
    }

    public async Task<string?> GetRefreshTokenAsync()
    {
        return await SecureStorage.Default.GetAsync(RefreshTokenKey);
    }

    public async Task SetRefreshTokenAsync(string token)
    {
        await SecureStorage.Default.SetAsync(RefreshTokenKey, token);
    }

    public async Task<DateTime?> GetAccessTokenExpirationAsync()
    {
        var expiresStr = await SecureStorage.Default.GetAsync(AccessTokenExpiresKey);
        if (string.IsNullOrEmpty(expiresStr))
            return null;

        if (DateTime.TryParse(expiresStr, out var expires))
            return expires;

        return null;
    }

    public async Task SetAccessTokenExpirationAsync(DateTime expires)
    {
        await SecureStorage.Default.SetAsync(AccessTokenExpiresKey, expires.ToString("O"));
    }

    public async Task<string?> GetUserInfoAsync()
    {
        return await SecureStorage.Default.GetAsync(UserInfoKey);
    }

    public async Task SetUserInfoAsync(string userInfoJson)
    {
        await SecureStorage.Default.SetAsync(UserInfoKey, userInfoJson);
    }

    public async Task<bool> IsAccessTokenValidAsync()
    {
        var token = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return false;

        var expires = await GetAccessTokenExpirationAsync();
        if (!expires.HasValue)
            return false;

        // Consider token invalid if it expires within 1 minute
        return expires.Value > DateTime.UtcNow.AddMinutes(1);
    }

    public void ClearAll()
    {
        SecureStorage.Default.Remove(AccessTokenKey);
        SecureStorage.Default.Remove(RefreshTokenKey);
        SecureStorage.Default.Remove(AccessTokenExpiresKey);
        SecureStorage.Default.Remove(UserInfoKey);
    }
}

public interface ISecureStorageService
{
    Task<string?> GetAccessTokenAsync();
    Task SetAccessTokenAsync(string token);
    Task<string?> GetRefreshTokenAsync();
    Task SetRefreshTokenAsync(string token);
    Task<DateTime?> GetAccessTokenExpirationAsync();
    Task SetAccessTokenExpirationAsync(DateTime expires);
    Task<string?> GetUserInfoAsync();
    Task SetUserInfoAsync(string userInfoJson);
    Task<bool> IsAccessTokenValidAsync();
    void ClearAll();
}
