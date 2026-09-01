using HpskSite.Models;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Aggregeringen i <see cref="MemberActivitySummary.From"/>. Den är avsiktligt en ren funktion
    /// just för att kunna prövas utan Umbraco och utan databas — och det som prövas är inte
    /// summeringen i sig utan de fyra reglerna sammanställningen bär:
    /// dagar-inte-poster, distinkta tävlingar, vad som inte räknas, och att varje svaghet i
    /// underlaget har en varning.
    ///
    /// ⚠️ Sviten är skriven för att FALLA om reglerna luckras upp. Ett påstående som inte kan falla
    /// är värre än inget (minnet an-assertion-that-cannot-fail), så varje test här beskriver ett
    /// utfall som skiljer sig från det naiva.
    /// </summary>
    public class MemberActivitySummaryTests
    {
        private static MemberActivityEntry Entry(
            string date,
            ActivityKind kind = ActivityKind.Training,
            ActivityEvidence evidence = ActivityEvidence.SelfReported,
            bool counts = true,
            int sourceId = 1,
            bool mandatory = false,
            string? notCountedReason = null,
            string title = "Post",
            string? sourceKind = null) => new()
            {
                Date = DateTime.Parse(date),
                Kind = kind,
                Evidence = evidence,
                CountsAsActivity = counts,
                SourceId = sourceId,
                SourceKind = sourceKind ?? kind switch
                {
                    ActivityKind.Competition => MemberActivityEntry.SourceKindCompetition,
                    ActivityKind.Event => MemberActivityEntry.SourceKindEvent,
                    _ => MemberActivityEntry.SourceKindTraining
                },
                IsMandatoryEvent = mandatory,
                NotCountedReason = notCountedReason,
                Title = title
            };

        private static MemberActivitySummary Build(params MemberActivityEntry[] entries) =>
            MemberActivitySummary.From(42, "Testskytt", 2026, entries);

        // ── Aktivitetsdagar, inte poster ──────────────────────────────

        [Fact]
        public void TwoSessionsSameDay_CountAsOneActivityDay()
        {
            // Två träningspass med olika vapen samma kväll är ett besök på banan. Att räkna poster
            // skulle blåsa upp underlaget i ett intyg med en faktor två för varje flervapenskytt.
            var s = Build(
                Entry("2026-03-04", sourceId: 1),
                Entry("2026-03-04", sourceId: 2));

            Assert.Equal(1, s.ActivityDays);
            Assert.Equal(2, s.CountedEntries);
        }

        [Fact]
        public void TimeOfDayIsIgnoredWhenCountingDays()
        {
            // Träningsrader bär klockslag ur TrainingDate. Utan .Date hade morgon- och kvällspasset
            // blivit två dagar.
            var s = Build(
                Entry("2026-03-04 08:15", sourceId: 1),
                Entry("2026-03-04 19:45", sourceId: 2));

            Assert.Equal(1, s.ActivityDays);
        }

        [Fact]
        public void UncountedEntriesDoNotAddActivityDays()
        {
            // Ett evenemang där uppropet aldrig togs får inte skapa en aktivitetsdag av ingenting.
            var s = Build(
                Entry("2026-05-01", ActivityKind.Event, ActivityEvidence.None, counts: false,
                      notCountedReason: MemberActivitySummary.NotRecordedReason));

            Assert.Equal(0, s.ActivityDays);
            Assert.Equal(0, s.CountedEntries);
            Assert.Single(s.Entries);   // men raden SYNS
        }

        // ── Tävlingar räknas distinkt ─────────────────────────────────

        [Fact]
        public void ThreeClassesInOneCompetition_CountAsOneCompetition()
        {
            // Anmälan sker per klass; deltagandet är ett. Läsvägen slår ihop per tävlings-id, och
            // det här är påståendet som faller om någon börjar räkna rader i stället.
            var s = Build(
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171),
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171),
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171));

            Assert.Equal(1, s.Competitions);
        }

        [Fact]
        public void TwoDifferentCompetitions_CountAsTwo()
        {
            var s = Build(
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171),
                Entry("2026-11-08", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2172));

            Assert.Equal(2, s.Competitions);
        }

        [Fact]
        public void OwnAndExternalCompetitionWithTheSameId_AreTwoCompetitions()
        {
            // ⚠️ Regressionen för källnyckeln. En SJÄLVRAPPORTERAD extern tävling bär
            // träningsradens id, en egen bär tävlingsnodens — två oberoende identitetsserier där
            // samma heltal betyder olika saker. Räknas distinkt på id:t ensamt viks de ihop och
            // intyget underrapporterar, tyst. Talet 2171 finns i båda serierna med flit.
            var s = Build(
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.OfficialResult,
                      sourceId: 2171, sourceKind: MemberActivityEntry.SourceKindCompetition),
                Entry("2026-10-01", ActivityKind.Competition, ActivityEvidence.SelfReported,
                      sourceId: 2171, sourceKind: MemberActivityEntry.SourceKindTraining));

            Assert.Equal(2, s.Competitions);
        }

        [Fact]
        public void DnsCompetition_IsListedButNotCounted()
        {
            var s = Build(
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.RegisteredOnly,
                      counts: false, sourceId: 2171, notCountedReason: "Ej start (DNS) — anmäld men sköt inte"));

            Assert.Equal(0, s.Competitions);
            Assert.Equal(0, s.ActivityDays);
            Assert.Single(s.Entries);
        }

        // ── Obligatoriska evenemang ───────────────────────────────────

        [Fact]
        public void MandatoryEvents_SplitIntoAttendedAndMissed()
        {
            // Styrelsens intygsbeslut hänger på just den här uppdelningen.
            var s = Build(
                Entry("2026-04-10", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded,
                      sourceId: 900, mandatory: true),
                Entry("2026-04-17", ActivityKind.Event, ActivityEvidence.None, counts: false,
                      sourceId: 901, mandatory: true, notCountedReason: "Frånvarande"),
                Entry("2026-04-24", ActivityKind.Event, ActivityEvidence.None, counts: false,
                      sourceId: 902, mandatory: true,
                      notCountedReason: MemberActivitySummary.NotRecordedReason));

            Assert.Equal(1, s.MandatoryEventsAttended);
            // Både frånvaro OCH "uppropet togs aldrig" räknas som saknad — men de står kvar med
            // sina egna skäl, så styrelsen kan skilja dem åt.
            Assert.Equal(2, s.MandatoryEventsMissed);
        }

        [Fact]
        public void NonMandatoryEvent_IsNotCountedAsMandatory()
        {
            var s = Build(Entry("2026-04-10", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded));

            Assert.Equal(0, s.MandatoryEventsAttended);
            Assert.Equal(0, s.MandatoryEventsMissed);
            Assert.Equal(1, s.CountedEntries);
        }

        // ── Fördelningarna ────────────────────────────────────────────

        [Fact]
        public void Breakdowns_CountOnlyCountedEntries()
        {
            // En fördelning som räknar in poster som inte räknas summerar inte till totalen, och
            // då kan de två talen inte ställas mot varandra i ett intyg.
            var s = Build(
                Entry("2026-01-05"),
                Entry("2026-02-05", ActivityKind.Event, ActivityEvidence.None, counts: false,
                      notCountedReason: MemberActivitySummary.NotRecordedReason));

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(1, s.ByKind.Values.Sum());
            Assert.Equal(1, s.ByEvidence.Values.Sum());
            Assert.False(s.ByEvidence.ContainsKey(ActivityEvidence.None));
        }

        [Fact]
        public void PracticeAndTraining_AreSeparateKinds()
        {
            // 0-poäng träning är verksamhet men inte poäng. Sammanslås de kan ett intyg inte visa
            // hur mycket av volymen som var poängsatt skjutning.
            var s = Build(
                Entry("2026-01-05", ActivityKind.Training),
                Entry("2026-01-06", ActivityKind.Practice));

            Assert.Equal(1, s.ByKind[ActivityKind.Training]);
            Assert.Equal(1, s.ByKind[ActivityKind.Practice]);
        }

        // ── Varningarna ───────────────────────────────────────────────

        [Fact]
        public void SelfReportedTraining_AlwaysWarns()
        {
            // Ingen träningslogg är funktionärsverifierad idag. Saknas varningen läses siffran som
            // attesterad verksamhet, vilket den inte är.
            var s = Build(Entry("2026-01-05", ActivityKind.Training, ActivityEvidence.SelfReported));

            Assert.Contains(s.Warnings, w => w.Contains("självrapporterade"));
        }

        [Fact]
        public void PracticeAlsoCountsTowardTheSelfReportedWarning()
        {
            // 0-poängspass är lika overifierade som poängsatta — varningen räknar båda sorterna.
            var s = Build(
                Entry("2026-01-05", ActivityKind.Training),
                Entry("2026-01-06", ActivityKind.Practice));

            Assert.Contains(s.Warnings, w => w.StartsWith("2 träningspass"));
        }

        [Fact]
        public void RegisteredOnlyCompetition_Warns()
        {
            var s = Build(Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.RegisteredOnly, sourceId: 7));

            Assert.Contains(s.Warnings, w => w.Contains("inget inskrivet resultat"));
        }

        [Fact]
        public void SelfRegisteredAttendance_Warns()
        {
            var s = Build(Entry("2026-04-10", ActivityKind.Event, ActivityEvidence.SelfRegistered, sourceId: 900));

            Assert.Contains(s.Warnings, w => w.Contains("självregistrerade"));
        }

        [Fact]
        public void MissingRollCall_Warns_AndSaysItIsNotAbsence()
        {
            var s = Build(Entry("2026-04-10", ActivityKind.Event, ActivityEvidence.None, counts: false,
                                sourceId: 900, notCountedReason: MemberActivitySummary.NotRecordedReason));

            var warning = Assert.Single(s.Warnings);
            Assert.Contains("inget upprop", warning);
            // Formuleringen är bärande: en läsare som tolkar den som frånvaro drar fel slutsats om
            // ett obligatoriskt evenemang.
            Assert.Contains("frånvarande", warning);
        }

        [Fact]
        public void AbsentEvent_DoesNotTriggerTheMissingRollCallWarning()
        {
            // Frånvaro ÄR en uppgift. Bara avsaknaden av en uppgift ska varna, annars slutar
            // varningen betyda något.
            var s = Build(Entry("2026-04-10", ActivityKind.Event, ActivityEvidence.None, counts: false,
                                sourceId: 900, notCountedReason: "Frånvarande"));

            Assert.DoesNotContain(s.Warnings, w => w.Contains("inget upprop"));
        }

        [Fact]
        public void OfficialResultsOnly_ProducesNoWarnings()
        {
            // Motsatsprovet. Utan det kan varningsbyggaren returnera en varning för varje läge och
            // alla ovanstående påståenden vore ändå gröna.
            var s = Build(
                Entry("2026-09-24", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171),
                Entry("2026-04-10", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded, sourceId: 900));

            Assert.Empty(s.Warnings);
        }

        // ── Form ──────────────────────────────────────────────────────

        [Fact]
        public void EntriesAreNewestFirst()
        {
            var s = Build(
                Entry("2026-01-05", title: "Äldst"),
                Entry("2026-12-01", title: "Nyast"),
                Entry("2026-06-15", title: "Mitten"));

            Assert.Equal(new[] { "Nyast", "Mitten", "Äldst" }, s.Entries.Select(e => e.Title));
        }

        [Fact]
        public void EmptyYear_IsAValidAnswer_NotAnError()
        {
            var s = Build();

            Assert.Equal(0, s.ActivityDays);
            Assert.Empty(s.Entries);
            Assert.Empty(s.Warnings);
            Assert.Equal(2026, s.Year);
            Assert.Equal("Testskytt", s.MemberName);
        }

        [Fact]
        public void EvidenceStrengthIsOrdered_WeakestFirst()
        {
            // Ordningen används för att sortera fördelningen och för att välja färg i gränssnittet.
            // Kastas den om utan att ytorna ändras börjar en svag post se ut som en stark.
            Assert.True(ActivityEvidence.None < ActivityEvidence.SelfReported);
            Assert.True(ActivityEvidence.SelfReported < ActivityEvidence.RegisteredOnly);
            Assert.True(ActivityEvidence.RegisteredOnly < ActivityEvidence.SelfRegistered);
            Assert.True(ActivityEvidence.SelfRegistered < ActivityEvidence.FunctionaryRecorded);
            Assert.True(ActivityEvidence.FunctionaryRecorded < ActivityEvidence.OfficialResult);
        }

        [Fact]
        public void EveryEvidenceAndKindHasASwedishLabel()
        {
            // Ett nytt enum-värde utan etikett renderas som en tom cell i ett myndighetsunderlag.
            foreach (ActivityEvidence e in Enum.GetValues<ActivityEvidence>())
                Assert.False(string.IsNullOrWhiteSpace(MemberActivityEntry.EvidenceDisplay(e)),
                    $"ActivityEvidence.{e} saknar svensk etikett");

            foreach (ActivityKind k in Enum.GetValues<ActivityKind>())
                Assert.False(string.IsNullOrWhiteSpace(MemberActivityEntry.KindDisplay(k)),
                    $"ActivityKind.{k} saknar svensk etikett");
        }
    }
}
