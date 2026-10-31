using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Engine;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Inställningar som bara patrullgeneratorn (den avancerade vägen) har, utöver <see cref="PatrolPlanSettings"/>: vila, dagens slut, prioritet, uppdelning, mästerskap,
    /// glidmarginal. Förvalen är motorns. <c>Order</c> null = ta uppdelningen ur <see cref="PatrolPlanSettings.WeaponGrouping"/>.
    /// </summary>
    public sealed class FaltPlanOptions
    {
        public double? MinRest, WishRest;            // minuter; null = MultiClassGapMinutes
        public double? CloseMinutes;                 // dagens slut i minuter från midnatt; null = första start + 16 h
        public int Priority = 1;                     // 0 kort dag, 1 balans, 2 vila
        public string? Order;                        // "auto" | "sep" | "ar" | "cbar" | "all"
        public string? Champion;                     // mästerskapsgruppens kod (C/B/A/R) som skjuter först
        public int GlideEvery, GlideMinutes;
        public double RoundExtraSeconds = 60;
        public string? StationConfigJson;            // tävlingens stationskonfiguration (kopian i tävlingen); ger riktiga stationer och skjuttider per grupp
        public double? RoundMinutes;                 // rundtid per patrull; null = 10 min per station (uppmätt på små tävlingar), minst skjutfönstret
        public bool UseExistingGroups = true;        // gruppernas egen första start, avstånd och storlek ur dagens startlista
        public int? Iterations;                      // deterministisk budget
        public double? PauseBlockMin, PauseExtMin;     // paus mellan block (min mellan blockens patruller) och hur mycket den får förlängas; null = tiden mellan patruller / 30
        public bool Day2;                              // tävlingen går över två dagar
        public double? Open2Min, Close2Min;            // dag 2: första start och slut (minuter från midnatt)
        public int DayBreak = 1;                       // dag 2 börjar med block nummer (1 = andra blocket)
        public bool ArTogether = true;                 // A och R skjuter i samma omgång
        public int PreCount;                           // förstartspatruller (bara funktionärer)
        public double? PreStartMin;                    // förstartens första start; null = en timme före första start
        public CancellationToken Ct;
    }

    /// <summary>
    /// Adaptern mellan databasens underlag (anmälningar, låsta/DNS-platser, uppdrag, reserver) och den rena motorn <see cref="FaltPatrolEngine"/>, och tillbaka till
    /// <see cref="PatrolPlan"/> (samma form som förhandsvisningen och skrivvägen redan använder). Ingen databas, inget scope: ren funktion, enhetstestbar.
    /// <para>Tid = minuter från tävlingsdagens midnatt (dag 2 = +1440), med sekunder som bråkdel, så att en befintlig starttid med sekunder hittar sitt lås.</para>
    /// <para>Skalet äger spärren (publicerad, resultat): adaptern skickar alltid <c>Published=false</c> till motorn.</para>
    /// </summary>
    public static class FaltPatrolAdapter
    {
        static double Mins(DateTime t, DateTime day) => (t - day).TotalSeconds / 60.0;
        static DateTime At(DateTime day, double minutes) => day.AddSeconds(Math.Round(minutes * 60));
        static string Key(int memberId, string cls) => memberId + "|" + ShootingClasses.ToCanonicalName(cls);

        /// <summary>Motorns grupp (C/B/A/R, A_Opt, annars klassens id) för en skyttes klass.</summary>
        public static string GroupOf(string cls)
        {
            var code = ShootingClasses.GetWeaponClassCode(cls);
            return string.IsNullOrEmpty(code) ? (cls ?? "") : code;
        }

        /// <summary>Banans stationer ur tävlingens konfiguration: nummer, namn och skjuttid (sek) per vapengrupp. Särskjutningsstationer räknas inte; en grupp utan egen tid lånar en annan grupps tid.</summary>
        public static List<(int Number, string? Name, Dictionary<string, double> Sec)> StationsFromConfig(string? json)
        {
            var result = new List<(int, string?, Dictionary<string, double>)>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            FaltskytteCompetitionConfig cfg;
            try { cfg = FaltskytteConfigParser.Parse(json); } catch { return result; }
            var byGroup = new Dictionary<string, Dictionary<int, double>>(); var names = new Dictionary<int, string?>(); var nums = new SortedSet<int>();
            foreach (var g in FaltPatrolEngine.Groups)
            {
                var key = cfg.WeaponConfigs.ContainsKey(g) ? g : g == "A" && cfg.WeaponConfigs.ContainsKey("A_Opt") ? "A_Opt" : null;
                if (key == null) continue;
                var m = new Dictionary<int, double>();
                foreach (var s in cfg.WeaponConfigs[key].Stations)
                    if (!s.IsShootOffOnly && s.Station > 0 && s.ShootingTimeSec > 0) { m[s.Station] = s.ShootingTimeSec; nums.Add(s.Station); if (!names.ContainsKey(s.Station) || string.IsNullOrWhiteSpace(names[s.Station])) names[s.Station] = s.Name; }
                byGroup[g] = m;
            }
            foreach (var n in nums)
            {
                var own = new Dictionary<string, double>();
                foreach (var g in FaltPatrolEngine.Groups) if (byGroup.TryGetValue(g, out var m) && m.TryGetValue(n, out var t)) own[g] = t;
                var any = own.Values.FirstOrDefault();
                var sec = new Dictionary<string, double>();
                foreach (var g in FaltPatrolEngine.Groups) sec[g] = own.TryGetValue(g, out var t) ? t : any;
                result.Add((n, names.GetValueOrDefault(n), sec));
            }
            return result;
        }

        public sealed record LinkSetupResult(Dictionary<string, string> Together, Dictionary<string, int> Wish, List<string> Problems);

        /// <summary>
        /// Krav "gå i samma patrull som" och mjuka önskemål om starttid ur anmälningarna (en rad per klassanmälan). Kravet gäller vapengruppen (alla C är samma grupp).
        /// Ett krav som inte kan tillämpas (kamraten är DNS, reserv eller inte längre anmäld i gruppen) lämnas utanför motorn och redovisas med namn i <see cref="LinkSetupResult.Problems"/>:
        /// generatorn bryter aldrig tyst och ändrar aldrig anmälan. Önskemål gäller bara om alla klassanmälningar i samma start vill samma sak.
        /// </summary>
        public static LinkSetupResult LinkSetup(IReadOnlyList<CompetitionRegistration> regs, ISet<int> reserves, ISet<string> dnsKeys)
        {
            var together = new Dictionary<string, string>(); var wish = new Dictionary<string, int>(); var problems = new List<string>();
            string NameOf(int id) => regs.FirstOrDefault(r => r.MemberId == id)?.MemberName ?? $"Medlem {id}";
            var startKeys = regs.Select(r => new Start(r.MemberId, GroupOf(r.MemberClass)).Key).ToHashSet();
            foreach (var g in regs.GroupBy(r => new Start(r.MemberId, GroupOf(r.MemberClass)).Key))
            {
                var ranks = g.Select(r => HpskSite.Models.StartPreference.Rank(r.StartPreference)).Where(x => x != HpskSite.Models.StartPreference.RankNone).Distinct().ToList();
                if (ranks.Count == 1 && !dnsKeys.Contains(g.Key)) wish[g.Key] = ranks[0] == HpskSite.Models.StartPreference.RankEarly ? -1 : 1;
            }
            var seen = new HashSet<string>();
            foreach (var r in regs.Where(r => r.JoinMemberId is > 0))
            {
                var group = GroupOf(r.MemberClass); var other = r.JoinMemberId!.Value;
                var me = new Start(r.MemberId, group).Key; var you = new Start(other, group).Key;
                if (!seen.Add(me + ">" + you)) continue;
                var what = $"{NameOf(r.MemberId)} ska gå i samma patrull som {NameOf(other)} i klass {group}";
                if (reserves.Contains(r.MemberId) || reserves.Contains(other)) { problems.Add(what + ", men en av dem är reserv utan fast start. Kravet följs inte."); continue; }
                if (dnsKeys.Contains(me) || dnsKeys.Contains(you)) { problems.Add(what + ", men en av dem är DNS. Kravet följs inte."); continue; }
                if (!startKeys.Contains(you)) { problems.Add(what + $", men {NameOf(other)} är inte anmäld i klassen längre. Kravet följs inte."); continue; }
                if (!together.ContainsKey(me)) together[me] = you; else if (!together.ContainsKey(you)) together[you] = me;   // två kamrater i samma grupp (dubbel C): den andra läggs som omvänd kant, klumpen blir densamma
            }
            return new LinkSetupResult(together, wish, problems);
        }

        /// <summary>Hur det gick: krav som höll och inte höll (med namn), och önskemål som uppfylldes. Ett önskemål räknas bara på personens första (tidig) respektive sista (sen) start; uppfyllt = i första respektive sista tredjedelen av gruppens patruller.</summary>
        static (int LinksTotal, int LinksMet, List<string> LinkIssues, int WishesTotal, int WishesMet, List<string> WishesMissed) Outcome(PlanResult res, LinkSetupResult setup, IReadOnlyList<CompetitionRegistration> regs, DateTime day)
        {
            string NameOf(int id) => regs.FirstOrDefault(r => r.MemberId == id)?.MemberName ?? $"Medlem {id}";
            var at = new Dictionary<string, int>();
            for (var i = 0; i < res.Patrols.Count; i++) foreach (var z in res.Patrols[i].P) at[z.Key] = i;
            int linksMet = 0, linksTotal = 0; var issues = new List<string>(setup.Problems);
            foreach (var kv in setup.Together)
            {
                if (!at.TryGetValue(kv.Key, out var a) || !at.TryGetValue(kv.Value, out var b)) continue;
                linksTotal++;
                if (a == b) linksMet++;
                else
                {
                    var s1 = Start0(kv.Key); var s2 = Start0(kv.Value);
                    issues.Add($"{NameOf(s1.Id)} och {NameOf(s2.Id)} kunde inte placeras i samma patrull i klass {s1.G} (patrull {a + 1} och {b + 1}).");
                }
            }
            int wTotal = 0, wMet = 0; var missed = new List<string>();
            var startsOf = res.Patrols.SelectMany((q, i) => q.P.Select(z => (z, i))).GroupBy(t => t.z.Id).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var kv in setup.Wish)
            {
                if (!at.TryGetValue(kv.Key, out var idx)) continue;
                var s = Start0(kv.Key);
                var own = startsOf[s.Id];
                var first = own.Min(t => res.Patrols[t.i].S); var last = own.Max(t => res.Patrols[t.i].S);
                var mine = res.Patrols[idx].S;
                if (kv.Value < 0 ? Math.Abs(mine - first) > 1e-9 : Math.Abs(mine - last) > 1e-9) continue;   // ett önskemål om tidig start räknas bara på första starten, sen på sista
                var times = res.Patrols.Where(q => q.P.Any(z => z.G == s.G)).Select(q => q.S).Distinct().OrderBy(t => t).ToList();
                if (times.Count < 2) continue;
                wTotal++;
                var pos = times.IndexOf(mine) / (double)(times.Count - 1);
                if (kv.Value < 0 ? pos <= 1.0 / 3 + 1e-9 : pos >= 2.0 / 3 - 1e-9) wMet++;
                else missed.Add($"{NameOf(s.Id)} ({s.G}) ville starta {(kv.Value < 0 ? "tidigt" : "sent")} men startar {At(day, mine):HH:mm}" + (own.Count > 1 ? ", eftersom hen skjuter flera klasser" : ""));
            }
            return (linksTotal, linksMet, issues, wTotal, wMet, missed);
        }

        static Start Start0(string key) { var i = key.IndexOf('|'); return new Start(int.Parse(key[..i], System.Globalization.CultureInfo.InvariantCulture), key[(i + 1)..]); }

        static string Label(IEnumerable<string> groups) => string.Join("+", groups.Distinct().OrderBy(g => Array.IndexOf(FaltPatrolEngine.Groups, g) is var i and >= 0 ? i : 99).ThenBy(g => g, StringComparer.Ordinal));

        public static (PlanParams Params, List<EPatrol> Input, Dictionary<string, CompetitionRegistration> RegByKey) Build(
            IReadOnlyList<CompetitionRegistration> regs, IReadOnlyCollection<LockedPlacement> locked, PatrolConstraints constraints, ISet<int> reserves,
            PatrolPlanSettings settings, IReadOnlyList<PatrolGroupSpec>? groups, FaltPlanOptions? options, DateTime day)
        {
            options ??= new FaltPlanOptions();
            var open = Mins(settings.FirstStart ?? day.AddHours(9), day);
            var shootWindow = Math.Max(1, constraints.Shoot.BeforeMinutes + constraints.Shoot.DurationMinutes + constraints.Shoot.AfterMinutes);
            var rest = options.MinRest ?? settings.MultiClassGapMinutes;
            // Stationer och skjuttider per grupp ur konfigurationen (samma tolkning som prototypens stationsFromConfig); utan konfiguration en enda station.
            var stations = StationsFromConfig(options.StationConfigJson);
            var nSt = Math.Max(1, stations.Count);
            // Rundtid utan angivet värde: 10 min per station när banans stationer är kända (uppmätt på små tävlingar; SM 2024 var 110 min). Tävlingens tid per start (förberedelse+skjutning+efteråt, ofta ~165 min) är
            // funktionärens upptagna tid, inte rundan, och användes förut som rundtid: det gjorde pausen mellan blocken flera timmar för lång.
            var round = options.RoundMinutes ?? (stations.Count > 0 ? stations.Count * 10.0 : shootWindow);
            var p = new PlanParams
            {
                Interval = settings.IntervalMinutes, Size = settings.PatrolSize, Open = open, Close = options.CloseMinutes ?? Math.Min(2880, open + 960),
                N = nSt, R = round, Vila = rest, VilaW = Math.Max(rest, options.WishRest ?? rest), Prio = Math.Clamp(options.Priority, 0, 2),
                Order = options.Order ?? settings.WeaponGrouping switch { "MixAll" => "all", "CombineAR" => "ar", _ => "sep" },
                Champ = options.Champion, GlideEvery = options.GlideEvery, GlideMin = options.GlideMinutes, RoundExtra = options.RoundExtraSeconds,
                NextFirst = true, PauseBlock = options.PauseBlockMin ?? settings.IntervalMinutes, PauseExt = options.PauseExtMin ?? 30, Day2 = options.Day2, Open2 = options.Open2Min ?? 540, Close2 = options.Close2Min ?? 900, DayBreak = Math.Max(1, options.DayBreak), ArTogether = options.ArTogether, Pre = Math.Max(0, options.PreCount), PreStart = options.PreStartMin ?? (open - 60), JobBuf = constraints.Shoot.BeforeMinutes, Iters = options.Iterations, Ct = options.Ct,
                St = stations.Count > 0 ? stations.Select(x => new StationCfg { Sec = x.Sec }).ToList() : new List<StationCfg> { new() },
                Early = new HashSet<int>(constraints.Early), Reserves = new HashSet<int>(reserves),
            };
            if (p.Order == "auto" && p.VilaW < p.Vila) p.VilaW = p.Vila;
            // Uppdragen: upptagen tid i minuter från dagens midnatt.
            p.Jobs = constraints.Jobs.ToDictionary(kv => kv.Key, kv => kv.Value.Select(j => (Mins(j.Start, day), Mins(j.End, day))).ToList());
            // Gruppernas egen första start, avstånd och storlek (en körning per grupp i dagens startlista).
            if (options.UseExistingGroups && groups is { Count: > 0 })
            {
                p.GroupStart = new(); p.GroupInterval = new(); p.GroupSize = new();
                foreach (var g in groups)
                    foreach (var code in g.WeaponCodes)
                    {
                        var c = code == "A_Opt" ? "A" : code;
                        if (!p.GroupStart.ContainsKey(c) || Mins(g.FirstStart, day) < p.GroupStart[c]) p.GroupStart[c] = Mins(g.FirstStart, day);
                        p.GroupInterval[c] = Math.Max(p.GroupInterval.GetValueOrDefault(c), g.IntervalMinutes);
                        p.GroupSize[c] = p.GroupSize.TryGetValue(c, out var s0) ? Math.Min(s0, g.PatrolSize) : g.PatrolSize;
                    }
            }
            var regByKey = new Dictionary<string, CompetitionRegistration>();
            foreach (var r in regs) regByKey.TryAdd(Key(r.MemberId, r.MemberClass), r);
            var lockedByKey = new Dictionary<string, LockedPlacement>();
            foreach (var l in locked) lockedByKey[Key(l.MemberId, l.ShootingClass)] = l;
            // Motorns indata: en patrull som bär ALLA starter (positionen i listan spelar ingen roll; motorn sorterar kanoniskt). Låsta/DNS får sin tid via p.Locked/p.Dns.
            var starts = new List<Start>(); var seen = new HashSet<string>();
            p.Locked = new(); p.Dns = new();
            foreach (var r in regs)
            {
                var k = Key(r.MemberId, r.MemberClass);
                var st = new Start(r.MemberId, GroupOf(r.MemberClass));
                if (!seen.Add(st.Key)) continue;
                starts.Add(st);
                if (lockedByKey.TryGetValue(k, out var l))
                {
                    if (l.IsDns) p.Dns[st.Key] = Mins(l.StartTime, day); else p.Locked[st.Key] = Mins(l.StartTime, day);
                }
            }
            var link = LinkSetup(regs, reserves, p.Dns.Keys.ToHashSet());
            p.Together = link.Together; p.Wish = link.Wish;
            var input = new List<EPatrol> { new() { N = 1, S = open, E = open + shootWindow, P = starts } };
            return (p, input, regByKey);
        }

        public static PatrolPlan Plan(IReadOnlyList<CompetitionRegistration> regs, IReadOnlyCollection<LockedPlacement> locked, PatrolConstraints constraints, ISet<int> reserves,
            PatrolPlanSettings settings, IReadOnlyList<PatrolGroupSpec>? groups, FaltPlanOptions? options, DateTime day, out PlanResult? engineResult) =>
            PlanKeep(regs, locked, null, constraints, reserves, settings, groups, options, day, out engineResult, out _);

        /// <summary>
        /// Som <see cref="Plan"/>, men med dagens startlista (<paramref name="existing"/>, alla placerade inklusive DNS) som underlag för "behåll de som passar": motorns RebuildKeep
        /// låser varje start som inte bryter en hård regel (minsta vila, uppdrag, tak, förstart) och placerar bara de övriga och de nya. <paramref name="keptFitting"/> = antal som behölls.
        /// Är KeepFitting av, eller finns ingen lista, byggs listan från noll.
        /// </summary>
        public static PatrolPlan PlanKeep(IReadOnlyList<CompetitionRegistration> regs, IReadOnlyCollection<LockedPlacement> locked, IReadOnlyCollection<LockedPlacement>? existing,
            PatrolConstraints constraints, ISet<int> reserves, PatrolPlanSettings settings, IReadOnlyList<PatrolGroupSpec>? groups, FaltPlanOptions? options, DateTime day,
            out PlanResult? engineResult, out int keptFitting)
        {
            keptFitting = 0; options ??= new FaltPlanOptions();
            var (p, input, regByKey) = Build(regs, locked, constraints, reserves, settings, groups, options, day);
            var lockedByKey = new Dictionary<string, LockedPlacement>();
            foreach (var l in locked) lockedByKey[Key(l.MemberId, l.ShootingClass)] = l;
            var regKeys = regByKey.Keys.ToHashSet();
            var dropped = lockedByKey.Where(kv => !regKeys.Contains(kv.Key)).Select(kv => kv.Value.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            engineResult = null;
            if (input[0].P.Count == 0) return new PatrolPlan { LockedDropped = dropped };
            PlanResult res = null!;
            // Går det inte ihop på grund av för kort vila (inte tidsbrist eller krockar) får blocken extra platser sist, så att skyttar med flera starter har något att vänta på. Minsta antal som gör att det går vinner.
            for (var extra = 0; extra <= 10; extra++)
            {
            p.ExtraSlots = extra;
            if (settings.KeepFitting && existing is { Count: > 0 })
            {
                var cur = existing.GroupBy(l => l.StartTime).OrderBy(g => g.Key).Select((g, i) => new EPatrol
                {
                    N = i + 1, S = Mins(g.Key, day), E = Mins(g.Key, day) + p.R, P = g.Select(l => new Start(l.MemberId, GroupOf(l.ShootingClass))).ToList(),
                }).ToList();
                var regStarts = input[0].P.Select(x => x.Key).ToHashSet();
                var remove = cur.SelectMany(q => q.P).Select(x => x.Key).Where(k => !regStarts.Contains(k)).ToHashSet();
                var have = cur.SelectMany(q => q.P).Select(x => x.Key).ToHashSet();
                var add = input[0].P.Where(x => !have.Contains(x.Key) && !reserves.Contains(x.Id)).ToList();
                var k2 = FaltPatrolEngine.RebuildKeep("fält", cur, p, add, remove);
                res = k2.G; keptFitting = k2.Kept;
                // Att behalla ar en onskan, reglerna ar hart: gar det inte att halla reglerna med det som behalls provas en lista fran noll, och den vinner om den har farre brott.
                if (res.Infeasible || (res.Q?.Hard ?? 0) > 0)
                {
                    var scratch = FaltPatrolEngine.Generate("falt", input, p);
                    if ((scratch.Q?.Hard ?? int.MaxValue) < (res.Q?.Hard ?? int.MaxValue)) { res = scratch; keptFitting = 0; }
                }
            }
            else res = FaltPatrolEngine.Generate("fält", input, p);
            // Extra platser hjälper både mot för kort vila och mot ett krav "samma patrull" som inte fick plats; de provas bara när inget annat hårt fel (krock) är orsaken.
            if (res.Q == null || res.Q.Hard == 0 || (res.Q.Under == 0 && res.Q.Link == 0) || res.Q.Coll > 0 || res.Invalid != null || res.Blocked != null) break;
            }
            engineResult = res;
            if (res.Invalid != null) throw new ArgumentException(string.Join("; ", res.Invalid));
            // Start (id|grupp) -> registreringens/låsets klass, för att skriva tillbaka rätt klass.
            string ClassOf(Start s)
            {
                foreach (var r in regs) if (r.MemberId == s.Id && GroupOf(r.MemberClass) == s.G) return r.MemberClass;
                return s.G;
            }
            var patrols = new List<FaltskytteGeneratedPatrol>();
            foreach (var q in res.Patrols)
            {
                var gp = new FaltskytteGeneratedPatrol { PatrolNumber = q.N, StartTime = At(day, q.S), WeaponGroup = Label(q.P.Select(z => z.G)) };
                foreach (var z in q.P)
                {
                    var cls = ClassOf(z); var k = Key(z.Id, cls);
                    regByKey.TryGetValue(k, out var reg);
                    lockedByKey.TryGetValue(k, out var lk);
                    gp.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = z.Id, Name = reg?.MemberName ?? lk?.Name ?? $"Medlem {z.Id}", Club = reg?.MemberClub ?? lk?.Club ?? "", ShootingClass = cls, Position = lk != null && !lk.IsDns ? lk.Position : 0 });
                }
                patrols.Add(gp);
            }
            // DNS: står kvar i patrullen på sin tid utan plats (Position 0); finns ingen patrull där skapas en.
            foreach (var d in res.Dns)
            {
                var t = At(day, d.T); var cls = ClassOf(new Start(d.Id, d.G)); var k = Key(d.Id, cls);
                regByKey.TryGetValue(k, out var reg); lockedByKey.TryGetValue(k, out var lk);
                var target = patrols.FirstOrDefault(x => x.StartTime == t && x.Members.Any(m => GroupOf(m.ShootingClass) == d.G))
                             ?? patrols.FirstOrDefault(x => x.StartTime == t);
                if (target == null) { target = new FaltskytteGeneratedPatrol { StartTime = t, WeaponGroup = Label(new[] { d.G }) }; patrols.Add(target); }
                target.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = d.Id, Name = reg?.MemberName ?? lk?.Name ?? $"Medlem {d.Id}", Club = reg?.MemberClub ?? lk?.Club ?? "", ShootingClass = cls, Position = 0, IsDns = true });
            }
            var ordered = patrols.Select((x, i) => (x, i)).OrderBy(t => t.x.StartTime).ThenBy(t => t.i).Select(t => t.x).ToList();
            for (var i = 0; i < ordered.Count; i++) { ordered[i].PatrolNumber = i + 1; Seat(ordered[i]); }

            // Uppdragskrock som inte gick att undvika: skyttar vars patrull ligger över ett uppdrag (och inte godkänts).
            var unavoidable = new List<FaltskytteUnavoidable>();
            foreach (var x in ordered)
                foreach (var m in x.Members.Where(m => !m.IsDns))
                    if (constraints.Blocks(m.MemberId, x.StartTime!.Value)) unavoidable.Add(new FaltskytteUnavoidable { MemberId = m.MemberId, Name = m.Name, StartTime = x.StartTime!.Value });
            var overflow = ordered.Where(x => x.Members.Count(m => !m.IsDns) > settings.PatrolSize).SelectMany(x => x.Members.Where(m => !m.IsDns)).Where(m => lockedByKey.ContainsKey(Key(m.MemberId, m.ShootingClass))).Select(m => m.Name).Distinct().ToList();
            var reservesLeft = regs.Where(r => reserves.Contains(r.MemberId) && !lockedByKey.ContainsKey(Key(r.MemberId, r.MemberClass))).Select(r => r.MemberName ?? $"Medlem {r.MemberId}").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            PlanMetrics? metrics = null;
            if (res.Q?.R is { } r)
            {
                string NameOf(int id) => regs.FirstOrDefault(x => x.MemberId == id)?.MemberName ?? $"Medlem {id}";
                var wait = r.Persons.Count(z => z.Pairs.Any(y => !y.Cross && y.Slack > 60));
                var colPatrols = r.Ps.Where(x => x.Short > 0.01).Select(x => "patrull " + (x.N)).ToList();
                metrics = new PlanMetrics(!res.Infeasible, res.Q.Hard, res.Q.Under, r.UnderWish.Count, res.Q.Coll, wait, res.Q.Over,
                    ordered.Count > 0 ? ordered.Max(x => x.StartTime)?.ToString("HH:mm") : null, ordered.Count > 0 ? At(day, r.LastEnd).ToString("HH:mm") : null,
                    r.Under.Select(z => NameOf(z.Id)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(), colPatrols);
            }
            PlanTimeline? timeline = null;
            if (res.Q?.R is { } rr)
            {
                // Motorns patruller -> planens nummer: samma starttid (och i ordning vid samma tid).
                var numberAt = new Dictionary<long, Queue<int>>();
                foreach (var x in ordered) { var key = (long)Math.Round(Mins(x.StartTime!.Value, day) * 60); if (!numberAt.TryGetValue(key, out var qn)) numberAt[key] = qn = new Queue<int>(); qn.Enqueue(x.PatrolNumber); }
                var numOfRun = new Dictionary<int, int>();
                foreach (var q in rr.Ps) { var key = (long)Math.Round(q.S0 * 60); numOfRun[q.I] = numberAt.TryGetValue(key, out var qn) && qn.Count > 0 ? qn.Dequeue() : q.N; }
                string Hm(double m) => At(day, m).ToString("HH:mm");
                var badRun = new HashSet<int>(); foreach (var z in rr.Under) foreach (var pr in z.Pairs) if (pr.Slack < -0.01) badRun.Add(pr.B.I);
                // Krockar per station: en patrull som kommer till stationen innan den framför lämnat den (samma regel som motorns Run).
                var nSta = rr.Ps.Count > 0 ? rr.Ps[0].A.Length : 0; var collAt = new HashSet<(int, int)>();
                for (var s = 0; s < nSta; s++)
                {
                    var arr = rr.Ps.Select(q => (q, a: q.S0 + q.A[s], o: q.Occ[s])).OrderBy(t => t.a).ToList();
                    for (var k = 1; k < arr.Count; k++) if (arr[k].a < arr[k - 1].a + arr[k - 1].o - 0.01) { collAt.Add((arr[k].q.I, s)); collAt.Add((arr[k - 1].q.I, s)); }
                }
                var tps = rr.Ps.Select(q => new TimelinePatrol(numOfRun[q.I], Hm(q.S0), Hm(q.End), q.S0, q.End,
                    q.People.GroupBy(z => z.G).ToDictionary(g => g.Key, g => g.Count()), q.ETot > 0.001, Math.Round(q.Short, 1), Math.Round(q.ETot, 1), badRun.Contains(q.I) || q.Short > 0.01,
                    q.A.Select(v => Math.Round(v, 1)).ToArray(), q.Occ.Select(v => Math.Round(v, 1)).ToArray(), Enumerable.Range(0, nSta).Where(s => collAt.Contains((q.I, s))).Select(s => s + 1).ToArray())).OrderBy(t => t.StartMin).ThenBy(t => t.Number).ToList();
                var stInfo = StationsFromConfig(options.StationConfigJson);
                var stationInfo = nSta > 0 ? Enumerable.Range(0, nSta).Select(s => new TimelineStationInfo(s + 1, s < stInfo.Count ? stInfo[s].Number : s + 1, s < stInfo.Count ? stInfo[s].Name : null)).ToList() : new List<TimelineStationInfo>();
                string NameOf2(int id) => regs.FirstOrDefault(x => x.MemberId == id)?.MemberName ?? $"Medlem {id}";
                var persons = rr.Persons.Select(z => new TimelinePerson(z.Id, NameOf2(z.Id), regs.FirstOrDefault(x => x.MemberId == z.Id)?.MemberClub ?? "",
                    string.Join(" + ", z.Starts.Select(s => s.G)), Math.Round(z.MinSlack, 1), Math.Round(z.Wait, 1),
                    z.Starts.Select(s => new TimelineStart(numOfRun[s.I], s.G, Hm(rr.Ps[s.I].S0), Hm(rr.Ps[s.I].End))).ToList(),
                    z.Pairs.Select(pr => new TimelinePair(numOfRun[pr.A.I], numOfRun[pr.B.I], Math.Round(pr.Slack, 1), Math.Round(pr.Idle, 1), pr.Cross)).ToList())).OrderBy(z => z.MinSlackMin).ToList();
                var pauses = res.Pauses.Select(pz => new TimelinePause(pz.Before, Math.Round(pz.Set, 1), Math.Round(pz.Min, 1), pz.Hard)).ToList();
                var kpi = new PlanKpi(ordered.Count, ordered.Sum(x => x.Members.Count(m => !m.IsDns)), rr.Persons.Count, Math.Round(rr.Total, 0), Math.Round(rr.Persons.Count > 0 ? rr.Persons.Max(z => z.Pairs.Where(y => !y.Cross).Select(y => Math.Max(0, y.Slack)).DefaultIfEmpty(0).Max()) : 0, 0),
                    res.Q.Under, rr.UnderWish.Count, rr.NeedAdd, rr.Mixed, res.Q.Coll, ordered.Count > 0 ? ordered.Max(x => x.StartTime)?.ToString("HH:mm") : null, ordered.Count > 0 ? Hm(rr.LastEnd) : null);
                timeline = new PlanTimeline(tps, persons, pauses, kpi, Math.Round(Math.Max(p.R, p.N * p.Interval), 0), stationInfo);
            }
            var outcome = Outcome(res, LinkSetup(regs, reserves, p.Dns!.Keys.ToHashSet()), regs, day);
            return new PatrolPlan
            {
                LinksTotal = outcome.LinksTotal, LinksMet = outcome.LinksMet, LinkIssues = outcome.LinkIssues, WishesTotal = outcome.WishesTotal, WishesMet = outcome.WishesMet, WishesMissed = outcome.WishesMissed,
                Timeline = timeline, Metrics = metrics, Patrols = ordered, Unavoidable = unavoidable, ReservesLeftOut = reservesLeft, LockedKept = lockedByKey.Count(kv => !kv.Value.IsDns && regKeys.Contains(kv.Key)),
                LockedDropped = dropped, LockedOverflow = overflow, Ungrouped = res.Ungrouped,
            };
        }

        /// <summary>Fasta platser: den som redan har en plats behåller den, övriga tar lägsta lediga (DNS har ingen plats).</summary>
        static void Seat(FaltskytteGeneratedPatrol p)
        {
            var taken = p.Members.Where(m => !m.IsDns && m.Position > 0).Select(m => m.Position).ToHashSet();
            var next = 1;
            foreach (var m in p.Members.Where(m => !m.IsDns && m.Position == 0))
            {
                while (taken.Contains(next)) next++;
                m.Position = next; taken.Add(next);
            }
        }
    }
}
