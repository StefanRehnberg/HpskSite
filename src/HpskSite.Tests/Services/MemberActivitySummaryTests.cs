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

        // ── Träningsmatcher är inte tävlingar ─────────────────────────

        [Fact]
        public void TrainingMatches_AreNotCountedAsCompetitions()
        {
            // Rapporterat 2026-09-01: tävlingssiffran var för hög eftersom träningsmatcher räknades in.
            // En träningsmatch är klubbens interna uppgörelse och hör inte i det tal en styrelse eller
            // en handläggare läser som "tävlingar".
            var s = Build(
                Entry("2026-03-01", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171),
                Entry("2026-03-08", ActivityKind.TrainingMatch, sourceId: 501),
                Entry("2026-03-15", ActivityKind.TrainingMatch, sourceId: 502));

            Assert.Equal(1, s.Competitions);
            Assert.Equal(2, s.TrainingMatches);
        }

        [Fact]
        public void TrainingMatches_StillCountAsActivity()
        {
            // De ska INTE bort ur underlaget — bara ur tävlingssiffran.
            var s = Build(
                Entry("2026-03-08", ActivityKind.TrainingMatch, sourceId: 501),
                Entry("2026-03-15", ActivityKind.TrainingMatch, sourceId: 502));

            Assert.Equal(2, s.ActivityDays);
            Assert.Equal(2, s.CountedEntries);
            Assert.Equal(2, s.ByKind[ActivityKind.TrainingMatch]);
        }

        [Fact]
        public void TrainingMatches_CountTowardTheTrainingLogWarning()
        {
            // Varningen om självrapporterat underlag måste täcka matcherna också — annars summerar
            // inte delarna till brickan "Självrapporterad: N", vilket är exakt motsägelsen som
            // rapporterades.
            var s = Build(
                Entry("2026-01-05", ActivityKind.Training),
                Entry("2026-01-06", ActivityKind.Practice),
                Entry("2026-01-07", ActivityKind.TrainingMatch, sourceId: 501));

            Assert.Contains(s.Warnings, w => w.StartsWith("3 poster från träningsloggen"));
        }

        [Fact]
        public void SelfReportedCompetitions_GetTheirOwnWarning()
        {
            // ⚠️ Kärnan i den rapporterade motsägelsen: brickan räknade ALLA självrapporterade poster
            // (86) medan varningen bara nämnde träningspassen (68). Nu redovisas båda sorterna, och
            // delarna summerar synligt till brickans tal.
            var s = Build(
                Entry("2026-01-05", ActivityKind.Training),
                Entry("2026-02-05", ActivityKind.Competition, ActivityEvidence.SelfReported, sourceId: 900),
                Entry("2026-03-05", ActivityKind.Competition, ActivityEvidence.SelfReported, sourceId: 901));

            Assert.Contains(s.Warnings, w => w.StartsWith("1 poster från träningsloggen"));
            Assert.Contains(s.Warnings, w => w.StartsWith("2 tävlingsresultat är egenrapporterade"));

            // Summan av de två varningarna = brickans antal självrapporterade poster.
            Assert.Equal(3, s.ByEvidence[ActivityEvidence.SelfReported]);
        }

        [Fact]
        public void TheOldWarningWordingIsGone()
        {
            // Stefan bad uttryckligen att "Funktionärsverifiering av träning är inte byggd ännu"
            // skulle bort. Ett positivt påstående om frånvaro, så texten inte kan smyga tillbaka.
            var s = Build(Entry("2026-01-05", ActivityKind.Training));

            Assert.DoesNotContain(s.Warnings, w => w.Contains("inte byggd"));
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

        // ── Incheckning på banan ──────────────────────────────────────
        //
        // Problemet: någon skannar QR-koden på banan OCH loggar en träningsmatch samma dag. Ett
        // tillfälle, två poster. Räknas båda blir dagen dubbelräknad i postantalet.

        private static MemberActivityEntry CheckIn(string date, int sourceId) =>
            new()
            {
                Date = DateTime.Parse(date),
                Kind = ActivityKind.RangeCheckIn,
                Evidence = ActivityEvidence.SelfRegistered,
                CountsAsActivity = true,
                SourceId = sourceId,
                SourceKind = MemberActivityEntry.SourceKindRangeCheckIn,
                Title = "Incheckad på banan"
            };

        [Fact]
        public void CheckIn_Alone_CountsAsActivity()
        {
            // Hela poängen med inställningen: ett besök på banan där inget resultat loggades är ändå
            // verksamhet.
            var s = Build(CheckIn("2026-04-10", 1));

            Assert.Equal(1, s.ActivityDays);
            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(1, s.ByKind[ActivityKind.RangeCheckIn]);
        }

        [Fact]
        public void CheckIn_SameDayAsTraining_IsShownButNotCounted()
        {
            var s = Build(
                Entry("2026-04-10", ActivityKind.Training, sourceId: 500),
                CheckIn("2026-04-10", 1));

            Assert.Equal(1, s.CountedEntries);          // bara träningen
            Assert.Equal(1, s.ActivityDays);
            Assert.Equal(2, s.Entries.Count);          // men incheckningen SYNS
            var ci = s.Entries.Single(e => e.Kind == ActivityKind.RangeCheckIn);
            Assert.False(ci.CountsAsActivity);
            Assert.Contains(MemberActivitySummary.RedundantCheckInReason, ci.NotCountedReason);
            Assert.Equal("training:500", ci.SameOccasionAs);
        }

        [Fact]
        public void CheckIn_SameDayAsCompetition_IsShownButNotCounted()
        {
            var s = Build(
                Entry("2026-04-10", ActivityKind.Competition, ActivityEvidence.OfficialResult, sourceId: 2171),
                CheckIn("2026-04-10", 1));

            Assert.Equal(1, s.Competitions);
            Assert.Equal(1, s.CountedEntries);
            Assert.Equal("comp:2171", s.Entries.Single(e => e.Kind == ActivityKind.RangeCheckIn).SameOccasionAs);
        }

        [Fact]
        public void CheckIn_OnAnotherDay_StillCounts()
        {
            // Motsatsprovet — utan det kan koden nolla varje incheckning och ändå se grön ut.
            var s = Build(
                Entry("2026-04-10", ActivityKind.Training, sourceId: 500),
                CheckIn("2026-04-11", 1));

            Assert.Equal(2, s.CountedEntries);
            Assert.Equal(2, s.ActivityDays);
        }

        [Fact]
        public void CheckIn_SameDayAsAnUNCOUNTEDEntry_StillCounts()
        {
            // ⚠️ En DNS-tävling räknas inte, och ska därför inte kunna knuffa bort incheckningen:
            // medlemmen var ändå på banan den dagen, och det är allt vi vet. Skulle regeln titta på
            // ALLA poster i stället för de räknade skulle en utebliven start radera beviset för att
            // hen var där.
            var s = Build(
                Entry("2026-04-10", ActivityKind.Competition, ActivityEvidence.RegisteredOnly,
                      counts: false, sourceId: 2171, notCountedReason: "Ej start (DNS)"),
                CheckIn("2026-04-10", 1));

            Assert.Equal(1, s.CountedEntries);
            Assert.True(s.Entries.Single(e => e.Kind == ActivityKind.RangeCheckIn).CountsAsActivity);
        }

        [Fact]
        public void TwoCheckInsSameDay_OnlyTheFirstCounts()
        {
            // In, ut och in igen är ett besök. Utan den här regeln hade båda sett "ingen annan post"
            // och räknats — alltså dubbelräkning av just det fallet regeln finns för.
            var s = Build(CheckIn("2026-04-10", 1), CheckIn("2026-04-10", 2));

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(1, s.ActivityDays);
            var second = s.Entries.Single(e => e.SourceId == 2);
            Assert.False(second.CountsAsActivity);
            Assert.Equal(MemberActivitySummary.DuplicateCheckInReason, second.NotCountedReason);
            Assert.Equal("checkin:1", second.SameOccasionAs);
        }

        [Fact]
        public void TwoCheckInsSameDayWithOtherActivity_NEITHERCounts()
        {
            var s = Build(
                Entry("2026-04-10", ActivityKind.TrainingMatch, sourceId: 500),
                CheckIn("2026-04-10", 1),
                CheckIn("2026-04-10", 2));

            Assert.Equal(1, s.CountedEntries);
            Assert.All(s.Entries.Where(e => e.Kind == ActivityKind.RangeCheckIn),
                       e => Assert.False(e.CountsAsActivity));
        }

        [Fact]
        public void ExplicitLink_IsRespected_EvenWithNoOtherEntryThatDay()
        {
            // Lager 2: tjänsten sätter CountsAsActivity=false och SameOccasionAs när passet bär en
            // LinkedCompetitionId. Deduperingen får inte ångra det bara för att den länkade posten
            // inte råkar ligga i samma årsurval.
            var linked = CheckIn("2026-04-10", 1);
            linked.CountsAsActivity = false;
            linked.NotCountedReason = MemberActivitySummary.RedundantCheckInReason + ": tävling";
            linked.SameOccasionAs = "comp:9999";

            var s = Build(linked);

            Assert.Equal(0, s.CountedEntries);
            Assert.Equal("comp:9999", s.Entries.Single().SameOccasionAs);
        }

        [Fact]
        public void CheckIn_HasNoWeaponGroup_AndIsDroppedByAFilter()
        {
            // En incheckning säger inte VAD som sköts, så den kan inte höra till en vapengrupp. Under
            // ett filter faller den bort — och det räknas som en post utan vapengrupp, så rutan kan
            // säga det.
            var s = BuildFiltered(new[] { "C" },
                GroupEntry("2026-04-11", ActivityKind.Training, 1, "C"),
                CheckIn("2026-04-10", 1));

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(1, s.ExcludedWithoutWeaponGroup);
        }

        [Fact]
        public void NoCheckIns_LeavesEverythingUntouched()
        {
            // Klubbar som inte slår på inställningen får aldrig någon incheckning inläst, och då ska
            // dedupliceringen vara en no-op.
            var s = Build(
                Entry("2026-04-10", ActivityKind.Training, sourceId: 500),
                Entry("2026-04-10", ActivityKind.TrainingMatch, sourceId: 501));

            Assert.Equal(2, s.CountedEntries);
            Assert.Equal(1, s.ActivityDays);
        }

        // ── Vapengruppsfiltret ────────────────────────────────────────

        private static MemberActivityEntry GroupEntry(string date, ActivityKind kind, int sourceId,
            params string[] groups)
        {
            var e = Entry(date, kind, sourceId: sourceId);
            e.WeaponGroups = groups.ToList();
            return e;
        }

        private static MemberActivitySummary BuildFiltered(string[] filter, params MemberActivityEntry[] entries) =>
            MemberActivitySummary.From(42, "Testskytt", 2026, entries, filter);

        [Fact]
        public void NoFilter_KeepsEverything()
        {
            var s = BuildFiltered(Array.Empty<string>(),
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"),
                GroupEntry("2026-01-06", ActivityKind.Training, 2, "A"),
                Entry("2026-01-07", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded, sourceId: 900));

            Assert.Equal(3, s.CountedEntries);
            Assert.Empty(s.WeaponGroupFilter);
            Assert.Equal(0, s.ExcludedWithoutWeaponGroup);
        }

        [Fact]
        public void Filter_KeepsOnlyTheChosenGroup()
        {
            var s = BuildFiltered(new[] { "C" },
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"),
                GroupEntry("2026-01-06", ActivityKind.Training, 2, "A"),
                GroupEntry("2026-01-07", ActivityKind.Training, 3, "B"));

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(1, s.ActivityDays);
        }

        [Fact]
        public void Filter_MatchesAnyOfAnEntrysGroups()
        {
            // En tävling kan innehålla flera vapenklasser för samma skytt — dev-data har A1 och
            // L_Vet_A på samma anmälan. Den tävlingen är aktivitet i BÅDA grupperna.
            var s = BuildFiltered(new[] { "L" },
                GroupEntry("2026-09-24", ActivityKind.Competition, 2171, "A", "L"));

            Assert.Equal(1, s.Competitions);
        }

        [Fact]
        public void Filter_AcceptsSeveralGroupsAtOnce()
        {
            // Ett vapen kan vara avsett för mer än en vapengrupp; då är summan underlaget.
            var s = BuildFiltered(new[] { "C", "A" },
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"),
                GroupEntry("2026-01-06", ActivityKind.Training, 2, "A"),
                GroupEntry("2026-01-07", ActivityKind.Training, 3, "B"));

            Assert.Equal(2, s.CountedEntries);
        }

        [Fact]
        public void Filter_IsCaseInsensitive()
        {
            var s = BuildFiltered(new[] { "c" },
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"));

            Assert.Equal(1, s.CountedEntries);
        }

        [Fact]
        public void Filter_DropsEntriesWithoutAGroup_AndSaysSo()
        {
            // ⚠️ Evenemang har ingen vapengrupp — en städdag är klubbverksamhet, inte skjutande i en
            // grupp. De faller bort under ett filter, och det MÅSTE stå på skärmen: en
            // aktivitetssiffra som tappat evenemangen utan att någon nämner det betyder något annat
            // än läsaren tror.
            var s = BuildFiltered(new[] { "C" },
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"),
                Entry("2026-01-07", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded, sourceId: 900),
                Entry("2026-01-08", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded, sourceId: 901));

            Assert.Equal(1, s.CountedEntries);
            Assert.Equal(2, s.ExcludedWithoutWeaponGroup);
            Assert.Contains(s.Warnings, w => w.Contains("Filtrerat på vapengrupp C"));
            Assert.Contains(s.Warnings, w => w.Contains("2 poster utan vapengrupp"));
        }

        [Fact]
        public void AvailableGroups_AreComputedBEFOREFiltering()
        {
            // ⚠️ Annars försvinner de grupper man just filtrerade bort ur väljaren och man kan inte
            // välja tillbaka dem — filtret blir en enkelriktad gata.
            var s = BuildFiltered(new[] { "C" },
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"),
                GroupEntry("2026-01-06", ActivityKind.Training, 2, "A"),
                GroupEntry("2026-01-07", ActivityKind.Training, 3, "B"));

            Assert.Equal(new[] { "A", "B", "C" }, s.WeaponGroupsAvailable);
        }

        [Fact]
        public void AvailableGroups_IgnoreEntriesWithoutAGroup()
        {
            var s = BuildFiltered(Array.Empty<string>(),
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"),
                Entry("2026-01-07", ActivityKind.Event, ActivityEvidence.FunctionaryRecorded, sourceId: 900));

            Assert.Equal(new[] { "C" }, s.WeaponGroupsAvailable);
        }

        [Fact]
        public void Filter_OnAGroupWithNoActivity_GivesZeroButStillListsTheOptions()
        {
            // Ett tomt svar är ett giltigt svar — men väljaren måste stå kvar så man kommer tillbaka.
            var s = BuildFiltered(new[] { "R" },
                GroupEntry("2026-01-05", ActivityKind.Training, 1, "C"));

            Assert.Equal(0, s.CountedEntries);
            Assert.Equal(0, s.ActivityDays);
            Assert.Equal(new[] { "C" }, s.WeaponGroupsAvailable);
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

            Assert.Contains(s.Warnings, w => w.StartsWith("2 poster från träningsloggen"));
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
