using System.Diagnostics;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models;
using HpskSite.Services.Staffing;
using Xunit;
using Xunit.Abstractions;

namespace HpskSite.Tests
{
    /// <summary>
    /// Övningsscenariot går genom exakt samma kod som en riktig tävling (<c>PatrolPlanService.PreviewOf</c>). Här bevisas byggaren (siffrorna ger rätt skyttar), att
    /// planen håller reglerna för många slumpade scenarier, och hur lång tid det tar för ett stort SM.
    /// </summary>
    public class PatrolScenarioTests
    {
        readonly ITestOutputHelper _out;
        public PatrolScenarioTests(ITestOutputHelper o) { _out = o; }

        static readonly DateTime Day = new(2028, 6, 10);

        static PatrolScenario Small() => new() { Seed = 3, C = 54, B = 18, A = 11, R = 6, Date = "2028-06-10", Also = { new ScenarioAlso { From = "C", To = "B", N = 12 }, new ScenarioAlso { From = "C", To = "A", N = 11 }, new ScenarioAlso { From = "C", To = "R", N = 6 } } };

        static (PatrolPreview Pv, PatrolPlanService.PatrolBasis B) Run(PatrolScenario s, double minRest = 15, int budgetSeconds = 60, int? iterations = null)
        {
            var b = ScenarioBuilder.Build(s);
            var settings = new PatrolPlanSettings { PatrolSize = 6, IntervalMinutes = 15, FirstStart = Day.AddHours(9), WeaponGrouping = "Separate", MultiClassGapMinutes = 0, KeepFitting = true };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds));
            var o = new FaltPlanOptions { MinRest = minRest, WishRest = minRest, Priority = 1, Order = "auto", UseExistingGroups = false, Ct = cts.Token, Iterations = iterations };
            return (PatrolPlanService.PreviewOf(b, settings, o), b);
        }

        [Fact]
        public void The_builder_gives_the_requested_starts_per_group_and_double_shooters()
        {
            var s = Small();
            var b = ScenarioBuilder.Build(s);
            int Starts(string g) => b.Regs.Count(r => FaltPatrolAdapter.GroupOf(r.MemberClass) == g);
            Assert.Equal(54, Starts("C")); Assert.Equal(18, Starts("B")); Assert.Equal(11, Starts("A")); Assert.Equal(6, Starts("R"));
            Assert.Equal(54 + 18 + 11 + 6, b.Regs.Count);
            Assert.Equal(89 - 29, b.Regs.Select(r => r.MemberId).Distinct().Count());   // 29 dubbelstarter = 29 skyttar med två starter
            Assert.Equal(29, b.Regs.GroupBy(r => r.MemberId).Count(g => g.Count() == 2));
        }

        [Fact]
        public void The_same_scenario_gives_the_same_shooters_and_a_different_seed_a_different_mix()
        {
            string Sig(PatrolScenario s) => string.Join(";", ScenarioBuilder.Build(s).Regs.Select(r => r.MemberId + r.MemberClass));
            Assert.Equal(Sig(Small()), Sig(Small()));
            var other = Small(); other.Seed = 4;
            Assert.NotEqual(Sig(Small()), Sig(other));
        }

        [Fact]
        public void The_builder_sets_followers_and_wishes_on_the_registrations_like_real_registrations_carry_them()
        {
            var s = Small(); s.Followers = 8; s.WishEarly = 6; s.WishLate = 4;
            Assert.Null(ScenarioBuilder.Validate(s));
            var b = ScenarioBuilder.Build(s);
            var followers = b.Regs.Where(r => r.JoinMemberId != null).ToList();
            Assert.Equal(8, followers.Count);
            foreach (var f in followers)
            {
                var group = FaltPatrolAdapter.GroupOf(f.MemberClass);
                var partner = b.Regs.FirstOrDefault(r => r.MemberId == f.JoinMemberId && FaltPatrolAdapter.GroupOf(r.MemberClass) == group);
                Assert.NotNull(partner);                       // kamraten är anmäld i samma vapengrupp
                Assert.Equal(f.MemberClub, partner!.MemberClub);   // och tävlar för samma klubb
            }
            Assert.All(followers.GroupBy(f => (f.JoinMemberId, FaltPatrolAdapter.GroupOf(f.MemberClass))), g => Assert.True(g.Count() <= 2));
            Assert.Equal(6, b.Regs.Where(r => StartPreference.Normalize(r.StartPreference) == StartPreference.Early).Select(r => r.MemberId).Distinct().Count());
            Assert.Equal(4, b.Regs.Where(r => StartPreference.Normalize(r.StartPreference) == StartPreference.Late).Select(r => r.MemberId).Distinct().Count());
            var tooMany = Small(); tooMany.Followers = 500;
            Assert.NotNull(ScenarioBuilder.Validate(tooMany));
        }

        [Fact]
        public void The_plan_for_a_scenario_with_followers_keeps_every_requirement_or_names_why_not()
        {
            var s = Small(); s.Followers = 8; s.WishEarly = 6; s.WishLate = 4;
            var (pv, b) = Run(s, iterations: 20000);
            var plan = pv.Plan;
            Assert.Equal(8, plan.LinksTotal + plan.LinkIssues.Count);   // varje krav är antingen med i planen eller redovisat med namn
            Assert.True(plan.LinksMet == plan.LinksTotal || plan.LinkIssues.Count > 0, "ett krav höll inte och ingen sa något");
            // Oberoende kontroll ur patrullerna: varje följare ligger i samma patrull som sin kamrat (i den klassen).
            var at = plan.Patrols.SelectMany(p => p.Members.Where(m => !m.IsDns).Select(m => (m.MemberId, Group: FaltPatrolAdapter.GroupOf(m.ShootingClass), p.PatrolNumber))).ToList();
            var held = b.Regs.Where(r => r.JoinMemberId != null).Count(f =>
            {
                var g = FaltPatrolAdapter.GroupOf(f.MemberClass);
                var mine = at.FirstOrDefault(x => x.MemberId == f.MemberId && x.Group == g); var theirs = at.FirstOrDefault(x => x.MemberId == f.JoinMemberId && x.Group == g);
                return mine.PatrolNumber != 0 && mine.PatrolNumber == theirs.PatrolNumber;
            });
            Assert.Equal(plan.LinksMet, held);
            Assert.True(plan.WishesTotal > 0 && plan.WishesMet <= plan.WishesTotal);
        }

        [Fact]
        public void Practice_names_are_invented_and_never_a_member_name()
        {
            var b = ScenarioBuilder.Build(Small());
            Assert.All(b.Regs, r => { Assert.StartsWith("Skytt ", r.MemberName); Assert.StartsWith("Övningsklubben", r.MemberClub); });
        }

        [Fact]
        public void The_practice_page_has_no_way_to_write_to_a_competition()
        {
            // Övningssidan ligger i samma kontroller som den riktiga, men kontrollern får aldrig röra skrivvägen: den injicerar varken PatrolPlanService eller databasen.
            var src = File.ReadAllText(FindSource("Controllers", "PatrolGeneratorController.cs"));
            Assert.DoesNotContain("PatrolPlanService", src); Assert.DoesNotContain("IScopeProvider", src); Assert.DoesNotContain("ApplyAsync", src);
            // Och skrivande endpoints kräver ett tävlings-id: ett anrop utan det (övning) avvisas av ägarkontrollen.
            var api = File.ReadAllText(FindSource("Controllers", "StaffingController.PatrolPlan.cs"));
            Assert.Contains("HasOwnerKey(request.CompetitionId)", api);
        }

        [Fact]
        public void The_throttle_limits_each_member_and_the_number_of_simultaneous_runs()
        {
            var t = new PatrolPracticeThrottle(); var now = new DateTime(2028, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var held = new List<IDisposable>();
            for (var i = 0; i < PatrolPracticeThrottle.MaxConcurrent; i++) held.Add(t.TryEnter(100 + i, now, out _)!);
            Assert.Null(t.TryEnter(999, now, out var busy)); Assert.NotNull(busy);          // alla platser upptagna
            held.ForEach(h => h.Dispose());
            for (var i = 0; i < PatrolPracticeThrottle.PerMemberPerMinute; i++) t.TryEnter(7, now, out _)!.Dispose();
            Assert.Null(t.TryEnter(7, now, out var many)); Assert.NotNull(many);            // en medlem har provat för många gånger
            Assert.NotNull(t.TryEnter(8, now, out _));                                       // en annan medlem påverkas inte
            Assert.NotNull(t.TryEnter(7, now.AddMinutes(2), out _));                         // och minuten går
        }

        [Fact]
        public void A_scenario_can_be_read_back_from_its_json()
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(Small());
            var back = ScenarioBuilder.Parse(json)!;
            Assert.Equal(ScenarioBuilder.Hash(Small()), ScenarioBuilder.Hash(back));
            Assert.Null(ScenarioBuilder.Parse("inte json")); Assert.Null(ScenarioBuilder.Parse(new string('x', 5000)));
        }

        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(ScenarioBuilder.MaxStarts, 1, 0, 0)]
        public void Too_big_or_empty_scenarios_are_refused(int c, int b, int a, int r)
        {
            var s = new PatrolScenario { C = c, B = b, A = a, R = r };
            Assert.NotNull(ScenarioBuilder.Validate(s));
            Assert.Throws<ArgumentException>(() => ScenarioBuilder.Build(s));
        }

        [Fact]
        public void A_scenario_never_reaches_a_database_type()
        {
            var src = File.ReadAllText(FindSource("Services", "Staffing", "PatrolScenario.cs"));
            Assert.DoesNotContain("IScopeProvider", src); Assert.DoesNotContain("IMemberService", src); Assert.DoesNotContain("IContentService", src); Assert.DoesNotContain("ApplyAsync", src);
        }

        [Fact]
        public void The_14266_sized_scenario_is_feasible_and_places_every_start_once()
        {
            var (pv, b) = Run(Small());
            var placed = pv.Plan.Patrols.SelectMany(p => p.Members.Where(m => !m.IsDns).Select(m => m.MemberId + "|" + ShootingClasses.ToCanonicalName(m.ShootingClass))).ToList();
            Assert.Equal(b.Regs.Count, placed.Count);
            Assert.Equal(placed.Count, placed.Distinct().Count());
            Assert.True(pv.Plan.Metrics!.Feasible, "metrics: " + System.Text.Json.JsonSerializer.Serialize(pv.Plan.Metrics));
        }

        [Fact]
        public void Random_scenarios_keep_the_hard_rules_or_say_they_cannot()
        {
            var rnd = new Random(2026);
            for (var seed = 1; seed <= 25; seed++)
            {
                var s = new PatrolScenario { Seed = seed, C = rnd.Next(6, 60), B = rnd.Next(0, 24), A = rnd.Next(0, 18), R = rnd.Next(0, 8), Date = "2028-06-10", Stations = rnd.Next(4, 10) };
                var cb = rnd.Next(0, Math.Min(s.B, s.C) + 1);
                s.Also.Add(new ScenarioAlso { From = "C", To = "B", N = cb });
                s.Also.Add(new ScenarioAlso { From = "C", To = "A", N = rnd.Next(0, Math.Min(s.A, s.C - cb) + 1) });
                if (rnd.Next(2) == 0) s.Jobs.Add(new ScenarioJob { N = rnd.Next(1, 5), From = "09:00", To = "12:00" });
                s.Reserves = rnd.Next(0, 3);
                var (pv, b) = Run(s, iterations: 20000);
                var m = pv.Plan.Metrics!;
                var placed = pv.Plan.Patrols.SelectMany(p => p.Members.Where(x => !x.IsDns)).Select(x => x.MemberId + "|" + ShootingClasses.ToCanonicalName(x.ShootingClass)).ToList();
                var expected = b.Regs.Where(r => !b.Reserves.Contains(r.MemberId)).Select(r => r.MemberId + "|" + ShootingClasses.ToCanonicalName(r.MemberClass)).ToList();
                Assert.True(expected.OrderBy(x => x).SequenceEqual(placed.OrderBy(x => x)), $"seed {seed}: varje start ska placeras exakt en gång ({placed.Count} av {expected.Count})");
                Assert.All(pv.Plan.Patrols, p => Assert.InRange(p.Members.Count(x => !x.IsDns), 1, 6));
                // Går det ihop ska varken vila eller krockar vara brutna; annars ska planen SÄGA att det inte går.
                if (m.Feasible)
                {
                    Assert.Equal(0, m.UnderMin); Assert.Equal(0, m.Collisions);
                    // Oberoende kontroll ur patrullerna (inte ur motorns egna mått): tiden mellan en starts slut (start + rundan) och nästa start är minst minsta vila.
                    var round = pv.Plan.Timeline!.RoundMin;
                    foreach (var g in pv.Plan.Patrols.SelectMany(p => p.Members.Where(x => !x.IsDns).Select(x => (x.MemberId, p.StartTime))).GroupBy(t => t.MemberId))
                    {
                        var times = g.Select(t => t.StartTime!.Value).OrderBy(t => t).ToList();
                        for (var i = 1; i < times.Count; i++) Assert.True((times[i] - times[i - 1]).TotalMinutes - round >= 15 - 0.01, $"seed {seed}: skytt {g.Key} har {(times[i] - times[i - 1]).TotalMinutes - round:0.#} min vila");
                    }
                }
                else Assert.True(m.Hard > 0 || m.UnderMin > 0 || m.Collisions > 0 || m.OverMinutes > 0, $"seed {seed}: 'går inte' utan skäl");
            }
        }

        [Fact]
        public void Validate_rejects_doubles_that_do_not_fit_instead_of_clipping_them()
        {
            // Stefans inmatning: C 54, B 11, A 45, R 19 med C+B 25 (B har bara 11) och C+A + A+C (samma par två gånger).
            var s = new PatrolScenario { C = 54, B = 11, A = 45, R = 19 };
            s.Also.Add(new ScenarioAlso { From = "C", To = "B", N = 25 });
            Assert.Contains("högst 11", ScenarioBuilder.Validate(s)!);
            s.Also.Clear();
            s.Also.Add(new ScenarioAlso { From = "C", To = "A", N = 40 });
            s.Also.Add(new ScenarioAlso { From = "A", To = "C", N = 40 });   // samma par i omvänd riktning: 80 dubbla i grupp A med 45 starter
            Assert.Contains("Klass C har 54 starter men 80", ScenarioBuilder.Validate(s)!);   // C och A är överbokade; C kommer först
            s.Also.Clear();
            s.Also.Add(new ScenarioAlso { From = "C", To = "A", N = 30 });
            s.Also.Add(new ScenarioAlso { From = "C", To = "R", N = 11 });
            s.Also.Add(new ScenarioAlso { From = "A", To = "R", N = 8 });
            Assert.Null(ScenarioBuilder.Validate(s));   // det som ryms går igenom
            var (_, persons) = ScenarioBuilder.Shooters(s);
            Assert.Equal(54 + 11 + 45 + 19 - 49, persons);
        }

        [Fact]
        public void Shooters_in_three_or_four_groups_are_one_person_with_several_starts()
        {
            var s = new PatrolScenario { C = 20, B = 10, A = 8, R = 6 };
            s.Also.Add(new ScenarioAlso { From = "C", To = "B", N = 4 });
            s.Multi.Add(new ScenarioMulti { Groups = "CBA", N = 2 });
            s.Multi.Add(new ScenarioMulti { Groups = "RABC", N = 3 });   // ordningen i koden spelar ingen roll
            Assert.Null(ScenarioBuilder.Validate(s));
            var (starts, persons) = ScenarioBuilder.Shooters(s);
            Assert.Equal(44, starts.Count);
            Assert.Equal(44 - 4 - 2 * 2 - 3 * 3, persons);   // par: en start extra, tre grupper: två extra, fyra grupper: tre extra
            Assert.Equal(3, starts.GroupBy(x => x.Id).Count(g => g.Count() == 4));
            Assert.Equal(2, starts.GroupBy(x => x.Id).Count(g => g.Count() == 3));
            // För många: grupp R har 6 starter, 7 vore för många.
            s.Multi[1].N = 7;
            Assert.NotNull(ScenarioBuilder.Validate(s));
            s.Multi[1].N = 3; s.Multi.Add(new ScenarioMulti { Groups = "CB", N = 1 });   // två grupper hör till Also, inte Multi
            Assert.Contains("Tre eller fyra", ScenarioBuilder.Validate(s)!);
        }

        [Fact]
        public void A_scenario_with_shooters_in_four_groups_still_places_every_start_once_and_never_twice_in_a_patrol()
        {
            var s = new PatrolScenario { Seed = 5, C = 24, B = 12, A = 10, R = 8, Date = "2028-06-10", Stations = 6 };
            s.Multi.Add(new ScenarioMulti { Groups = "CBAR", N = 3 });
            s.Multi.Add(new ScenarioMulti { Groups = "CBA", N = 2 });
            var (pv, b) = Run(s, iterations: 20000);
            var placed = pv.Plan.Patrols.SelectMany(p => p.Members.Where(x => !x.IsDns)).Select(x => x.MemberId + "|" + ShootingClasses.ToCanonicalName(x.ShootingClass)).ToList();
            var expected = b.Regs.Select(r => r.MemberId + "|" + ShootingClasses.ToCanonicalName(r.MemberClass)).ToList();
            Assert.True(expected.OrderBy(x => x).SequenceEqual(placed.OrderBy(x => x)), $"varje start ska placeras exakt en gång ({placed.Count} av {expected.Count})");
            Assert.All(pv.Plan.Patrols, p => Assert.Equal(p.Members.Count(x => !x.IsDns), p.Members.Where(x => !x.IsDns).Select(x => x.MemberId).Distinct().Count()));
        }

        [Fact]
        public void Validate_rejects_more_job_holders_reserves_and_early_than_there_are_people()
        {
            var s = new PatrolScenario { C = 10 };
            s.Jobs.Add(new ScenarioJob { N = 8, AllDay = true });
            s.Reserves = 3;
            Assert.Contains("fler än personerna", ScenarioBuilder.Validate(s)!);
            s.Reserves = 2;
            Assert.Null(ScenarioBuilder.Validate(s));
        }

        /// <summary>Mäter hur lång tid motorn tar för ett stort SM. Körs bara med HPSK_PERF=1 (långsamt); skriver tiderna i testutdata.</summary>
        [Fact]
        public void Perf_report_for_large_scenarios()
        {
            if (Environment.GetEnvironmentVariable("HPSK_PERF") != "1") return;
            foreach (var total in new[] { 300, 800, 1500 })
            {
                var s = new PatrolScenario { Seed = 1, C = total * 55 / 100, B = total * 20 / 100, A = total * 15 / 100, R = total * 10 / 100, Stations = 12, Date = "2028-06-10" };
                s.Also.Add(new ScenarioAlso { From = "C", To = "B", N = s.B / 2 });
                s.Also.Add(new ScenarioAlso { From = "C", To = "A", N = s.A / 2 });
                var sw = Stopwatch.StartNew();
                string res;
                try { var (pv, _) = Run(s, minRest: 45, budgetSeconds: 120); res = $"patruller={pv.Plan.Patrols.Count} feasible={pv.Plan.Metrics?.Feasible} hard={pv.Plan.Metrics?.Hard}"; }
                catch (OperationCanceledException) { res = "AVBRUTEN efter 120 s"; }
                _out.WriteLine($"PERF starter={total} tid={sw.Elapsed.TotalSeconds:0.0}s {res}");
            }
        }

        static string FindSource(params string[] parts) => FindSourceFrom(ThisFile(), parts);
        static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string f = "") => f;
        static string FindSourceFrom(string thisFile, string[] parts)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
            while (dir != null)
            {
                var p = Path.Combine(new[] { dir.FullName, "src", "HpskSite" }.Concat(parts).ToArray());
                if (File.Exists(p)) return p;
                p = Path.Combine(new[] { dir.FullName, "HpskSite" }.Concat(parts).ToArray());
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(string.Join("/", parts));
        }
    }
}
