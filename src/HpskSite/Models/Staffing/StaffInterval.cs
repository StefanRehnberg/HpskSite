using System;

namespace HpskSite.Models.Staffing
{
    /// <summary>Delens dagsprogram — vad "heldag" betyder i klockslag. Förval 08–17 (Expertgruppen 1c, B10).</summary>
    public readonly record struct DayProgram(TimeSpan Start, TimeSpan End)
    {
        public static readonly DayProgram Default = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17));

        /// <summary>Ett program som slutar före eller samtidigt med att det börjar är ett fel i uppgifterna — då gäller förvalet.</summary>
        public DayProgram OrDefault() => End > Start && Start >= TimeSpan.Zero && End <= TimeSpan.FromDays(1) ? this : Default;
    }

    /// <summary>
    /// Expertgruppen 1c (2026-10-25): <b>ett tidsintervall som klarar heldag och midnatt</b>, använt av krockarna och
    /// <see cref="PersonWeek"/>. Rena funktioner, ingen databas.
    /// <list type="bullet">
    /// <item><b>Heldag</b> (varken start eller slut) = delens dagsprogram. Förr var heldag "ingen tid" och krockade aldrig
    /// — Fält-sekretariat heldag + Precision 13–15 syntes aldrig som en krock.</item>
    /// <item><b>Midnatt</b>: slut före eller lika med start = slutet nästa dag (nattbevakning 22–02). Start lika med slut
    /// är ett fel och ger inget intervall.</item>
    /// <item><b>Bara ena änden</b> angiven = okänt (null). Ett intervall hittas aldrig på; ett okänt intervall krockar aldrig.</item>
    /// <item><b>Kant i kant</b> är ingen överlappning — en överlämning 13:00 är inte två samtidigt.</item>
    /// <item><b>Platsbyte</b>: olika kända platser med mindre än marginalen emellan är en trång förflyttning (gul varning,
    /// aldrig stopp).</item>
    /// </list>
    /// </summary>
    public readonly record struct StaffInterval(DateTime Start, DateTime End, bool AllDay)
    {
        /// <summary>Förvald marginal vid platsbyte (beslut B10).</summary>
        public static readonly TimeSpan DefaultTravelMargin = TimeSpan.FromMinutes(30);

        public TimeSpan Length => End - Start;

        /// <summary>Slutar efter midnatt nästa dag. Slutar det precis 00:00 är det fortfarande samma dag.</summary>
        public bool CrossesMidnight => End > Start.Date.AddDays(1);

        /// <summary>
        /// Intervallet för en rad. Bägge tiderna null = heldag (delens dagsprogram). Bara en av dem = okänt (null).
        /// </summary>
        public static StaffInterval? From(DateTime date, TimeSpan? start, TimeSpan? end, DayProgram? program = null)
        {
            var day = date.Date;
            if (start == null && end == null)
            {
                var p = (program ?? DayProgram.Default).OrDefault();
                return new StaffInterval(day + p.Start, day + p.End, AllDay: true);
            }
            if (start == null || end == null) return null;
            if (start < TimeSpan.Zero || start >= TimeSpan.FromDays(1) || end < TimeSpan.Zero || end > TimeSpan.FromDays(1)) return null;
            if (start == end) return null;

            var s = day + start.Value;
            var e = day + end.Value;
            if (e <= s) e = e.AddDays(1);   // nattpass
            return new StaffInterval(s, e, AllDay: false);
        }

        /// <summary>Med absoluta klockslag, t.ex. ur <c>StaffAssignment.StartsAt/EndsAt</c>. Båda null = okänt (heldag har ingen dag här — använd <see cref="From"/>).</summary>
        public static StaffInterval? FromMoments(DateTime? startsAt, DateTime? endsAt)
        {
            if (startsAt == null || endsAt == null || endsAt <= startsAt) return null;
            return new StaffInterval(startsAt.Value, endsAt.Value, AllDay: false);
        }

        /// <summary>Kant i kant räknas inte.</summary>
        public bool Overlaps(StaffInterval other) => Start < other.End && other.Start < End;

        /// <summary>Tiden mellan två intervall; noll när de överlappar eller möts.</summary>
        public TimeSpan GapTo(StaffInterval other)
        {
            if (Overlaps(other)) return TimeSpan.Zero;
            return End <= other.Start ? other.Start - End : Start - other.End;
        }

        /// <summary>Samma plats = samma nyckel (skiftläge och blanksteg spelar ingen roll); okänd plats på någon sida = ingen skillnad.</summary>
        public static bool DifferentPlaces(string? a, string? b)
        {
            var x = a?.Trim();
            var y = b?.Trim();
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return false;
            return !string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Hur två tider förhåller sig: överlappar (krock), för trångt mellan två platser, eller inget.</summary>
        public static ClashKind Between(StaffInterval a, string? placeA, StaffInterval b, string? placeB, TimeSpan? travelMargin = null)
        {
            if (a.Overlaps(b)) return ClashKind.Overlap;
            if (DifferentPlaces(placeA, placeB) && a.GapTo(b) < (travelMargin ?? DefaultTravelMargin)) return ClashKind.TightTravel;
            return ClashKind.None;
        }
    }

    public enum ClashKind
    {
        None = 0,
        /// <summary>Samtidigt — en riktig krock.</summary>
        Overlap = 1,
        /// <summary>Olika platser och mindre tid emellan än marginalen — en varning.</summary>
        TightTravel = 2,
        /// <summary>Personen har sagt att hen tänker tävla den dagen och har samtidigt ett uppdrag — en varning.</summary>
        Intent = 3,
    }
}
