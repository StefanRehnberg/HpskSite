using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HpskSite.Shared.DTOs;

namespace HpskSite.Mobile.Services;

/// <summary>
/// HTTP client service for API communication with automatic token refresh
/// </summary>
public class ApiService : IApiService
{
    private readonly HttpClient _httpClient;
    private readonly ISecureStorageService _secureStorage;
    private readonly JsonSerializerOptions _jsonOptions;

    // Use SemaphoreSlim to prevent race conditions during token refresh
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastRefreshAttempt = DateTime.MinValue;

    // Track if we've already tried refreshing for the current request cycle
    private bool _refreshAttemptedForCurrentRequest;

    public ApiService(ISecureStorageService secureStorage)
    {
        _secureStorage = secureStorage;

#if DEBUG
        // In debug mode, bypass SSL certificate validation for development certificates
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };
        _httpClient = new HttpClient(handler);
#else
        _httpClient = new HttpClient();
#endif

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public string BaseUrl { get; set; } = "https://localhost:5001";

    public async Task<ApiResponse<T>> GetAsync<T>(string endpoint)
    {
        await EnsureValidTokenAsync();

        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/{endpoint}");
            return await HandleResponseWithRetryAsync<T>(response, endpoint, "GET", null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.GetAsync error: {ex.Message}");
            return ApiResponse<T>.Error($"Network error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<T>> PostAsync<T>(string endpoint, object? data = null)
    {
        await EnsureValidTokenAsync();

        try
        {
            HttpResponseMessage response;
            if (data != null)
            {
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", content);
            }
            else
            {
                response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", null);
            }

            return await HandleResponseWithRetryAsync<T>(response, endpoint, "POST", data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.PostAsync error: {ex.Message}");
            return ApiResponse<T>.Error($"Network error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<T>> PutAsync<T>(string endpoint, object? data = null)
    {
        await EnsureValidTokenAsync();

        try
        {
            HttpResponseMessage response;
            if (data != null)
            {
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                response = await _httpClient.PutAsync($"{BaseUrl}/{endpoint}", content);
            }
            else
            {
                response = await _httpClient.PutAsync($"{BaseUrl}/{endpoint}", null);
            }

            return await HandleResponseWithRetryAsync<T>(response, endpoint, "PUT", data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.PutAsync error: {ex.Message}");
            return ApiResponse<T>.Error($"Network error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<T>> DeleteAsync<T>(string endpoint)
    {
        await EnsureValidTokenAsync();

        try
        {
            var response = await _httpClient.DeleteAsync($"{BaseUrl}/{endpoint}");
            return await HandleResponseWithRetryAsync<T>(response, endpoint, "DELETE", null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.DeleteAsync error: {ex.Message}");
            return ApiResponse<T>.Error($"Network error: {ex.Message}");
        }
    }

    public async Task<ApiResponse> DeleteAsync(string endpoint)
    {
        await EnsureValidTokenAsync();

        try
        {
            var response = await _httpClient.DeleteAsync($"{BaseUrl}/{endpoint}");
            return await HandleResponseWithRetryAsync(response, endpoint, "DELETE", null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.DeleteAsync error: {ex.Message}");
            return ApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    public async Task<ApiResponse> PostAsync(string endpoint, object? data = null)
    {
        await EnsureValidTokenAsync();

        try
        {
            HttpResponseMessage response;
            if (data != null)
            {
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", content);
            }
            else
            {
                response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", null);
            }

            return await HandleResponseWithRetryAsync(response, endpoint, "POST", data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.PostAsync error: {ex.Message}");
            return ApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Post without auth (for login/refresh endpoints)
    /// </summary>
    public async Task<ApiResponse<T>> PostWithoutAuthAsync<T>(string endpoint, object? data = null)
    {
        try
        {
            HttpResponseMessage response;
            if (data != null)
            {
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", content);
            }
            else
            {
                response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", null);
            }

            return await HandleResponseAsync<T>(response);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.PostWithoutAuthAsync error: {ex.Message}");
            return ApiResponse<T>.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Post multipart/form-data for file uploads
    /// </summary>
    public async Task<ApiResponse<T>> PostMultipartAsync<T>(string endpoint, byte[] fileData, string fileName, string formFieldName = "photo")
    {
        await EnsureValidTokenAsync();

        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileData);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, formFieldName, fileName);

            var response = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", content);
            return await HandleResponseWithRetryAsync<T>(response, endpoint, "POST_MULTIPART", null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService.PostMultipartAsync error: {ex.Message}");
            return ApiResponse<T>.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures a valid token is set on the HTTP client.
    /// Uses a semaphore to prevent race conditions when multiple requests need to refresh simultaneously.
    /// </summary>
    private async Task EnsureValidTokenAsync()
    {
        // Reset the refresh attempt flag for this request cycle
        _refreshAttemptedForCurrentRequest = false;

        // Wait for any ongoing refresh to complete (with timeout to prevent deadlock)
        var acquired = await _refreshLock.WaitAsync(TimeSpan.FromSeconds(30));
        if (!acquired)
        {
            System.Diagnostics.Debug.WriteLine("ApiService: Timeout waiting for refresh lock");
            // Continue anyway with current token
            var currentToken = await _secureStorage.GetAccessTokenAsync();
            if (!string.IsNullOrEmpty(currentToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);
            }
            return;
        }

        try
        {
            var isValid = await _secureStorage.IsAccessTokenValidAsync();
            if (isValid)
            {
                var token = await _secureStorage.GetAccessTokenAsync();
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                System.Diagnostics.Debug.WriteLine("ApiService: Token is valid");
                return;
            }

            System.Diagnostics.Debug.WriteLine("ApiService: Token expired or invalid, attempting refresh");

            // Try to refresh the token
            await RefreshTokenInternalAsync();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Internal refresh logic with retry support
    /// </summary>
    private async Task RefreshTokenInternalAsync()
    {
        const int maxRetries = 2;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService: Refresh attempt {attempt}/{maxRetries}");

            var refreshToken = await _secureStorage.GetRefreshTokenAsync();
            if (string.IsNullOrEmpty(refreshToken))
            {
                System.Diagnostics.Debug.WriteLine("ApiService: No refresh token available");
                // No refresh token available - user needs to login
                // But don't clear tokens here - let the 401 handler deal with it
                return;
            }

            try
            {
                // Remove auth header for refresh request
                _httpClient.DefaultRequestHeaders.Authorization = null;

                var response = await PostWithoutAuthAsync<LoginResponse>("api/auth/refresh", new RefreshTokenRequest
                {
                    RefreshToken = refreshToken
                });

                if (response.Success && response.Data != null)
                {
                    System.Diagnostics.Debug.WriteLine("ApiService: Token refresh successful");

                    await _secureStorage.SetAccessTokenAsync(response.Data.AccessToken);
                    await _secureStorage.SetRefreshTokenAsync(response.Data.RefreshToken);
                    await _secureStorage.SetAccessTokenExpirationAsync(response.Data.AccessTokenExpires);

                    _httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", response.Data.AccessToken);

                    _lastRefreshAttempt = DateTime.UtcNow;
                    _refreshAttemptedForCurrentRequest = true;
                    return; // Success!
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"ApiService: Refresh failed - {response.Message}");

                    // If it's the last attempt, we've exhausted retries
                    if (attempt == maxRetries)
                    {
                        System.Diagnostics.Debug.WriteLine("ApiService: All refresh attempts failed");
                        // Don't clear tokens here - the server may have accepted the refresh
                        // but we had a network issue receiving the response
                        // Let the 401 handler clear tokens if needed
                        _refreshAttemptedForCurrentRequest = true;
                    }
                    else
                    {
                        // Wait a bit before retrying
                        await Task.Delay(500);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApiService: Refresh exception - {ex.Message}");

                if (attempt == maxRetries)
                {
                    System.Diagnostics.Debug.WriteLine("ApiService: All refresh attempts failed with exception");
                    _refreshAttemptedForCurrentRequest = true;
                }
                else
                {
                    await Task.Delay(500);
                }
            }
        }
    }

    /// <summary>
    /// Handle response with automatic retry on 401 (attempt token refresh once)
    /// </summary>
    private async Task<ApiResponse<T>> HandleResponseWithRetryAsync<T>(
        HttpResponseMessage response,
        string endpoint,
        string method,
        object? data)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !_refreshAttemptedForCurrentRequest)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService: Got 401 on {method} {endpoint}, attempting token refresh");

            // Try to refresh and retry the request
            await _refreshLock.WaitAsync();
            try
            {
                await RefreshTokenInternalAsync();
            }
            finally
            {
                _refreshLock.Release();
            }

            // Check if refresh was successful
            var isValid = await _secureStorage.IsAccessTokenValidAsync();
            if (isValid)
            {
                System.Diagnostics.Debug.WriteLine("ApiService: Retrying request after token refresh");

                // Retry the request
                try
                {
                    HttpResponseMessage retryResponse;
                    switch (method)
                    {
                        case "GET":
                            retryResponse = await _httpClient.GetAsync($"{BaseUrl}/{endpoint}");
                            break;
                        case "DELETE":
                            retryResponse = await _httpClient.DeleteAsync($"{BaseUrl}/{endpoint}");
                            break;
                        case "PUT":
                            if (data != null)
                            {
                                var json = JsonSerializer.Serialize(data, _jsonOptions);
                                var content = new StringContent(json, Encoding.UTF8, "application/json");
                                retryResponse = await _httpClient.PutAsync($"{BaseUrl}/{endpoint}", content);
                            }
                            else
                            {
                                retryResponse = await _httpClient.PutAsync($"{BaseUrl}/{endpoint}", null);
                            }
                            break;
                        default: // POST
                            if (data != null)
                            {
                                var json = JsonSerializer.Serialize(data, _jsonOptions);
                                var content = new StringContent(json, Encoding.UTF8, "application/json");
                                retryResponse = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", content);
                            }
                            else
                            {
                                retryResponse = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", null);
                            }
                            break;
                    }

                    return await HandleResponseAsync<T>(retryResponse);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ApiService: Retry failed - {ex.Message}");
                    return ApiResponse<T>.Error($"Network error: {ex.Message}");
                }
            }
        }

        return await HandleResponseAsync<T>(response);
    }

    /// <summary>
    /// Handle response with automatic retry on 401 (non-generic version)
    /// </summary>
    private async Task<ApiResponse> HandleResponseWithRetryAsync(
        HttpResponseMessage response,
        string endpoint,
        string method,
        object? data)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !_refreshAttemptedForCurrentRequest)
        {
            System.Diagnostics.Debug.WriteLine($"ApiService: Got 401 on {method} {endpoint}, attempting token refresh");

            await _refreshLock.WaitAsync();
            try
            {
                await RefreshTokenInternalAsync();
            }
            finally
            {
                _refreshLock.Release();
            }

            var isValid = await _secureStorage.IsAccessTokenValidAsync();
            if (isValid)
            {
                System.Diagnostics.Debug.WriteLine("ApiService: Retrying request after token refresh");

                try
                {
                    HttpResponseMessage retryResponse;
                    switch (method)
                    {
                        case "GET":
                            retryResponse = await _httpClient.GetAsync($"{BaseUrl}/{endpoint}");
                            break;
                        case "DELETE":
                            retryResponse = await _httpClient.DeleteAsync($"{BaseUrl}/{endpoint}");
                            break;
                        case "PUT":
                            if (data != null)
                            {
                                var json = JsonSerializer.Serialize(data, _jsonOptions);
                                var content = new StringContent(json, Encoding.UTF8, "application/json");
                                retryResponse = await _httpClient.PutAsync($"{BaseUrl}/{endpoint}", content);
                            }
                            else
                            {
                                retryResponse = await _httpClient.PutAsync($"{BaseUrl}/{endpoint}", null);
                            }
                            break;
                        default:
                            if (data != null)
                            {
                                var json = JsonSerializer.Serialize(data, _jsonOptions);
                                var content = new StringContent(json, Encoding.UTF8, "application/json");
                                retryResponse = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", content);
                            }
                            else
                            {
                                retryResponse = await _httpClient.PostAsync($"{BaseUrl}/{endpoint}", null);
                            }
                            break;
                    }

                    return await HandleResponseAsync(retryResponse);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ApiService: Retry failed - {ex.Message}");
                    return ApiResponse.Error($"Network error: {ex.Message}");
                }
            }
        }

        return await HandleResponseAsync(response);
    }

    private async Task<ApiResponse<T>> HandleResponseAsync<T>(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var result = JsonSerializer.Deserialize<ApiResponse<T>>(content, _jsonOptions);
                return result ?? ApiResponse<T>.Error("Invalid response format");
            }
            catch (JsonException)
            {
                return ApiResponse<T>.Error("Failed to parse response");
            }
        }

        // Handle error responses
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            System.Diagnostics.Debug.WriteLine("ApiService: 401 Unauthorized - clearing tokens");
            _secureStorage.ClearAll();
            return ApiResponse<T>.Error("Session expired. Please login again.");
        }

        try
        {
            var errorResult = JsonSerializer.Deserialize<ApiResponse<T>>(content, _jsonOptions);
            return errorResult ?? ApiResponse<T>.Error($"Request failed: {response.StatusCode}");
        }
        catch
        {
            return ApiResponse<T>.Error($"Request failed: {response.StatusCode}");
        }
    }

    private async Task<ApiResponse> HandleResponseAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var result = JsonSerializer.Deserialize<ApiResponse>(content, _jsonOptions);
                return result ?? ApiResponse.Error("Invalid response format");
            }
            catch (JsonException)
            {
                return ApiResponse.Error("Failed to parse response");
            }
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            System.Diagnostics.Debug.WriteLine("ApiService: 401 Unauthorized - clearing tokens");
            _secureStorage.ClearAll();
            return ApiResponse.Error("Session expired. Please login again.");
        }

        try
        {
            var errorResult = JsonSerializer.Deserialize<ApiResponse>(content, _jsonOptions);
            return errorResult ?? ApiResponse.Error($"Request failed: {response.StatusCode}");
        }
        catch
        {
            return ApiResponse.Error($"Request failed: {response.StatusCode}");
        }
    }
}

public interface IApiService
{
    string BaseUrl { get; set; }
    Task<ApiResponse<T>> GetAsync<T>(string endpoint);
    Task<ApiResponse<T>> PostAsync<T>(string endpoint, object? data = null);
    Task<ApiResponse<T>> PutAsync<T>(string endpoint, object? data = null);
    Task<ApiResponse<T>> DeleteAsync<T>(string endpoint);
    Task<ApiResponse> DeleteAsync(string endpoint);
    Task<ApiResponse> PostAsync(string endpoint, object? data = null);
    Task<ApiResponse<T>> PostWithoutAuthAsync<T>(string endpoint, object? data = null);
    Task<ApiResponse<T>> PostMultipartAsync<T>(string endpoint, byte[] fileData, string fileName, string formFieldName = "photo");
}
