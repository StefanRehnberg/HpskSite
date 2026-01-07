using System.Collections.ObjectModel;
using HpskSite.Shared.Models;

namespace HpskSite.Mobile.Models;

/// <summary>
/// Represents a row in the scoreboard (either a score row or a subtotal row)
/// </summary>
public class ScoreboardRow
{
    /// <summary>
    /// 0-based series index for score rows, or the last series included for subtotal rows
    /// </summary>
    public int SeriesIndex { get; set; }

    /// <summary>
    /// Type of row (Score or Subtotal)
    /// </summary>
    public ScoreboardRowType RowType { get; set; }

    /// <summary>
    /// For subtotal rows: how many series are included in the subtotal (1-based count)
    /// </summary>
    public int SubtotalUpToSeries { get; set; }

    /// <summary>
    /// Display label for subtotal rows (e.g., "Sum 1-6")
    /// </summary>
    public string SubtotalLabel => RowType == ScoreboardRowType.Subtotal
        ? $"Sum 1-{SubtotalUpToSeries}"
        : string.Empty;

    /// <summary>
    /// Pre-computed cells for each participant in this row.
    /// This avoids complex binding with series index in converters.
    /// </summary>
    public ObservableCollection<ScoreboardCell> Cells { get; set; } = new();

    /// <summary>
    /// Whether this is a score row (not a subtotal)
    /// </summary>
    public bool IsScoreRow => RowType == ScoreboardRowType.Score;

    /// <summary>
    /// Whether this is a subtotal row
    /// </summary>
    public bool IsSubtotalRow => RowType == ScoreboardRowType.Subtotal;
}

/// <summary>
/// Represents a single cell in the scoreboard (one participant's score for one series)
/// </summary>
public class ScoreboardCell
{
    /// <summary>
    /// Score text to display (e.g., "48" or "-")
    /// </summary>
    public string ScoreText { get; set; } = "-";

    /// <summary>
    /// X count text (e.g., "3x" or empty)
    /// </summary>
    public string XCountText { get; set; } = "";

    /// <summary>
    /// Whether to show the X count label
    /// </summary>
    public bool HasXCount { get; set; }

    /// <summary>
    /// Whether this cell has a score
    /// </summary>
    public bool HasScore { get; set; }

    /// <summary>
    /// Background color hex (green if has score, gray if empty)
    /// </summary>
    public string BackgroundColorHex { get; set; } = "#374151";

    /// <summary>
    /// Series number (1-based) for this cell
    /// </summary>
    public int SeriesNumber { get; set; }

    /// <summary>
    /// Member ID of the participant this cell belongs to
    /// </summary>
    public int MemberId { get; set; }

    /// <summary>
    /// Whether this cell belongs to the current user
    /// </summary>
    public bool IsCurrentUserCell { get; set; }

    /// <summary>
    /// Whether this cell can be edited (current user + has score + match active)
    /// </summary>
    public bool CanEdit { get; set; }

    /// <summary>
    /// URL to the target photo for this series (if uploaded)
    /// </summary>
    public string? TargetPhotoUrl { get; set; }

    /// <summary>
    /// Individual shots for this series (for editing)
    /// </summary>
    public List<string>? Shots { get; set; }

    /// <summary>
    /// Entry method used for this series
    /// </summary>
    public string? EntryMethod { get; set; }

    /// <summary>
    /// Whether this cell has a target photo
    /// </summary>
    public bool HasPhoto => !string.IsNullOrEmpty(TargetPhotoUrl);

    /// <summary>
    /// Reactions on this series photo
    /// </summary>
    public List<PhotoReaction>? Reactions { get; set; }

    /// <summary>
    /// Number of reactions on this photo
    /// </summary>
    public int ReactionCount => Reactions?.Count ?? 0;

    /// <summary>
    /// Whether this photo has any reactions
    /// </summary>
    public bool HasReactions => ReactionCount > 0;

    /// <summary>
    /// First emoji from reactions (for indicator display)
    /// </summary>
    public string? FirstReactionEmoji => Reactions?.FirstOrDefault()?.Emoji;

    /// <summary>
    /// Total count of all reactions for compact display (alias for ReactionCount)
    /// </summary>
    public int TotalReactionCount => ReactionCount;

    /// <summary>
    /// Whether there are more than one reaction (for showing +N badge)
    /// </summary>
    public bool HasMultipleReactions => TotalReactionCount > 1;

    /// <summary>
    /// Additional count text (+2, +3, etc.) for compact display
    /// </summary>
    public string AdditionalReactionsText => TotalReactionCount > 1 ? $"+{TotalReactionCount - 1}" : "";
}

/// <summary>
/// Type of scoreboard row
/// </summary>
public enum ScoreboardRowType
{
    /// <summary>
    /// Regular series score row
    /// </summary>
    Score,

    /// <summary>
    /// Summary row showing cumulative totals (after series 6, 7, 10, 12)
    /// </summary>
    Subtotal
}
