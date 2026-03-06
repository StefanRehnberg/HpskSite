using NPoco;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Duell.Models
{
    /// <summary>
    /// Duell competition result entry — identical schema to PrecisionResultEntry
    /// but stored in a separate table for data isolation.
    /// This prevents Duell scores from being mixed into Precision statistics,
    /// handicap calculations, and personal bests.
    /// </summary>
    [TableName("DuellResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class DuellResultEntry : PrecisionResultEntry
    {
    }
}
