using NPoco;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Milsnabb.Models
{
    /// <summary>
    /// Milsnabb competition result entry — identical schema to PrecisionResultEntry
    /// but stored in a separate table for data isolation.
    /// This prevents Milsnabb scores from being mixed into Precision statistics,
    /// handicap calculations, and personal bests.
    /// </summary>
    [TableName("MilsnabbResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MilsnabbResultEntry : PrecisionResultEntry
    {
    }
}
