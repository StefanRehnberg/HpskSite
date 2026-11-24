namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// En mätpunkt för "går det att skriva i rutan": minsta bredd/höjd en ruta av en viss sort får ha (mm).
    /// Ok = gott om plats, Warn = tight. Gränserna hänger på rutans innehåll, inte på layouten (Expertgruppen 2026-11-24).
    /// </summary>
    public record ShootingCardDimension(string Label, double Value, double Ok, double Warn)
    {
        /// <summary>Avrundat till en decimal, som det visas.</summary>
        public double Shown => Math.Round(Value, 1, MidpointRounding.AwayFromZero);
        public string Level => Shown >= Ok ? "ok" : Shown >= Warn ? "warn" : "bad";
        public double Ratio => Ok <= 0 ? 1 : Value / Ok;
    }

    /// <summary>Resultatet av måttkontrollen för ett kort: alla mätpunkter, den värsta avgör.</summary>
    public record ShootingCardFitReport(IReadOnlyList<ShootingCardDimension> Dimensions)
    {
        static int Rank(string level) => level == "bad" ? 2 : level == "warn" ? 1 : 0;

        /// <summary>Den mätpunkt som begränsar mest: sämst nivå, och bland lika nivåer den som ligger närmast under sin gräns.</summary>
        public ShootingCardDimension Limiting => Dimensions.OrderByDescending(d => Rank(d.Level)).ThenBy(d => d.Ratio).First();
        public string Level => Limiting.Level;
        public static string Words(string level) => level == "ok" ? "går att skriva i" : level == "warn" ? "tight" : "för trångt";

        /// <summary>Texten i utskriftssidans verktygsrad: "Poängruta 5,2 mm · tight".</summary>
        public string ChipText
        {
            get { var l = Limiting; return l.Label + " " + l.Shown.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")) + " mm · " + Words(l.Level); }
        }

        /// <summary>Den sämsta av två rapporter (för att visa det trängsta kortet i en utskrift).</summary>
        public static ShootingCardFitReport Worse(ShootingCardFitReport? a, ShootingCardFitReport b)
        {
            if (a == null) return b;
            var (la, lb) = (a.Limiting, b.Limiting);
            if (Rank(lb.Level) != Rank(la.Level)) return Rank(lb.Level) > Rank(la.Level) ? b : a;
            return lb.Ratio < la.Ratio ? b : a;
        }
    }

    /// <summary>Gränserna per rutsort (mm). Siffran för en ruta mäts efter vad som ska skrivas i den.</summary>
    public static class ShootingCardFitRules
    {
        /// <summary>Träff i en figurruta (en siffra): bredd 4 mm bra, 3,5 tight (som dagens figurruta).</summary>
        public static ShootingCardDimension HitWidth(double w) => new("Träffruta, bredd", w, 4.0, 3.5);
        /// <summary>Höjden för handskrift med kulspetspenna: 8 mm bra, 6,5 tight.</summary>
        public static ShootingCardDimension HitHeight(double h) => new("Träffruta, höjd", h, 8.0, 6.5);
        /// <summary>Poäng (två siffror, högst 50): 7 mm bra, 5,5 tight.</summary>
        public static ShootingCardDimension PoangWidth(double w) => new("Poängruta, bredd", w, 7.0, 5.5);
        public static ShootingCardDimension PoangHeight(double h) => new("Poängruta, höjd", h, 8.0, 6.5);
        /// <summary>Träff/figurer (till exempel 6/3): 9 mm bra, 7 tight (som dagens träffruta).</summary>
        public static ShootingCardDimension SumWidth(double w) => new("Summaruta, bredd", w, 9.0, 7.0);
        public static ShootingCardDimension SumHeight(double h) => new("Summaruta, höjd", h, 8.0, 6.5);
    }

    /// <summary>En station i ett skyttblock: antal kolumner (tre figurer per kolumn), vikt och bredd i mm.</summary>
    public record ShootingBlockStation(ShootingCardStation Station, int Cols, double Weight, double Width)
    {
        /// <summary>Bredden på en figurkolumn.</summary>
        public double CellW => Width / Cols;
    }

    /// <summary>
    /// Måtten för ett Skyttblock-kort (A4 liggande, fyra skyttar): ENDA stället som räknar ut hur blocken delar upp arket.
    /// Rendereringen och måttvarningen läser båda planen.
    /// </summary>
    public record ShootingBlockPlan(
        double ContentW, double ContentH, double HeaderH, double HeaderGap, double Gap, double NameW,
        double BlockH, double StationHeaderH, double CellH, double PoangRowH, double SumRowH,
        bool PoangRow, bool SumRow, int Blocks, IReadOnlyList<ShootingBlockStation> Stations);

    /// <summary>Måtten för ett Skyttkort (A5 liggande): stationerna som rader, en kolumn per figur.</summary>
    public record ShootingCardPlan(
        double ContentW, double ContentH, double HeaderH, double WhoH, double TableHeaderH, double StationW,
        int MaxFigures, int MaxPoang, double FigW, double PoangW, double SumW, double RowH, bool PoangColumns, bool SumColumn);

    public static partial class ShootingCardGeometry
    {
        /// <summary>Andelen av en figurruta som hamnar på träffen när poängrutan sitter vid figuren (resten är poängrutan).</summary>
        public const double HitShare = 0.58;
        public const int BlockShooters = 4;
        public const double BlockNameW = 38;
        public const double BlockHeaderH = 12;
        public const double BlockHeaderGap = 3;
        public const double BlockGap = 2.4;
        public const double BlockStationHeaderH = 6.5;
        public const double BlockExtraRowH = 8.5;
        public const double BlockBorder = 0.5;
        public const double StationDivider = 0.3;
        /// <summary>Lite luft längst ned så att ett avrundningsfel aldrig ger en extra sida.</summary>
        public const double BottomSafety = 0.6;

        public static int Cols(ShootingCardStation s) => Math.Max(1, (int)Math.Ceiling(s.Figures / 3.0));

        /// <summary>Stationens bredd i enheter. Med poängrutor på egen rad behövs plats för dem: högst två per kolumn.</summary>
        public static double Weight(ShootingCardStation s, bool poangRow) =>
            poangRow ? Math.Max(Cols(s), Math.Ceiling(s.PoangmalCount / 2.0)) : Cols(s);

        public static ShootingBlockPlan Block(IReadOnlyList<ShootingCardStation> stations, ShootingCardRect card, bool poangRow, bool sum)
        {
            var contentW = card.W - 2 * ShootingCardLayout.Margin;
            var contentH = card.H - 2 * ShootingCardLayout.Margin;
            var hasPoang = poangRow && stations.Any(s => s.PoangmalCount > 0);
            var weights = stations.Select(s => Weight(s, hasPoang)).ToList();
            var total = Math.Max(1.0, weights.Sum());
            var unit = (contentW - BlockNameW - 2 * BlockBorder - StationDivider * stations.Count) / total;
            var blockH = (contentH - BottomSafety - BlockHeaderH - BlockHeaderGap - BlockGap * (BlockShooters - 1)) / BlockShooters;
            var extras = (hasPoang ? BlockExtraRowH : 0) + (sum ? BlockExtraRowH : 0);
            var cellH = (blockH - 2 * BlockBorder - BlockStationHeaderH - extras) / 3.0;
            var list = stations.Select((s, i) => new ShootingBlockStation(s, Cols(s), weights[i], unit * weights[i])).ToList();
            return new ShootingBlockPlan(contentW, contentH, BlockHeaderH, BlockHeaderGap, BlockGap, BlockNameW, blockH, BlockStationHeaderH, cellH,
                hasPoang ? BlockExtraRowH : 0, sum ? BlockExtraRowH : 0, hasPoang, sum, BlockShooters, list);
        }

        public static ShootingCardFitReport BlockFit(ShootingBlockPlan p)
        {
            var dims = new List<ShootingCardDimension>();
            double minHit = double.MaxValue, minPoang = double.MaxValue, minSum = double.MaxValue;
            foreach (var s in p.Stations)
            {
                var hasP = s.Station.PoangmalCount > 0;
                minHit = Math.Min(minHit, hasP && !p.PoangRow ? s.CellW * HitShare : s.CellW);
                if (hasP) minPoang = Math.Min(minPoang, p.PoangRow ? s.Width / s.Station.PoangmalCount : s.CellW * (1 - HitShare));
                minSum = Math.Min(minSum, s.Width);
            }
            if (minHit < double.MaxValue) dims.Add(ShootingCardFitRules.HitWidth(minHit));
            dims.Add(ShootingCardFitRules.HitHeight(p.CellH));
            if (minPoang < double.MaxValue)
            {
                dims.Add(ShootingCardFitRules.PoangWidth(minPoang));
                if (p.PoangRow) dims.Add(ShootingCardFitRules.PoangHeight(p.PoangRowH));
            }
            if (p.SumRow && minSum < double.MaxValue) { dims.Add(ShootingCardFitRules.SumWidth(minSum)); dims.Add(ShootingCardFitRules.SumHeight(p.SumRowH)); }
            return new ShootingCardFitReport(dims);
        }

        public const double CardHeaderH = 11;
        public const double CardWhoH = 10;
        public const double CardTableHeaderH = 6;
        public const double CardStationW = 16;
        public const double CardPoangW = 15;
        public const double CardSumW = 14;
        public const double CardMaxRowH = 11.5;

        public static ShootingCardPlan Card(IReadOnlyList<ShootingCardStation> stations, ShootingCardRect card, bool poangRow, bool sum)
        {
            var contentW = card.W - 2 * ShootingCardLayout.Margin;
            var contentH = card.H - 2 * ShootingCardLayout.Margin;
            var maxF = Math.Max(1, stations.Count == 0 ? 1 : stations.Max(s => s.Figures));
            var maxP = poangRow ? stations.Count == 0 ? 0 : stations.Max(s => s.PoangmalCount) : 0;
            var sumW = sum ? CardSumW : 0;
            var figW = (contentW - CardStationW - maxP * CardPoangW - sumW - 0.5) / maxF;
            var area = contentH - BottomSafety - CardHeaderH - 2.4 - CardWhoH - 2.4 - CardTableHeaderH - 1;
            var rowH = Math.Min(CardMaxRowH, area / Math.Max(1, stations.Count));
            return new ShootingCardPlan(contentW, contentH, CardHeaderH, CardWhoH, CardTableHeaderH, CardStationW, maxF, maxP, figW, CardPoangW, sumW, rowH, maxP > 0, sum);
        }

        public static ShootingCardFitReport CardFit(IReadOnlyList<ShootingCardStation> stations, ShootingCardPlan p)
        {
            var dims = new List<ShootingCardDimension>();
            var anyP = stations.Any(s => s.PoangmalCount > 0);
            dims.Add(ShootingCardFitRules.HitWidth(anyP && !p.PoangColumns ? p.FigW * HitShare : p.FigW));
            dims.Add(ShootingCardFitRules.HitHeight(p.RowH));
            if (anyP)
            {
                dims.Add(ShootingCardFitRules.PoangWidth(p.PoangColumns ? p.PoangW : p.FigW * (1 - HitShare)));
                dims.Add(ShootingCardFitRules.PoangHeight(p.RowH));
            }
            if (p.SumColumn) dims.Add(ShootingCardFitRules.SumWidth(p.SumW));
            return new ShootingCardFitReport(dims);
        }
    }
}
