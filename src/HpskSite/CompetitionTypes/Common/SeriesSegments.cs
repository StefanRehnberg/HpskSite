namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Hur en grens 12 serier delas i AVSNITT — Nationell Helmatchs tre delmoment, Milsnabbs tre
    /// tider, Sportpistols två halvor.
    ///
    /// ⚠️ Kartan låg handskriven i resultatvyns JavaScript, och inmatningsskärmen hade ingen alls:
    /// serieremsan visade tolv likadana rutor för en tävling som resultatlistan mycket riktigt
    /// delade i Prec / Duell / Fält. Samma lukt som <see cref="PrecisionFamily"/> beskriver — en
    /// karta per yta är fri att glida isär, och den som glider tyst är värst.
    ///
    /// Och den är inte bara kosmetisk: för Nationell Helmatch är avsnitten **delmoment A, B och C**,
    /// och SHB särskiljer lika poäng på delmoment C och därefter B. Samma gränser som ritas i
    /// remsan avgör alltså placeringen — se
    /// <c>NationellHelmatch.Services.NationellHelmatchTieBreaker</c>.
    ///
    /// Avsnitten förutsätter det normala 12-seriesformatet. Har arrangören satt ett annat antal
    /// serier gäller ingen indelning, och anropande yta ska rita en vanlig serieremsa
    /// (<see cref="For"/> kräver därför serieantalet).
    /// </summary>
    public static class SeriesSegments
    {
        /// <summary>Ett avsnitt av serieordningen. Serienumren är 1-baserade och inklusive i båda ändar.</summary>
        public sealed class Segment
        {
            /// <summary>Första serienumret i avsnittet (1-baserat).</summary>
            public int FirstSeries { get; init; }

            /// <summary>Sista serienumret i avsnittet (1-baserat, inklusive).</summary>
            public int LastSeries { get; init; }

            /// <summary>Kort etikett som får plats i en tabellrubrik eller på en telefon: "Prec", "Duell", "Fält", "10s".</summary>
            public string ShortLabel { get; init; } = "";

            /// <summary>
            /// Delmomentets bokstav — "A", "B", "C" — för de grenar där SHB namnger avsnitten som
            /// delmoment. <c>null</c> för de grenar där avsnitten bara är tider eller halvor, och
            /// där en bokstav alltså vore påhittad.
            /// </summary>
            public string? Delmoment { get; init; }

            /// <summary>
            /// Vad märkessystemet gör med avsnittets serier. ⚠️ Egen egenskap, inte <see cref="Delmoment"/>:
            /// Sportpistol och Standardpistol har ingen delmomentsbokstav (SHB namnger dem inte, och
            /// <c>PrecisionResultOrdering</c> grenar på bokstaven), men deras avsnitt är ändå märkesserier.
            /// </summary>
            public MarkenSeriesKind MarkenKind { get; init; } = MarkenSeriesKind.None;
        }

        /// <summary>Vilken sorts märkesserie ett avsnitt är.</summary>
        public enum MarkenSeriesKind
        {
            /// <summary>Varken eller: ingen märkesserieform (fält/figur, Milsnabb, Standardpistols 20 s och 10 s).</summary>
            None,
            /// <summary>Precisionsserie — Guldfordringens precisionsdel och Elits precisionshalva.</summary>
            Precision,
            /// <summary>Duellserie (snabbpistoltavla) — bara Elits snabbhalva.</summary>
            Speed
        }

        private static readonly Dictionary<string, Segment[]> Map =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Nationell Helmatch: tre DELMOMENT, tre deltävlingar i samma tävling.
                // A = precision, B = duell (snabbskjutning), C = fält-/figurskjutning
                // (4 × 5 skott på 18, 16, 14 och 12 sekunder).
                ["NationellHelmatch"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 4,  ShortLabel = "Prec",  Delmoment = "A", MarkenKind = MarkenSeriesKind.Precision },
                    new Segment { FirstSeries = 5, LastSeries = 8,  ShortLabel = "Duell", Delmoment = "B", MarkenKind = MarkenSeriesKind.Speed },
                    new Segment { FirstSeries = 9, LastSeries = 12, ShortLabel = "Fält",  Delmoment = "C" },
                },

                // Milsnabb: samma 3×4-form, men avsnitten är TIDER och inte delmoment — därav
                // ingen bokstav. Den som sätter en här sätter också en särskiljningsregel som
                // SHB inte har gett för grenen.
                ["Milsnabb"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 4,  ShortLabel = "10s" },
                    new Segment { FirstSeries = 5, LastSeries = 8,  ShortLabel = "8s" },
                    new Segment { FirstSeries = 9, LastSeries = 12, ShortLabel = "6s" },
                },

                // Standardpistol: bara 150 s-strängen (serie 1–4) är märkesserier (Stefan 2026-11-27) —
                // precisionsserier mot precisionstavlan inom SHB 5.1.1.1:s fem minuter. 20 s och 10 s räknas inte.
                ["Standardpistol"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 4,  ShortLabel = "150s", MarkenKind = MarkenSeriesKind.Precision },
                    new Segment { FirstSeries = 5, LastSeries = 8,  ShortLabel = "20s" },
                    new Segment { FirstSeries = 9, LastSeries = 12, ShortLabel = "10s" },
                },

                // Sportpistol: precisionshalva + duellhalva, alltså 2 avsnitt om 6. Båda halvorna är
                // märkesserier (Stefan 2026-11-27): precisionshalvan som precisionsserier, duellhalvan som
                // duellserier (Speed) för de märken som kräver sådana (Elits snabbhalva).
                ["Sportpistol"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 6,  ShortLabel = "Prec",  MarkenKind = MarkenSeriesKind.Precision },
                    new Segment { FirstSeries = 7, LastSeries = 12, ShortLabel = "Duell", MarkenKind = MarkenSeriesKind.Speed },
                },
            };

        private static readonly Segment[] None = Array.Empty<Segment>();

        /// <summary>
        /// Avsnitten för en gren, eller en tom lista när grenen inte har någon indelning — eller
        /// när tävlingen inte skjuts på de 12 serier indelningen är skriven för.
        /// </summary>
        public static IReadOnlyList<Segment> For(string? typeId, int numberOfSeries)
        {
            if (numberOfSeries != 12) return None;
            return Map.TryGetValue((typeId ?? "").Trim(), out var segs) ? segs : None;
        }

        /// <summary>
        /// Avsnitten som JSON för en vy: <c>[{first,last,label,delmoment}]</c>, serienumren
        /// 1-baserade och inklusive i båda ändar. Ligger här och inte i vyerna så att de två
        /// ytor som ritar remsan respektive delsummekolumnerna inte kan forma samma fakta olika.
        /// </summary>
        public static string ToJson(string? typeId, int numberOfSeries) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(
                For(typeId, numberOfSeries)
                    .Select(s => new { first = s.FirstSeries, last = s.LastSeries, label = s.ShortLabel, delmoment = s.Delmoment }));

        /// <summary>
        /// Vad märkessystemet gör med serie <paramref name="seriesNumber"/> (1-baserat) i en tävling av typen
        /// <paramref name="typeId"/>. EN fråga för alla grenar — synken har ingen egen karta.
        ///
        /// <para>⚠️ Sportpistol och Standardpistol kräver exakt 12 serier: 12 är ett förval i guiden, inte en
        /// SPSF-regel, och en tävling med annat antal har ingen indelning att läsa serienumret mot
        /// (<see cref="For"/> ger tomt). Nationell Helmatch läses som förut utan att antalet prövas — det är en
        /// befintlig lucka som inte ändras här (beteendeändring kräver prodkoll).</para>
        /// </summary>
        public static MarkenSeriesKind KindOf(string? typeId, int seriesNumber, int numberOfSeries)
        {
            var key = (typeId ?? "").Trim();
            if (!Map.TryGetValue(key, out var segs)) return MarkenSeriesKind.None;
            bool countIndependent = string.Equals(key, "NationellHelmatch", StringComparison.OrdinalIgnoreCase);
            if (!countIndependent && numberOfSeries != 12) return MarkenSeriesKind.None;
            var seg = segs.FirstOrDefault(s => seriesNumber >= s.FirstSeries && seriesNumber <= s.LastSeries);
            return seg?.MarkenKind ?? MarkenSeriesKind.None;
        }

        /// <summary>
        /// Avsnittet med en given delmomentsbokstav, eller <c>null</c> om grenen inte har det.
        /// Används av särskiljningen, som frågar efter delmoment C och B vid namn.
        /// </summary>
        public static Segment? Delmoment(string? typeId, string letter) =>
            Map.TryGetValue((typeId ?? "").Trim(), out var segs)
                ? segs.FirstOrDefault(s => string.Equals(s.Delmoment, letter, StringComparison.OrdinalIgnoreCase))
                : null;
    }
}
