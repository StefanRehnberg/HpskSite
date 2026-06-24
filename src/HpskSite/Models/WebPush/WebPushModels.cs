namespace HpskSite.Models.WebPush
{
    /// <summary>Mirrors the WebPushSubscription table.</summary>
    public class WebPushSubscriptionRow
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public string Endpoint { get; set; } = "";
        public string P256dh { get; set; } = "";
        public string Auth { get; set; } = "";
        public string? UserAgent { get; set; }
        public string MatchPref { get; set; } = "OpenMatchesOnly"; // All | OpenMatchesOnly | Off
        public bool RankingEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
    }

    /// <summary>Shape posted by the browser's PushSubscription.toJSON().</summary>
    public class WebPushSubscribeRequest
    {
        public string? Endpoint { get; set; }
        public WebPushKeys? Keys { get; set; }
    }

    public class WebPushKeys
    {
        public string? P256dh { get; set; }
        public string? Auth { get; set; }
    }

    public class WebPushUnsubscribeRequest
    {
        public string? Endpoint { get; set; }
    }

    public class WebPushPrefsRequest
    {
        public string? Endpoint { get; set; }
        public string? MatchPref { get; set; }
        public bool RankingEnabled { get; set; } = true;
    }
}
