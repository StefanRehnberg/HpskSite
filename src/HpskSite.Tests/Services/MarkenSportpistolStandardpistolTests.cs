using HpskSite.CompetitionTypes.Common;
using HpskSite.Models;
using HpskSite.Services;
using Kind = HpskSite.CompetitionTypes.Common.SeriesSegments.MarkenSeriesKind;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Sportpistol serie 1–6 och Standardpistol serie 1–4 som precisionsserier, Sportpistol serie 7–12 som
    /// duellserier (Stefan 2026-11-27). Klassificeringen är EN karta, <see cref="SeriesSegments"/>.
    ///
    /// <para>⚠️ Gränsfallen prövas på båda sidor (6/7, 4/5) och mot serieantal ≠ 12 — ett test som bara
    /// frågar efter serie 3 passerar även om kartan sträckte sig över hela tävlingen. Spärrtesterna längst ned
    /// fäller bygget när en ny gren läggs till i resultatregistret utan att märkessynken eller GDPR-listan
    /// tagit ställning.</para>
    /// </summary>
    public class MarkenSportpistolStandardpistolTests
    {
        private const string Sp = "SportpistolResultEntry";
        private const string Stp = "StandardpistolResultEntry";

        [Theory]
        [InlineData(1, Kind.Precision)]
        [InlineData(3, Kind.Precision)]
        [InlineData(6, Kind.Precision)]
        [InlineData(7, Kind.Speed)]
        [InlineData(12, Kind.Speed)]
        [InlineData(0, Kind.None)]
        [InlineData(13, Kind.None)]
        public void Sportpistol_PrecisionshalvanOchDuellhalvan(int series, Kind expected)
            => Assert.Equal(expected, MarkenCompetitionSeriesSync.KindOf(Sp, series, 12));

        [Theory]
        [InlineData(1, Kind.Precision)]
        [InlineData(4, Kind.Precision)]
        [InlineData(5, Kind.None)]    // 20 s räknas inte
        [InlineData(8, Kind.None)]
        [InlineData(9, Kind.None)]    // 10 s räknas inte
        [InlineData(12, Kind.None)]
        [InlineData(0, Kind.None)]
        [InlineData(13, Kind.None)]
        public void Standardpistol_BaraDe150sSerierna(int series, Kind expected)
            => Assert.Equal(expected, MarkenCompetitionSeriesSync.KindOf(Stp, series, 12));

        [Theory]
        [InlineData(Sp)]
        [InlineData(Stp)]
        public void AnnatSerieantalAn12_GerIngenMarkesserie(string table)
        {
            foreach (var count in new[] { 0, 6, 10, 11, 13 })
                for (int s = 1; s <= 12; s++)
                    Assert.Equal(Kind.None, MarkenCompetitionSeriesSync.KindOf(table, s, count));
        }

        [Fact]
        public void PrecisionOchDuell_GerSvaretPaTabellen_OavsettSerienummer()
        {
            Assert.Equal(Kind.Precision, MarkenCompetitionSeriesSync.KindOf("PrecisionResultEntry", 9, 7));
            Assert.Equal(Kind.Speed, MarkenCompetitionSeriesSync.KindOf("DuellResultEntry", 1, 12));
        }

        [Fact]
        public void Helmatch_OforandratBeteende_AochBMenInteC_OchAntaletPrövasInte()
        {
            const string t = "NationellHelmatchResultEntry";
            for (int s = 1; s <= 4; s++) Assert.Equal(Kind.Precision, MarkenCompetitionSeriesSync.KindOf(t, s, 12));
            for (int s = 5; s <= 8; s++) Assert.Equal(Kind.Speed, MarkenCompetitionSeriesSync.KindOf(t, s, 12));
            for (int s = 9; s <= 12; s++) Assert.Equal(Kind.None, MarkenCompetitionSeriesSync.KindOf(t, s, 12));
            // Befintlig lucka som medvetet INTE ändras här: helmatch läses utan att serieantalet prövas.
            Assert.Equal(Kind.Precision, MarkenCompetitionSeriesSync.KindOf(t, 1, 10));
        }

        [Fact]
        public void OkandTabell_OchGrenarUtanMarkesserier_GerNone()
        {
            Assert.Equal(Kind.None, MarkenCompetitionSeriesSync.KindOf("MilsnabbResultEntry", 1, 12));
            Assert.Equal(Kind.None, MarkenCompetitionSeriesSync.KindOf("SpringskytteResultEntry", 1, 12));
            Assert.Equal(Kind.None, MarkenCompetitionSeriesSync.KindOf("", 1, 12));
        }

        [Fact]
        public void Kartan_DelmomentsbokstavSkaVaraNullForSportpistolOchStandardpistol()
        {
            // PrecisionResultOrdering grenar på bokstaven; en bokstav här skulle införa en särskiljningsregel
            // som förbundet inte har gett. Märkesserien har därför en EGEN egenskap.
            Assert.All(SeriesSegments.For("Sportpistol", 12), s => Assert.Null(s.Delmoment));
            Assert.All(SeriesSegments.For("Standardpistol", 12), s => Assert.Null(s.Delmoment));
            Assert.Contains(SeriesSegments.For("Sportpistol", 12), s => s.MarkenKind == Kind.Precision);
            Assert.Contains(SeriesSegments.For("Standardpistol", 12), s => s.MarkenKind == Kind.Precision);
        }

        [Fact]
        public void Etiketten_SagerGrenHalvaOchSerienummer_ForDeNyaGrenarna()
        {
            Assert.Equal("KM · Sportpistol, precisionshalvan, serie 3",
                MarkenCompetitionSeriesSync.LabelFor("KM", Sp, 3, Kind.Precision));
            Assert.Equal("KM · Sportpistol, duellhalvan, serie 8",
                MarkenCompetitionSeriesSync.LabelFor("KM", Sp, 8, Kind.Speed));
            Assert.Equal("KM · Standardpistol, 150 s, serie 2",
                MarkenCompetitionSeriesSync.LabelFor("KM", Stp, 2, Kind.Precision));
            // Övriga källor oförändrade — befintliga rader får inte byta etikett (annars en uppdatering av alla).
            Assert.Equal("KM", MarkenCompetitionSeriesSync.LabelFor("KM", "PrecisionResultEntry", 3, Kind.Precision));
        }

        [Fact]
        public void SammaSkott_KraverSammaOrdningOchMinstEttSkott()
        {
            Assert.True(MarkenCompetitionSeriesSync.SameShots("[\"10\",\"9\",\"X\",\"8\",\"9\"]", "[\"10\",\"9\",\"x\",\"8\",\"9\"]"));
            Assert.False(MarkenCompetitionSeriesSync.SameShots("[\"10\",\"9\",\"X\",\"8\",\"9\"]", "[\"9\",\"10\",\"X\",\"8\",\"9\"]"));
            Assert.False(MarkenCompetitionSeriesSync.SameShots("[]", "[]"));
            Assert.False(MarkenCompetitionSeriesSync.SameShots(null, "[\"10\"]"));
            Assert.False(MarkenCompetitionSeriesSync.SameShots("inte json", "inte json"));
        }

        // ── Spärrtester: nästa gren ska inte kunna glömmas tyst ──────────────────────────────────

        /// <summary>Tabeller som märkessynken MEDVETET inte läser, med skälet.</summary>
        private static readonly Dictionary<string, string> NotMarkenSources = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MilsnabbResultEntry"] = "Milsnabbmärket är ett tävlingsmärke på totalsumma (MarkenCompetitionService), inte serier",
            ["MagnumPrecisionResultEntry"] = "inget märkesprov för magnumprecision",
            ["SpringskytteResultEntry"] = "springskyttemärket är uppskjutet (marken-system)",
            ["FaltskytteResultEntry"] = "fält har eget märke (fältstandardmedalj), inga precisionsserier",
        };

        [Fact]
        public void VarjeResultattabell_ArAntingenMarkessyntesKalla_EllerUttryckligtUtesluten()
        {
            var sources = MarkenCompetitionSeriesSync.SourceTableNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unaccounted = CompetitionResultTables.AllTables
                .Where(t => !sources.Contains(t) && !NotMarkenSources.ContainsKey(t))
                .ToList();
            Assert.True(unaccounted.Count == 0,
                "En resultattabell saknar ställningstagande i märkessynken (SourceTables) och i undantagslistan: "
                + string.Join(", ", unaccounted));

            // Undantagen ska fortfarande vara riktiga tabeller — annars ruttnar listan.
            var all = CompetitionResultTables.AllTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(NotMarkenSources.Keys, k => Assert.Contains(k, all));
            // Och en källa får inte också stå som undantag.
            Assert.DoesNotContain(NotMarkenSources.Keys, k => sources.Contains(k));
        }

        /// <summary>Tabeller som GDPR-rensningen/sammanslagningen medvetet inte täcker ännu.</summary>
        private static readonly Dictionary<string, string> NotPurged = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FaltskytteResultEntry"] = "saknas i SubjectTables sedan före 2026-11-27 — egen fråga till Stefan, ska läggas dit",
        };

        [Fact]
        public void VarjeResultattabell_FinnsIGdprListan_EllerUttryckligtUtesluten()
        {
            var purged = MemberDataPurgeService.SubjectTables.Select(t => t.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = CompetitionResultTables.AllTables
                .Where(t => !purged.Contains(t) && !NotPurged.ContainsKey(t))
                .ToList();
            Assert.True(missing.Count == 0,
                "Resultattabell som saknas i MemberDataPurgeService.SubjectTables (GDPR-rensning och sammanslagning): "
                + string.Join(", ", missing));
            Assert.DoesNotContain(NotPurged.Keys, k => purged.Contains(k));
        }
    }
}
