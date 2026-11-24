using System.Globalization;
using System.Net;
using System.Text;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>Ett pappersformat en layout kan skrivas ut på: id (<c>p1</c>, <c>l2</c> …), text, papper och antal kort per sida.</summary>
    public record ShootingCardFormat(string Id, string Label, string Paper, int PerSheet);

    /// <summary>
    /// En skjutkortslayout (Expertgruppen 2026-11-24). Ny layout = en klass som implementerar det här och en rad i <see cref="ShootingCardLayouts"/>.
    /// Layouten äger sitt pappersformat, sin kapacitet (skyttar per kort), sin uppdelning, sin måttkontroll och sin ritning;
    /// mm räknas bara i <see cref="ShootingCardLayout"/> och <see cref="ShootingCardGeometry"/>, aldrig i ritningen.
    /// </summary>
    public interface IShootingCardLayout
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        /// <summary>De pappersformat layouten klarar. Första är standard; ett enda format betyder att papperet är låst.</summary>
        IReadOnlyList<ShootingCardFormat> Formats { get; }
        /// <summary>Hur många skyttar ett kort rymmer.</summary>
        int Capacity { get; }
        /// <summary>Sant när layouten ritar poängrutan vid en viss figur, så att poängmålens plats avgör vilka skyttar som delar kort.</summary>
        bool PoangByFigure { get; }
        bool UsesHatch { get; }
        string Css { get; }
        string BarLabel(ShootingCardOptions o);
        /// <summary>Hur skyttarna fördelas på kort.</summary>
        IReadOnlyList<int> Split(int shooters);
        /// <summary>Hur mycket plats en stationsuppsättning kräver (högre = trängre), för att välja det mest krävande kortet.</summary>
        double Demand(IReadOnlyList<ShootingCardStation> stations);
        ShootingCardFitReport Fit(IReadOnlyList<ShootingCardStation> stations, int shooters, ShootingCardRect card, ShootingCardOptions o);
        void Render(StringBuilder sb, ShootingCardModel card, ShootingCardRect rect, ShootingCardOptions o);
    }

    /// <summary>Registret över layouter. Id:n är ett kontrakt (länkar, sparade val): byt aldrig ett id.</summary>
    public static class ShootingCardLayouts
    {
        public static readonly IReadOnlyList<IShootingCardLayout> All = new IShootingCardLayout[]
        {
            new ShootingBlockLayout(), new ShootingCardSheetLayout(), new ShootingTableLayout(ShootingCardLayout.Figures), new ShootingTableLayout(ShootingCardLayout.Total)
        };

        /// <summary>Null för ett okänt id: anroparen ska säga det, inte tyst välja en annan layout.</summary>
        public static IShootingCardLayout? Get(string? id) => All.FirstOrDefault(l => l.Id == id);
        public static IShootingCardLayout Default => All.First(l => l.Id == ShootingCardLayout.Figures);

        /// <summary>Skyttarna fyller kort efter kort (4+1, inte 3+2): den sista bär resten.</summary>
        public static IReadOnlyList<int> FillSizes(int shooters, int capacity)
        {
            if (shooters <= 0) return new[] { 0 };
            var sizes = new List<int>();
            for (var left = shooters; left > 0; left -= capacity) sizes.Add(Math.Min(capacity, left));
            return sizes;
        }
    }

    /// <summary>Gemensam markering för de nya layouterna (huvudet, cellerna, snedstreckningen).</summary>
    internal static class ShootingCardHtml
    {
        public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        public static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
        public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Huvudet: tävling, patrull, starttid och en ruta för justerad starttid (samma för alla layouter).</summary>
        public static string Header(ShootingCardModel c, ShootingCardOptions o, double headerH, bool narrow, bool showShooterCount)
        {
            var sub = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.Label)) sub.Add(c.Label!);
            if (!string.IsNullOrWhiteSpace(c.WeaponGroupText)) sub.Add("Vapengrupp " + c.WeaponGroupText);
            if (!c.IsBlank && showShooterCount) sub.Add(c.Shooters.Count + (c.Shooters.Count == 1 ? " skytt" : " skyttar"));
            if (c.Parts > 1) sub.Add("blad " + c.Part + " av " + c.Parts);
            var pn = c.IsBlank || c.PatrolNumber == null
                ? "<div class=\"pn\"><small>PATRULL</small><span class=\"blank\"></span></div>"
                : "<div class=\"pn\"><small>PATRULL</small>" + c.PatrolNumber + "</div>";
            var start = "<div class=\"st\"><small>Starttid</small>" + (string.IsNullOrEmpty(c.StartTime) ? "<span class=\"blank\"></span>" : "<b>" + E(c.StartTime) + "</b>") + "</div>";
            var adj = "<div class=\"adj\" style=\"height:" + Mm(narrow ? 9.5 : headerH - 1.5) + "\"><i>Justerad starttid</i></div>";
            var left = "<div class=\"l\"><div class=\"t1\">" + E(o.CompetitionName) + "</div><div class=\"t2\">" + E(string.Join(" · ", sub)) + "</div></div>";
            return "<div class=\"hd" + (narrow ? " wrap" : "") + "\" style=\"height:" + Mm(headerH) + "\">" + left + pn + start + adj + "</div>";
        }

        /// <summary>Snedstreckningen definieras en gång per dokument som vektor (SVG), så den inte beror på webbläsarens val om bakgrundsgrafik.</summary>
        public const string HatchDefs =
            "<svg width=\"0\" height=\"0\" style=\"position:absolute\" aria-hidden=\"true\"><defs>" +
            "<pattern id=\"skh\" patternUnits=\"userSpaceOnUse\" width=\"1.1mm\" height=\"1.1mm\" patternTransform=\"rotate(45)\"><rect width=\"1.1mm\" height=\".34mm\" fill=\"#000\"/></pattern>" +
            "</defs></svg>\n";

        /// <summary>En ruta som inte får skrivas i: tät svart snedstreckning.</summary>
        public const string OffCell = "<i class=\"c off\"><svg preserveAspectRatio=\"none\"><rect width=\"100%\" height=\"100%\" fill=\"url(#skh)\"/></svg></i>";

        public static string HitCell(int slot) => "<i class=\"c\"><u>" + slot + "</u></i>";
        public static string SplitCell(int slot, int ordinal) => "<i class=\"c sp\"><u>" + slot + "</u><b class=\"pq\">P" + ordinal + "</b></i>";
        public static string PoangBox(int ordinal) => "<i class=\"c p\"><b>P" + ordinal + "</b></i>";
        public static string SumBox() => "<i class=\"c sum\"><svg preserveAspectRatio=\"none\"><line x1=\"0\" y1=\"100%\" x2=\"100%\" y2=\"0\" stroke=\"#111\" stroke-width=\".25mm\"/></svg><small>Σ träff/fig</small></i>";

        /// <summary>Cellernas utseende, delat mellan Skyttblock och Skyttkort. Ramar är riktiga <c>border</c> (inte bakgrund eller skugga), så de följer med vid utskrift.</summary>
        public const string CellCss = @"
