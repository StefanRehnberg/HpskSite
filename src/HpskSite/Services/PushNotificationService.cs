using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using HpskSite.Migrations;
using HpskSite.Shared.DTOs;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for sending push notifications via Firebase Cloud Messaging
    /// and managing device registrations
    /// </summary>
    public class PushNotificationService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PushNotificationService> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly bool _firebaseInitialized;

        public PushNotificationService(
            IScopeProvider scopeProvider,
            IConfiguration configuration,
            ILogger<PushNotificationService> logger,
            IWebHostEnvironment environment)
        {
            _scopeProvider = scopeProvider;
            _configuration = configuration;
            _logger = logger;
            _environment = environment;

            // Initialize Firebase if credentials are configured
            _firebaseInitialized = InitializeFirebase();
        }

        private bool InitializeFirebase()
        {
            try
            {
                if (FirebaseApp.DefaultInstance != null)
                {
                    return true;
                }

                var credentialPath = _configuration["Firebase:CredentialPath"];
                if (string.IsNullOrEmpty(credentialPath))
                {
                    _logger.LogWarning("Firebase:CredentialPath not configured. Push notifications will be disabled.");
                    return false;
                }

                // Resolve relative path from application root
                var fullPath = Path.IsPathRooted(credentialPath)
                    ? credentialPath
                    : Path.Combine(_environment.ContentRootPath, credentialPath);

                if (!File.Exists(fullPath))
                {
                    _logger.LogWarning("Firebase credentials file not found at {Path}. Push notifications will be disabled.", fullPath);
                    return false;
                }

                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromFile(fullPath)
                });

                _logger.LogWarning("Firebase initialized successfully from {Path}", fullPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Firebase");
                return false;
            }
        }

        /// <summary>
        /// Send push notification when a new match is created
        /// </summary>
        public async Task SendMatchCreatedNotificationAsync(
            string matchCode,
            string matchName,
            string creatorName,
            string weaponClass,
            bool isOpen)
        {
            if (!_firebaseInitialized)
            {
                _logger.LogWarning("Firebase not initialized, skipping push notification for match {MatchCode}", matchCode);
                return;
            }

            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;

                // Get device tokens based on notification preferences
                // If match is open: send to all who have OpenMatchesOnly OR All
                // If match is closed: send only to those with All preference
                var devices = isOpen
                    ? await db.FetchAsync<DeviceRegistrationDto>(
                        @"SELECT * FROM DeviceRegistrations
                          WHERE NotificationsEnabled = 1
                          AND (NotificationPreference = 'OpenMatchesOnly' OR NotificationPreference = 'All')")
                    : await db.FetchAsync<DeviceRegistrationDto>(
                        @"SELECT * FROM DeviceRegistrations
                          WHERE NotificationsEnabled = 1
                          AND NotificationPreference = 'All'");

                scope.Complete();

                if (!devices.Any())
                {
                    _logger.LogWarning("No devices to notify for match {MatchCode}", matchCode);
                    return;
                }

                _logger.LogWarning("Sending notifications to {DeviceCount} devices for match {MatchCode}", devices.Count, matchCode);

                var tokens = devices.Select(d => d.DeviceToken).ToList();
                var displayName = string.IsNullOrEmpty(matchName) ? matchCode : matchName;

                var message = new MulticastMessage
                {
                    Tokens = tokens,
                    Notification = new Notification
                    {
                        Title = "Ny träningsmatch!",
                        Body = $"{creatorName} skapade '{displayName}' ({weaponClass})"
                    },
                    Data = new Dictionary<string, string>
                    {
                        { "matchCode", matchCode },
                        { "isOpen", isOpen.ToString().ToLower() },
                        { "type", "match_created" }
                    },
                    Android = new AndroidConfig
                    {
                        Priority = Priority.High,
                        Notification = new AndroidNotification
                        {
                            ChannelId = "match_notifications",
                            Icon = "ic_notification"
                        }
                    },
                    Apns = new ApnsConfig
                    {
                        Aps = new Aps
                        {
                            Sound = "default",
                            Badge = 1
                        }
                    }
                };

                var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message);
                _logger.LogInformation(
                    "Sent {SuccessCount} notifications, {FailureCount} failures for match {MatchCode}",
                    response.SuccessCount, response.FailureCount, matchCode);

                // Clean up invalid/unregistered tokens
                if (response.FailureCount > 0)
                {
                    await CleanupFailedTokensAsync(response, tokens);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push notifications for match {MatchCode}", matchCode);
            }
        }

        /// <summary>
        /// Register a device for push notifications
        /// </summary>
        public async Task RegisterDeviceAsync(int memberId, string deviceToken, string platform)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;

                // Check if token already exists
                var existing = await db.FirstOrDefaultAsync<DeviceRegistrationDto>(
                    "WHERE DeviceToken = @0", deviceToken);

                var now = DateTime.UtcNow;

                if (existing != null)
                {
                    // Update existing registration
                    existing.MemberId = memberId;
                    existing.Platform = platform;
                    existing.UpdatedDateUtc = now;
                    await db.UpdateAsync(existing);
                    _logger.LogInformation("Updated device registration for member {MemberId}", memberId);
                }
                else
                {
                    // Create new registration
                    var registration = new DeviceRegistrationDto
                    {
                        MemberId = memberId,
                        DeviceToken = deviceToken,
                        Platform = platform,
                        NotificationPreference = "OpenMatchesOnly",
                        NotificationsEnabled = true,
                        CreatedDateUtc = now,
                        UpdatedDateUtc = now
                    };
                    await db.InsertAsync(registration);
                    _logger.LogInformation("Registered new device for member {MemberId}", memberId);
                }

                scope.Complete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register device for member {MemberId}", memberId);
                throw;
            }
        }

        /// <summary>
        /// Unregister a device from push notifications
        /// </summary>
        public async Task UnregisterDeviceAsync(string deviceToken)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;

                await db.ExecuteAsync("DELETE FROM DeviceRegistrations WHERE DeviceToken = @0", deviceToken);
                scope.Complete();

                _logger.LogInformation("Unregistered device with token ending in ...{TokenEnd}",
                    deviceToken.Length > 10 ? deviceToken[^10..] : deviceToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unregister device");
                throw;
            }
        }

        /// <summary>
        /// Update notification preferences for a member
        /// </summary>
        public async Task UpdatePreferencesAsync(int memberId, bool enabled, string preference)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;

                // Update all devices for this member
                await db.ExecuteAsync(
                    @"UPDATE DeviceRegistrations
                      SET NotificationsEnabled = @0, NotificationPreference = @1, UpdatedDateUtc = @2
                      WHERE MemberId = @3",
                    enabled, preference, DateTime.UtcNow, memberId);

                scope.Complete();

                _logger.LogInformation("Updated notification preferences for member {MemberId}: Enabled={Enabled}, Preference={Preference}",
                    memberId, enabled, preference);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update preferences for member {MemberId}", memberId);
                throw;
            }
        }

        /// <summary>
        /// Get notification preferences for a member
        /// </summary>
        public async Task<NotificationPreferencesResponse> GetPreferencesAsync(int memberId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;

                // Get first device registration for this member (all should have same preferences)
                var registration = await db.FirstOrDefaultAsync<DeviceRegistrationDto>(
                    "WHERE MemberId = @0", memberId);

                scope.Complete();

                if (registration == null)
                {
                    // Return defaults if no device registered
                    return new NotificationPreferencesResponse
                    {
                        NotificationsEnabled = true,
                        NotificationPreference = "OpenMatchesOnly"
                    };
                }

                return new NotificationPreferencesResponse
                {
                    NotificationsEnabled = registration.NotificationsEnabled,
                    NotificationPreference = registration.NotificationPreference
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get preferences for member {MemberId}", memberId);
                throw;
            }
        }

        /// <summary>
        /// Clean up tokens that failed to receive notifications (unregistered devices)
        /// </summary>
        private async Task CleanupFailedTokensAsync(BatchResponse response, List<string> tokens)
        {
            try
            {
                var failedTokens = response.Responses
                    .Select((r, i) => (Response: r, Token: tokens[i]))
                    .Where(x => !x.Response.IsSuccess &&
                                x.Response.Exception?.MessagingErrorCode == MessagingErrorCode.Unregistered)
                    .Select(x => x.Token)
                    .ToList();

                if (failedTokens.Any())
                {
                    _logger.LogInformation("Cleaning up {Count} unregistered device tokens", failedTokens.Count);
                    foreach (var token in failedTokens)
                    {
                        await UnregisterDeviceAsync(token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup unregistered tokens");
            }
        }
    }
}
