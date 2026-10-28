using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Planera dagen steg 3: luckfyllaren som RENT bevisbar funktion. Egenskapstester över många slumpade men deterministiska instanser (frö), med en
    /// oberoende validator, en förkontroll som FALLER om en instans är för snäll, och en naiv referenslösare att jämföra med.
    /// </summary>
    public class StaffingSolverTests
    {
        private static readonly DateTime D = new(2027, 6, 5);
        private static StaffInterval Iv(int fh, int th) => new(D.AddHours(fh), D.AddHours(th), false);

        /// <summary>En ond instans: många dubbelroller (starter som sammanfaller med passen), behov som är större än utbudet, säkerhetsroller och en heldagslucka.</summary>
        private sealed record Instance(List<SolverSlot> Slots, List<SolverPerson> People, int DoubleRoles, int ChiefSlots, int TotalNeeded)
        {
            public int Volunteers => People.Count(p => p.Volunteer);
        }

        private static Instance Make(int seed)
        {
            var r = new Random(seed);
            var roles = new[] { ("stationsmarkor", "Stationsmarkör"), ("sekretariat", "Sekretariat"), ("servering", "Servering"), ("stationschef", "Stationschef") };
            var people = new List<SolverPerson>();
            var n = r.Next(18, 61);
            var dbl = 0;
            for (var i = 1; i <= n; i++)
            {
                var vol = r.NextDouble() < 0.7;
                var busy = new List<BusyItem>();
                if (r.NextDouble() < 0.4)   // dubbelroll: skjuter någon gång 09–14 (fönstret är 3,5 h med förberedelse och vila)
                {
                    var s = r.Next(9, 14);
                    busy.Add(new BusyItem(BusyKind.Start, new StaffInterval(D.AddHours(s).AddMinutes(-45), D.AddHours(s + 3).AddMinutes(30), false), $"egen start {s}:00"));
                    dbl++;
                }
                if (r.NextDouble() < 0.1) busy.Add(new BusyItem(BusyKind.Assignment, Iv(8, 17), "annat uppdrag hela dagen"));
                var rolesOf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (r.NextDouble() < 0.5) rolesOf.Add(roles[r.Next(0, 3)].Item1);
                people.Add(new SolverPerson(100 + i, $"Person {r.Next(0, 5)} {(char)('A' + r.Next(0, 26))}{i}", vol, rolesOf, busy, r.Next(0, 3) == 0 ? 1 : 0));
            }
            var slots = new List<SolverSlot>();
            var k = r.Next(8, 31);
            var chief = 0; var needed = 0;
            for (var i = 0; i < k; i++)
            {
                var (rk, rn) = roles[r.Next(0, roles.Length)];
                var from = r.Next(8, 17); var to = Math.Min(18, from + r.Next(1, 5));
                var station = r.Next(1, 9).ToString();
                var missing = r.Next(1, 4);
                slots.Add(new SolverSlot($"{rk}|{station}|{D:yyyyMMdd}|{from}-{to}|{i}", rk, rn, "Station", station, $"Station {station}", D, Iv(from, to), null, missing));
                needed += missing;
                if (rk == "stationschef") chief++;
            }
            // Ett behov som är större än utbudet med flit: hela dagen, fem personer.
            slots.Add(new SolverSlot($"servering|X|{D:yyyyMMdd}|stor", "servering", "Servering", null, null, null, D, Iv(8, 18), null, 5));
            needed += 5;
            return new Instance(slots, people, dbl, chief, needed);
        }

        public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 300).Select(i => new object[] { i });

        [Theory]
        [MemberData(nameof(Seeds))]
        public void De_harda_villkoren_haller_pa_varje_instans(int seed)
        {
            var inst = Make(seed);
            var res = StaffingSolver.Solve(inst.Slots, inst.People);
            StaffingSolver.Validate(inst.Slots, inst.People, res).Should().BeEmpty($"fröet {seed} ska ge ett förslag utan brott");
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void Samma_indata_ger_exakt_samma_forslag(int seed)
        {
            var inst = Make(seed);
            var a = StaffingSolver.Solve(inst.Slots, inst.People);
            var b = StaffingSolver.Solve(inst.Slots.AsEnumerable().Reverse().ToList(), inst.People.AsEnumerable().Reverse().ToList());
            a.Proposed.Select(r => $"{r.SlotKey}>{r.MemberId}").Should().Equal(b.Proposed.Select(r => $"{r.SlotKey}>{r.MemberId}"), $"fröet {seed}: ordningen på indata får inte påverka förslaget");
            a.Unfilled.Select(u => $"{u.SlotKey}:{u.Missing}").Should().Equal(b.Unfilled.Select(u => $"{u.SlotKey}:{u.Missing}"));
        }

        [Fact]
        public void Instanserna_ar_onda_nog_for_att_testerna_ska_betyda_nagot()
        {
            // Förkontrollen som FALLER om generatorn blir snäll: annars kan "inga krockar" bli sant av en slump.
            var all = Enumerable.Range(1, 300).Select(Make).ToList();
            all.Should().OnlyContain(i => i.TotalNeeded > 0);
            all.Count(i => i.DoubleRoles >= 5).Should().BeGreaterThan(250, "de flesta instanser ska ha minst fem som både skjuter och jobbar");
            all.Count(i => i.ChiefSlots > 0).Should().BeGreaterThan(200, "stationschefsluckor ska finnas i de flesta instanser, så att testet nedan prövar att de fylls");
            all.Average(i => i.Volunteers).Should().BeGreaterThan(15);
            // Och minst en instans där utbudet inte räcker (annars testas aldrig 'olöst').
            all.Count(i => StaffingSolver.Solve(i.Slots, i.People).StillMissing > 0).Should().BeGreaterThan(100);
        }

        [Fact]
        public void Stationschef_fylls_som_alla_andra_roller()
        {
            // Stefan 2026-10-28: förslaget fyller allt det kan, även stationschef. Ingen lucka lämnas åt "en människa".
            var placed = 0;
            for (var seed = 1; seed <= 300; seed++)
            {
                var inst = Make(seed);
                var res = StaffingSolver.Solve(inst.Slots, inst.People);
                placed += res.Proposed.Count(r => r.RoleKey == "stationschef");
                res.Unfilled.Should().NotContain(u => u.Reason.Contains("utses av en människa"));
            }
            placed.Should().BeGreaterThan(0, "stationschefsluckor ska kunna fyllas");
        }

        [Fact]
        public void En_person_som_inte_anmalt_sig_foreslas_aldrig_och_en_upptagen_inte_heller()
        {
            var slot = new SolverSlot("s", "stationsmarkor", "Stationsmarkör", "Station", "1", "Station 1", D, Iv(13, 15), null, 2);
            var free = new SolverPerson(1, "Ledig", true, new HashSet<string> { "stationsmarkor" }, new List<BusyItem>(), 0);
            var notVol = new SolverPerson(2, "Ej anmäld", false, new HashSet<string>(), new List<BusyItem>(), 0);
            var busy = new SolverPerson(3, "Upptagen", true, new HashSet<string>(), new List<BusyItem> { new(BusyKind.Start, Iv(11, 14), "egen start 12:00") }, 0);
            var res = StaffingSolver.Solve(new[] { slot }, new[] { free, notVol, busy });
            res.Proposed.Select(r => r.MemberId).Should().Equal(1);
            res.Unfilled.Single().Missing.Should().Be(1);
            res.Unfilled.Single().Reason.Should().Contain("Bara 1 av de anmälda är ledig");
        }

        [Fact]
        public void Samma_person_foreslas_inte_pa_tva_platser_samtidigt_men_gar_pa_efter_varandra()
        {
            var a = new SolverSlot("a", "sekretariat", "Sekretariat", null, null, null, D, Iv(9, 11), null, 1);
            var b = new SolverSlot("b", "sekretariat", "Sekretariat", null, null, null, D, Iv(10, 12), null, 1);
            var c = new SolverSlot("c", "sekretariat", "Sekretariat", null, null, null, D, Iv(11, 13), null, 1);
            var only = new SolverPerson(1, "Ensam", true, new HashSet<string>(), new List<BusyItem>(), 0);
            var res = StaffingSolver.Solve(new[] { a, b, c }, new[] { only });
            res.Proposed.Should().HaveCount(2);                           // a och c (kant i kant går), inte b samtidigt med båda
            StaffingSolver.Validate(new[] { a, b, c }, new[] { only }, res).Should().BeEmpty();
            res.StillMissing.Should().Be(1);
        }

        [Fact]
        public void Den_som_ar_anmald_till_rollen_gar_forst_och_bland_lika_den_med_farst_uppdrag()
        {
            var slot = new SolverSlot("s", "servering", "Servering", null, null, null, D, Iv(9, 12), null, 1);
            var generic = new SolverPerson(1, "Aaa", true, new HashSet<string>(), new List<BusyItem>(), 0);
            var matching = new SolverPerson(2, "Zzz", true, new HashSet<string> { "servering" }, new List<BusyItem>(), 3);
            StaffingSolver.Solve(new[] { slot }, new[] { generic, matching }).Proposed.Single().MemberId.Should().Be(2);   // rätt roll före färst uppdrag
            var a = new SolverPerson(3, "Bo", true, new HashSet<string> { "servering" }, new List<BusyItem>(), 2);
            var b = new SolverPerson(4, "Åke", true, new HashSet<string> { "servering" }, new List<BusyItem>(), 0);
            StaffingSolver.Solve(new[] { slot }, new[] { a, b }).Proposed.Single().MemberId.Should().Be(4);                  // lika roll: färst uppdrag
        }

        [Fact]
        public void En_lucka_utan_nagon_ledig_redovisas_med_skal_och_forsvinner_inte()
        {
            var slot = new SolverSlot("s", "servering", "Servering", null, null, null, D, Iv(9, 12), null, 3);
            var p = new SolverPerson(1, "P", true, new HashSet<string>(), new List<BusyItem> { new(BusyKind.Assignment, Iv(8, 13), "annat") }, 0);
            var res = StaffingSolver.Solve(new[] { slot }, new[] { p });
            res.Proposed.Should().BeEmpty();
            res.Unfilled.Single().Should().Match<UnfilledSlot>(u => u.Missing == 3 && u.Reason.Contains("upptagna"));
            var none = StaffingSolver.Solve(new[] { slot }, Array.Empty<SolverPerson>());
            none.Unfilled.Single().Reason.Should().Contain("Ingen har anmält sig");
        }

        [Fact]
        public void En_flytt_av_forslagets_eget_uppdrag_ger_plats_at_en_lucka_som_annars_blir_tom()
        {
            // Lucka A (9–11) kan bara Ann; lucka B (10–12) kan Ann och Bo. Greedy utan flytt kunde ge B till Ann och lämna A tom.
            var a = new SolverSlot("a", "servering", "Servering", null, null, null, D, Iv(9, 11), null, 1);
            var b = new SolverSlot("b", "servering", "Servering", null, null, null, D, Iv(10, 12), null, 1);
            var ann = new SolverPerson(1, "Ann", true, new HashSet<string>(), new List<BusyItem>(), 0);
            var bo = new SolverPerson(2, "Bo", true, new HashSet<string>(), new List<BusyItem> { new(BusyKind.Start, Iv(8, 10), "egen start") }, 0);
            var res = StaffingSolver.Solve(new[] { a, b }, new[] { ann, bo });
            res.StillMissing.Should().Be(0);
            res.Proposed.Single(r => r.SlotKey == "a").MemberId.Should().Be(1);
            res.Proposed.Single(r => r.SlotKey == "b").MemberId.Should().Be(2);
            StaffingSolver.Validate(new[] { a, b }, new[] { ann, bo }, res).Should().BeEmpty();
        }

        [Fact]
        public void Forbattringsfasen_fyller_fler_luckor_pa_nagon_instans_och_gor_aldrig_nagot_samre()
        {
            var better = 0;
            for (var seed = 1; seed <= 300; seed++)
            {
                var inst = Make(seed);
                var with = StaffingSolver.Solve(inst.Slots, inst.People);
                var without = StaffingSolver.Solve(inst.Slots, inst.People, new SolverOptions { Improve = false });
                StaffingSolver.Validate(inst.Slots, inst.People, with).Should().BeEmpty($"fröet {seed}");
                (Hours(with) <= Hours(without) + 0.001).Should().BeTrue($"fröet {seed}: förbättringsfasen får aldrig lämna mer obemannat");
                if (Hours(with) < Hours(without) - 0.001) better++;
            }
            better.Should().BeGreaterThan(0, "annars bevisar ingen instans att fasen gör nytta — och den kan tas bort utan att något testas");
        }

        // ---- validatorn måste kunna falla (annars bevisar de andra testerna ingenting) ----

        [Fact]
        public void Validatorn_hittar_en_person_som_foreslagits_nar_hen_sjalv_skjuter()
        {
            var slot = new SolverSlot("s", "servering", "Servering", null, null, null, D, Iv(12, 14), null, 1);
            var p = new SolverPerson(1, "Skytt", true, new HashSet<string>(), new List<BusyItem> { new(BusyKind.Start, Iv(11, 15), "egen start") }, 0);
            var bad = new SolverResult(new[] { new ProposedRow("s", 1, "Skytt", "servering", null, null, D, slot.Interval, "x", null) }, Array.Empty<UnfilledSlot>());
            StaffingSolver.Validate(new[] { slot }, new[] { p }, bad).Should().Contain(x => x.Contains("upptagen"));
        }

        [Fact]
        public void Validatorn_hittar_en_dubblett_och_en_ej_anmald()
        {
            var slot = new SolverSlot("s", "stationschef", "Stationschef", null, null, null, D, Iv(9, 11), null, 1);
            var slot2 = new SolverSlot("t", "servering", "Servering", null, null, null, D, Iv(9, 11), null, 1);
            var p = new SolverPerson(1, "Nej", false, new HashSet<string>(), new List<BusyItem>(), 0);
            var bad = new SolverResult(new[]
            {
                new ProposedRow("s", 1, "Nej", "stationschef", null, null, D, slot.Interval, "x", null),
                new ProposedRow("t", 1, "Nej", "servering", null, null, D, slot2.Interval, "x", null),
                new ProposedRow("t", 1, "Nej", "servering", null, null, D, slot2.Interval, "x", null),
            }, Array.Empty<UnfilledSlot>());
            var problems = StaffingSolver.Validate(new[] { slot, slot2 }, new[] { p }, bad);
            problems.Should().Contain(x => x.Contains("inte anmält sig"));
            problems.Should().Contain(x => x.Contains("två platser samtidigt"));
            problems.Should().Contain(x => x.Contains("samma person två gånger"));
            problems.Should().Contain(x => x.Contains("fler föreslagna"));
        }

        [Fact]
        public void Validatorn_hittar_en_lucka_som_tyst_forsvunnit()
        {
            var slot = new SolverSlot("s", "servering", "Servering", null, null, null, D, Iv(9, 11), null, 2);
            var p = new SolverPerson(1, "P", true, new HashSet<string>(), new List<BusyItem>(), 0);
            var lost = new SolverResult(new[] { new ProposedRow("s", 1, "P", "servering", null, null, D, slot.Interval, "x", null) }, Array.Empty<UnfilledSlot>());
            StaffingSolver.Validate(new[] { slot }, new[] { p }, lost).Should().Contain(x => x.Contains("≠"));
        }

        // ---- jämförelse mot en naiv referenslösare ----

        [Fact]
        public void Losaren_ar_sallan_samre_an_en_naiv_girig_losare_och_bada_haller_de_harda_villkoren()
        {
            var worse = 0;
            for (var seed = 1; seed <= 300; seed++)
            {
                var inst = Make(seed);
                var real = StaffingSolver.Solve(inst.Slots, inst.People);
                var naive = Naive(inst.Slots, inst.People);
                StaffingSolver.Validate(inst.Slots, inst.People, naive).Should().BeEmpty($"fröet {seed}: referenslösaren ska också hålla villkoren");
                if (Hours(real) > Hours(naive) + 0.01) worse++;
            }
            worse.Should().BeLessThanOrEqualTo(30, "lösaren ska sällan lämna fler persontimmar obemannade än en naiv körning i given ordning");
        }

        /// <summary>
        /// Det som fortfarande saknas i PERSONTIMMAR — en lucka på tio timmar väger tio gånger en på en timme. Antal personer vore en orättvis mätare:
        /// en lösare som fyller en heldagslucka ger upp många korta, och det är ofta rätt.
        /// </summary>
        private static double Hours(SolverResult r) => r.Unfilled.Sum(u => u.Missing * u.Interval.Length.TotalHours);

        /// <summary>40 rader: luckorna i given ordning, första lediga anmälda — samma hårda villkor, ingen smart ordning.</summary>
        private static SolverResult Naive(IReadOnlyList<SolverSlot> slots, IReadOnlyList<SolverPerson> people)
        {
            var proposed = new List<ProposedRow>(); var unfilled = new List<UnfilledSlot>();
            var added = people.ToDictionary(p => p.MemberId, _ => new List<BusyItem>());
            foreach (var s in slots.Where(s => s.Missing > 0))
            {
                var left = s.Missing;
                foreach (var p in people.Where(p => p.Volunteer).OrderBy(p => p.MemberId))
                {
                    if (left == 0) break;
                    if (StaffingRules.FirstConflict(s.Interval, s.Place, p.Busy.Concat(added[p.MemberId])) is { IsHard: true }) continue;
                    proposed.Add(new ProposedRow(s.Key, p.MemberId, p.Name, s.RoleKey, s.ScopeType, s.ScopeKey, s.Date, s.Interval, "naiv", null));
                    added[p.MemberId].Add(new BusyItem(BusyKind.Assignment, s.Interval, "förslag"));
                    left--;
                }
                if (left > 0) unfilled.Add(new UnfilledSlot(s.Key, s.RoleName, s.ScopeLabel, s.Date, s.Interval, left, "ingen ledig", 0));
            }
            return new SolverResult(proposed, unfilled);
        }
    }
}
