using HpskSite.Models.Events;
using HpskSite.Models.StartLists;

namespace HpskSite.Services.Addresses
{
    /// <summary>
    /// C3-planen S7: vad en tävlingssida visar, när det inte är noden själv — en startlista ur lagringen
    /// (ingen nod under tävlingen) eller en händelse ur tabellen (sidan bärs av ägarens nod). Sätts av
    /// <see cref="HpskSite.Controllers.CompetitionPagesController"/> och de gamla adressupplösarna, läses av
    /// vyerna. Ersätter de råa nycklarna i <c>HttpContext.Items</c>. Scoped = en per förfrågan.
    /// </summary>
    public class CompetitionPageContext
    {
        /// <summary>Startlistan som visas när sidan saknar egen nod (Model är då tävlingen).</summary>
        public StartListRecord? StartList { get; set; }

        /// <summary>Händelsen som visas på ägarens nod.</summary>
        public ClubEventRecord? Event { get; set; }
    }
}
