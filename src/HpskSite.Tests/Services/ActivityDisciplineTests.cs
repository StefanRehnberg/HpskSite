using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Grennormaliseringen och grenfiltret.
    ///
    /// <para><b>Testerna är byggda på VAD SOM FINNS I PROD</b>, mätt 2026-09-02, inte på vad
    /// konstanterna påstår. Det avgörande fallet är <c>"Magnum Fält"</c>: två tävlingar i prod bär
    /// visningsNAMNET i stället för id:t <c>MagnumFalt</c>, och en literal jämförelse tappar dem ur
    /// varje grenfilter — tyst, vilket läser som "ingen aktivitet i grenen".</para>
    /// </summary>
    public class ActivityDisciplineTests
    {
        /// <summary>Distinkta värden i <c>TrainingScores.Discipline</c> i prod 2026-09-02.</summary>
        private static readonly string[] ProdTrainingValues =
            { "Precision", "Milsnabb", "Faltskytte", "Duell", "MagnumPrecision", "NationellHelmatch" };

        /// <summary>Distinkta värden i <c>competition.competitionType</c> i prod 2026-09-02.</summary>
        private static readonly string[] ProdCompetitionValues =
            { "Precision", "Faltskytte", "Milsnabb", "Springskytte", "Duell",
              "NationellHelmatch", "Magnum Fält", "Sportpistol" };

        // ── Normaliseringen mot verklig data ────────────────────────────────────────────────────

        [Fact]
        public void Every_prod_training_value_resolves()
        {
            foreach (var raw in ProdTrainingValues)
            {
                var canonical = ActivityDiscipline.Canonical(raw);
                Assert.False(string.IsNullOrEmpty(canonical), $"'{raw}' resolverade inte till någon gren.");
                Assert.Contains(canonical, ActivityDiscipline.All);
            }
        }

        [Fact]
        public void Every_prod_competition_value_resolves()
        {
            foreach (var raw in ProdCompetitionValues)
            {
                var canonical = ActivityDiscipline.Canonical(raw);
                Assert.False(string.IsNullOrEmpty(canonical), $"'{raw}' resolverade inte till någon gren.");
                Assert.Contains(canonical, ActivityDiscipline.All);
            }
        }

        [Fact]
        public void The_display_name_in_prod_resolves_to_the_id()
        {
            // ⚠️ DET HÄR ÄR FYNDET. Två tävlingar i prod bär "Magnum Fält" — visningsnamnet, med
            // mellanslag och ä — där alla andra bär id:t. Utan normaliseringen faller de ur filtret.
            Assert.Equal("MagnumFalt", ActivityDiscipline.Canonical("Magnum Fält"));

            // Och katalogens EGET namn för samma gren är en tredje sträng ("Magnum Fältskytte"),
            // så exakt-namn-matchningen räcker inte — det är den normaliserade jämförelsen som
            // löser det.
            Assert.Equal("MagnumFalt", ActivityDiscipline.Canonical("Magnum Fältskytte"));
            Assert.Equal("MagnumFalt", ActivityDiscipline.Canonical("MagnumFalt"));
            Assert.Equal("MagnumFalt", ActivityDiscipline.Canonical("magnumfalt"));
        }

        [Theory]
        [InlineData("Fältskytte", "Faltskytte")]   // visningsnamn med ä
        [InlineData("faltskytte", "Faltskytte")]   // gemener
        [InlineData(" Precision ", "Precision")]   // blanksteg runtom
        [InlineData("Nationell Helmatch", "NationellHelmatch")]
        [InlineData("Magnum Precision", "MagnumPrecision")]
        public void Diacritics_case_and_spacing_are_normalised(string raw, string expected)
        {
            Assert.Equal(expected, ActivityDiscipline.Canonical(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Bågskytte")]
        [InlineData("Something else")]
        public void An_unknown_value_resolves_to_empty_not_to_a_guess(string? raw)
        {
            Assert.Equal("", ActivityDiscipline.Canonical(raw));
        }

        // ── Den viktigaste skillnaden mot GetFuzzy ──────────────────────────────────────────────

        [Fact]
        public void A_partial_value_is_refused_not_prefix_matched()
        {
            // ⚠️ HELA SKÄLET ATT VI INTE ANVÄNDER CompetitionTypes.GetFuzzy.
            // GetFuzzy faller tillbaka på StartsWith, och "Magnum" träffar då MagnumPrecision — den
            // kommer först i katalogen. Ett trunkerat eller halvskrivet värde skulle alltså tyst
            // attribueras till EN ANNAN GREN, på ett filter vars hela syfte är att belägga aktivitet
            // inför en licensansökan.
            Assert.Equal("MagnumPrecision", HpskSite.Models.CompetitionTypes.GetFuzzy("Magnum")?.Id);
            Assert.Equal("", ActivityDiscipline.Canonical("Magnum"));

            // Samma sak för andra prefix som råkar peka in i katalogen.
            Assert.Equal("", ActivityDiscipline.Canonical("Prec"));
            Assert.Equal("", ActivityDiscipline.Canonical("Fal"));
        }

        // ── Etikett och sortering ───────────────────────────────────────────────────────────────

        [Fact]
        public void Label_renders_the_display_name()
        {
            Assert.Equal("Fältskytte", ActivityDiscipline.Label("Faltskytte"));
            Assert.Equal("Magnum Fältskytte", ActivityDiscipline.Label("MagnumFalt"));
            Assert.Equal("Precision", ActivityDiscipline.Label("Precision"));
        }

        [Fact]
        public void Label_of_an_unknown_id_is_the_id_itself_never_blank()
        {
            // Ett tomt fält i en aktivitetslista läses som att posten saknar gren. En rad skriven av
            // en framtida version ska visa NÅGOT.
            Assert.Equal("NagonNyGren", ActivityDiscipline.Label("NagonNyGren"));
            Assert.Equal("", ActivityDiscipline.Label(""));
            Assert.Equal("", ActivityDiscipline.Label(null));
        }

        [Fact]
        public void Sort_follows_the_catalogue_not_the_alphabet()
        {
            // Precision är den ojämförligt största grenen (1273 av 1512 träningsrader i prod) och
            // ska stå först i en chip-rad. Alfabetiskt hade Duell hamnat där.
            var sorted = new[] { "Springskytte", "Duell", "Precision", "Faltskytte" }
                .OrderBy(ActivityDiscipline.SortKey).ToList();

            Assert.Equal("Precision", sorted[0]);
            Assert.True(ActivityDiscipline.SortKey("Precision") < ActivityDiscipline.SortKey("Duell"));
            Assert.Equal(int.MaxValue, ActivityDiscipline.SortKey("HittePa"));
        }

        [Fact]
        public void The_canonical_set_is_the_competition_type_catalogue()
        {
            // ⚠️ Ingen fjärde disciplinlista. Kodbasen bär redan tre som är oense:
            // MemberDataPresenceService har Faltskytte, RankingSnapshotService saknar den, och
            // MarkenFamilies stavar den "Falt". Faller det här testet har någon börjat på en ny.
            Assert.Equal(HpskSite.Models.CompetitionTypes.All.Select(t => t.Id).ToList(), ActivityDiscipline.All.ToList());
            Assert.Equal(10, ActivityDiscipline.All.Count);
        }

        // ── Filtret i MemberActivitySummary.From ────────────────────────────────────────────────

        private static MemberActivityEntry Entry(
            string title, DateTime date, ActivityKind kind, string? discipline, params string[] groups) =>
            new()
            {
                Title = title,
                Date = date,
                Kind = kind,
                Evidence = ActivityEvidence.SelfReported,
                Disciplines = string.IsNullOrEmpty(discipline)
                    ? new List<string>()
                    : new List<string> { ActivityDiscipline.Canonical(discipline) },
                WeaponGroups = groups.ToList(),
                SourceId = Math.Abs(title.GetHashCode() % 10000),
                SourceKind = MemberActivityEntry.SourceKindTraining,
                CountsAsActivity = true,
            };

        private static List<MemberActivityEntry> Sample() => new()
        {
            Entry("Träning precision", new DateTime(2026, 3, 1), ActivityKind.Training, "Precision", "C"),
            Entry("Träning fält", new DateTime(2026, 3, 2), ActivityKind.Training, "Faltskytte", "C"),
            Entry("Fälttävling", new DateTime(2026, 4, 10), ActivityKind.Competition, "Faltskytte", "A"),
            // En tävling som bär prods visningsnamn — den MÅSTE hamna i fältfiltret.
            Entry("Magnumfält", new DateTime(2026, 5, 5), ActivityKind.Competition, "Magnum Fält", "M"),
            // Ett evenemang har ingen gren. Det ska falla bort OCH räknas som bortfall.
            Entry("Städdag", new DateTime(2026, 6, 1), ActivityKind.Event, null),
        };

        [Fact]
        public void No_filter_keeps_everything()
        {
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample());

            Assert.Equal(5, s.CountedEntries);
            Assert.Empty(s.DisciplineFilter);
            Assert.Equal(0, s.ExcludedWithoutDiscipline);
        }

        [Fact]
        public void Available_disciplines_are_listed_before_filtering()
        {
            // Annars försvinner de grenar man just filtrerade bort ur väljaren, och filtret blir en
            // enkelriktad gata.
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "Precision" });

            Assert.Contains("Faltskytte", s.DisciplinesAvailable);
            Assert.Contains("MagnumFalt", s.DisciplinesAvailable);
            Assert.Equal("Precision", s.DisciplinesAvailable.First());
        }

        [Fact]
        public void Filtering_on_faltskytte_keeps_only_field_activity()
        {
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "Faltskytte" });

            Assert.Equal(2, s.CountedEntries);
            Assert.All(s.Entries, e => Assert.Contains("Faltskytte", e.Disciplines));
            // Två aktivitetsdagar (3 mars, 10 april) — inte fyra.
            Assert.Equal(2, s.ActivityDays);
        }

        [Fact]
        public void The_prod_display_name_is_filterable()
        {
            // Posten lagrades från "Magnum Fält" och måste gå att filtrera på det kanoniska id:t.
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "MagnumFalt" });

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal("Magnumfält", s.Entries.Single().Title);
        }

        [Fact]
        public void A_filter_given_as_a_display_name_still_works()
        {
            // En länk eller ett sparat filter kan bära visningsnamnet. Ett literalt filter hade
            // matchat noll poster och läst som "ingen aktivitet i grenen".
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "Magnum Fält" });

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(new[] { "MagnumFalt" }, s.DisciplineFilter);
        }

        [Fact]
        public void Entries_without_a_discipline_are_reported_not_silently_dropped()
        {
            // ⚠️ Kärnan: en aktivitetssiffra som tappat evenemangen utan att någon nämner det
            // betyder något annat än läsaren tror.
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "Precision" });

            Assert.Equal(1, s.ExcludedWithoutDiscipline);
            Assert.Contains(s.Warnings, w => w.Contains("Filtrerat på gren"));
            Assert.Contains(s.Warnings, w => w.Contains("utan gren"));
            // Varningen ska bära VISNINGSNAMNET, inte id:t.
            Assert.Contains(s.Warnings, w => w.Contains("Precision"));
        }

        [Fact]
        public void The_warning_names_the_discipline_by_its_display_name()
        {
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "Faltskytte" });

            Assert.Contains(s.Warnings, w => w.Contains("Fältskytte"));
        }

        [Fact]
        public void An_unknown_discipline_filter_is_ignored_rather_than_emptying_the_list()
        {
            // Ett filter som inte går att tolka får inte tömma sammanställningen — då ser det ut som
            // att medlemmen inte har någon verksamhet alls.
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                disciplineFilter: new[] { "Bågskytte", "   " });

            Assert.Empty(s.DisciplineFilter);
            Assert.Equal(5, s.CountedEntries);
        }

        [Fact]
        public void Weapon_group_and_discipline_filters_compose_without_double_counting()
        {
            // ⚠️ Ordningen avgör siffrorna: vapenfiltret räknar sina bortfall på hela mängden,
            // grenfiltret på det som återstår. Räknades båda mot hela mängden skulle städdagen
            // rapporteras som bortfiltrerad två gånger.
            var s = MemberActivitySummary.From(1, "Test", 2026, Sample(),
                weaponGroupFilter: new[] { "C" },
                disciplineFilter: new[] { "Faltskytte" });

            // C-posterna är de två träningarna; av dem är en fältskytte.
            Assert.Equal(1, s.CountedEntries);
            Assert.Equal("Träning fält", s.Entries.Single().Title);

            // Städdagen föll bort på VAPENGRUPPEN (den kom först) och räknas bara där.
            Assert.Equal(1, s.ExcludedWithoutWeaponGroup);
            Assert.Equal(0, s.ExcludedWithoutDiscipline);
        }
    }
}
