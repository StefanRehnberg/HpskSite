using NPoco;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.MagnumPrecision.Models
{
    /// <summary>
    /// Magnum Precision competition result entry — identical schema to PrecisionResultEntry
    /// but stored in a separate table for data isolation.
    /// This prevents Magnum Precision scores from being mixed into Precision statistics,
    /// handicap calculations, and personal bests.
    /// </summary>
    [TableName("MagnumPrecisionResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MagnumPrecisionResultEntry : PrecisionResultEntry
    {
    }
}
