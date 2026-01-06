using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;

namespace HpskSite.Models
{
    /// <summary>
    /// Represents a simple club event (training session, practice, social event, etc.)
    /// These are lightweight events created by clubs separate from competitions
    /// </summary>
    public class ClubSimpleEvent : BasePage
    {
        public ClubSimpleEvent(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Basic event properties
        public string EventName => this.Value<string>("eventName") ?? this.Name;
        public string Description => this.Value<string>("description") ?? "";
        public DateTime EventDate => this.Value<DateTime>("eventDate", fallback: Fallback.ToDefaultValue, defaultValue: DateTime.Today);
        public DateTime? EventEndDate => this.Value<DateTime?>("eventEndDate");
        public string EventType => this.Value<string>("eventType") ?? "Träning"; // Training, Practice, Social, Workshop, etc.
        public string Venue => this.Value<string>("venue") ?? "";
        public string ContactPerson => this.Value<string>("contactPerson") ?? "";
        public string ContactEmail => this.Value<string>("contactEmail") ?? "";
        public string ContactPhone => this.Value<string>("contactPhone") ?? "";
        public bool IsActive => this.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true);

        // Club reference - which club created this event
        public int ClubId => this.Value<int>("clubId", fallback: Fallback.ToDefaultValue, defaultValue: 0);

        // Event capacity
        public int MaxParticipants => this.Value<int>("maxParticipants", fallback: Fallback.ToDefaultValue, defaultValue: 0);

        // Display helpers
        public string GetEventTypeDisplay()
        {
            return EventType switch
            {
                "Tävling" => "🏆 Tävling",
                "Träning" => "🏹 Träning",
                "Städning" => "🧹 Städning",
                "Möte" => "📢 Möte",
                "Socialt" => "🎉 Socialt",
                "Annat" => "📌 Annat",
                _ => EventType
            };
        }

        // Date range properties
        private bool HasValidEndDate => EventEndDate.HasValue &&
                                        EventEndDate.Value > DateTime.MinValue &&
                                        EventEndDate.Value.Year > 1900 &&
                                        EventEndDate.Value.Date >= EventDate.Date;

        public bool IsMultiDay => HasValidEndDate && EventEndDate!.Value.Date != EventDate.Date;
        public bool IsSingleDay => !IsMultiDay;
        public int DurationDays => IsMultiDay ? (EventEndDate!.Value.Date - EventDate.Date).Days + 1 : 1;

        public string GetDateDisplay()
        {
            if (IsMultiDay)
            {
                return $"{EventDate:yyyy-MM-dd} - {EventEndDate!.Value:yyyy-MM-dd}";
            }
            return EventDate.ToString("yyyy-MM-dd");
        }

        public string GetDateDisplayWithTime()
        {
            if (IsMultiDay)
            {
                return $"{EventDate:MMM dd} - {EventEndDate!.Value:MMM dd, yyyy}";
            }
            return $"{EventDate:MMM dd, yyyy}";
        }

        public string GetDurationDisplay()
        {
            if (IsMultiDay)
            {
                return $"{DurationDays} dagar";
            }
            return "1 dag";
        }

        // Status calculation (simplified)
        public bool IsUpcoming => EventDate.Date >= DateTime.Now.Date;
        public bool IsOngoing => !IsUpcoming && (HasValidEndDate && EventEndDate!.Value.Date >= DateTime.Now.Date || !HasValidEndDate && EventDate.Date == DateTime.Now.Date);
        public bool IsPast => !IsUpcoming && !IsOngoing;

        public string GetStatusDisplay()
        {
            return (IsActive, IsUpcoming, IsOngoing, IsPast) switch
            {
                (false, _, _, _) => "Inaktiv",
                (_, true, _, _) => "Kommande",
                (_, _, true, _) => "Pågår",
                (_, _, _, true) => "Avslutad",
                _ => "Okänd status"
            };
        }

        public string GetStatusColor()
        {
            return (IsActive, IsUpcoming, IsOngoing, IsPast) switch
            {
                (false, _, _, _) => "#757575", // Dark gray - Inactive
                (_, true, _, _) => "#2196F3",  // Blue - Upcoming
                (_, _, true, _) => "#FF5722",  // Red-orange - Ongoing
                (_, _, _, true) => "#9E9E9E",  // Gray - Past
                _ => "#9E9E9E"
            };
        }
    }
}
