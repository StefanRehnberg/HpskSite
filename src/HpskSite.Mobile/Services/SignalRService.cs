using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using HpskSite.Shared.Models;
using System.Text.Json;

namespace HpskSite.Mobile.Services;

/// <summary>
/// SignalR service for real-time match updates
/// </summary>
public class SignalRService : ISignalRService, IAsyncDisposable
{
    private readonly ISecureStorageService _secureStorage;
    private HubConnection? _hubConnection;
    private string _baseUrl = "https://pistol.nu";

    public SignalRService(ISecureStorageService secureStorage)
    {
        _secureStorage = secureStorage;
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = value;
    }

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    // Events
    public event EventHandler<TrainingMatch>? MatchCreated;
    public event EventHandler<int>? ParticipantJoined;  // memberId
    public event EventHandler<int>? ParticipantLeft;    // memberId
    public event EventHandler<ScoreUpdate>? ScoreUpdated;
    public event EventHandler? MatchCompleted;
    public event EventHandler<string>? MatchDeleted;
    public event EventHandler<List<MatchSpectator>>? SpectatorListUpdated;
    public event EventHandler<JoinRequest>? JoinRequestReceived;
    public event EventHandler<string>? JoinRequestAccepted;  // matchCode
    public event EventHandler<string>? JoinRequestBlocked;   // matchCode
    public event EventHandler<ReactionUpdate>? ReactionUpdated;
    public event EventHandler<SettingsUpdate>? SettingsUpdated;
    public event EventHandler<object>? TeamScoreUpdated;
    public event EventHandler? Disconnected;
    public event EventHandler? Reconnected;

