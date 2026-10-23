using System;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Planning;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Ansökan ur planeringen (P3 steg F, beslut 9.7). Planeringen skapar ett förifyllt UTKAST i klubbens vanliga
    /// tävlingsansökningar — själva ansökan skickas in därifrån, med kretsens regler, och ingen andra blankett byggs.
    /// När tävlingen skapas ur ansökan kopplas den till planeringen (<see cref="PlanningApplicationLink"/>).
    /// </summary>
    public partial class PlanningController
    {
        private async Task<object?> ApplicationJsonAsync(int planningId, PlanningCaller c)
        {
            try
            {
                var link = HttpContext.RequestServices.GetRequiredService<PlanningApplicationLink>();
                var app = link.ForPlanning(planningId);
                if (app != null)
                    return new
                    {
                        id = app.Id, name = app.Name, status = app.Status, statusLabel = CompetitionApplicationStatus.Label(app.Status),
                        club = app.ClubId > 0 ? OrgName(Models.DocumentOwnerType.Club, app.ClubId) : "",
                        date = D(app.CompetitionDate), competitionId = app.CompetitionId,
                        url = ClubCompetitionsUrl(app.ClubId),
                    };
                // Bara den som kan ändra planeringen, inloggad, och bara medan planeringen inte är kopplad.
                if (!PlanningAccess.CanEditPlanning(c) || c.ViaLink || _planning.LinkFor(planningId) != null) return null;
                // Klubbarna som någon i planeringen företräder och som den som frågar får administrera — inte alla
                // klubbar en sajtadministratör når (de är hundratals).
                var clubs = new System.Collections.Generic.List<(int id, string name)>();
                foreach (var t in BoardTargets(planningId).Where(t => t.OwnerType == Models.DocumentOwnerType.Club))
                    if (await _auth.IsClubAdminForClub(t.OwnerId)) clubs.Add((t.OwnerId, t.Name));
                if (clubs.Count == 0) return null;
                return new
                {
                    id = 0, clubs = clubs.Select(x => new { x.id, x.name }),
                    levels = CompetitionApplicationRules.ApplicableLevels.Select(l => new { value = l, label = CompetitionLevel.Find(l)?.Label ?? l }),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Planering {Id}: ansökan kunde inte läsas.", planningId);
                return null;
            }
        }

        /// <summary>Klubbens tävlingar i adminpanelen — där ansökan skickas in och tävlingen skapas.</summary>
        private string ClubCompetitionsUrl(int clubId)
        {
            if (clubId <= 0 || !UmbracoContextAccessor.TryGetUmbracoContext(out var ctx)) return "";
            var n = ctx.Content?.GetById(clubId);
            return n == null || n.ContentType.Alias != "club" ? "" : n.Url() + "#clubAdmin/competitions";
        }
    }
}
