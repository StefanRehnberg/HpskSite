namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>En station på skjutkortet: nummer, antal figurer (scoring slots) och antal poängmål.</summary>
    public record ShootingCardStation(int Number, string? Name, int Figures, int PoangmalCount);

    /// <summary>Var ett kort ligger på arket, i mm.</summary>
    public record ShootingCardRect(double X, double Y, double W, double H);

    /// <summary>Måtten för ett kort: allt som ritningen och varningen behöver.</summary>
    public record ShootingCardMetrics(
        double ContentW, double ContentH, double NameW, double HeaderH, double TableHeaderH,
        double Unit, double BoxW, double RowH, double NotesH, double FontScale, bool Narrow);

    /// <summary>"ok" = går att skriva i, "warn" = tight, "bad" = för trångt.</summary>
    public record ShootingCardFit(string Level, double BoxW);

    /// <summary>
    /// ⚠️ ENDA stället som räknar ut hur ett skjutkort delas upp i mm. Skjutkortets utskrift
    /// (<see cref="ShootingCardRenderer"/>) och dialogens måttvarning (<c>/faltskytte/skjutkort/{id}/matt</c>) läser båda härifrån,
    /// så varningen kan aldrig säga något annat än det som skrivs ut.
    ///
    /// Två layouter: <c>fig</c> (en ruta per figur + en ruta för träff/figurer, plus en poängruta per poängmål) och
    /// <c>tot</c> (bara rutan för träff/figurer). Pappersformat: A4 stående eller liggande, ett eller två kort per sida.
    /// Två kort per sida på A4 stående = två liggande A5 (ovanpå varandra); på A4 liggande = två stående A5 (bredvid varandra).
    /// Kortets innehåll är kortet minus 7 mm marginal. Rutornas bredd är den hårda gränsen: en siffra behöver ungefär 4 mm.
    /// </summary>
    public static class ShootingCardLayout
    {
        public const string Figures = "fig";
        public const string Total = "tot";
        public const string Portrait = "portrait";
        public const string Landscape = "landscape";

        public const double Margin = 7;
        public const double GroupBorder = 0.5;
        /// <summary>Rutan för träff/figurer mäts i figurrutor.</summary>
        public const double TotalBoxUnits = 1.6;
        /// <summary>En poängruta (poängmål) i figurlayouten respektive i layouten med bara träff/figurer.</summary>
        public const double PoangBoxUnitsFig = 1.6;
        public const double PoangBoxUnitsTot = 0.9;
        /// <summary>Summakolumnen (valfri), i mm.</summary>
        public const double SumColumnW = 13;
        public const int MaxShootersPerCard = 12;

        public static bool IsLayout(string? v) => v is Figures or Total;
        public static string NormalizeLayout(string? v) => v == Total ? Total : Figures;
        public static string NormalizePaper(string? v) => v == Portrait ? Portrait : Landscape;
        public static int NormalizePerSheet(int v) => v == 2 ? 2 : 1;

        public static (double W, double H) SheetSize(string paper) =>
            NormalizePaper(paper) == Portrait ? (210.0, 297.0) : (297.0, 210.0);

        /// <summary>Kortens platser på arket. Två kort per sida: ovanpå varandra på stående A4, bredvid varandra på liggande A4.</summary>
        public static IReadOnlyList<ShootingCardRect> Cards(string paper, int perSheet)
        {
            var (w, h) = SheetSize(paper);
            if (NormalizePerSheet(perSheet) == 1) return new[] { new ShootingCardRect(0, 0, w, h) };
            return NormalizePaper(paper) == Portrait
                ? new[] { new ShootingCardRect(0, 0, w, h / 2), new ShootingCardRect(0, h / 2, w, h / 2) }
                : new[] { new ShootingCardRect(0, 0, w / 2, h), new ShootingCardRect(w / 2, 0, w / 2, h) };
        }

        /// <summary>Hur många rutor-enheter en station tar (figurrutor, rutan för träff/figurer och poängrutor).</summary>
        public static double StationUnits(string layout, ShootingCardStation s) =>
            (NormalizeLayout(layout) == Figures ? s.Figures : 0) + TotalBoxUnits
            + s.PoangmalCount * (NormalizeLayout(layout) == Figures ? PoangBoxUnitsFig : PoangBoxUnitsTot);

        public static ShootingCardMetrics Measure(string layout, IReadOnlyList<ShootingCardStation> stations, int shooterCount, ShootingCardRect card, bool sumColumn)
        {
            layout = NormalizeLayout(layout);
            var contentW = card.W - 2 * Margin;
            var contentH = card.H - 2 * Margin;
            var narrow = contentW < 150;
            var wide = contentW > 250;
            var nameW = narrow ? 30.0 : wide ? 48.0 : 38.0;
            var headerH = narrow ? 23.0 : 14.0;
            const double tableHeaderH = 11;
            var units = stations.Sum(s => StationUnits(layout, s));
            if (units <= 0) units = 1;
            var sumW = sumColumn ? SumColumnW + GroupBorder : 0;
            var avail = contentW - nameW - sumW - 1 - GroupBorder * stations.Count;
            var unit = avail / units;
            var area = contentH - headerH - tableHeaderH - 2 - 1.6;
            var rows = Math.Max(1, shooterCount);
            var rowH = Math.Min(area / rows, contentH >= 190 ? 15.0 : 13.5);
            return new ShootingCardMetrics(contentW, contentH, nameW, headerH, tableHeaderH, unit,
                layout == Figures ? unit : unit * TotalBoxUnits, rowH, area - rowH * rows,
                Math.Max(0.85, Math.Min(1.3, contentW / 196.0)), narrow);
        }

        /// <summary>Gränserna: figurruta 4 mm (tight från 3,5), träffruta 9 mm (tight från 7). Avrundat till en decimal, som det visas.</summary>
        public static ShootingCardFit Fit(string layout, double boxW)
        {
            var r = Math.Round(boxW, 1, MidpointRounding.AwayFromZero);
            var (ok, warn) = NormalizeLayout(layout) == Figures ? (4.0, 3.5) : (9.0, 7.0);
            return new ShootingCardFit(r >= ok ? "ok" : r >= warn ? "warn" : "bad", r);
        }

        /// <summary>Delar en patrulls skyttar på så få kort som möjligt, jämnt fördelade (13 skyttar blir 7+6, inte 12+1).</summary>
        public static IReadOnlyList<int> SplitSizes(int shooters)
        {
            if (shooters <= 0) return new[] { 0 };
            var cards = (int)Math.Ceiling(shooters / (double)MaxShootersPerCard);
            var baseSize = shooters / cards;
            var extra = shooters % cards;
            return Enumerable.Range(0, cards).Select(i => baseSize + (i < extra ? 1 : 0)).ToList();
        }

        /// <summary>Kortens ordning på arken: [1,2], [3,4] … Ett udda antal lämnar sista platsen tom (null).</summary>
        public static IReadOnlyList<IReadOnlyList<T?>> Sheets<T>(IReadOnlyList<T> cards, int perSheet) where T : class
        {
            var n = NormalizePerSheet(perSheet);
            var sheets = new List<IReadOnlyList<T?>>();
            for (var i = 0; i < cards.Count; i += n)
                sheets.Add(Enumerable.Range(0, n).Select(k => i + k < cards.Count ? cards[i + k] : null).ToList());
            return sheets;
        }

        /// <summary>De fyra pappersformaten dialogen visar, i den ordning de listas.</summary>
        public static readonly IReadOnlyList<(string Id, string Label, string Paper, int PerSheet)> Formats = new[]
        {
            ("p1", "A4 stående, 1 kort", Portrait, 1),
            ("l1", "A4 liggande, 1 kort", Landscape, 1),
            ("p2", "A4 stående, 2 kort", Portrait, 2),
            ("l2", "A4 liggande, 2 kort", Landscape, 2),
        };

        /// <summary>Tiden som visas på kortet, utan datum.</summary>
        public static string TimeText(DateTime? t) => t.HasValue ? t.Value.ToString("HH:mm") : "";
    }
}