    public async Task ConnectAsync()
    {
        if (_hubConnection != null)
        {
            await DisconnectAsync();
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/hubs/trainingmatch", options =>
            {
                // Fetch token fresh for each request
                options.AccessTokenProvider = async () => await _secureStorage.GetAccessTokenAsync();
#if DEBUG
                // Bypass SSL certificate validation in debug mode
                options.HttpMessageHandlerFactory = _ =>
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                    };
                    return handler;
                };

                // On Android, WebSockets and ServerSentEvents have SSL issues that can't be bypassed
                // via HttpMessageHandlerFactory. Only LongPolling fully uses the HTTP handler.
                if (DeviceInfo.Platform == DevicePlatform.Android)
                {
                    options.Transports = HttpTransportType.LongPolling;
                }
#endif
            })
            .AddJsonProtocol(options =>
            {
                // Use camelCase to match server's JSON format
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        RegisterHandlers();

        _hubConnection.Closed += OnConnectionClosed;
        _hubConnection.Reconnected += OnReconnected;

        await _hubConnection.StartAsync();
    }

    public async Task DisconnectAsync()
    {
        if (_hubConnection != null)
        {
            _hubConnection.Closed -= OnConnectionClosed;
            _hubConnection.Reconnected -= OnReconnected;

            await _hubConnection.StopAsync();
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    public async Task JoinMatchAsync(string matchCode)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            // Not connected, skip silently
            return;
        }

        await _hubConnection.InvokeAsync("JoinMatchGroup", matchCode);
    }

    public async Task LeaveMatchAsync(string matchCode)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        await _hubConnection.InvokeAsync("LeaveMatchGroup", matchCode);
    }

    public async Task JoinOrganizerGroupAsync(string matchCode)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        await _hubConnection.InvokeAsync("JoinOrganizerGroup", matchCode);
    }

    public async Task LeaveOrganizerGroupAsync(string matchCode)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        await _hubConnection.InvokeAsync("LeaveOrganizerGroup", matchCode);
    }

    public async Task RegisterSpectatorAsync(string matchCode, int memberId, string name, string profilePictureUrl, string clubName)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        await _hubConnection.InvokeAsync("RegisterSpectator", matchCode, memberId, name, profilePictureUrl ?? "", clubName ?? "");
    }

    public async Task UnregisterSpectatorAsync(string matchCode)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        await _hubConnection.InvokeAsync("UnregisterSpectator", matchCode);
    }

    public async Task SendScoreUpdateAsync(string matchCode, int participantId, int seriesNumber, int shotNumber, int score)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException("Not connected to SignalR hub");
        }

        await _hubConnection.InvokeAsync("UpdateScore", matchCode, participantId, seriesNumber, shotNumber, score);
    }

    private void RegisterHandlers()
    {
        if (_hubConnection == null)
            return;

        _hubConnection.On<TrainingMatch>("MatchCreated", match =>
        {
            MatchCreated?.Invoke(this, match);
        });

        // Server sends: ParticipantJoined(memberId)
        _hubConnection.On<int>("ParticipantJoined", memberId =>
        {
            ParticipantJoined?.Invoke(this, memberId);
        });

        // Server sends: ParticipantLeft(memberId)
        _hubConnection.On<int>("ParticipantLeft", memberId =>
        {
            ParticipantLeft?.Invoke(this, memberId);
        });

        // Server sends: ScoreUpdated as object { memberId, seriesNumber } (from website)
        // or as two params (memberId, seriesNumber) from mobile API
        _hubConnection.On<JsonElement>("ScoreUpdated", jsonElement =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR ScoreUpdated: {jsonElement.GetRawText()}");
            var update = new ScoreUpdate();
            if (jsonElement.TryGetProperty("memberId", out var mid))
                update.MemberId = mid.GetInt32();
            if (jsonElement.TryGetProperty("seriesNumber", out var sn))
                update.SeriesNumber = sn.GetInt32();
            ScoreUpdated?.Invoke(this, update);
        });

        // Server sends: MatchCompleted() with no parameters
        _hubConnection.On("MatchCompleted", () =>
        {
            MatchCompleted?.Invoke(this, EventArgs.Empty);
        });

        _hubConnection.On<string>("MatchDeleted", matchCode =>
        {
            MatchDeleted?.Invoke(this, matchCode);
        });

        // Server sends: SpectatorListUpdated(list of spectator objects)
        // Server may use PascalCase or camelCase depending on configuration
        _hubConnection.On<JsonElement>("SpectatorListUpdated", jsonElement =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR SpectatorListUpdated RAW: {jsonElement.GetRawText()}");
            var spectators = new List<MatchSpectator>();

            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in jsonElement.EnumerateArray())
                {
                    var spectator = new MatchSpectator();
                    // Try both camelCase and PascalCase
                    if (item.TryGetProperty("memberId", out var mid) || item.TryGetProperty("MemberId", out mid))
                        spectator.MemberId = mid.GetInt32();
                    if (item.TryGetProperty("name", out var name) || item.TryGetProperty("Name", out name))
                        spectator.Name = name.GetString() ?? "";
                    if (item.TryGetProperty("profilePictureUrl", out var ppu) || item.TryGetProperty("ProfilePictureUrl", out ppu))
                        spectator.ProfilePictureUrl = ppu.GetString() ?? "";
                    if (item.TryGetProperty("clubName", out var cn) || item.TryGetProperty("ClubName", out cn))
                        spectator.ClubName = cn.GetString() ?? "";
                    spectators.Add(spectator);
                    System.Diagnostics.Debug.WriteLine($"Parsed spectator: MemberId={spectator.MemberId}, Name={spectator.Name}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"SpectatorListUpdated: {spectators.Count} spectators");
            SpectatorListUpdated?.Invoke(this, spectators);
        });

        // Server sends: JoinRequestReceived with "id" (not "requestId")
        _hubConnection.On<System.Text.Json.JsonElement>("JoinRequestReceived", jsonElement =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR RAW JoinRequestReceived: {jsonElement.GetRawText()}");

            var joinRequest = new JoinRequest();
            // Server sends "id", not "requestId"
            if (jsonElement.TryGetProperty("id", out var reqId))
                joinRequest.RequestId = reqId.GetInt32();
            if (jsonElement.TryGetProperty("matchCode", out var mc))
                joinRequest.MatchCode = mc.GetString() ?? "";
            if (jsonElement.TryGetProperty("memberId", out var mid))
                joinRequest.MemberId = mid.GetInt32();
            if (jsonElement.TryGetProperty("memberName", out var mn))
                joinRequest.MemberName = mn.GetString() ?? "";
            if (jsonElement.TryGetProperty("profilePictureUrl", out var ppu))
                joinRequest.ProfilePictureUrl = ppu.GetString();

            JoinRequestReceived?.Invoke(this, joinRequest);
        });

        // Server sends: JoinRequestAccepted(matchCode) to member_{memberId} group
        _hubConnection.On<string>("JoinRequestAccepted", matchCode =>
        {
            JoinRequestAccepted?.Invoke(this, matchCode);
        });

        // Server sends: JoinRequestBlocked(matchCode) to member_{memberId} group
        _hubConnection.On<string>("JoinRequestBlocked", matchCode =>
        {
            JoinRequestBlocked?.Invoke(this, matchCode);
        });

        // Server sends: ReactionUpdated with { targetMemberId, seriesNumber, reactions }
        _hubConnection.On<JsonElement>("ReactionUpdated", jsonElement =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR ReactionUpdated: {jsonElement.GetRawText()}");
            var update = new ReactionUpdate();

            if (jsonElement.TryGetProperty("targetMemberId", out var tmid))
                update.TargetMemberId = tmid.GetInt32();
            if (jsonElement.TryGetProperty("seriesNumber", out var sn))
                update.SeriesNumber = sn.GetInt32();
            if (jsonElement.TryGetProperty("reactions", out var reactionsElement) &&
                reactionsElement.ValueKind == JsonValueKind.Array)
            {
                update.Reactions = new List<PhotoReaction>();
                foreach (var item in reactionsElement.EnumerateArray())
                {
                    var reaction = new PhotoReaction();
                    if (item.TryGetProperty("memberId", out var mid))
                        reaction.MemberId = mid.GetInt32();
                    if (item.TryGetProperty("firstName", out var fn))
                        reaction.FirstName = fn.GetString();
                    if (item.TryGetProperty("emoji", out var em))
                        reaction.Emoji = em.GetString() ?? "";
                    update.Reactions.Add(reaction);
                }
            }

            ReactionUpdated?.Invoke(this, update);
        });

        // Server sends: SettingsUpdated with { maxSeriesCount }
        _hubConnection.On<JsonElement>("SettingsUpdated", jsonElement =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR SettingsUpdated: {jsonElement.GetRawText()}");
            var update = new SettingsUpdate();

            if (jsonElement.TryGetProperty("maxSeriesCount", out var msc))
            {
                if (msc.ValueKind == JsonValueKind.Null)
                    update.MaxSeriesCount = null;
                else
                    update.MaxSeriesCount = msc.GetInt32();
            }

            SettingsUpdated?.Invoke(this, update);
        });

        // Server sends: TeamScoreUpdated with team scores data
        _hubConnection.On<JsonElement>("TeamScoreUpdated", jsonElement =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR TeamScoreUpdated: {jsonElement.GetRawText()}");
            TeamScoreUpdated?.Invoke(this, jsonElement);
        });
    }

    private Task OnConnectionClosed(Exception? exception)
    {
        Disconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private Task OnReconnected(string? connectionId)
    {
        Reconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}

public class ScoreUpdate
{
    public int MemberId { get; set; }
    public int SeriesNumber { get; set; }
}

public class ReactionUpdate
{
    public int TargetMemberId { get; set; }
    public int SeriesNumber { get; set; }
    public List<PhotoReaction>? Reactions { get; set; }
}

public class SettingsUpdate
{
    public int? MaxSeriesCount { get; set; }
}

public class MatchSpectator
{
    public int MemberId { get; set; }
    public string Name { get; set; } = "";
    public string ProfilePictureUrl { get; set; } = "";
    public string ClubName { get; set; } = "";

    /// <summary>
    /// Gets initials from the Name (first letter of first two words)
    /// </summary>
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "?";
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0][0]}{parts[1][0]}".ToUpper();
            if (parts.Length == 1 && parts[0].Length >= 2)
                return parts[0].Substring(0, 2).ToUpper();
            if (parts.Length == 1)
                return parts[0][0].ToString().ToUpper();
            return "?";
        }
    }
}

