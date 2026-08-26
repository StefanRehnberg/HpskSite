using NPoco;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Standardpistol.Models
{
    /// <summary>
    /// Standardpistol — 25 m, 12 serier om 5 skott (60 skott, 600 poäng), där serierna skjuts på
    /// 150 s, 20 s och 10 s. Tiden växlar mellan strängarna men varje serie poängsätts likadant,
    /// så inget i beräkningen skiljer sig från precisionsfamiljen.
    ///
    /// Identiskt schema med <see cref="PrecisionResultEntry"/> men EGEN tabell. Att dela tabell hade
    /// förorenat handikappindex, personbästa, statistik och träningsmatcher — se
    /// `competition-type-implementation`, punkt 1. NPoco använder körtidstypen vid
    /// insert/update/delete, så polymorf tilldelning hittar rätt tabell av sig själv.
    /// </summary>
    [TableName("StandardpistolResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StandardpistolResultEntry : PrecisionResultEntry
    {
    }
}
