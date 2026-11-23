using System.Globalization;
using System.Net;
using System.Text;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    public class ShootingCardShooter
    {
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
    }

    /// <summary>Ett skjutkort: en patrull (eller en del av en) med samma stationer för alla skyttar på kortet.</summary>
    public class ShootingCardModel
    {
        /// <summary>Null på ett blankt kort (numret skrivs för hand).</summary>
        public int? PatrolNumber { get; set; }
        public string? Label { get; set; }
        public string StartTime { get; set; } = "";
        public string WeaponGroupText { get; set; } = "";
        public int Part { get; set; } = 1;
        public int Parts { get; set; } = 1;
        public bool IsBlank { get; set; }
        public List<ShootingCardStation> Stations { get; set; } = new();
        public List<ShootingCardShooter> Shooters { get; set; } = new();
    }

    public class ShootingCardOptions
    {
        public string CompetitionName { get; set; } = "";
        public string Layout { get; set; } = ShootingCardLayout.Figures;
        public string Paper { get; set; } = ShootingCardLayout.Landscape;
        public int PerSheet { get; set; } = 1;
        public bool SumColumn { get; set; }
        public bool Notes { get; set; } = true;
        public bool AutoPrint { get; set; }
        public string? BackUrl { get; set; }
        /// <summary>Texter som visas överst på skärmen (aldrig i utskriften).</summary>
        public List<string> Notices { get; set; } = new();
    }

    /// <summary>
    /// Skjutkorten som en fristående utskriftssida (Layout = null), som stationskorten. Geometrin kommer ur
    /// <see cref="ShootingCardLayout"/>; här finns bara markeringen. Allt i mm, sidstorleken satt med @page.
    /// </summary>
    public static class ShootingCardRenderer
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
        static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string F1(double v) => v.ToString("0.0", CultureInfo.GetCultureInfo("sv-SE"));

        public static string RenderDocument(IReadOnlyList<ShootingCardModel> cards, ShootingCardOptions o)
        {
            var layout = ShootingCardLayout.NormalizeLayout(o.Layout);
            var paper = ShootingCardLayout.NormalizePaper(o.Paper);
            var per = ShootingCardLayout.NormalizePerSheet(o.PerSheet);
            var (sw, sh) = ShootingCardLayout.SheetSize(paper);
            var rects = ShootingCardLayout.Cards(paper, per);
            var sheets = ShootingCardLayout.Sheets(cards, per);

            // Den trängsta kortet avgör varningen på skärmen.
            ShootingCardFit? worst = null;
            foreach (var c in cards)
            {
                var m = ShootingCardLayout.Measure(layout, c.Stations, c.Shooters.Count, rects[0], o.SumColumn);
                var f = ShootingCardLayout.Fit(layout, m.BoxW);
                if (worst == null || f.BoxW < worst.BoxW) worst = f;
            }

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"sv\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\">\n");
            sb.Append("<title>Skjutkort").Append(string.IsNullOrEmpty(o.CompetitionName) ? "" : " — " + E(o.CompetitionName)).Append("</title>\n<style>\n");
            sb.Append("@page{size:A4 ").Append(paper).Append(";margin:0}\n");
            sb.Append(Css);
            sb.Append("</style>\n</head>\n<body>\n");

            // Skärmen: verktygsrad och varningar. Döljs helt vid utskrift.
            sb.Append("<div class=\"bar\">");
            if (!string.IsNullOrEmpty(o.BackUrl)) sb.Append("<a href=\"").Append(E(o.BackUrl)).Append("\">← Tillbaka</a>");
            sb.Append("<span><strong>").Append(cards.Count).Append(cards.Count == 1 ? " kort" : " kort").Append("</strong> på ").Append(sheets.Count)
              .Append(sheets.Count == 1 ? " sida" : " sidor").Append(" · ")
              .Append(layout == ShootingCardLayout.Figures ? "Rutor per figur" : "Bara träff/figurer").Append(" · A4 ")
              .Append(paper == ShootingCardLayout.Portrait ? "stående" : "liggande").Append(" · ").Append(per).Append(per == 1 ? " kort" : " kort").Append(" per sida</span>");
            if (worst != null)
            {
                var fit = ShootingCardLayout.Fit(layout, worst.BoxW);
                sb.Append("<span class=\"chip ").Append(fit.Level).Append("\">").Append(layout == ShootingCardLayout.Figures ? "Figurruta " : "Träffruta ")
                  .Append(F1(fit.BoxW)).Append(" mm · ").Append(fit.Level == "ok" ? "går att skriva i" : fit.Level == "warn" ? "tight" : "för trångt").Append("</span>");
            }
            sb.Append("<button type=\"button\" onclick=\"window.print()\" class=\"go\">Skriv ut</button></div>\n");
            foreach (var n in o.Notices)
                sb.Append("<div class=\"notice\">").Append(E(n)).Append("</div>\n");
            if (cards.Count == 0)
                sb.Append("<div class=\"notice\">Inga skjutkort att skriva ut. Kontrollera urvalet av patruller.</div>\n");

            sb.Append("<div class=\"stage\">\n");
            foreach (var sheet in sheets)
            {
                sb.Append("<div class=\"holder\" data-w=\"").Append(Num(sw)).Append("\" data-h=\"").Append(Num(sh)).Append("\"><div class=\"sheet\" style=\"width:")
                  .Append(Mm(sw)).Append(";height:").Append(Mm(sh)).Append("\">");
                for (var i = 0; i < sheet.Count; i++)
                    if (sheet[i] != null) RenderCard(sb, sheet[i]!, rects[i], layout, o);
                if (per == 2)
                    sb.Append(paper == ShootingCardLayout.Portrait
                        ? "<div class=\"cut h\"></div>"
                        : "<div class=\"cut v\"></div>");
                sb.Append("</div></div>\n");
            }
            sb.Append("</div>\n");
            sb.Append("<script>\n(function(){\n" +
                "var p=document.createElement('div');p.style.cssText='position:absolute;visibility:hidden;width:100mm;height:1px';document.body.appendChild(p);\n" +
                "var px=(p.offsetWidth/100)||3.78;document.body.removeChild(p);\n" +
                "function fit(){document.querySelectorAll('.holder').forEach(function(h){var s=h.firstElementChild,w=+h.dataset.w,hh=+h.dataset.h;\n" +
                "var k=Math.min(1.1,(h.parentElement.clientWidth-32)/(w*px));s.style.transform='scale('+k+')';h.style.width=(w*px*k)+'px';h.style.height=(hh*px*k)+'px';});}\n" +
                "window.addEventListener('resize',fit);fit();\n" +
                (o.AutoPrint ? "window.addEventListener('load',function(){setTimeout(function(){window.print();},350);});\n" : "") +
                "})();\n</script>\n</body>\n</html>\n");
            return sb.ToString();
        }

        static void RenderCard(StringBuilder sb, ShootingCardModel c, ShootingCardRect rect, string layout, ShootingCardOptions o)
        {
            var m = ShootingCardLayout.Measure(layout, c.Stations, c.Shooters.Count, rect, o.SumColumn);
            var fig = layout == ShootingCardLayout.Figures;
            var poangU = fig ? ShootingCardLayout.PoangBoxUnitsFig : ShootingCardLayout.PoangBoxUnitsTot;
            double GroupW(ShootingCardStation s) => ShootingCardLayout.StationUnits(layout, s) * m.Unit + ShootingCardLayout.GroupBorder;
            var sumW = ShootingCardLayout.SumColumnW + ShootingCardLayout.GroupBorder;

            sb.Append("<div class=\"card\" style=\"left:").Append(Mm(rect.X)).Append(";top:").Append(Mm(rect.Y)).Append(";width:").Append(Mm(rect.W))
              .Append(";height:").Append(Mm(rect.H)).Append(";--k:").Append(Num(Math.Round(m.FontScale, 3))).Append(";--cw:").Append(Mm(m.ContentW)).Append("\">");

            // Huvud: tävling, patrull, starttid och en ruta för justerad starttid.
            var sub = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.Label)) sub.Add(c.Label!);
            if (!string.IsNullOrWhiteSpace(c.WeaponGroupText)) sub.Add("Vapengrupp " + c.WeaponGroupText);
            if (!c.IsBlank) sub.Add(c.Shooters.Count + (c.Shooters.Count == 1 ? " skytt" : " skyttar"));
            if (c.Parts > 1) sub.Add("blad " + c.Part + " av " + c.Parts);
            var pn = c.IsBlank || c.PatrolNumber == null
                ? "<div class=\"pn\"><small>PATRULL</small><span class=\"blank\"></span></div>"
                : "<div class=\"pn\"><small>PATRULL</small>" + c.PatrolNumber + "</div>";
            var start = "<div class=\"st\"><small>Starttid</small>" + (string.IsNullOrEmpty(c.StartTime) ? "<span class=\"blank\"></span>" : "<b>" + E(c.StartTime) + "</b>") + "</div>";
            var adj = "<div class=\"adj\" style=\"height:" + Mm(m.Narrow ? 9.5 : m.HeaderH - 1.5) + "\"><i>Justerad starttid</i></div>";
            var left = "<div class=\"l\"><div class=\"t1\">" + E(o.CompetitionName) + "</div><div class=\"t2\">" + E(string.Join(" · ", sub)) + "</div></div>";
            sb.Append("<div class=\"hd").Append(m.Narrow ? " wrap" : "").Append("\" style=\"height:").Append(Mm(m.HeaderH)).Append("\">")
              .Append(m.Narrow ? left + pn + start + adj : left + pn + start + adj).Append("</div>");

            // Stationsrubriker
            sb.Append("<div class=\"th\" style=\"height:").Append(Mm(m.TableHeaderH)).Append("\"><div class=\"nm thn\" style=\"width:").Append(Mm(m.NameW)).Append("\">Skytt</div>");
            foreach (var s in c.Stations)
            {
                var subText = (fig ? s.Figures + " fig" : "träff/fig") + (s.PoangmalCount > 0 ? " +P" : "");
                sb.Append("<div class=\"g\" style=\"width:").Append(Mm(GroupW(s))).Append("\"><div class=\"gh\"><span class=\"n\">").Append(s.Number)
                  .Append("</span><span class=\"f\">").Append(E(subText)).Append("</span></div></div>");
            }
            if (o.SumColumn)
                sb.Append("<div class=\"g\" style=\"width:").Append(Mm(sumW)).Append("\"><div class=\"gh\"><span class=\"n\">Σ</span><span class=\"f\">träff/fig</span></div></div>");
            sb.Append("</div>");

            // Rader
            var n = Math.Max(1, c.Shooters.Count);
            for (var r = 0; r < n; r++)
            {
                var sh = r < c.Shooters.Count ? c.Shooters[r] : null;
                var last = r == n - 1;
                sb.Append("<div class=\"row").Append(last ? " last" : "").Append("\" style=\"height:").Append(Mm(m.RowH)).Append("\">");
                sb.Append("<div class=\"nm\" style=\"width:").Append(Mm(m.NameW)).Append("\"><b><span class=\"ix\">").Append(r + 1).Append("</span>")
                  .Append(E(sh?.Name)).Append("</b><small>").Append(E(sh == null ? "" : string.Join(" · ", new[] { sh.Club, sh.ShootingClass }.Where(x => !string.IsNullOrWhiteSpace(x))))).Append("</small></div>");
                foreach (var s in c.Stations)
                {
                    sb.Append("<div class=\"g\" style=\"width:").Append(Mm(GroupW(s))).Append("\">");
                    if (fig)
                        for (var i = 0; i < s.Figures; i++) sb.Append("<i class=\"c\" style=\"width:").Append(Mm(m.Unit)).Append("\"></i>");
                    sb.Append("<i class=\"c d\" style=\"width:").Append(Mm(ShootingCardLayout.TotalBoxUnits * m.Unit)).Append("\"></i>");
                    for (var i = 0; i < s.PoangmalCount; i++) sb.Append("<i class=\"c p\" style=\"width:").Append(Mm(poangU * m.Unit)).Append("\"><b>P</b></i>");
                    sb.Append("</div>");
                }
                if (o.SumColumn) sb.Append("<div class=\"g\" style=\"width:").Append(Mm(sumW)).Append("\"><i class=\"c d\" style=\"width:100%\"></i></div>");
                sb.Append("</div>");
            }
            if (o.Notes && m.NotesH >= 8)
                sb.Append("<div class=\"notes\" style=\"height:").Append(Mm(m.NotesH)).Append("\"><i>Anteckningar</i></div>");
            sb.Append("</div>");
        }

        const string Css = @"