public class JoinRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public int RequestId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("matchCode")]
    public string MatchCode { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("memberId")]
    public int MemberId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("memberName")]
    public string MemberName { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("profilePictureUrl")]
    public string? ProfilePictureUrl { get; set; }
}

public interface ISignalRService
{
    string BaseUrl { get; set; }
    bool IsConnected { get; }

    event EventHandler<TrainingMatch>? MatchCreated;
    event EventHandler<int>? ParticipantJoined;
    event EventHandler<int>? ParticipantLeft;
    event EventHandler<ScoreUpdate>? ScoreUpdated;
    event EventHandler? MatchCompleted;
    event EventHandler<string>? MatchDeleted;
    event EventHandler<List<MatchSpectator>>? SpectatorListUpdated;
    event EventHandler<JoinRequest>? JoinRequestReceived;
    event EventHandler<string>? JoinRequestAccepted;
    event EventHandler<string>? JoinRequestBlocked;
    event EventHandler<ReactionUpdate>? ReactionUpdated;
    event EventHandler<SettingsUpdate>? SettingsUpdated;
    event EventHandler<object>? TeamScoreUpdated;
    event EventHandler? Disconnected;
    event EventHandler? Reconnected;

    Task ConnectAsync();
    Task DisconnectAsync();
    Task JoinMatchAsync(string matchCode);
    Task LeaveMatchAsync(string matchCode);
    Task JoinOrganizerGroupAsync(string matchCode);
    Task LeaveOrganizerGroupAsync(string matchCode);
    Task RegisterSpectatorAsync(string matchCode, int memberId, string name, string profilePictureUrl, string clubName);
    Task UnregisterSpectatorAsync(string matchCode);
    Task SendScoreUpdateAsync(string matchCode, int participantId, int seriesNumber, int shotNumber, int score);
}