.sk .c,.kc .c{display:block;position:relative;border-right:.15mm solid #555;border-bottom:.15mm solid #555;box-sizing:border-box;background:#fff;min-width:0;font-style:normal}
.sk .c u,.kc .c u{position:absolute;left:.7mm;top:.3mm;font-size:6.5pt;line-height:1;color:#333;text-decoration:none}
.sk .c.off svg,.kc .c.off svg,.sk .c.sum svg,.kc .c.sum svg{position:absolute;left:0;top:0;width:100%;height:100%;display:block}
.sk .c.sp .pq,.kc .c.sp .pq{position:absolute;right:0;top:0;bottom:0;width:42%;border:.5mm solid #111;background:#fff;font-size:8pt;font-weight:800;color:#111;padding:.4mm 1mm;box-sizing:border-box}
.sk .c.p,.kc .c.p{border:.5mm solid #111}
.sk .c.p b,.kc .c.p b{position:absolute;left:1mm;top:.4mm;font-size:8pt;color:#111}
.sk .c.sum small,.kc .c.sum small{position:absolute;left:.8mm;top:.4mm;font-size:6pt;color:#333;background:#fff;padding:0 .4mm}
.col .sh-bg,.col .sk-sh,.col .kc-t .h,.col .kc-t .sn{background:#dbe7ff}
.col .c.p,.col .c.sp .pq{background:#ffefa8}
.col .c.sum{background:#d9f2df}
.col .c.off{background:#e4e4e4}
";
    }

    /// <summary>Dagens layouter, <c>fig</c> och <c>tot</c>: en tabell med en rad per skytt. Ritningen och måtten är oförändrade (se facit i testerna).</summary>
    public sealed class ShootingTableLayout : IShootingCardLayout
    {
        readonly string _id;
        public ShootingTableLayout(string id) { _id = id; }
        bool Fig => _id == ShootingCardLayout.Figures;
        public string Id => _id;
        public string Name => Fig ? "Ruta per figur" : "En ruta per station";
        public string Description => Fig
            ? "En rad per skytt: en ruta för varje figur och en ruta för träff/figurer. Behöver mest plats."
            : "En rad per skytt och en ruta per station, till exempel 6/3.";
        public IReadOnlyList<ShootingCardFormat> Formats { get; } =
            ShootingCardLayout.Formats.Select(f => new ShootingCardFormat(f.Id, f.Label, f.Paper, f.PerSheet)).ToList();
        public int Capacity => ShootingCardLayout.MaxShootersPerCard;
        public bool PoangByFigure => false;
        public bool UsesHatch => false;
        public string Css => "";
        public string BarLabel(ShootingCardOptions o) => Fig ? "Rutor per figur" : "Bara träff/figurer";
        public IReadOnlyList<int> Split(int shooters) => ShootingCardLayout.SplitSizes(shooters);
        public double Demand(IReadOnlyList<ShootingCardStation> stations) => stations.Sum(s => ShootingCardLayout.StationUnits(ShootingCardLayout.Figures, s)) + stations.Count * 0.001;

        public ShootingCardFitReport Fit(IReadOnlyList<ShootingCardStation> stations, int shooters, ShootingCardRect card, ShootingCardOptions o)
        {
            var m = ShootingCardLayout.Measure(_id, stations, shooters, card, o.SumColumn);
            var (ok, warn) = Fig ? (4.0, 3.5) : (9.0, 7.0);
            return new ShootingCardFitReport(new[] { new ShootingCardDimension(Fig ? "Figurruta" : "Träffruta", m.BoxW, ok, warn) });
        }

        public void Render(StringBuilder sb, ShootingCardModel card, ShootingCardRect rect, ShootingCardOptions o) =>
            ShootingCardRenderer.RenderCard(sb, card, rect, _id, o);
    }

    /// <summary>
    /// Skyttblock (A): ett block per skytt, fyra på ett A4 liggande, som WinShooters patrullkort. Stationerna står i en rad med tre rader
    /// figurrutor per station (kolumner = figurer / 3, uppåt avrundat). Poängrutan sitter vid figuren (en delad ruta) eller på en egen rad
    /// under stationen. Summa (träff/figurer per station) är ett tillval på en egen rad. Papperet är låst (Stefan 2026-11-24).
    /// </summary>
    public sealed class ShootingBlockLayout : IShootingCardLayout
    {
        public string Id => ShootingCardLayout.Block;
        public string Name => "Fyra skyttar på ett blad";
        public string Description => "Skyttblock, som WinShooter: hela patrullen på ett A4 liggande, en rad per skytt med stationerna bredvid varandra.";
        public IReadOnlyList<ShootingCardFormat> Formats { get; } = new[] { new ShootingCardFormat("l1", "A4 liggande, fyra skyttar", ShootingCardLayout.Landscape, 1) };
        public int Capacity => ShootingCardGeometry.BlockShooters;
        public bool PoangByFigure => true;
        public bool UsesHatch => true;
        public string BarLabel(ShootingCardOptions o) => "Skyttblock";
        public IReadOnlyList<int> Split(int shooters) => ShootingCardLayouts.FillSizes(shooters, Capacity);
        public double Demand(IReadOnlyList<ShootingCardStation> stations) => stations.Sum(s => ShootingCardGeometry.Cols(s)) + stations.Count * 0.001;

        public ShootingCardFitReport Fit(IReadOnlyList<ShootingCardStation> stations, int shooters, ShootingCardRect card, ShootingCardOptions o) =>
            ShootingCardGeometry.BlockFit(ShootingCardGeometry.Block(stations, card, o.PoangRow, o.SumColumn));

        public string Css => ShootingCardHtml.CellCss + @"
.sk .sk-b{display:flex;border:.5mm solid #111;box-sizing:border-box;margin-top:2.4mm}
.sk .sk-b:first-of-type{margin-top:3mm}
.sk-nm{flex:none;padding:1.3mm 1.8mm;border-right:.3mm solid #111;display:flex;flex-direction:column;gap:.7mm;overflow:hidden;white-space:nowrap;box-sizing:border-box}
.sk-nm b{font-size:calc(9.5pt * var(--k));line-height:1.15;overflow:hidden;text-overflow:ellipsis;display:block}
.sk-nm small{font-size:7pt;color:#333;line-height:1.2;overflow:hidden;text-overflow:ellipsis;display:block}
.sk-nm .ix{font-size:7pt;color:#333;margin-right:1.2mm;font-weight:400}
.sk-nm .ln{display:block;border-bottom:.25mm solid #555;height:5.2mm;font-size:6.5pt;color:#555;font-style:normal;line-height:6.5mm}
.sk-st{flex:none;display:flex;flex-direction:column;border-left:.3mm solid #111;box-sizing:border-box}
.sk-nm + .sk-st{border-left:0}
.sk-sh{display:flex;justify-content:space-between;align-items:center;padding:0 1.2mm;background:#ececec;border-bottom:.2mm solid #111;flex:none;box-sizing:border-box}
.sk-sh b{font-size:10pt}.sk-sh span{font-size:6.5pt;color:#333;white-space:nowrap}
.sk-g{display:grid;grid-auto-flow:column;flex:none}
.sk-row{display:flex;flex:none}
.sk-row .c{flex:1}
";

        public void Render(StringBuilder sb, ShootingCardModel c, ShootingCardRect rect, ShootingCardOptions o)
        {
            var p = ShootingCardGeometry.Block(c.Stations, rect, o.PoangRow, o.SumColumn);
            sb.Append("<div class=\"card sk").Append(o.Color ? " col" : "").Append("\" style=\"left:").Append(ShootingCardHtml.Mm(rect.X)).Append(";top:").Append(ShootingCardHtml.Mm(rect.Y))
              .Append(";width:").Append(ShootingCardHtml.Mm(rect.W)).Append(";height:").Append(ShootingCardHtml.Mm(rect.H)).Append(";--k:1;--cw:").Append(ShootingCardHtml.Mm(p.ContentW)).Append("\">");
            sb.Append(ShootingCardHtml.Header(c, o, p.HeaderH, false, true));
            for (var i = 0; i < p.Blocks; i++)
            {
                var sh = i < c.Shooters.Count ? c.Shooters[i] : null;
                var named = sh != null && !o.BlankNames && !string.IsNullOrWhiteSpace(sh.Name);
                var ix = sh != null && sh.Position > 0 ? sh.Position : i + 1;
                sb.Append("<div class=\"sk-b\" style=\"height:").Append(ShootingCardHtml.Mm(p.BlockH)).Append("\">");
                sb.Append("<div class=\"sk-nm\" style=\"width:").Append(ShootingCardHtml.Mm(p.NameW)).Append("\">");
                if (named)
                    sb.Append("<b><span class=\"ix\">").Append(ix).Append("</span>").Append(ShootingCardHtml.E(sh!.Name)).Append("</b><small>")
                      .Append(ShootingCardHtml.E(string.Join(" · ", new[] { sh.Club, sh.ShootingClass }.Where(x => !string.IsNullOrWhiteSpace(x))))).Append("</small>");
                else
                    sb.Append("<b><span class=\"ix\">").Append(ix).Append("</span></b><i class=\"ln\">Namn</i><i class=\"ln\">Klubb · klass</i>");
                sb.Append("</div>");
                foreach (var s in p.Stations) RenderStation(sb, s, p);
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        static void RenderStation(StringBuilder sb, ShootingBlockStation s, ShootingBlockPlan p)
        {
            var st = s.Station;
            var slots = st.EffectivePoangSlots;
            sb.Append("<div class=\"sk-st\" style=\"width:").Append(ShootingCardHtml.Mm(s.Width + ShootingCardGeometry.StationDivider)).Append("\">");
            sb.Append("<div class=\"sk-sh\" style=\"height:").Append(ShootingCardHtml.Mm(p.StationHeaderH)).Append("\"><b>").Append(st.Number).Append("</b><span>").Append(st.Figures)
              .Append(" fig").Append(st.PoangmalCount > 0 ? " · " + st.PoangmalCount + " p" : "").Append("</span></div>");
            sb.Append("<div class=\"sk-g\" style=\"grid-template-columns:repeat(").Append(s.Cols).Append(",1fr);grid-template-rows:repeat(3,").Append(ShootingCardHtml.Mm(p.CellH))
              .Append(");height:").Append(ShootingCardHtml.Mm(3 * p.CellH)).Append("\">");
            for (var k = 0; k < 3 * s.Cols; k++)
            {
                var slot = k + 1;
                if (slot > st.Figures) { sb.Append(ShootingCardHtml.OffCell); continue; }
                var pi = slots.ToList().IndexOf(slot);
                sb.Append(pi >= 0 && !p.PoangRow ? ShootingCardHtml.SplitCell(slot, pi + 1) : ShootingCardHtml.HitCell(slot));
            }
            sb.Append("</div>");
            if (p.PoangRow)
            {
                sb.Append("<div class=\"sk-row\" style=\"height:").Append(ShootingCardHtml.Mm(p.PoangRowH)).Append("\">");
                if (st.PoangmalCount == 0) sb.Append(ShootingCardHtml.OffCell);
                else for (var q = 1; q <= st.PoangmalCount; q++) sb.Append(ShootingCardHtml.PoangBox(q));
                sb.Append("</div>");
            }
            if (p.SumRow) sb.Append("<div class=\"sk-row\" style=\"height:").Append(ShootingCardHtml.Mm(p.SumRowH)).Append("\">").Append(ShootingCardHtml.SumBox()).Append("</div>");
            sb.Append("</div>");
        }
    }

    /// <summary>
    /// Skyttkort (B): ett kort per skytt, A5 liggande, två per A4 stående (klipps isär). Stationerna är rader, en kolumn per figur. Poängrutan
    /// sitter vid figuren (delad ruta) eller i egna poängkolumner sist på raden. Summa (träff/figurer) är ett tillval i en sista kolumn.
    /// Papperet är låst (Stefan 2026-11-24).
    /// </summary>
    public sealed class ShootingCardSheetLayout : IShootingCardLayout
    {
        public string Id => ShootingCardLayout.Card;
        public string Name => "Ett kort per skytt";
        public string Description => "Skyttkort: ett eget kort för varje skytt, A5 liggande, två per A4 stående som klipps isär.";
        public IReadOnlyList<ShootingCardFormat> Formats { get; } = new[] { new ShootingCardFormat("p2", "A5 liggande, två per A4 stående", ShootingCardLayout.Portrait, 2) };
        public int Capacity => 1;
        public bool PoangByFigure => true;
        public bool UsesHatch => true;
        public string BarLabel(ShootingCardOptions o) => "Skyttkort";
        public IReadOnlyList<int> Split(int shooters) => shooters <= 0 ? new[] { 0 } : Enumerable.Repeat(1, shooters).ToList();
        public double Demand(IReadOnlyList<ShootingCardStation> stations) =>
            (stations.Count == 0 ? 0 : stations.Max(s => s.Figures)) * 100.0 + stations.Count;

        public ShootingCardFitReport Fit(IReadOnlyList<ShootingCardStation> stations, int shooters, ShootingCardRect card, ShootingCardOptions o) =>
            ShootingCardGeometry.CardFit(stations, ShootingCardGeometry.Card(stations, card, o.PoangRow, o.SumColumn));

        public string Css => ShootingCardHtml.CellCss + @"
.kc-who{display:grid;grid-template-columns:2.4fr 1.5fr .7fr 1fr;gap:3mm;margin-top:2.4mm;width:var(--cw)}
.kc-who div{border-bottom:.3mm solid #111;padding-bottom:.5mm;display:flex;flex-direction:column;justify-content:flex-end;min-width:0}
.kc-who small{font-size:6pt;letter-spacing:.06em;color:#444;text-transform:uppercase}
.kc-who b{font-size:calc(12pt * var(--k));line-height:1.1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.kc-t{display:grid;border:.5mm solid #111;margin-top:2.4mm;box-sizing:border-box;width:var(--cw)}
.kc-t .h{background:#ececec;font-size:6.5pt;color:#333;text-transform:uppercase;letter-spacing:.05em;border-right:.15mm solid #555;border-bottom:.2mm solid #111;padding:1mm 1.2mm;box-sizing:border-box}
.kc-t .sn{display:flex;align-items:center;gap:1.6mm;padding:0 1.6mm;background:#f4f4f4;border-right:.2mm solid #111;border-bottom:.15mm solid #555;font-size:13pt;font-weight:800;box-sizing:border-box}
.kc-t .sn small{font-size:6.5pt;font-weight:400;color:#333}
";

        public void Render(StringBuilder sb, ShootingCardModel c, ShootingCardRect rect, ShootingCardOptions o)
        {
            var p = ShootingCardGeometry.Card(c.Stations, rect, o.PoangRow, o.SumColumn);
            var sh = c.Shooters.Count > 0 ? c.Shooters[0] : null;
            var named = sh != null && !o.BlankNames && !string.IsNullOrWhiteSpace(sh.Name);
            sb.Append("<div class=\"card kc").Append(o.Color ? " col" : "").Append("\" style=\"left:").Append(ShootingCardHtml.Mm(rect.X)).Append(";top:").Append(ShootingCardHtml.Mm(rect.Y))
              .Append(";width:").Append(ShootingCardHtml.Mm(rect.W)).Append(";height:").Append(ShootingCardHtml.Mm(rect.H)).Append(";--k:1;--cw:").Append(ShootingCardHtml.Mm(p.ContentW)).Append("\">");
            sb.Append(ShootingCardHtml.Header(c, o, p.HeaderH, false, false));
            var pos = sh != null && sh.Position > 0 ? "plats " + sh.Position : "";
            sb.Append("<div class=\"kc-who\" style=\"height:").Append(ShootingCardHtml.Mm(p.WhoH)).Append("\">")
              .Append("<div><small>Skytt</small>").Append(named ? "<b>" + ShootingCardHtml.E(sh!.Name) + "</b>" : "").Append("</div>")
              .Append("<div><small>Klubb</small>").Append(named ? "<b>" + ShootingCardHtml.E(sh!.Club) + "</b>" : "").Append("</div>")
              .Append("<div><small>Klass</small>").Append(named ? "<b>" + ShootingCardHtml.E(sh!.ShootingClass) + "</b>" : "").Append("</div>")
              .Append("<div><small>Vapen").Append(pos.Length > 0 ? " · " + pos : "").Append("</small></div></div>");

            var cols = ShootingCardHtml.Mm(p.StationW) + " repeat(" + p.MaxFigures + "," + ShootingCardHtml.Mm(p.FigW) + ")"
                + (p.PoangColumns ? " repeat(" + p.MaxPoang + "," + ShootingCardHtml.Mm(p.PoangW) + ")" : "") + (p.SumColumn ? " " + ShootingCardHtml.Mm(p.SumW) : "");
            sb.Append("<div class=\"kc-t\" style=\"grid-template-columns:").Append(cols).Append(";grid-template-rows:").Append(ShootingCardHtml.Mm(p.TableHeaderH))
              .Append(";grid-auto-rows:").Append(ShootingCardHtml.Mm(p.RowH)).Append("\">");
            sb.Append("<div class=\"h\">Stn</div><div class=\"h\" style=\"grid-column:span ").Append(p.MaxFigures).Append("\">Träffar per figur")
              .Append(p.PoangColumns ? "" : " (P = poäng)").Append("</div>");
            if (p.PoangColumns) sb.Append("<div class=\"h\" style=\"grid-column:span ").Append(p.MaxPoang).Append("\">Poäng</div>");
            if (p.SumColumn) sb.Append("<div class=\"h\">Σ</div>");
            foreach (var s in c.Stations)
            {
                var slots = s.EffectivePoangSlots.ToList();
                sb.Append("<div class=\"sn\">").Append(s.Number).Append("<small>").Append(s.Figures).Append(" fig</small></div>");
                for (var k = 1; k <= p.MaxFigures; k++)
                {
                    if (k > s.Figures) { sb.Append(ShootingCardHtml.OffCell); continue; }
                    var pi = slots.IndexOf(k);
                    sb.Append(pi >= 0 && !p.PoangColumns ? ShootingCardHtml.SplitCell(k, pi + 1) : ShootingCardHtml.HitCell(k));
                }
                if (p.PoangColumns)
                    for (var q = 1; q <= p.MaxPoang; q++) sb.Append(q <= s.PoangmalCount ? ShootingCardHtml.PoangBox(q) : ShootingCardHtml.OffCell);
                if (p.SumColumn) sb.Append(ShootingCardHtml.SumBox());
            }
            sb.Append("</div></div>");
        }
    }
}
