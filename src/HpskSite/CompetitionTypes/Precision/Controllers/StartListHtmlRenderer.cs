using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;
using System.Text;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using HpskSite.Services;

namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class StartListHtmlRenderer
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;

        public StartListHtmlRenderer(IMemberManager memberManager, IMemberService memberService, ClubService clubService)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _clubService = clubService;
        }

        public async Task<string> GenerateStartListHtml(StartListConfiguration config, string competitionName)
        {
            var html = new StringBuilder();

            // Get current user info for highlighting
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            var currentMemberName = currentMember?.Name ?? "";
            var currentMemberClub = "";

            if (currentMember != null)
            {
                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData != null)
                {
                    var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                    {
                        currentMemberClub = _clubService.GetClubNameById(primaryClubId) ?? "";
                    }
                }
            }

            html.AppendLine("<div class='start-list-content'>");
            html.AppendLine($"<h3 class='competition-title'>{competitionName}</h3>");
            // Format and Generated date are now hidden - they're shown in the collapsible info section
            html.AppendLine();

            if (config.Teams != null)
            {
                foreach (var team in config.Teams)
                {
                    html.AppendLine($"<h3>Skjutlag: {team.TeamNumber} Tid (ca): {team.StartTime}-{team.EndTime}");

                    if (config.Settings?.Format == "En vapengrupp per Skjutlag" && team.WeaponClasses.Any())
                    {
                        var displayClasses = team.WeaponClasses.Select(GetShootingClassName);
                        html.AppendLine($" Vapengrupp: {string.Join(" ", displayClasses)}");
                    }

                    html.AppendLine($" ({team.ShooterCount} st)</h3>");

                    html.AppendLine("<table class='table table-striped'>");
                    html.AppendLine("<thead><tr><th>Plats</th><th>Namn</th><th>Förening</th>");

                    if (config.Settings?.Format == "Mixade Skjutlag")
                    {
                        html.AppendLine("<th>Vapengrupp</th>");
                    }
                    else
                    {
                        html.AppendLine("<th>Klass</th>");
                    }

                    html.AppendLine("</tr></thead>");
                    html.AppendLine("<tbody>");

                    if (team.Shooters != null)
                    {
                        foreach (var shooter in team.Shooters.OrderBy(s => s.Position))
                        {
                            // Determine row class for highlighting
                            var rowClass = "";
                            if (!string.IsNullOrEmpty(currentMemberName) && shooter.Name == currentMemberName)
                            {
                                rowClass = " class='current-user'";
                            }
                            else if (!string.IsNullOrEmpty(currentMemberClub) && shooter.Club == currentMemberClub)
                            {
                                rowClass = " class='same-club'";
                            }

                            html.AppendLine($"<tr{rowClass}>");
                            html.AppendLine($"<td>{shooter.Position}</td>");
                            html.AppendLine($"<td>{shooter.Name}</td>");
                            html.AppendLine($"<td>{shooter.Club}</td>");
                            html.AppendLine($"<td>{GetShootingClassName(shooter.WeaponClass)}</td>");
                            html.AppendLine("</tr>");
                        }
                    }

                    html.AppendLine("</tbody></table>");
                    html.AppendLine("<br>");
                }
            }

            html.AppendLine("</div>");

            return html.ToString();
        }

        private string GetShootingClassName(string classId)
        {
            var shootingClass = ShootingClasses.GetById(classId);
            return shootingClass?.Name ?? classId;
        }

        public string GetTeamFormatDisplay(string format)
        {
            return format switch
            {
                "Mixade Skjutlag" => "Mixade vapengrupper per skjutlag",
                "En vapengrupp per Skjutlag" => "En vapengrupp per skjutlag",
                _ => format
            };
        }

        public string BuildHtmlWrapper(string contentHtml, string competitionName, string? currentMemberName = null, string? currentMemberClub = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine($"<title>Startlista - {competitionName}</title>");
            sb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">");
            sb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.0/font/bootstrap-icons.css\" rel=\"stylesheet\">");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; background-color: #f8f9fa; }");
            sb.AppendLine(".start-list-content h1:first-child, .start-list-content h2:first-child, .start-list-content h3:first-child, .start-list-content .competition-title { font-size: 1.1rem !important; font-weight: 600 !important; margin-bottom: 0.5rem !important; color: #333 !important; }");
            sb.AppendLine(".start-list-content p:nth-child(2), .start-list-content p:nth-child(3) { display: none !important; }");
            sb.AppendLine(".start-list-content { font-size: 0.9rem; }");
            sb.AppendLine(".start-list-content table { font-size: 0.85rem; width: 100%; border-collapse: collapse; margin: 20px 0; }");
            sb.AppendLine(".start-list-content table th, .start-list-content table td { padding: 4px 8px !important; line-height: 1.2 !important; border: 1px solid #ddd !important; text-align: left; }");
            sb.AppendLine(".start-list-content table th { background-color: #f5f5f5 !important; font-weight: 600 !important; font-size: 0.8rem !important; }");
            sb.AppendLine(".start-list-content table td { font-size: 0.8rem !important; }");
            sb.AppendLine(".start-list-content table tbody tr { height: auto !important; min-height: 28px !important; }");
            sb.AppendLine(".current-user { background-color: #cce5ff !important; }");
            sb.AppendLine(".same-club { background-color: #f0f8ff !important; }");
            sb.AppendLine(".start-list-content table tbody tr:nth-child(even) { background-color: transparent !important; }");
            sb.AppendLine(".start-list-content .table-striped tbody tr:nth-child(odd) { background-color: transparent !important; }");
            sb.AppendLine(".start-list-content .table-striped tbody tr:nth-child(even) { background-color: transparent !important; }");
            sb.AppendLine(".card { border: 1px solid #dee2e6; border-radius: 0.375rem; box-shadow: 0 0.125rem 0.25rem rgba(0, 0, 0, 0.075); }");
            sb.AppendLine(".card-header { background-color: #f8f9fa; border-bottom: 1px solid #dee2e6; padding: 0.75rem 1rem; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"container-fluid\">");
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<div class=\"card-body\">");
            sb.AppendLine("<div class=\"start-list-content\">");
            sb.AppendLine(contentHtml);
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js\"></script>");

            if (!string.IsNullOrEmpty(currentMemberName))
            {
                sb.AppendLine(BuildUserHighlightingScript(currentMemberName, currentMemberClub));
            }

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private string BuildUserHighlightingScript(string currentMemberName, string? currentMemberClub)
        {
            var currentMemberForJs = currentMemberName.Replace("'", "\\'");
            var currentClubForJs = (currentMemberClub ?? "").Replace("'", "\\'");

            var sb = new StringBuilder();
            sb.AppendLine("<script>");
            sb.AppendLine("document.addEventListener('DOMContentLoaded', function() {");
            sb.AppendLine($"    const currentUserName = '{currentMemberForJs}';");
            sb.AppendLine($"    const currentUserClub = '{currentClubForJs}';");
            sb.AppendLine("    ");
            sb.AppendLine("    const tables = document.querySelectorAll('.start-list-content table tbody');");
            sb.AppendLine("    tables.forEach(tbody => {");
            sb.AppendLine("        const rows = tbody.querySelectorAll('tr');");
            sb.AppendLine("        rows.forEach(row => {");
            sb.AppendLine("            const cells = row.querySelectorAll('td');");
            sb.AppendLine("            if (cells.length >= 3) {");
            sb.AppendLine("                const nameCell = cells[1].textContent.trim();");
            sb.AppendLine("                const clubCell = cells[2].textContent.trim();");
            sb.AppendLine("                ");
            sb.AppendLine("                if (currentUserName && nameCell === currentUserName) {");
            sb.AppendLine("                    row.classList.add('current-user');");
            sb.AppendLine("                } else if (currentUserClub && clubCell === currentUserClub) {");
            sb.AppendLine("                    row.classList.add('same-club');");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        });");
            sb.AppendLine("    });");
            sb.AppendLine("});");
            sb.AppendLine("</script>");

            return sb.ToString();
        }

    }
}
