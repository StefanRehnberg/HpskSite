using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models.ViewModels.Competition;
using HpskSite.Models.Staffing;
using Newtonsoft.Json;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Ett övningsscenario: påhittade skyttar i stället för en tävlings anmälningar. Bara data (siffror) — inga namn, inget tävlings-id, inget som sparas.
    /// Det blir <see cref="PatrolPlanService.PatrolBasis"/> (samma underlag som <c>LoadAsync</c> bygger ur databasen) och går sedan genom exakt samma kod som en riktig tävling:
    /// <c>PatrolPlanService.PreviewOf</c> → <c>FaltPatrolAdapter</c> → motorn. Det finns ingen gren efter den punkten.
    /// </summary>
    public sealed class PatrolScenario
    {
        public int V { get; set; } = 1;
        /// <summary>Samma frö ger samma skyttar, samma dubbelstarter och samma uppdrag.</summary>
        public int Seed { get; set; } = 1;
        /// <summary>Antal starter per vapengrupp (C/B/A/R). En dubbelskytt räknas i båda grupperna.</summary>
        public int C { get; set; }
        public int B { get; set; }
        public int A { get; set; }
        public int R { get; set; }
        /// <summary>Dubbelstarter: N skyttar som skjuter i <c>From</c> skjuter också i <c>To</c>.</summary>
        public List<ScenarioAlso> Also { get; set; } = new();
        /// <summary>Skyttar som skjuter tre eller fyra grupper (t.ex. "CBA" eller "CBAR"): N personer med en start i varje. Räknas inte in i <see cref="Also"/>. Små tävlingar och flerdagarstävlingar kan tillåta det.</summary>
        public List<ScenarioMulti> Multi { get; set; } = new();
        /// <summary>Funktionärer som också skjuter och har ett uppdrag: upptagen tid (eller hela dagen). Funktionärer som inte skjuter påverkar inte patrullerna.</summary>
        public List<ScenarioJob> Jobs { get; set; } = new();
        /// <summary>Skyttar som är reserver (ingen fast start) respektive ska skjuta tidigt (funktionärspatrull / skjuter först).</summary>
        public int Reserves { get; set; }
        public int Early { get; set; }
        /// <summary>Nybörjare som måste gå i samma patrull som en annan person i samma klass och klubb (högst 2 följare per person). Kamraten väljs deterministiskt ur gruppen.</summary>
        public int Followers { get; set; }
        /// <summary>Personer som önskar tidig respektive sen start (mjuka önskemål på anmälan, inte funktionärernas "skjuter först").</summary>
        public int WishEarly { get; set; }
        public int WishLate { get; set; }
        /// <summary>Banan: antal stationer. Utan bana körs en enda station, så en övning anger alltid en. Skjuttiderna per klass är motorns förval; rundtiden anges för sig i inställningarna.</summary>
        public int Stations { get; set; } = 8;
        /// <summary>Tävlingsdagen (yyyy-MM-dd). Tom = dagens datum.</summary>
        public string? Date { get; set; }
    }

    public sealed class ScenarioAlso { public string From { get; set; } = "C"; public string To { get; set; } = "B"; public int N { get; set; } }
    public sealed class ScenarioMulti { public string Groups { get; set; } = "CBA"; public int N { get; set; } }
    public sealed class ScenarioJob { public int N { get; set; } = 1; public string? From { get; set; } public string? To { get; set; } public bool AllDay { get; set; } }

    public static class ScenarioBuilder
    {
        /// <summary>Taken gäller även interna körningar; ett stort SM har flera tusen starter.</summary>
        public const int MaxStarts = 6000, MaxJobs = 1000, MaxStations = 20, MaxAlso = 12;
        public static readonly string[] Groups = { "C", "B", "A", "R" };
        /// <summary>Klasserna per vapengrupp ur klassregistret (öppna klasser först); aldrig hårdkodade id.</summary>
        static readonly Dictionary<string, string[]> ClassIds = Groups.ToDictionary(g => g, g =>
        {
            var w = Enum.Parse<HpskSite.Models.WeaponClass>(g);
            var ids = HpskSite.Models.ShootingClasses.For(w, HpskSite.Models.ClassCategory.Open).Select(c => c.Id).ToArray();
            return ids.Length > 0 ? ids : HpskSite.Models.ShootingClasses.ForWeapon(w).Select(c => c.Id).ToArray();
        });

        public static PatrolScenario? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 4000) return null;
            try { return JsonConvert.DeserializeObject<PatrolScenario>(json); } catch { return null; }
        }

        /// <summary>Felmeddelande om scenariot är för stort eller orimligt; annars null.</summary>
        public static string? Validate(PatrolScenario s)
        {
            if (s.V != 1) return "Okänd version av scenariot.";
            var counts = new[] { s.C, s.B, s.A, s.R };
            if (counts.Any(c => c < 0)) return "Antalet skyttar kan inte vara negativt.";
            if (counts.Sum() > MaxStarts) return $"Högst {MaxStarts} starter i en övning.";
            if (counts.Sum() == 0) return "Ange minst en skytt.";
            if (s.Stations is < 1 or > MaxStations) return $"Antal stationer ska vara 1–{MaxStations}.";
            if ((s.Also?.Count ?? 0) > MaxAlso) return "För många dubbelstarter.";
            foreach (var a in s.Also ?? new())
            {
                if (!Groups.Contains(a.From) || !Groups.Contains(a.To) || a.From == a.To) return "Dubbelstart: välj två olika klasser (C, B, A eller R).";
                if (a.N < 0) return "Dubbelstart: antalet kan inte vara negativt.";
            }
            if ((s.Jobs?.Count ?? 0) > 50 || (s.Jobs?.Sum(j => Math.Max(0, j.N)) ?? 0) > MaxJobs) return "För många uppdrag.";
            if (s.Reserves < 0 || s.Early < 0) return "Antalet reserver och tidiga kan inte vara negativt.";
            if (s.Followers < 0 || s.WishEarly < 0 || s.WishLate < 0) return "Antalet som går med någon eller har önskemål kan inte vara negativt.";
            // Dubbelstarter ryms bara om varje grupps dubbla ryms i gruppens starter (ingen skjuter tre grupper). Förut klipptes de tyst.
            var n = new Dictionary<string, int> { ["C"] = s.C, ["B"] = s.B, ["A"] = s.A, ["R"] = s.R };
            var doubles = 0;
            foreach (var a in s.Also ?? new())
            {
                if (a.N > Math.Min(n[a.From], n[a.To])) return $"Dubbelstart {a.From} och {a.To}: högst {Math.Min(n[a.From], n[a.To])}.";
                doubles += a.N;
            }
            if ((s.Multi?.Count ?? 0) > 5 + MaxAlso) return "För många kombinationer av tre eller fyra klasser.";
            var extraStarts = 0;   // starter utöver personens första, för skyttar med tre eller fyra grupper
            foreach (var t in s.Multi ?? new())
            {
                var gs = ComboGroups(t.Groups);
                if (gs.Length < 3) return "Tre eller fyra klasser: välj olika av C, B, A och R.";
                if (t.N < 0) return "Tre eller fyra klasser: antalet kan inte vara negativt.";
                if (t.N > gs.Min(g => n[g])) return $"Klasserna {string.Join(" + ", gs)}: högst {gs.Min(g => n[g])}.";
                extraStarts += t.N * (gs.Length - 1);
            }
            foreach (var g in Groups)
            {
                var inGroup = (s.Also ?? new()).Where(a => a.From == g || a.To == g).Sum(a => a.N) + (s.Multi ?? new()).Where(t => ComboGroups(t.Groups).Contains(g)).Sum(t => t.N);
                if (inGroup > n[g]) return $"Klass {g} har {n[g]} starter men {inGroup} av dem ska också skjuta en annan grupp.";
            }
            var persons = counts.Sum() - doubles - extraStarts;
            var staff = (s.Jobs?.Sum(j => Math.Max(0, j.N)) ?? 0) + s.Reserves + s.Early;
            if (staff > persons) return $"Uppdrag, reserver och tidiga ({staff}) är fler än personerna ({persons}).";
            if (s.WishEarly + s.WishLate + s.Followers > persons) return $"Önskemål och nybörjare med kamrat ({s.WishEarly + s.WishLate + s.Followers}) är fler än personerna ({persons}).";
            return null;
        }

        /// <summary>Ett kort fingeravtryck av scenariot (för loggning och delning); innehåller inga personuppgifter.</summary>
        public static string Hash(PatrolScenario s) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(s))))[..8];

        /// <summary>Gruppens kod (C/B/A/R) per skytt och start: (id, grupp). Ren och deterministisk — samma scenario ger samma lista.</summary>
        public static (List<(int Id, string Group)> Starts, int PersonCount) Shooters(PatrolScenario s)
        {
            var n = new Dictionary<string, int> { ["C"] = Math.Max(0, s.C), ["B"] = Math.Max(0, s.B), ["A"] = Math.Max(0, s.A), ["R"] = Math.Max(0, s.R) };
            // Personer per kombination: först de som skjuter tre grupper, sedan två, sist de som bara skjuter en. Riktningen i ett par betyder inget.
            // Ryms inte en kombination i gruppernas starter klipps den (Validate avvisar det innan, så en riktig körning klipper aldrig).
            var members = Groups.ToDictionary(g => g, _ => new List<int>());
            var next = 1;
            void Combo(string[] gs, int count)
            {
                var take = Math.Max(0, Math.Min(count, gs.Min(g => n[g] - members[g].Count)));
                for (var i = 0; i < take; i++) { var id = next++; foreach (var g in gs) members[g].Add(id); }
            }
            foreach (var t in (s.Multi ?? new()).OrderByDescending(t => ComboGroups(t.Groups).Length)) { var gs = ComboGroups(t.Groups); if (gs.Length >= 3) Combo(gs, t.N); }
            foreach (var a in s.Also ?? new()) Combo(new[] { a.From, a.To }, a.N);
            foreach (var g in Groups) while (members[g].Count < n[g]) members[g].Add(next++);
            // Varianten (slumptalet) blandar vilka id:n som hamnar var, så "samma variant = samma skyttar" men en ny variant ger en annan blandning.
            var rng = new Random(s.Seed);
            var perm = Enumerable.Range(1, next - 1).OrderBy(_ => rng.Next()).ToArray();
            var starts = new List<(int, string)>();
            foreach (var g in Groups) foreach (var id in members[g]) starts.Add((perm[id - 1], g));
            return (starts, starts.Select(x => x.Item1).Distinct().Count());
        }

        /// <summary>Tre eller fyra olika grupper ur t.ex. "CBA" eller "CBAR" (i fast ordning C, B, A, R); annars en tom lista.</summary>
        public static string[] ComboGroups(string? code)
        {
            var c = (code ?? "").ToUpperInvariant();
            var gs = c.Select(x => x.ToString()).Distinct().Where(g => Groups.Contains(g)).OrderBy(g => Array.IndexOf(Groups, g)).ToArray();
            return gs.Length is 3 or 4 && gs.Length == c.Length ? gs : Array.Empty<string>();
        }

        public static string Name(int id) => "Skytt " + id.ToString("000");
        public static string Club(int id) => "Övningsklubben " + (char)('A' + (id % 8));

        /// <summary>Banan som tävlingskonfiguration (samma format som en riktig tävlings kopia): <paramref name="stations"/> stationer med lika skjuttid för varje grupp.</summary>
        public static string StationConfig(int stations)
        {
            // Skjuttid per klass vid varje station: motorns egna förval (C 12, B 15, A 21, R 21 sekunder). Den styr bara hur mycket längre en patrull med blandade klasser tar; rundtiden anges för sig.
            var sec = new Dictionary<string, int> { ["C"] = 12, ["B"] = 15, ["A"] = 21, ["R"] = 21 };
            var cfg = new FaltskytteCompetitionConfig();
            foreach (var g in Groups)
                cfg.WeaponConfigs[g] = new FaltskytteWeaponClassConfig { Stations = Enumerable.Range(1, stations).Select(i => new FaltskytteStationConfig { Station = i, ShootingTimeSec = sec[g] }).ToList() };
            return JsonConvert.SerializeObject(cfg);
        }

        /// <summary>Scenariot som samma underlag en riktig tävling ger. Kastar <see cref="ArgumentException"/> om <see cref="Validate"/> inte är null.</summary>
        public static PatrolPlanService.PatrolBasis Build(PatrolScenario s)
        {
            var err = Validate(s);
            if (err != null) throw new ArgumentException(err);
            var (starts, persons) = Shooters(s);
            var day = (DateTime.TryParse(s.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.Today).Date;
            var rng = new Random(s.Seed * 31 + 7);
            var regs = new List<CompetitionRegistration>();
            var seq = new Dictionary<string, int>();
            foreach (var (id, g) in starts)
            {
                seq[g] = seq.GetValueOrDefault(g) + 1;
                regs.Add(new CompetitionRegistration { MemberId = id, MemberClass = ClassIds[g][(seq[g] - 1) % 3], MemberName = Name(id), MemberClub = Club(id), IsActive = true });
            }
            // Önskemål om starttid och "gå i samma patrull som" sätts på anmälningarna på samma sätt som riktiga anmälningar bär dem (StartPreference, JoinMemberId),
            // så labbet går genom exakt samma kod som en riktig tävling. Samma klubb och samma vapengrupp gäller per konstruktion (Club(id), kamraten väljs ur gruppen).
            {
                var rw = new Random(s.Seed * 17 + 3);
                var people = starts.Select(x => x.Id).Distinct().OrderBy(_ => rw.Next()).ToList();
                var cursorW = 0;
                foreach (var id in people.Skip(cursorW).Take(Math.Max(0, s.WishEarly))) foreach (var i in Enumerable.Range(0, starts.Count).Where(i => starts[i].Id == id)) regs[i].StartPreference = HpskSite.Models.StartPreference.Early;
                cursorW += Math.Max(0, s.WishEarly);
                foreach (var id in people.Skip(cursorW).Take(Math.Max(0, s.WishLate))) foreach (var i in Enumerable.Range(0, starts.Count).Where(i => starts[i].Id == id)) regs[i].StartPreference = HpskSite.Models.StartPreference.Late;
                cursorW += Math.Max(0, s.WishLate);
                var followers = new Dictionary<(int Partner, string G), int>(); var isFollower = new HashSet<(int Id, string G)>();
                foreach (var f in people.Skip(cursorW).Take(Math.Max(0, s.Followers)))
                {
                    var fi = Enumerable.Range(0, starts.Count).FirstOrDefault(i => starts[i].Id == f && !isFollower.Contains((f, starts[i].Group)));
                    var g = starts[fi].Group;
                    var candidates = Enumerable.Range(0, starts.Count).Where(i => starts[i].Group == g && starts[i].Id != f && Club(starts[i].Id) == Club(f) && !isFollower.Contains((starts[i].Id, g)) && followers.GetValueOrDefault((starts[i].Id, g)) < HpskSite.Models.Registrations.RegistrationJoinRule.MaxFollowersPerPartner).ToList();
                    if (candidates.Count == 0) continue;
                    var partner = starts[candidates[rw.Next(candidates.Count)]].Id;
                    regs[fi].JoinMemberId = partner; isFollower.Add((f, g)); followers[(partner, g)] = followers.GetValueOrDefault((partner, g)) + 1;
                }
            }
            var ids = starts.Select(x => x.Id).Distinct().OrderBy(_ => rng.Next()).ToList();
            var cursor = 0;
            IEnumerable<int> Take(int count) { var r = ids.Skip(cursor).Take(Math.Max(0, count)).ToList(); cursor += r.Count; return r; }
            var jobs = new Dictionary<int, List<StaffInterval>>();
            foreach (var j in s.Jobs ?? new())
                foreach (var id in Take(j.N))
                {
                    StaffInterval iv;
                    if (j.AllDay || !TimeSpan.TryParse(j.From, out var f) || !TimeSpan.TryParse(j.To, out var t) || t <= f) iv = new StaffInterval(day, day.AddDays(1), true);
                    else iv = new StaffInterval(day + f, day + t, false);
                    jobs[id] = new List<StaffInterval> { iv };
                }
            var reserves = Take(s.Reserves).ToHashSet();
            var early = Take(s.Early).ToHashSet();
            var cfg = StationConfig(s.Stations);
            return new PatrolPlanService.PatrolBasis
            {
                Regs = regs,
                Reserves = reserves,
                Constraints = new PatrolConstraints { Jobs = jobs, Shoot = ShootTimeRules.Default("Faltskytte"), Early = early },
                StationConfigJson = cfg,
                Date = day,
                Fingerprint = new List<string> { "scenario:" + Hash(s) },
            };
        }
    }
}
