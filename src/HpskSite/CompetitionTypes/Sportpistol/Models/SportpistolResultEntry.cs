using NPoco;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Sportpistol.Models
{
    /// <summary>
    /// Sportpistol — 25 m, en precisionshalva och en duellhalva (snabb), 12 serier om 5 skott
    /// (60 skott, 600 poäng).
    ///
    /// Halvorna är en KONVENTION över serieordningen, inte två datamodeller: serie 1–6 är
    /// precisionshalvan och 7–12 duellhalvan. Samma form som NationellHelmatch, som också är en
    /// flerdelad match utan egen motor. Det betyder att en delsumma per halva är en VISNINGSfråga
    /// (som Milsnabbs tidsgruppssummor i CompetitionResult.cshtml), inte en lagringsfråga.
    ///
    /// Identiskt schema med <see cref="PrecisionResultEntry"/> men EGEN tabell — se
    /// StandardpistolResultEntry för varför.
    /// </summary>
    [TableName("SportpistolResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class SportpistolResultEntry : PrecisionResultEntry
    {
    }
}
