using System.Collections.ObjectModel;
using System.Globalization;
using HpskSite.Mobile.Models;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.Converters;

/// <summary>
/// Converts a participant to their ranking text color (gold/silver/bronze for top 3, white for others).
/// Uses MultiBinding with participant and rankings collection.
/// </summary>
public class RankingToColorConverter : IMultiValueConverter
{
    private static readonly Color GoldColor = Color.FromArgb("#FFD700");
    private static readonly Color SilverColor = Color.FromArgb("#C0C0C0");
    private static readonly Color BronzeColor = Color.FromArgb("#CD7F32");
    private static readonly Color DefaultColor = Color.FromArgb("#9CA3AF"); // Gray for non-podium

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return DefaultColor;

        var participant = values[0] as TrainingMatchParticipant;
        var rankings = values[1] as ObservableCollection<ParticipantRanking>;

        if (participant == null || rankings == null)
            return DefaultColor;

        var participantId = participant.MemberId ?? participant.GuestParticipantId ?? 0;
        var ranking = rankings.FirstOrDefault(r => r.ParticipantId == participantId);

        if (ranking == null)
            return DefaultColor;

        // Return the appropriate color based on ranking
        return ranking.Ranking switch
        {
            1 => GoldColor,
            2 => SilverColor,
            3 => BronzeColor,
            _ => DefaultColor
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a participant to their ranking text ("#1", "#2", "#3", etc.).
/// Uses MultiBinding with participant and rankings collection.
/// Shows ranking for ALL participants, not just top 3.
/// </summary>
public class RankingToTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return "";

        var participant = values[0] as TrainingMatchParticipant;
        var rankings = values[1] as ObservableCollection<ParticipantRanking>;

        if (participant == null || rankings == null)
            return "";

        var participantId = participant.MemberId ?? participant.GuestParticipantId ?? 0;
        var ranking = rankings.FirstOrDefault(r => r.ParticipantId == participantId);

        if (ranking == null || ranking.Ranking <= 0)
            return "";

        // Show ranking for all participants
        return $"#{ranking.Ranking}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a participant to visibility (true if has valid ranking).
/// Uses MultiBinding with participant and rankings collection.
/// Shows ranking for ALL participants with a valid ranking.
/// </summary>
public class RankingToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return false;

        var participant = values[0] as TrainingMatchParticipant;
        var rankings = values[1] as ObservableCollection<ParticipantRanking>;

        if (participant == null || rankings == null)
            return false;

        var participantId = participant.MemberId ?? participant.GuestParticipantId ?? 0;
        var ranking = rankings.FirstOrDefault(r => r.ParticipantId == participantId);

        if (ranking == null)
            return false;

        // Show for all participants with a valid ranking
        return ranking.Ranking >= 1;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Simple converter that takes a participant index and rankings to return border color.
/// Alternative approach using participant index binding.
/// </summary>
public class ParticipantRankingColorConverter : IValueConverter
{
    private static readonly Color GoldColor = Color.FromArgb("#FFD700");
    private static readonly Color SilverColor = Color.FromArgb("#C0C0C0");
    private static readonly Color BronzeColor = Color.FromArgb("#CD7F32");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int ranking || ranking <= 0)
            return Colors.Transparent;

        return ranking switch
        {
            1 => GoldColor,
            2 => SilverColor,
            3 => BronzeColor,
            _ => Colors.Transparent
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
