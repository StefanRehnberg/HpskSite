using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Årsmötets mästerskapsmedaljer: vad klubben eller kretsen ska beställa, gravera och dela ut.
    ///
    /// <para><b>Auktorisationen har två former</b>, precis som tävlingarna själva: en klubblista
    /// gränsas av <c>IsClubAdminForClub</c>, en kretslista av <c>IsRegionalAdminForRegion</c>.
    /// Att bara fråga efter klubbadmin hade låst ute kretsens egna funktionärer från kretsens egen
    /// lista — samma fälla som redan uppstått fyra gånger på tävlingsytorna.</para>
    /// </summary>
    public class MedalHandoutController : SurfaceController
    {
        private readonly AdminAuthorizationService _auth;
        private readonly MedalHandoutService _handout;

        public MedalHandoutController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth,
            MedalHandoutService handout)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _handout = handout;
        }

        /// <summary>
        /// Sammanställningen som JSON.
        /// GET /umbraco/surface/MedalHandout/GetMedalHandout?scopeType=Club&amp;scopeId=1098&amp;year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMedalHandout(string? scopeType, string? scopeId, int? year)
        {
            var (list, error) = await LoadAsync(scopeType, scopeId, year);
            if (error != null) return Json(new { success = false, message = error });

            return Json(new
            {
                success = true,
                year = list!.Year,
                scopeType = list.ScopeType,
                scopeName = list.ScopeName,
                scope = list.Scope,
                totalMedals = list.TotalMedals,
                recipientCount = list.RecipientCount,
                warnings = list.Warnings,
                competitions = list.Competitions.Select(c => new
                {
                    c.CompetitionId,
                    c.Name,
                    date = c.Date?.ToString("yyyy-MM-dd"),
                    endDate = c.EndDate?.ToString("yyyy-MM-dd"),
                    c.CompetitionType,
                    c.MedalCount,
                    c.HasMedals,
                    c.IsUpcoming,
                    c.Problem,
                    c.MedalGroupingText,
                    c.MedalGroupingStale,
                    resultsUpdatedAt = c.ResultsUpdatedAt?.ToString("yyyy-MM-dd HH:mm")
                }),
                order = list.Order.Select(l => new { l.Group, l.Medal, l.Count, l.Note }),
                unresolved = list.Unresolved.Select(u => new
                {
                    u.CompetitionId, u.CompetitionName, u.Category, u.Text, u.IsTeam
                }),
                handout = list.Handout.Select(r => new
                {
                    r.MemberId,
                    r.Name,
                    r.Club,
                    items = r.Items.Select(i => new
                    {
                        i.Medal, i.Category, i.CompetitionName, i.CompetitionId,
                        competitionDate = i.CompetitionDate?.ToString("yyyy-MM-dd"),
                        i.IsTeam, i.TeamName, i.Detail,
                        engraving = i.Engraving(r.Name)
                    })
                })
            });
        }

        /// <summary>
        /// Beställnings- och gravyrlistan: <b>EN RAD PER FYSISK MEDALJ</b>, med en färdig
        /// graveringstext och delarna den är byggd av i egna kolumner.
        ///
        /// <para><b>⚠️ Var en summering per valör och var därmed oanvändbar</b> (Stefan
        /// 2026-09-20). "3 guld" räcker för att beställa råmedaljerna men säger ingenting till
        /// gravören, och det är gravören som är mottagaren av en beställningslista — antalet kan
        /// vem som helst räkna ur listan, graveringstexten kan ingen gissa. Summeringen per valör
        /// ligger kvar SIST i filen, så båda frågorna besvaras av samma fil.</para>
        ///
        /// <para><b>⚠️ Utdelningslistan som CSV är BORTTAGEN</b> (samma beslut): utskriften ska
        /// duga som utdelningslista, och två exporter som nästan är samma sak gör att fel fil
        /// skickas till gravören.</para>
        ///
        /// GET /umbraco/surface/MedalHandout/ExportMedalHandout?scopeType=Club&amp;scopeId=1098&amp;year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportMedalHandout(string? scopeType, string? scopeId, int? year)
        {
            var (data, error) = await LoadAsync(scopeType, scopeId, year);
            if (error != null) return Content(error);

            var sb = new System.Text.StringBuilder();
            var slug = (data!.ScopeType == MedalHandoutService.ScopeRegion ? "krets" : "klubb")
                       + "-" + Slug(data.ScopeId);

            // ⚠️ Mottagarens klubb bara på kretsens lista, samma regel som skärmen och utskriften.
            // En klubbs egen fil ska inte bära en kolumn som kan namnge en ANNAN klubb — och en
            // kolumn som står tom på varje rad läses som saknad data, så den utgår helt.
            var showClub = data.ScopeType == MedalHandoutService.ScopeRegion;

            string Row(params string[] cells) => string.Join(";", cells);

            sb.AppendLine(showClub
                ? "Nr;Valör;Gravyrtext;Mottagare;Klubb;Kategori;Lag;Tävling;Datum;Resultat"
                : "Nr;Valör;Gravyrtext;Mottagare;Kategori;Lag;Tävling;Datum;Resultat");

            int nr = 0;
            foreach (var r in data.Handout)
                foreach (var i in r.Items)
                {
                    var cells = new List<string>
                    {
                        (++nr).ToString(),
                        Csv(i.Medal),
                        Csv(i.Engraving(r.Name)),
                        Csv(r.Name)
                    };
                    if (showClub) cells.Add(Csv(r.Club));
                    cells.Add(Csv(i.Category));
                    cells.Add(i.IsTeam ? Csv(i.TeamName) : "");
                    cells.Add(Csv(i.CompetitionName));
                    cells.Add(i.CompetitionDate?.ToString("yyyy-MM-dd") ?? "");
                    cells.Add(Csv(i.Detail));
                    sb.AppendLine(Row(cells.ToArray()));
                }

            // ⚠️ De oavgjorda står MED i filen, utan graveringstext. En gravyrlista som tyst
            // utelämnar dem läses som komplett — medaljen beställs, men ingen upptäcker att den
            // inte kan graveras förrän på årsmötet.
            foreach (var u in data.Unresolved)
            {
                var cells = new List<string>
                {
                    (++nr).ToString(),
                    Csv(MedalHandoutService.MedalOf(u.Text)),
                    "GRAVERAS EJ - mottagare saknas",
                    ""
                };
                if (showClub) cells.Add("");
                cells.Add(Csv(u.Category));
                cells.Add("");
                cells.Add(Csv(u.CompetitionName));
                cells.Add("");
                cells.Add(Csv(u.Text));
                sb.AppendLine(Row(cells.ToArray()));
            }

            // Summeringen per valör sist, för beställningen av råmedaljerna. Tomma celler fylls ut
            // till samma bredd som raderna ovan, annars hamnar den i fel kolumner i Excel.
            int width = showClub ? 10 : 9;
            string Pad(params string[] head) =>
                Row(head.Concat(Enumerable.Repeat("", Math.Max(0, width - head.Length))).ToArray());

            sb.AppendLine();
            sb.AppendLine(Pad("Att beställa"));
            foreach (var l in data.Order)
                sb.AppendLine(Pad("", Csv(l.Medal), Csv(l.Group), l.Count.ToString(), Csv(l.Note)));
            sb.AppendLine(Pad("", "Totalt", "", data.TotalMedals.ToString()));

            return CsvFile(sb.ToString(), $"masterskapsmedaljer-bestallning-gravyr-{slug}-{data.Year}.csv");
        }

        /// <summary>
        /// Båda halvorna på en utskriftsvänlig sida — beställningen till leverantören och listan
        /// att läsa upp ur vid utdelningen. Fristående HTML, ingen Umbraco-nod.
        /// <para><paramml name="gruppering"/> = "person" (en rad per mottagare, allt hen vunnit
        /// samlat) eller "tavling" (en rubrik per mästerskap). Se
        /// <see cref="BuildPrintHtml"/> för varför båda måste finnas.</para>
        ///
        /// GET /umbraco/surface/MedalHandout/PrintMedalHandout?scopeType=Club&amp;scopeId=1098&amp;year=2026&amp;gruppering=tavling
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PrintMedalHandout(string? scopeType, string? scopeId, int? year, string? gruppering)
        {
            var (data, error) = await LoadAsync(scopeType, scopeId, year);
            if (error != null) return Content(error);
            var byCompetition = string.Equals(gruppering, "tavling", StringComparison.OrdinalIgnoreCase);
            return Content(BuildPrintHtml(data!, byCompetition), "text/html; charset=utf-8");
        }

        // ── Gemensam laddning och grindning ──────────────────────────────────────

        private async Task<(MedalHandoutList? List, string? Error)> LoadAsync(string? scopeType, string? scopeId, int? year)
        {
            int y = year ?? DateTime.Now.Year;
            var st = string.Equals(scopeType, MedalHandoutService.ScopeRegion, StringComparison.OrdinalIgnoreCase)
                ? MedalHandoutService.ScopeRegion
                : MedalHandoutService.ScopeClub;

            if (string.IsNullOrWhiteSpace(scopeId)) return (null, "Saknar klubb eller krets.");

            if (st == MedalHandoutService.ScopeRegion)
            {
                if (!await _auth.IsRegionalAdminForRegion(scopeId)) return (null, "Åtkomst nekad.");
                return (await _handout.BuildForRegionAsync(scopeId, y), null);
            }

            if (!int.TryParse(scopeId, out var clubId) || clubId <= 0) return (null, "Ogiltigt klubb-ID.");
            if (!await _auth.IsClubAdminForClub(clubId)) return (null, "Åtkomst nekad.");
            return (await _handout.BuildForClubAsync(clubId, y), null);
        }

        private static string Slug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "okand";
            var chars = value.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            return new string(chars).Trim('-');
        }

        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private FileContentResult CsvFile(string content, string fileName)
        {
            // UTF-8 med BOM — svensk Excel läser annars å/ä/ö som mojibake.
            var bom = System.Text.Encoding.UTF8.GetPreamble();
            var body = System.Text.Encoding.UTF8.GetBytes(content);
            var bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            return File(bytes, "text/csv", fileName);
        }

        /// <summary>
        /// Utskriften, som <b>ÄR</b> utdelningslistan — den ska gå att ta med till årsmötet och
        /// pricka av ur, precis som skärmen (Stefan 2026-09-20).
        ///
        /// <para><b>⚠️ TVÅ ORDNINGAR, och ingen av dem är "rätt".</b> Per PERSON kallas var och en
        /// fram en gång och får allt hen vunnit; per MÄSTERSKAP avverkas en tävling i taget och
        /// samma person kan kallas fram flera gånger. Båda formerna förekommer, och vilken
        /// föreningen använder är en tradition vi inte känner — så valet ligger hos arrangören och
        /// måste följa med till utskriften, annars är den utskrivna listan i en annan ordning än
        /// den man nyss tittade på.</para>
        /// </summary>
        private static string BuildPrintHtml(MedalHandoutList data, bool byCompetition)
        {
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new System.Text.StringBuilder();
            var heading = data.ScopeType == MedalHandoutService.ScopeRegion
                ? "Kretsmästerskapsmedaljer"
                : "Klubbmästerskapsmedaljer";

            // ⚠️ Mottagarens klubb bara på kretsens lista — se panelens kommentar. På en klubbs
            // egen lista är den brus i bästa fall och FEL i värsta, och utskriften får inte säga
            // något annat än skärmen den skrevs ut från.
            var showClub = data.ScopeType == MedalHandoutService.ScopeRegion;

            sb.Append("<!DOCTYPE html><html lang='sv'><head><meta charset='utf-8'>");
            sb.Append("<title>").Append(Enc(heading)).Append(' ').Append(data.Year)
              .Append(" – ").Append(Enc(data.ScopeName)).Append("</title>");
            sb.Append("<style>body{font-family:Arial,Helvetica,sans-serif;margin:2rem;color:#222}");
            sb.Append("h1{font-size:1.4rem;margin-bottom:.2rem}h2{font-size:1.1rem;margin-top:1.8rem}");
            sb.Append("h3{font-size:.95rem;margin:1.1rem 0 .1rem}");
            sb.Append("table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}");
            sb.Append("th,td{border:1px solid #ccc;padding:.35rem .6rem;text-align:left;font-size:.88rem;vertical-align:top}");
            sb.Append("th{background:#f3f3f3}td.num{text-align:right;font-variant-numeric:tabular-nums}");
            sb.Append(".muted{color:#666;font-size:.82rem}.warn{border-left:4px solid #d19b00;background:#fff8e6;padding:.5rem .7rem;margin:.6rem 0;font-size:.85rem}");
            sb.Append(".open{border-left:4px solid #c0392b;background:#fdeceb;padding:.5rem .7rem;margin:.6rem 0;font-size:.85rem}");
            sb.Append(".tick{width:1.6rem;text-align:center}.grp{color:#555}");
            sb.Append("tr.total td{font-weight:bold;background:#f9f9f9}");
            // ⚠️ h3 får inte hamna sist på en sida — en tävlingsrubrik utan sina medaljer under
            // läses som att tävlingen saknar medaljörer.
            sb.Append("@media print{button{display:none}h2,h3{page-break-after:avoid}tr{page-break-inside:avoid}}");
            sb.Append("</style></head><body>");
            sb.Append("<button onclick='window.print()'>Skriv ut</button>");

            sb.Append("<h1>").Append(Enc(heading)).Append(' ').Append(data.Year).Append("</h1>");
            sb.Append("<p class='muted'>").Append(Enc(data.ScopeName))
              .Append(" · ").Append(Enc(data.Scope))
              .Append(" · ").Append(byCompetition ? "ordnad per mästerskap" : "ordnad per mottagare")
              .Append(" · utskriven ").Append(DateTime.Now.ToString("yyyy-MM-dd")).Append("</p>");

            foreach (var w in data.Warnings)
                sb.Append("<div class='warn'>").Append(Enc(w)).Append("</div>");

            // ── Att beställa ──
            sb.Append("<h2>Att beställa</h2>");
            if (data.Order.Count == 0)
            {
                sb.Append("<p class='muted'>Inga mästerskapsmedaljer att beställa för ")
                  .Append(data.Year).Append(".</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Grupp</th><th>Valör</th><th style='width:5rem'>Antal</th><th>Notering</th></tr>");
                foreach (var l in data.Order)
                    sb.Append("<tr><td class='grp'>").Append(Enc(l.Group))
                      .Append("</td><td>").Append(Enc(l.Medal))
                      .Append("</td><td class='num'>").Append(l.Count)
                      .Append("</td><td class='muted'>").Append(Enc(l.Note)).Append("</td></tr>");
                sb.Append("<tr class='total'><td>Totalt</td><td></td><td class='num'>")
                  .Append(data.TotalMedals).Append("</td><td></td></tr>");
                sb.Append("</table>");
                sb.Append("<p class='muted'>En lagmedalj räknas per lagmedlem — det är skyttarna som får medaljerna.</p>");
            }

            // ── Att dela ut ──
            sb.Append("<h2>Att dela ut</h2>");
            if (data.Handout.Count == 0)
            {
                sb.Append("<p class='muted'>Ingen mottagare för ").Append(data.Year).Append(".</p>");
            }
            else if (byCompetition)
            {
                // En rubrik per mästerskap, medaljerna i valörordning under. Samma person kan
                // förekomma under flera tävlingar — det är hela poängen med den här ordningen.
                var groups = data.Handout
                    .SelectMany(r => r.Items.Select(i => new { r, i }))
                    .GroupBy(x => new { x.i.CompetitionId, x.i.CompetitionName, x.i.CompetitionDate })
                    .OrderBy(g => g.Key.CompetitionDate ?? DateTime.MinValue)
                    .ThenBy(g => g.Key.CompetitionName, StringComparer.CurrentCulture);

                foreach (var g in groups)
                {
                    sb.Append("<h3>").Append(Enc(g.Key.CompetitionName));
                    if (g.Key.CompetitionDate.HasValue)
                        sb.Append(" <span class='muted'>")
                          .Append(g.Key.CompetitionDate.Value.ToString("yyyy-MM-dd")).Append("</span>");
                    sb.Append("</h3>");

                    sb.Append("<table><tr><th class='tick'>&#10003;</th><th>Medalj</th>")
                      .Append("<th style='width:14rem'>Mottagare</th><th>Detalj</th></tr>");
                    foreach (var x in g
                        .OrderBy(x => MedalHandoutService.MedalSort(x.i.Medal))
                        .ThenBy(x => x.i.Category, StringComparer.CurrentCulture))
                    {
                        sb.Append("<tr><td class='tick'>&#9744;</td><td>").Append(Enc(x.i.Medal))
                          .Append(" <span class='grp'>").Append(Enc(x.i.Category)).Append("</span>");
                        if (x.i.IsTeam) sb.Append(" <span class='muted'>(lag)</span>");
                        sb.Append("</td><td>").Append(Enc(x.r.Name));
                        if (showClub && !string.IsNullOrWhiteSpace(x.r.Club))
                            sb.Append("<div class='muted'>").Append(Enc(x.r.Club)).Append("</div>");
                        sb.Append("</td><td class='muted'>").Append(Enc(x.i.Detail)).Append("</td></tr>");
                    }
                    sb.Append("</table>");
                }
            }
            else
            {
                sb.Append("<table><tr><th class='tick'>&#10003;</th><th style='width:13rem'>Mottagare</th>")
                  .Append("<th>Medalj</th><th>Tävling</th><th>Detalj</th></tr>");
                foreach (var r in data.Handout)
                {
                    bool first = true;
                    foreach (var i in r.Items)
                    {
                        sb.Append("<tr><td class='tick'>&#9744;</td><td>");
                        if (first)
                        {
                            sb.Append(Enc(r.Name));
                            if (showClub && !string.IsNullOrWhiteSpace(r.Club))
                                sb.Append("<div class='muted'>").Append(Enc(r.Club)).Append("</div>");
                        }
                        sb.Append("</td><td>").Append(Enc(i.Medal))
                          .Append(" <span class='grp'>").Append(Enc(i.Category)).Append("</span>");
                        if (i.IsTeam) sb.Append(" <span class='muted'>(lag)</span>");
                        sb.Append("</td><td>").Append(Enc(i.CompetitionName));
                        if (i.CompetitionDate.HasValue)
                            sb.Append("<div class='muted'>").Append(i.CompetitionDate.Value.ToString("yyyy-MM-dd")).Append("</div>");
                        sb.Append("</td><td class='muted'>").Append(Enc(i.Detail)).Append("</td></tr>");
                        first = false;
                    }
                }
                sb.Append("</table>");
            }

            // ── Utan mottagare ──
            //
            // ⚠️ EGEN RUBRIK, aldrig bortsopade. Medaljen ska beställas, men namnet kan inte
            // graveras och ingen får kallas fram — den som håller i ceremonin måste se det här
            // innan hen står i salen.
            if (data.Unresolved.Count > 0)
            {
                sb.Append("<h2>Medaljer utan mottagare</h2>");
                sb.Append("<p class='muted'>Medaljerna är inräknade i beställningen ovan, men platsen är "
                        + "inte avgjord. Avgör särskjutningen eller lagordningen på tävlingens resultatflik "
                        + "och hämta listan igen innan gravyren beställs.</p>");
                foreach (var u in data.Unresolved)
                    sb.Append("<div class='open'><strong>").Append(Enc(u.CompetitionName))
                      .Append("</strong> · ").Append(Enc(u.Category)).Append("<br>")
                      .Append(Enc(u.Text)).Append("</div>");
            }

            // ── Underlaget ──
            sb.Append("<h2>Tävlingar listan bygger på</h2>");
            sb.Append("<table><tr><th style='width:6rem'>Datum</th><th>Tävling</th>")
              .Append("<th style='width:5rem'>Medaljer</th><th>Medaljindelning</th></tr>");
            foreach (var c in data.Competitions)
            {
                sb.Append("<tr><td>").Append(c.Date?.ToString("yyyy-MM-dd") ?? "")
                  .Append("</td><td>").Append(Enc(c.Name));
                // Bara när tävlingen inte bidrar — se panelens kommentar, en tävling med medaljer
                // som samtidigt märks "inte avgjord" motsäger sina egna siffror.
                if (c.IsUpcoming && !c.HasMedals) sb.Append(" <span class='muted'>(inte avgjord än)</span>");
                if (!string.IsNullOrWhiteSpace(c.Problem))
                    sb.Append("<div class='muted'>").Append(Enc(c.Problem)).Append("</div>");
                sb.Append("</td><td class='num'>").Append(c.MedalCount)
                  .Append("</td><td class='muted'>").Append(Enc(c.MedalGroupingText)).Append("</td></tr>");
            }
            sb.Append("</table>");

            sb.Append("</body></html>");
            return sb.ToString();
        }
    }
}
