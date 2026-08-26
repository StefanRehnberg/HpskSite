using System.Collections.Immutable;

namespace HpskSite.Models
{
    /// <summary>
    /// Vilka pistolskyttekretsar som GRÄNSAR till varandra, så en tävling i grannkretsen kan visas
    /// som "nära dig" på Tävlingar-sidan.
    ///
    /// ⚠️ Det här är HANDSKRIVEN geografi, inte data från något register. Kretsarna följer i stort
    /// de gamla länen — Skåne är delat i Malmöhus och Kristianstad, Kalmar län i Norra och Södra,
    /// Västra Götaland i Göteborg-Bohuslän, Älvsborg, Skaraborg och Västgöta-Dal. Ett par som ser
    /// fel ut är ett par som ska rättas: ändra i <see cref="Borders"/> och kör
    /// RegionAdjacencyTests, som kontrollerar formen men inte kan kontrollera kartan.
    ///
    /// **Kanterna deklareras EN gång och speglas i konstruktorn.** Att skriva båda riktningarna för
    /// hand är dubbelt så mycket data och gör det möjligt att ha en gräns som bara går ena vägen —
    /// vilket i den här användningen ger det märkliga utfallet att A är nära B men inte B nära A.
    /// </summary>
    public static class RegionAdjacency
    {
        /// <summary>
        /// Grannpar, en rad per gräns. Ordningen inom paret betyder ingenting.
        /// Ungefär söder → norr, så listan går att läsa mot en karta.
        /// </summary>
        private static readonly (string A, string B)[] Borders = new[]
        {
            // ── Skåne, Blekinge, Halland, Småland ───────────────────────────────────────────────
            ("Malmohus", "Kristianstad"),
            ("Malmohus", "Halland"),          // nordvästra Skåne möter Halland vid Hallandsåsen
            ("Kristianstad", "Blekinge"),
            ("Kristianstad", "Kronoberg"),
            ("Kristianstad", "Halland"),
            ("Blekinge", "Kronoberg"),
            ("Blekinge", "KalmarSodra"),
            ("Halland", "Kronoberg"),
            ("Halland", "Jonkoping"),
            ("Halland", "Alvsborg"),
            ("Halland", "Goteborg"),
            ("Kronoberg", "Jonkoping"),
            ("Kronoberg", "KalmarSodra"),
            ("Kronoberg", "KalmarNorra"),
            ("KalmarSodra", "KalmarNorra"),
            ("KalmarNorra", "Jonkoping"),
            ("KalmarNorra", "Ostergotland"),

            // ── Gotland: enbart havsgränser. Tas med för att alternativet är att ön aldrig har
            //    någon granne alls, vilket gör "nära dig" meningslöst just där. Färjelägena styr:
            //    Oskarshamn (Kalmar Norra) och Nynäshamn (Stockholm).
            ("Gotland", "KalmarNorra"),
            ("Gotland", "KalmarSodra"),
            ("Gotland", "Stockholm"),
            ("Gotland", "Ostergotland"),

            // ── Västergötland, Bohuslän, Dalsland, Värmland ─────────────────────────────────────
            ("Goteborg", "Alvsborg"),
            ("Goteborg", "VastgotaDal"),
            ("Alvsborg", "VastgotaDal"),
            ("Alvsborg", "Skaraborg"),
            ("Alvsborg", "Jonkoping"),
            ("Alvsborg", "Varmland"),
            ("VastgotaDal", "Varmland"),
            ("Skaraborg", "Jonkoping"),
            ("Skaraborg", "Ostergotland"),
            ("Skaraborg", "Orebro"),
            ("Skaraborg", "Varmland"),

            // ── Östergötland och Mälardalen ─────────────────────────────────────────────────────
            ("Ostergotland", "Jonkoping"),
            ("Ostergotland", "Sodermanland"),
            ("Ostergotland", "Orebro"),
            ("Sodermanland", "Orebro"),
            ("Sodermanland", "Vastmanland"),
            ("Sodermanland", "Stockholm"),
            ("Stockholm", "Uppsala"),
            ("Stockholm", "Vastmanland"),
            ("Uppsala", "Vastmanland"),
            ("Uppsala", "Dalarna"),
            ("Uppsala", "Gavleborg"),
            ("Vastmanland", "Orebro"),
            ("Vastmanland", "Dalarna"),
            ("Orebro", "Varmland"),
            ("Orebro", "Dalarna"),
            ("Varmland", "Dalarna"),

            // ── Norrland ────────────────────────────────────────────────────────────────────────
            ("Dalarna", "Gavleborg"),
            ("Dalarna", "Jamtland"),
            ("Gavleborg", "Jamtland"),
            ("Gavleborg", "Vasternorrland"),
            ("Jamtland", "Vasternorrland"),
            ("Jamtland", "Vasterbotten"),
            ("Vasternorrland", "Vasterbotten"),
            ("Vasterbotten", "Norrbotten"),

            // Ankeland är en påhittad krets för demo- och testdata (jfr demoklubben Ankeborg) och
            // har medvetet inga grannar — den ska inte dra in riktiga kretsars tävlingar.
        };

        private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> _map = Build();

        private static ImmutableDictionary<string, ImmutableHashSet<string>> Build()
        {
            var acc = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void Link(string from, string to)
            {
                if (!acc.TryGetValue(from, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    acc[from] = set;
                }
                set.Add(to);
            }

            foreach (var (a, b) in Borders)
            {
                // Speglingen är hela poängen: en gräns är ömsesidig.
                Link(a, b);
                Link(b, a);
            }

            return acc.ToImmutableDictionary(
                kv => kv.Key,
                kv => kv.Value.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Kretsarna som gränsar till <paramref name="regionCode"/>. Tom mängd för en okänd kod,
        /// för en krets utan grannar, och för null/blankt — anropssidan ska aldrig behöva null-kolla.
        /// Koden är enum-NAMNET (<c>"Halland"</c>), samma form som klubbens
        /// <c>regionalFederation</c>. Jämförs skiftlägesokänsligt, eftersom en del kodvägar
        /// normaliserar till gemener (se NormalizeRegionCode).
        /// </summary>
        public static ImmutableHashSet<string> NeighboursOf(string? regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode)) return ImmutableHashSet<string>.Empty;
            return _map.TryGetValue(regionCode.Trim(), out var set)
                ? set
                : ImmutableHashSet<string>.Empty;
        }

        /// <summary>
        /// Alla kretsar som gränsar till NÅGON av <paramref name="regionCodes"/>, utan de egna.
        /// Det är exakt vad "Nära dig"-sektionen behöver: en medlem kan ha klubbar i flera kretsar,
        /// och en granne som samtidigt är min egen krets hör i "Din krets", inte i "Nära dig".
        /// </summary>
        public static ImmutableHashSet<string> NeighboursOfAny(IEnumerable<string> regionCodes)
        {
            var own = regionCodes
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

            var result = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in own)
            {
                foreach (var n in NeighboursOf(code))
                {
                    if (!own.Contains(n)) result.Add(n);
                }
            }
            return result.ToImmutable();
        }

        /// <summary>Kanterna, för tester och för en eventuell felsökningsyta. En rad per gräns.</summary>
        public static IReadOnlyList<(string A, string B)> DeclaredBorders => Borders;

        /// <summary>Kretsar som har minst en granne — alltså allt utom öar och testkretsar.</summary>
        public static IReadOnlyCollection<string> RegionsWithNeighbours => _map.Keys.ToList();
    }
}
