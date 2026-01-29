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

        // New properties for landing page
        public IPublishedContent? EventImage => this.Value<IPublishedContent>("eventImage");
        public bool RegistrationRequired => this.Value<bool>("registrationRequired", fallback: Fallback.ToDefaultValue, defaultValue: false);
        public string FeeAmount => this.Value<string>("feeAmount") ?? "";
        public string EquipmentRequired => this.Value<string>("equipmentRequired") ?? "";
        public string TargetAudience => this.Value<string>("targetAudience") ?? "";

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

        public string GetEventTypeColor()
        {
            return EventType switch
            {
                "Tävling" => "#0d6efd",  // Blue
                "Träning" => "#198754",  // Green
                "Städning" => "#d63384", // Pink
                "Möte" => "#fd7e14",     // Orange
                "Socialt" => "#0dcaf0",  // Cyan
                "Annat" => "#6c757d",    // Gray
                _ => "#6c757d"
            };
        }

        public string GetEventTypeIcon()
        {
            return EventType switch
            {
                "Tävling" => "bi-trophy",
                "Träning" => "bi-bullseye",
                "Städning" => "bi-bucket",
                "Möte" => "bi-megaphone",
                "Socialt" => "bi-people",
                "Annat" => "bi-calendar-event",
                _ => "bi-calendar-event"
            };
        }

        public string GetEventTypeEmoji()
        {
            return EventType switch
            {
                "Tävling" => "🏆",
                "Träning" => "🎯",
                "Städning" => "🧹",
                "Möte" => "📢",
                "Socialt" => "🎉",
                "Annat" => "📌",
                _ => "📌"
            };
        }

        public string GetBootstrapBadgeClass()
        {
            return EventType switch
            {
                "Tävling" => "primary",
                "Träning" => "success",
                "Städning" => "pink",   // Custom CSS class needed
                "Möte" => "warning",
                "Socialt" => "info",
                "Annat" => "secondary",
                _ => "secondary"
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