*{box-sizing:border-box}
html,body{margin:0;padding:0}
body{font-family:Arial,'Liberation Sans',Helvetica,sans-serif;color:#111;background:#e6e9ee;-webkit-print-color-adjust:exact;print-color-adjust:exact}
.bar{position:sticky;top:0;z-index:5;display:flex;flex-wrap:wrap;gap:8px 18px;align-items:center;padding:10px 16px;background:#fff;border-bottom:1px solid #cfd5de;font-size:14px}
.bar a{color:#0d6efd;text-decoration:none}
.bar .go{margin-left:auto;background:#0d6efd;color:#fff;border:0;border-radius:6px;padding:7px 16px;font:inherit;cursor:pointer}
.chip{border-radius:4px;padding:2px 8px;font-size:13px;font-weight:600}
.chip.ok{background:#e2f4ea;color:#14663a}.chip.warn{background:#fff2d9;color:#8a5200}.chip.bad{background:#fde7e4;color:#a8231b}
.notice{margin:12px 16px 0;border:2px solid #b58105;background:#fff8e1;padding:10px 14px;border-radius:6px;font-size:14px}
.stage{padding:16px;display:flex;flex-direction:column;align-items:center;gap:18px}
.holder{position:relative}
.sheet{position:absolute;left:0;top:0;background:#fff;transform-origin:0 0;box-shadow:0 2px 14px rgba(0,0,0,.28);overflow:hidden}
.card{position:absolute;padding:7mm;--k:1;--cw:196mm}
.cut{position:absolute;border:0 dashed #8a8a8a}
.cut.h{left:0;right:0;top:50%;border-top-width:.2mm}
.cut.v{top:0;bottom:0;left:50%;border-left-width:.2mm}
.hd{display:flex;align-items:stretch;gap:4mm;width:var(--cw)}
.hd.wrap{flex-wrap:wrap;align-items:center}
.hd.wrap .l{flex:1 1 100%}
.hd .l{flex:1;min-width:0;display:flex;flex-direction:column;justify-content:center}
.hd .t1{font-size:calc(10pt * var(--k));font-weight:700;line-height:1.15}
.hd .t2{font-size:6.5pt;color:#444;margin-top:.6mm}
.hd .pn{font-size:calc(21pt * var(--k));font-weight:800;letter-spacing:.02em;line-height:1;display:flex;align-items:center;white-space:nowrap}
.hd .pn small{font-size:7pt;font-weight:700;margin-right:2mm;letter-spacing:.08em;color:#444}
.hd .st{display:flex;flex-direction:column;justify-content:center;align-items:flex-end;white-space:nowrap}
.hd .st small{font-size:5.5pt;letter-spacing:.06em;color:#444;text-transform:uppercase}
.hd .st b{font-size:calc(14pt * var(--k));line-height:1.05}
.blank{display:inline-block;min-width:16mm;height:6mm;border-bottom:.3mm solid #111}
.adj{border:.3mm solid #111;width:31mm;position:relative;flex:none}
.adj i{position:absolute;left:1mm;top:.6mm;font-size:5pt;font-style:normal;color:#444;letter-spacing:.04em;text-transform:uppercase}
.th,.row{display:flex;width:var(--cw);border-left:.5mm solid #111;border-right:.5mm solid #111}
.th{border-top:.5mm solid #111;border-bottom:.5mm solid #111;margin-top:2mm}
.row{border-bottom:.18mm solid #555}
.row.last{border-bottom:.5mm solid #111}
.nm{flex:none;padding:0 1.4mm;display:flex;flex-direction:column;justify-content:center;overflow:hidden;white-space:nowrap}
.nm b{font-size:calc(7.5pt * var(--k));line-height:1.15;display:block;overflow:hidden;text-overflow:ellipsis}
.nm small{font-size:calc(5.5pt * var(--k));color:#444;line-height:1.2;display:block;overflow:hidden;text-overflow:ellipsis}
.nm .ix{font-size:5.5pt;color:#777;margin-right:1.2mm;display:inline-block;min-width:2.2mm}
.nm.thn{font-size:6pt;color:#444;justify-content:flex-end;padding-bottom:1mm;letter-spacing:.06em;text-transform:uppercase}
.g{flex:none;display:flex;border-left:.5mm solid #111}
.g .c{display:block;height:100%;border-left:.15mm solid #666;position:relative}
.g .c:first-child{border-left:0}
.g .c.d{background:linear-gradient(to bottom right,transparent calc(50% - .13mm),#555 calc(50% - .13mm),#555 calc(50% + .13mm),transparent calc(50% + .13mm))}
.g .c.p{background:#efefef}
.g .c.p b{position:absolute;left:.5mm;top:.3mm;font-size:4.5pt;color:#555}
.gh{display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;width:100%;line-height:1.1}
.gh .n{font-size:calc(9pt * var(--k));font-weight:800}
.gh .f{font-size:4.8pt;color:#444;white-space:nowrap}
.notes{margin-top:1.6mm;width:var(--cw);border:.18mm dashed #777;position:relative}
.notes i{position:absolute;left:1.4mm;top:.9mm;font-size:5.5pt;color:#666;font-style:normal;letter-spacing:.05em;text-transform:uppercase}
@media print{
  body{background:#fff}
  .bar,.notice{display:none!important}
  .stage{padding:0;gap:0;display:block}
  .holder{width:auto!important;height:auto!important;margin:0}
  .sheet{position:relative!important;transform:none!important;box-shadow:none!important;break-after:page;page-break-after:always}
  .holder:last-child .sheet{break-after:auto;page-break-after:auto}
}
";
    }
}
