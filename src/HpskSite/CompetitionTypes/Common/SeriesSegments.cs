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
        }

        private static readonly Dictionary<string, Segment[]> Map =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Nationell Helmatch: tre DELMOMENT, tre deltävlingar i samma tävling.
                // A = precision, B = duell (snabbskjutning), C = fält-/figurskjutning
                // (4 × 5 skott på 18, 16, 14 och 12 sekunder).
                ["NationellHelmatch"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 4,  ShortLabel = "Prec",  Delmoment = "A" },
                    new Segment { FirstSeries = 5, LastSeries = 8,  ShortLabel = "Duell", Delmoment = "B" },
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

                ["Standardpistol"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 4,  ShortLabel = "150s" },
                    new Segment { FirstSeries = 5, LastSeries = 8,  ShortLabel = "20s" },
                    new Segment { FirstSeries = 9, LastSeries = 12, ShortLabel = "10s" },
                },

                // Sportpistol: precisionshalva + duellhalva, alltså 2 avsnitt om 6.
                ["Sportpistol"] = new[]
                {
                    new Segment { FirstSeries = 1, LastSeries = 6,  ShortLabel = "Prec" },
                    new Segment { FirstSeries = 7, LastSeries = 12, ShortLabel = "Duell" },
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
        /// Avsnittet med en given delmomentsbokstav, eller <c>null</c> om grenen inte har det.
        /// Används av särskiljningen, som frågar efter delmoment C och B vid namn.
        /// </summary>
        public static Segment? Delmoment(string? typeId, string letter) =>
            Map.TryGetValue((typeId ?? "").Trim(), out var segs)
                ? segs.FirstOrDefault(s => string.Equals(s.Delmoment, letter, StringComparison.OrdinalIgnoreCase))
                : null;
    }
}
