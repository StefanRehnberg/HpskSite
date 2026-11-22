using System.Net;
using System.Text;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using Newtonsoft.Json.Linq;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>En sida stationskort: en station och de vapenklasser som delar innehåll.</summary>
    public sealed class StationCardBlock
    {
        public int Station { get; set; }
        public string? Name { get; set; }
        /// <summary>Tävlingens stationsnummer när konfigurationen bara är en del av banan (`_courseStations`).</summary>
        public int? CourseNo { get; set; }
        /// <summary>Klasserna som delar kortet, i kortens ordning. Den första är den som kortet läses ur.</summary>
        public List<string> Classes { get; set; } = new();
        /// <summary>Klassen kortets figurer och texter läses ur (den som beställdes; annars den första).</summary>
        public string PrimaryClass { get; set; } = "";
        public Dictionary<string, FaltskytteStationConfig> ByClass { get; set; } = new();
        /// <summary>Skriv klasserna i rubriken (alltid för en klass; för flera bara när inte alla klasser delar kortet).</summary>
        public bool ShowClassesInHeader { get; set; }
        public string Qr1Url { get; set; } = "";
        public string Qr2Url { get; set; } = "";
        /// <summary>Kompakt (1 sida): inga bilder, tät sättning, QR-koderna bredvid varandra. Den obligatoriska kärnan står kvar.</summary>
        public bool Compact { get; set; }
    }

    /// <summary>Verktygsfältet ovanför förhandsvisningen (bara på skärmen, aldrig i utskriften). Bara konfiguratorns popup har ett.</summary>
    public sealed class StationCardToolbar
    {
        public int Station { get; set; }
        public string? WeaponClass { get; set; }
        public string Mode { get; set; } = StationPrintMode.Full;
        /// <summary>Kan läget sparas? (Bara när konfigurationen har ett id och den som ser kan ändra den.)</summary>
        public bool CanSetMode { get; set; }
    }

    /// <summary>
    /// EN renderare för stationskortet (Stefan 2026-11-21, fas 3a). Förut fanns två: konfiguratorns
    /// `faltCfgPrintStation` (JS) och serverns FaltskyttePrintStationCards.cshtml, med olika text och olika
    /// sammanslagning av vapenklasser. Nu skriver båda ut genom den här: tävlingens kort ur sparade data, och
    /// konfiguratorn genom att skicka editorns aktuella JSON till servern (osparade ändringar följer med).
    /// All text från användaren HTML-kodas.
    /// </summary>
    public static class StationCardRenderer
    {
        static readonly string[] WcOrder = { "C", "B", "A", "A_Opt", "R", "L", "M" };

        public static string OrderKey(string wc)
        {
            var i = Array.IndexOf(WcOrder, wc);
            return (i < 0 ? "99" : i.ToString("D2")) + "_" + wc;
        }

        // Bara tecknen som har betydelse i HTML (WebUtility.HtmlEncode skriver å/ä/ö som &#228; osv.).
        static string E(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

        // ── Beteendetexten (EN funktion, Stefan 2026-11-21: "fram efter 3 sek, visas i 8 sek") ──

        /// <summary>Bara det som är sparat: en tid som ingen skrivit in visas inte.</summary>
        public static string BehaviorTiming(FaltskytteFigure f)
        {
            var parts = new List<string>();
            if (f.Behavior == "Framsvängande" || f.Behavior == "Rörligt")
            {
                if (f.DelayBeforeShowSec > 0) parts.Add($"fram efter {f.DelayBeforeShowSec} sek");
                if (f.ShowTimeSec > 0) parts.Add($"visas i {f.ShowTimeSec} sek");
                foreach (var e in f.Exposures ?? new List<FaltskytteExposure>())
                    if (e.DurSec > 0) parts.Add($"visas igen {e.FromSec} sek in, i {e.DurSec} sek");
            }
            else if (f.Behavior == "Bortsvängande")
            {
                if (f.HideAfterSec > 0) parts.Add($"bort efter {f.HideAfterSec} sek");
                if (f.ReappearSec > 0) parts.Add($"visar igen efter {f.ReappearSec} sek");
            }
            return string.Join(", ", parts);
        }

        public static string BehaviorText(FaltskytteFigure f)
        {
            var name = f.Behavior is "Framsvängande" or "Bortsvängande" or "Rörligt" ? f.Behavior : "Fast";
            var t = BehaviorTiming(f);
            return t.Length == 0 ? name : name + ", " + t;
        }

        // ── Vilka klasser delar kort ──

        /// <summary>Enkelt läge: samma figurer, bilder, beskrivningar och beteenden. Skjuttid, lägen och tider får skilja.</summary>
        public static bool ShapeEqual(FaltskytteStationConfig a, FaltskytteStationConfig b)
        {
            if (a.TargetGroups.Count != b.TargetGroups.Count) return false;
            for (var g = 0; g < a.TargetGroups.Count; g++)
            {
                var x = a.TargetGroups[g]; var y = b.TargetGroups[g];
                if ((x.ImageUrl ?? "") != (y.ImageUrl ?? "")) return false;
                if ((x.Description ?? "") != (y.Description ?? "")) return false;
                if (x.Figures.Count != y.Figures.Count) return false;
                for (var i = 0; i < x.Figures.Count; i++)
                {
                    var p = x.Figures[i]; var q = y.Figures[i];
                    if (p.IsPoangmal != q.IsPoangmal) return false;
                    if ((p.Behavior ?? "Fast") != (q.Behavior ?? "Fast")) return false;
                    if ((p.TargetName ?? "") != (q.TargetName ?? "")) return false;
                    if ((p.TargetColor ?? "") != (q.TargetColor ?? "")) return false;
                    if ((p.ImageUrl ?? "") != (q.ImageUrl ?? "")) return false;
                    if (Math.Max(1, p.TargetsPerFigure) != Math.Max(1, q.TargetsPerFigure)) return false;
                }
            }
            return true;
        }

        /// <summary>Avancerat läge: förutsättningarna måste också vara lika.</summary>
        public static bool StructurallyEqual(FaltskytteStationConfig a, FaltskytteStationConfig b)
        {
            if (a.ShooterStartPosition != b.ShooterStartPosition) return false;
            if (a.WeaponStartPosition != b.WeaponStartPosition) return false;
            if (a.SupportHand != b.SupportHand) return false;
            if (a.MaxShotsPerFigure != b.MaxShotsPerFigure) return false;
            if (a.MinShotsPerFigure != b.MinShotsPerFigure) return false;
            if (a.TargetGroups.Count != b.TargetGroups.Count) return false;
            for (var g = 0; g < a.TargetGroups.Count; g++)
            {
                var x = a.TargetGroups[g]; var y = b.TargetGroups[g];
                if (x.Figures.Count != y.Figures.Count) return false;
                for (var i = 0; i < x.Figures.Count; i++)
                {
                    if (x.Figures[i].IsPoangmal != y.Figures[i].IsPoangmal) return false;
                    if (x.Figures[i].Behavior != y.Figures[i].Behavior) return false;
                }
            }
            return true;
        }

        /// <summary>Id på konfigurationen som tävlingens stationskonfiguration kopierades ur (`_attachedConfigId`), eller 0.</summary>
        public static int ReadAttachedConfigId(string? json)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{")
                    && JObject.Parse(json)["_attachedConfigId"] is JToken t)
                    return t.Type == JTokenType.Integer ? (int)t : (int.TryParse(t.ToString(), out var n) ? n : 0);
            }
            catch { }
            return 0;
        }

        /// <summary>Konfigurationens egna nycklar: `_mode` ("simple" ger enkelt läge) och `_courseStations`.</summary>
        public static (bool Strict, List<int> CourseStations) ReadMeta(string? json)
        {
            var strict = true; var course = new List<int>();
            try
            {
                if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{"))
                {
                    var o = JObject.Parse(json);
                    strict = !string.Equals(o["_mode"]?.ToString(), "simple", StringComparison.OrdinalIgnoreCase);
                    if (o["_courseStations"] is JArray arr)
                        foreach (var t in arr) course.Add(t.Type == JTokenType.Integer ? (int)t : 0);
                }
            }
            catch { }
            return (strict, course);
        }

        /// <summary>
        /// Korten för en station. <paramref name="onlyClass"/> (konfiguratorn) ger bara den grupp som innehåller klassen;
        /// annars alla grupper (tävlingens kort). Klasser grupperas efter <see cref="ShapeEqual"/> / <see cref="StructurallyEqual"/>.
        /// </summary>
        public static List<StationCardBlock> BuildBlocks(StationPrintItem item, bool strict, IReadOnlyList<int>? courseStations, string? onlyClass = null)
        {
            var keys = item.StationsByWeaponClass.Keys.OrderBy(OrderKey).ToList();
            var blocks = new List<StationCardBlock>();
            Func<FaltskytteStationConfig, FaltskytteStationConfig, bool> eq = strict ? StructurallyEqual : ShapeEqual;
            if (onlyClass != null && keys.Contains(onlyClass))
            {
                // Konfiguratorn: gruppen runt den klass som skrivs ut, jämförd mot just den, som kortet läses ur.
                var head = item.StationsByWeaponClass[onlyClass];
                var group = keys.Where(k => k == onlyClass || eq(head, item.StationsByWeaponClass[k])).ToList();
                blocks.Add(Make(item, group, keys.Count, courseStations, onlyClass));
            }
            else
            {
                var rest = new List<string>(keys);
                while (rest.Count > 0)
                {
                    var head = item.StationsByWeaponClass[rest[0]];
                    var group = rest.Where((k, i) => i == 0 || eq(head, item.StationsByWeaponClass[k])).ToList();
                    rest.RemoveAll(group.Contains);
                    blocks.Add(Make(item, group, keys.Count, courseStations));
                }
            }
            if (blocks.Count > 1)
                foreach (var b in blocks) b.ShowClassesInHeader = true;
            if (blocks.Count > 0)
            {
                // QR-koderna en gång per station, på sista kortet.
                blocks[^1].Qr1Url = item.Qr1Url;
                blocks[^1].Qr2Url = item.Qr2Url;
            }
            return blocks;
        }

        static StationCardBlock Make(StationPrintItem item, List<string> classes, int totalClasses, IReadOnlyList<int>? courseStations, string? primaryClass = null)
        {
            var primary = item.StationsByWeaponClass[primaryClass ?? classes[0]];
            int? courseNo = courseStations != null && item.Station >= 1 && item.Station <= courseStations.Count && courseStations[item.Station - 1] > 0
                ? courseStations[item.Station - 1] : null;
            return new StationCardBlock
            {
                Station = item.Station,
                Name = primary.Name,
                CourseNo = courseNo,
                Classes = classes,
                PrimaryClass = primaryClass ?? classes[0],
                ByClass = classes.ToDictionary(k => k, k => item.StationsByWeaponClass[k]),
                ShowClassesInHeader = classes.Count == 1 || classes.Count < totalClasses,
                Compact = item.Compact,
            };
        }

        // ── HTML ──

        public const string Css =
            "body { font-family: sans-serif; margin: 30px; max-width: 210mm; }\n" +
            ".station-block { page-break-after: always; }\n" +
            ".station-block:last-child { page-break-after: auto; }\n" +
            "h1 { font-size: 1.8em; border-bottom: 3px solid #333; padding-bottom: 8px; margin-top: 0; }\n" +
            ".rules { background: #f0f0f0; padding: 12px; border-radius: 6px; margin: 15px 0; font-size: 0.95em; }\n" +
            ".rules-shared { margin-bottom: 4px; }\n" +
            ".rules-tbl { border-collapse: collapse; margin-top: 4px; font-size: 0.95em; width: 100%; }\n" +
            ".rules-tbl th, .rules-tbl td { padding: 2px 12px 2px 0; text-align: left; }\n" +
            ".rules-tbl th { font-weight: 600; border-bottom: 1px solid #888; }\n" +
            ".rules-tbl td { border-bottom: 1px solid #ddd; }\n" +
            ".rules-tbl tr:last-child td { border-bottom: none; }\n" +
            ".tg { border: 2px solid #666; border-radius: 8px; padding: 15px; margin: 20px 0; page-break-inside: avoid; }\n" +
            ".tg h3 { margin: 0 0 10px; font-size: 1.2em; }\n" +
            ".tg-desc { font-style: italic; margin: 10px 0; font-size: 0.95em; line-height: 1.4; }\n" +
            ".tg-img { text-align: center; margin: 10px 0; }\n" +
            ".tg-img img { max-width: 80%; max-height: 200px; border-radius: 6px; }\n" +
            ".fig { margin: 8px 0; padding: 8px; background: #fafafa; border-radius: 4px; display: flex; align-items: center; gap: 12px; }\n" +
            ".fig img { height: 50px; border-radius: 4px; }\n" +
            ".fig-info { flex: 1; }\n" +
            ".pm-badge { background: #ffc107; color: #000; padding: 2px 8px; border-radius: 4px; font-weight: bold; font-size: 0.85em; }\n" +
            ".time-variants { margin: 2px 0 2px 20px; font-size: 0.9em; color: #444; }\n" +
            ".qr-card { text-align: center; margin-top: 20px; page-break-inside: avoid; }\n" +
            ".qr-cut { margin-top: 24px; page-break-before: always; border: 2px dashed #888; border-radius: 8px; padding: 16px; text-align: center; }\n" +
            // Kompakt (fas 3b): en sida. Inga bilder, tät sättning, QR-koderna bredvid varandra på samma sida.
            ".station-block.compact { page-break-after: always; font-size: 0.88em; }\n" +
            ".compact h1 { font-size: 1.35em; padding-bottom: 4px; border-bottom-width: 2px; }\n" +
            ".compact .rules { padding: 6px 10px; margin: 8px 0; }\n" +
            ".compact .tg { padding: 6px 10px; margin: 8px 0; border-width: 1px; }\n" +
            ".compact .tg h3 { margin: 0 0 4px; font-size: 1.05em; }\n" +
            ".compact .tg-desc { margin: 4px 0; font-size: 0.9em; line-height: 1.3; }\n" +
            ".compact .fig { margin: 2px 0; padding: 2px 6px; }\n" +
            ".compact .qr-row { display: flex; gap: 16px; align-items: stretch; margin-top: 10px; page-break-inside: avoid; }\n" +
            ".compact .qr-row .qr-card, .compact .qr-row .qr-cut { flex: 1; margin: 0; page-break-before: auto; padding: 8px; }\n" +
            ".compact .qr-row img { width: 110px !important; height: 110px !important; }\n" +
            ".card-toolbar { position: sticky; top: 0; z-index: 5; background: #fff; border: 1px solid #bbb; border-radius: 6px; padding: 8px 12px; margin: 0 0 16px; display: flex; flex-wrap: wrap; gap: 10px; align-items: center; font-size: 0.95em; }\n" +
            ".card-toolbar button { font: inherit; padding: 4px 12px; border: 1px solid #666; border-radius: 4px; background: #fff; cursor: pointer; }\n" +
            ".card-toolbar button.on { background: #0d6efd; color: #fff; border-color: #0d6efd; }\n" +
            ".card-toolbar .pages { font-weight: 600; }\n" +
            ".card-toolbar .pages.over { color: #b02a37; }\n" +
            "@media print { body { margin: 15mm; } .card-toolbar { display: none !important; } }\n";

        const string Sep = " &nbsp;|&nbsp; ";

        public static string RenderDocument(string title, string? notice, IReadOnlyList<StationCardBlock> blocks, bool isPoang, bool autoPrint, StationCardToolbar? toolbar = null)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"sv\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\">\n");
            sb.Append("<title>").Append(E(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
            if (toolbar != null) RenderToolbar(sb, toolbar);
            if (blocks.Count == 0)
                sb.Append("<div class=\"alert alert-warning\">Inga stationer att skriva ut.</div>\n");
            if (!string.IsNullOrEmpty(notice))
                sb.Append("<div style=\"border:2px solid #b58105;background:#fff8e1;padding:10px 14px;border-radius:6px;margin-bottom:18px;\">")
                  .Append(E(notice)).Append("</div>\n");
            foreach (var b in blocks) RenderBlock(sb, b, isPoang);
            if (autoPrint)
                sb.Append("<script>\n" +
                    "window.addEventListener('load', function () {\n" +
                    "  var imgs = Array.prototype.slice.call(document.images);\n" +
                    "  var pending = imgs.filter(function (im) { return !im.complete; }).length;\n" +
                    "  if (pending === 0) { window.print(); return; }\n" +
                    "  function done() { if (--pending <= 0) window.print(); }\n" +
                    "  imgs.forEach(function (im) {\n" +
                    "    if (im.complete) return;\n" +
                    "    im.addEventListener('load', done);\n" +
                    "    im.addEventListener('error', done);\n" +
                    "  });\n" +
                    "});\n</script>\n");
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        /// <summary>Full / Kompakt, sidräknare och Skriv ut. Bara på skärmen. Läget sparas av konfiguratorn (opener), inte av kortet självt.</summary>
        static void RenderToolbar(StringBuilder sb, StationCardToolbar t)
        {
            var wc = new string((t.WeaponClass ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
            string Btn(string mode, string label)
            {
                var on = t.Mode == mode;
                var dis = t.CanSetMode ? "" : " disabled title=\"Spara konfigurationen först (och du måste få ändra den) för att välja läge.\"";
                return "<button type=\"button\" class=\"" + (on ? "on" : "") + "\"" + dis + " onclick=\"setMode('" + mode + "')\">" + label + "</button>";
            }
            sb.Append("<div class=\"card-toolbar\"><span><strong>Utskrift:</strong></span>")
              .Append(Btn(StationPrintMode.Full, "Full")).Append(Btn(StationPrintMode.Kompakt, "Kompakt (1 sida)"))
              .Append("<span class=\"pages\" id=\"cardPages\"></span>")
              .Append("<button type=\"button\" onclick=\"window.print()\" style=\"margin-left:auto\">Skriv ut</button></div>\n");
            sb.Append("<script>\n")
              .Append("function setMode(m){ var o = window.opener; if (o && o.faltCfgPrintStationMode) { document.body.style.opacity = '0.5'; o.faltCfgPrintStationMode(")
              .Append(t.Station).Append(", '").Append(wc).Append("', m, window); } }\n")
              .Append("function countPages(){ var H = 1009, n = 0; // A4 minus 15 mm marginal, i skärmpixlar\n")
              .Append("  document.querySelectorAll('.station-block').forEach(function(b){ var cut = b.querySelector('.qr-cut'); var sep = cut && !b.classList.contains('compact');\n")
              .Append("    var h = b.offsetHeight - (sep ? cut.offsetHeight + 24 : 0); n += Math.max(1, Math.ceil(h / H)) + (sep ? 1 : 0); });\n")
              .Append("  var el = document.getElementById('cardPages'); if (!el) return; el.textContent = 'Ungefär ' + n + (n === 1 ? ' sida' : ' sidor'); el.className = 'pages' + (n > 1 && document.querySelector('.station-block.compact') ? ' over' : ''); }\n")
              .Append("window.addEventListener('load', countPages);\n")
              .Append("</script>\n");
        }

        static void RenderBlock(StringBuilder sb, StationCardBlock b, bool isPoang)
        {
            var primary = b.ByClass[b.PrimaryClass];
            var combined = b.Classes.Count > 1;
            sb.Append(b.Compact ? "<div class=\"station-block compact\">\n" : "<div class=\"station-block\">\n");

            var header = "Station " + b.Station
                + (string.IsNullOrWhiteSpace(b.Name) ? "" : " – " + E(b.Name))
                + (b.CourseNo.HasValue ? " · tävlingens station " + b.CourseNo : "");
            if (b.ShowClassesInHeader) header += " — Vapenklass " + string.Join(", ", b.Classes);
            sb.Append("<h1>").Append(header).Append("</h1>\n");

            // Förutsättningar
            sb.Append("<div class=\"rules\">");
            if (combined) RenderCombinedRules(sb, b, isPoang);
            else
            {
                sb.Append("<strong>Skjuttid:</strong> ").Append(primary.ShootingTimeSec).Append(" sek");
                sb.Append(Sep).Append("<strong>Utgångsläge:</strong> ").Append(E(primary.ShooterStartPosition));
                sb.Append(Sep).Append("<strong>Vapenläge:</strong> ").Append(E(primary.WeaponStartPosition));
                sb.Append(Sep).Append("<strong>").Append(E(primary.SupportHand)).Append("</strong>");
                if (primary.MinShotsPerFigure > 0)
                    sb.Append("<br><strong>Min träff/fig:</strong> ").Append(primary.MinShotsPerFigure);
                if (!isPoang && primary.MaxShotsPerFigure > 0 && primary.MaxShotsPerFigure < 6)
                    sb.Append(Sep).Append("<strong>Max träff/fig:</strong> ").Append(primary.MaxShotsPerFigure);
            }
            sb.Append("</div>\n");

            // Målgrupper och figurer
            for (var tgIdx = 0; tgIdx < primary.TargetGroups.Count; tgIdx++)
            {
                var tg = primary.TargetGroups[tgIdx];
                var letter = (char)(64 + tg.Group);
                sb.Append("<div class=\"tg\">\n<h3>Målgrupp ").Append(letter).Append("</h3>\n");
                if (!string.IsNullOrWhiteSpace(tg.Description))
                    sb.Append("<div class=\"tg-desc\">").Append(E(tg.Description).Replace("\n", "<br>")).Append("</div>\n");
                if (!b.Compact && !string.IsNullOrWhiteSpace(tg.ImageUrl))
                    sb.Append("<div class=\"tg-img\"><img src=\"").Append(E(tg.ImageUrl)).Append("\" alt=\"Målgrupp ").Append(letter).Append("\"></div>\n");
                for (var fIdx = 0; fIdx < tg.Figures.Count; fIdx++)
                {
                    var fig = tg.Figures[fIdx];
                    sb.Append("<div class=\"fig\">");
                    if (!b.Compact && !string.IsNullOrWhiteSpace(fig.ImageUrl)) sb.Append("<img src=\"").Append(E(fig.ImageUrl)).Append("\" alt=\"\">");
                    sb.Append("<div class=\"fig-info\"><strong>Figur ").Append(fig.FigureNumber).Append("</strong>");
                    if (fig.IsPoangmal) sb.Append(" <span class=\"pm-badge\">Poängräkning</span>");

                    if (combined && fig.Behavior != "Fast")
                    {
                        sb.Append(" — ").Append(E(fig.Behavior));
                        // Klasserna grupperade efter sin tid för just den här figuren.
                        var groups = new List<(string Timing, List<string> Classes)>();
                        foreach (var c in b.Classes)
                        {
                            var cFig = b.ByClass[c].TargetGroups.ElementAtOrDefault(tgIdx)?.Figures.ElementAtOrDefault(fIdx);
                            if (cFig == null) continue;
                            var t = BehaviorTiming(cFig);
                            var g = groups.FindIndex(x => x.Timing == t);
                            if (g < 0) groups.Add((t, new List<string> { c })); else groups[g].Classes.Add(c);
                        }
                        if (groups.Count == 1)
                        {
                            if (groups[0].Timing.Length > 0) sb.Append(", ").Append(E(groups[0].Timing));
                        }
                        else
                        {
                            sb.Append("<br>");
                            foreach (var g in groups)
                                sb.Append("<div class=\"time-variants\">").Append(E(string.Join(",", g.Classes))).Append(": ")
                                  .Append(g.Timing.Length == 0 ? "inga tider" : E(g.Timing)).Append("</div>");
                        }
                    }
                    else
                    {
                        sb.Append(" — ").Append(E(BehaviorText(fig)));
                    }
                    sb.Append("</div></div>\n");
                }
                sb.Append("</div>\n");
            }

            if (!string.IsNullOrEmpty(b.Qr1Url))
            {
                if (b.Compact) sb.Append("<div class=\"qr-row\">");
                sb.Append("<div class=\"qr-card\"><img src=\"").Append(E(b.Qr1Url)).Append("\" alt=\"QR Förutsättningar\" style=\"width:150px;height:150px\">");
                sb.Append("<div style=\"font-size:0.85em;color:#333;margin-top:4px;font-weight:bold\">Förutsättningar — skanna här</div></div>\n");
                sb.Append("<div class=\"qr-cut\"><div style=\"font-size:1.1em;font-weight:bold;margin-bottom:8px\">Station ").Append(b.Station)
                  .Append(" — Resultatregistrering</div><img src=\"").Append(E(b.Qr2Url)).Append("\" alt=\"QR Resultat\" style=\"width:150px;height:150px\">");
                sb.Append("<div style=\"font-size:0.85em;color:#333;margin-top:4px\">Klipp ut och placera vid målgrupperna</div>");
                sb.Append("<div style=\"font-size:0.8em;color:#666\">Kräver inloggning</div></div>\n");
                if (b.Compact) sb.Append("</div>\n");
            }
            sb.Append("</div>\n");
        }

        sealed record Field(string Label, string Suffix, bool Descriptive, Func<FaltskytteStationConfig, string> Get);

        static void RenderCombinedRules(StringBuilder sb, StationCardBlock b, bool isPoang)
        {
            var st = b.Classes.Select(c => b.ByClass[c]).ToList();
            var fields = new List<Field>
            {
                new("Skjuttid", " sek", false, s => s.ShootingTimeSec.ToString()),
                new("Utgångsläge", "", false, s => s.ShooterStartPosition ?? ""),
                new("Vapenläge", "", false, s => s.WeaponStartPosition ?? ""),
                new("Stödhand", "", true, s => s.SupportHand ?? ""),
            };
            if (st.Any(s => s.MinShotsPerFigure > 0)) fields.Add(new("Min träff/fig", "", false, s => s.MinShotsPerFigure.ToString()));
            if (!isPoang && st.Any(s => s.MaxShotsPerFigure > 0 && s.MaxShotsPerFigure < 6)) fields.Add(new("Max träff/fig", "", false, s => s.MaxShotsPerFigure.ToString()));

            var shared = fields.Where(f => st.All(s => f.Get(s) == f.Get(st[0]))).ToList();
            var varying = fields.Where(f => !shared.Contains(f)).ToList();

            if (shared.Count > 0)
            {
                sb.Append("<div class=\"rules-shared\">");
                sb.Append(string.Join(Sep, shared.Select(f => f.Descriptive
                    ? "<strong>" + E(f.Get(st[0])) + "</strong>"
                    : "<strong>" + f.Label + ":</strong> " + E(f.Get(st[0])) + f.Suffix)));
                sb.Append("</div>");
            }
            if (varying.Count > 0)
            {
                // Rader sammanslagna på hela sin avvikande uppsättning: A, A_Opt, R på en rad när allt stämmer.
                var sigGroups = new List<(string Sig, List<string> Classes)>();
                foreach (var c in b.Classes)
                {
                    var sig = string.Join("|", varying.Select(f => f.Get(b.ByClass[c])));
                    var g = sigGroups.FindIndex(x => x.Sig == sig);
                    if (g < 0) sigGroups.Add((sig, new List<string> { c })); else sigGroups[g].Classes.Add(c);
                }
                sb.Append("<table class=\"rules-tbl\"><thead><tr><th>Vapenklass</th>");
                foreach (var f in varying) sb.Append("<th>").Append(f.Label).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                foreach (var g in sigGroups)
                {
                    sb.Append("<tr><td>").Append(E(string.Join(", ", g.Classes))).Append("</td>");
                    foreach (var f in varying) sb.Append("<td>").Append(E(f.Get(b.ByClass[g.Classes[0]]))).Append(f.Suffix).Append("</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }
        }
    }
}
