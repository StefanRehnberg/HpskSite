using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Hubs
{
    /// <summary>
    /// Represents a spectator viewing a match
    /// </summary>
    public class MatchSpectator
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string ProfilePictureUrl { get; set; } = "";
        public string ClubName { get; set; } = "";
        public string ConnectionId { get; set; } = "";
    }

    /// <summary>
    /// SignalR Hub for real-time training match communication
    /// Handles join requests, score updates, and match events
    /// </summary>
    public class TrainingMatchHub : Hub
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        // Track spectators per match (matchCode -> list of spectators)
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MatchSpectator>> _matchSpectators = new();

        // Track which match each connection is viewing (connectionId -> matchCode)
        private static readonly ConcurrentDictionary<string, string> _connectionMatches = new();

        public TrainingMatchHub(IMemberManager memberManager, IMemberService memberService)
        {
            _memberManager = memberManager;
            _memberService = memberService;
        }

        /// <summary>
        /// Join a match group to receive real-time updates for that match
        /// Called when a user views or participates in a match
        /// </summary>
        public async Task JoinMatchGroup(string matchCode)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"match_{matchCode.ToUpperInvariant()}");
        }

        /// <summary>
        /// Leave a match group
        /// Called when a user navigates away from a match
        /// </summary>
        public async Task LeaveMatchGroup(string matchCode)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"match_{matchCode.ToUpperInvariant()}");
        }

        /// <summary>
        /// Register as a spectator for a match
        /// </summary>
        public async Task RegisterSpectator(string matchCode, int memberId, string name, string profilePictureUrl, string clubName)
        {
            var normalizedCode = matchCode.ToUpperInvariant();

            // Remove from previous match if any
            if (_connectionMatches.TryGetValue(Context.ConnectionId, out var previousMatch))
            {
                await UnregisterSpectatorInternal(previousMatch, Context.ConnectionId);
            }

            // Add to new match
            var spectators = _matchSpectators.GetOrAdd(normalizedCode, _ => new ConcurrentDictionary<string, MatchSpectator>());
            var spectator = new MatchSpectator
            {
                MemberId = memberId,
                Name = name,
                ProfilePictureUrl = profilePictureUrl,
                ClubName = clubName,
                ConnectionId = Context.ConnectionId
            };
            spectators[Context.ConnectionId] = spectator;
            _connectionMatches[Context.ConnectionId] = normalizedCode;

            // Broadcast updated spectator list to all match viewers
            await BroadcastSpectatorList(normalizedCode);
        }

        /// <summary>
        /// Unregister as a spectator from current match
        /// </summary>
        public async Task UnregisterSpectator(string matchCode)
        {
            var normalizedCode = matchCode.ToUpperInvariant();
            await UnregisterSpectatorInternal(normalizedCode, Context.ConnectionId);
            await BroadcastSpectatorList(normalizedCode);
        }

        private async Task UnregisterSpectatorInternal(string matchCode, string connectionId)
        {
            if (_matchSpectators.TryGetValue(matchCode, out var spectators))
            {
                spectators.TryRemove(connectionId, out _);
            }
            _connectionMatches.TryRemove(connectionId, out _);
            await Task.CompletedTask;
        }

        private async Task BroadcastSpectatorList(string matchCode)
        {
            // matchCode should already be normalized by callers
            var spectatorList = GetSpectatorList(matchCode);
            await Clients.Group($"match_{matchCode}").SendAsync("SpectatorListUpdated", spectatorList);
        }

        /// <summary>
        /// Get current spectators for a match
        /// </summary>
        public static List<MatchSpectator> GetSpectatorList(string matchCode)
        {
            var normalizedCode = matchCode.ToUpperInvariant();
            if (_matchSpectators.TryGetValue(normalizedCode, out var spectators))
            {
                return spectators.Values.ToList();
            }
            return new List<MatchSpectator>();
        }

        /// <summary>
        /// Join the organizer notification group for a match
        /// Called when a match creator wants to receive join requests
        /// </summary>
        public async Task JoinOrganizerGroup(string matchCode)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"organizer_{matchCode.ToUpperInvariant()}");
        }

        /// <summary>
        /// Leave the organizer notification group
        /// </summary>
        public async Task LeaveOrganizerGroup(string matchCode)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"organizer_{matchCode.ToUpperInvariant()}");
        }

        /// <summary>
        /// Called when a client connects
        /// Adds user to their personal notification group if logged in
        /// Supports both cookie-based (web) and JWT-based (mobile) authentication
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            int? memberId = null;

            // First, try to get member ID from JWT claims (mobile app)
            var memberIdClaim = Context.User?.FindFirst("memberId")?.Value;
            if (!string.IsNullOrEmpty(memberIdClaim) && int.TryParse(memberIdClaim, out var jwtMemberId))
            {
                memberId = jwtMemberId;
            }
            else
            {
                // Fallback to cookie-based authentication (web)
                try
                {
                    var member = await _memberManager.GetCurrentMemberAsync();
                    if (member != null)
                    {
                        var memberData = _memberService.GetByEmail(member.Email ?? "");
                        memberId = memberData?.Id;
                    }
                }
                catch
                {
                    // Cookie auth not available (e.g., JWT-only connection), continue without member group
                }
            }

            if (memberId.HasValue)
            {
                // Add to personal notification group for direct messages
                await Groups.AddToGroupAsync(Context.ConnectionId, $"member_{memberId.Value}");
            }

            await base.OnConnectedAsync();
        }

        /// <summary>
        /// Called when a client disconnects
        /// Supports both cookie-based (web) and JWT-based (mobile) authentication
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // Clean up spectator tracking
            if (_connectionMatches.TryRemove(Context.ConnectionId, out var matchCode))
            {
                if (_matchSpectators.TryGetValue(matchCode, out var spectators))
                {
                    spectators.TryRemove(Context.ConnectionId, out _);
                    // Broadcast updated list
                    await Clients.Group($"match_{matchCode}").SendAsync("SpectatorListUpdated", spectators.Values.ToList());
                }
            }

            int? memberId = null;

            // First, try to get member ID from JWT claims (mobile app)
            var memberIdClaim = Context.User?.FindFirst("memberId")?.Value;
            if (!string.IsNullOrEmpty(memberIdClaim) && int.TryParse(memberIdClaim, out var jwtMemberId))
            {
                memberId = jwtMemberId;
            }
            else
            {
                // Fallback to cookie-based authentication (web)
                try
                {
                    var member = await _memberManager.GetCurrentMemberAsync();
                    if (member != null)
                    {
                        var memberData = _memberService.GetByEmail(member.Email ?? "");
                        memberId = memberData?.Id;
                    }
                }
                catch
                {
                    // Cookie auth not available, continue without cleanup
                }
            }

            if (memberId.HasValue)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"member_{memberId.Value}");
            }

            await base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>
    /// Static helper class for sending SignalR notifications from controllers
    /// </summary>
    public static class TrainingMatchHubExtensions
    {
        /// <summary>
        /// Send join request notification to match organizer
        /// </summary>
        public static async Task SendJoinRequestToOrganizer(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode,
            object joinRequest)
        {
            await hubContext.Clients.Group($"organizer_{matchCode.ToUpperInvariant()}")
                .SendAsync("JoinRequestReceived", joinRequest);
        }

        /// <summary>
        /// Send join request accepted notification to requester
        /// </summary>
        public static async Task SendJoinRequestAccepted(
            this IHubContext<TrainingMatchHub> hubContext,
            int memberId,
            string matchCode)
        {
            await hubContext.Clients.Group($"member_{memberId}")
                .SendAsync("JoinRequestAccepted", matchCode);
        }

        /// <summary>
        /// Send join request blocked notification to requester
        /// </summary>
        public static async Task SendJoinRequestBlocked(
            this IHubContext<TrainingMatchHub> hubContext,
            int memberId,
            string matchCode)
        {
            await hubContext.Clients.Group($"member_{memberId}")
                .SendAsync("JoinRequestBlocked", matchCode);
        }

        /// <summary>
        /// Notify all match viewers that a new participant joined
        /// </summary>
        public static async Task SendParticipantJoined(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode,
            object participant)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("ParticipantJoined", participant);
        }

        /// <summary>
        /// Notify all match viewers that a participant left
        /// </summary>
        public static async Task SendParticipantLeft(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode,
            int memberId)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("ParticipantLeft", memberId);
        }

        /// <summary>
        /// Notify all match viewers that a score was updated
        /// </summary>
        public static async Task SendScoreUpdated(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode,
            object scoreData)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("ScoreUpdated", scoreData);
        }

        /// <summary>
        /// Notify all match viewers that the match was completed
        /// </summary>
        public static async Task SendMatchCompleted(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("MatchCompleted", matchCode);
        }

        /// <summary>
        /// Notify all match viewers that the match data should be refreshed
        /// </summary>
        public static async Task SendMatchRefresh(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("MatchRefresh", matchCode);
        }

        /// <summary>
        /// Notify all match viewers that a scheduled match has started
        /// </summary>
        public static async Task SendMatchStarted(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("MatchStarted", matchCode);
        }

        /// <summary>
        /// Notify all match viewers that match settings were updated
        /// </summary>
        public static async Task SendSettingsUpdated(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode,
            int? maxSeriesCount)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("SettingsUpdated", new { maxSeriesCount });
        }

        /// <summary>
        /// Notify all match viewers that team scores were updated
        /// </summary>
        public static async Task SendTeamScoreUpdated(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode,
            object teamScores)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("TeamScoreUpdated", teamScores);
        }

        /// <summary>
        /// Notify all match viewers that a match was deleted
        /// </summary>
        public static async Task SendMatchDeleted(
            this IHubContext<TrainingMatchHub> hubContext,
            string matchCode)
        {
            await hubContext.Clients.Group($"match_{matchCode.ToUpperInvariant()}")
                .SendAsync("MatchDeleted", matchCode);
        }
    }
}
