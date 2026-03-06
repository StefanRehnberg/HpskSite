using NPoco;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.NationellHelmatch.Models
{
    /// <summary>
    /// Nationell Helmatch competition result entry — identical schema to PrecisionResultEntry
    /// but stored in a separate table for data isolation.
    /// Always 12 series in 3 groups of 4 (Precision, Duell, Fält).
    /// </summary>
    [TableName("NationellHelmatchResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class NationellHelmatchResultEntry : PrecisionResultEntry
    {
    }
}
