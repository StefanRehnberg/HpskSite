using System.Globalization;
using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// ⚠️ P2 (Stefan 2026-10-23): förberedelserna bor i tävlingens PLANERING (<see cref="PlanningService"/>).
    /// Den här klassen är bara en LÄSADAPTER som visar planeringens områden och uppgifter i den gamla formen
    /// (<see cref="WorkBreakdownResponse"/>), för de två ytor som läser den: bemanningens personlista
    /// ("Förberedelser"-personerna, <see cref="CompetitionPeopleService"/>) och utskriftsbladet
    /// (/tavlingsplanering/blad). Den skriver ingenting, och de gamla tabellerna (WorkArea, WorkItem …) läses
    /// inte längre — de flyttas in när tävlingens planering startas. Mallarna (<see cref="PrepTemplates"/>) står
    /// kvar här och fylls i genom planeringen.
    /// </summary>
    public class WorkBreakdownService
    {
        private readonly PlanningService _planning;

        public WorkBreakdownService(PlanningService planning)
        {
            _planning = planning;
        }

        /// <summary>Tävlingens planering i förberedelsernas form. Tom när tävlingen saknar planering.</summary>
        public WorkBreakdownResponse Build(int competitionId, bool canEdit)
            => BuildForPlanning(_planning.ForCompetition(competitionId)?.Id ?? 0, canEdit);

        /// <summary>P2b: planeringens förberedelser — även för en planering som inte är kopplad till en tävling.</summary>
        public WorkBreakdownResponse BuildForPlanning(int planningId, bool canEdit)
        {
            var resp = new WorkBreakdownResponse { CanEdit = false };
            var p = planningId > 0 ? _planning.Get(planningId) : null;
            if (p == null) return resp;

            var people = _planning.Participants(p.Id, includeRemoved: true).ToDictionary(x => x.Id);
            var tasks = _planning.Tasks(p.Id);
            var docs = _planning.Documents(p.Id);
            var docById = docs.ToDictionary(d => d.Id);
            var refs = _planning.DocumentRefs(p.Id).Where(r => docById.ContainsKey(r.DocumentId)).ToList();
            List<PlanningDocumentRow> DocsOn(string kind, int targetId) =>
                refs.Where(r => r.TargetKind == kind && r.TargetId == targetId).Select(r => docById[r.DocumentId]).ToList();
            var attachedToPrep = refs.Where(r => r.TargetKind is PlanningDocTarget.Task or PlanningDocTarget.Area)
                .Select(r => r.DocumentId).ToHashSet();
            var today = DateTime.Today;
            (int? MemberId, string? Name) Who(int? pid, string? free) =>
                pid.HasValue && people.TryGetValue(pid.Value, out var row) ? (row.MemberId, row.DisplayName) : (null, free);
            WorkLinkView Link(PlanningDocumentRow d) => new()
            {
                Id = d.Id, Title = d.Title, IsFile = d.IsFile,
                Url = d.IsFile ? $"/umbraco/surface/Planning/DownloadDocument?id={p.Id}&documentId={d.Id}" : d.Url,
            };
            WorkItemView Item(PlanningTaskRow t, int areaId)
            {
                var (mid, name) = Who(t.AssigneeParticipantId, t.AssigneeName);
                return new WorkItemView
                {
                    Id = t.Id, WorkAreaId = areaId, Title = t.Title, Description = t.Description,
                    AssignedMemberId = mid, AssignedName = name,
                    DueDate = t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Status = LegacyStatus(t.Status), ScopeType = t.ScopeType, ScopeKey = t.ScopeKey,
                    IsOverdue = PlanningTaskRules.IsOverdue(t, today), EstimatedCost = t.EstimatedCost, SortOrder = t.SortOrder,
                    Links = DocsOn(PlanningDocTarget.Task, t.Id).Select(Link).ToList(),
                };
            }
            WorkAreaView Area(int id, string name, int? respPid, string? respName, int sort, List<PlanningTaskRow> items)
            {
                var (mid, rname) = Who(respPid, respName);
                var views = items.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => Item(t, id)).ToList();
                return new WorkAreaView
                {
                    Id = id, Name = name, ResponsibleMemberId = mid, ResponsibleName = rname, SortOrder = sort, Items = views,
                    Links = id > 0 ? DocsOn(PlanningDocTarget.Area, id).Select(Link).ToList() : new(),
                    TotalCount = views.Count, DoneCount = views.Count(v => v.Status == WorkItemStatus.Klar),
                    OverdueCount = views.Count(v => v.IsOverdue), EstimatedCostSum = views.Sum(v => v.EstimatedCost ?? 0m),
                };
            }

            var areas = _planning.Areas(p.Id);
            var areaIds = areas.Select(a => a.Id).ToHashSet();
            foreach (var a in areas)
                resp.Areas.Add(Area(a.Id, a.Name, a.ResponsibleParticipantId, a.ResponsibleName, a.SortOrder, tasks.Where(t => t.AreaId == a.Id).ToList()));
            var loose = tasks.Where(t => !t.AreaId.HasValue || !areaIds.Contains(t.AreaId.Value)).ToList();
            if (loose.Count > 0) resp.Areas.Add(Area(0, "Utan område", null, null, int.MaxValue, loose));
            resp.CompLinks = docs.Where(d => !attachedToPrep.Contains(d.Id)).Select(Link).ToList();
            resp.TotalEstimatedCost = resp.Areas.Sum(a => a.EstimatedCostSum);
            return resp;
        }

        /// <summary>Planeringens läge i förberedelsernas ord (de läsare som finns kvar känner de orden).</summary>
        public static string LegacyStatus(string? status) => status switch
        {
            PlanningTaskStatus.Done => WorkItemStatus.Klar,
            PlanningTaskStatus.InProgress or PlanningTaskStatus.ReadyForReview => WorkItemStatus.Pagar,
            PlanningTaskStatus.NeedsHelp => WorkItemStatus.Blockerad,
            _ => WorkItemStatus.Planerad,
        };
    }

    /// <summary>
    /// En punkt i mallen. P3 (2026-10-23): en punkt kan vara en MILSTOLPE och kan skickas till förbundet;
    /// <see cref="DueFromAnchor"/> räknar en förbundsfrist ur tävlingens dag ("30 juni året före").
    /// </summary>
    internal record PrepItem(string Title, int? DaysBeforeComp, string? Description = null, bool Milestone = false,
        bool ToForbundet = false, Func<DateTime, DateTime>? DueFromAnchor = null, Func<DateTime, string>? TitleFromAnchor = null);
    /// <summary>
    /// Ett område i mallen. Med flera grenar (<see cref="PrepTemplates.ForMany"/>) bär grenens egna områden
    /// <see cref="Discipline"/> och namnet utan grenen (<see cref="BaseName"/>) — Expertgruppen N8: har huvudplaneringen
    /// en delplanering för grenen hamnar området där, med sitt vanliga namn.
    /// </summary>
    internal record PrepArea(string Area, List<PrepItem> Items, string? Discipline = null, string? BaseName = null);

    /// <summary>
    /// Built-in preparation templates (spec §5.1), keyed by DISCIPLINE (Fält / Springskytte / Precision)
    /// and sized klubb/krets/sm. System defaults live in code so a fresh install works with zero setup;
    /// the editable per-club/region StaffingTemplate layer is Phase 1.5. Each uppgift carries an optional
    /// DaysBeforeComp so SeedTemplate can anchor deadlines to the competition date. An SM template carries
    /// far more områden/uppgifter than a klubbtävling.
    ///
    /// NOTE: the Springskytte + Fältskytte content is a first pass — sanity-check the crew-specific tasks
    /// with a real arrangör (same caveat as the FunctionaryRoles first-pass role sets).
    /// </summary>
    internal static class PrepTemplates
    {
        /// <summary>
        /// Storlekarna (omgång 3, K10 — SHB C.3.5): klubb · krets · nationell · ldm (landsdelsmästerskap) · sm. Ett okänt
        /// värde läses som klubb; det äldre valet "Mästerskap" skickade "sm".
        /// </summary>
        public static readonly string[] Sizes = { "klubb", "krets", "nationell", "ldm", "sm" };

        public static List<PrepArea> For(string? discipline, string? size)
        {
            var s = NormalizeSize(size);
            var big = s is "sm" or "ldm";
            var mid = s != "klubb";
            var d = (discipline ?? "").ToLowerInvariant();

            var list = d is "faltskytte" or "magnumfalt" ? Falt(mid, big)
                : d == "springskytte" ? Spring(mid, big)
                : d == "milsnabb" ? Milsnabb(mid, big)
                : Precision(mid, big);
            ApplyLevel(list, s, falt: d is "faltskytte" or "magnumfalt");
            return list;
        }

        /// <summary>
        /// Flera grenar på en gång (omgång 3, persona 3: en SM-vecka med fält och precision). De gemensamma områdena
        /// (kansliet, sekretariatet, förbundet …) kommer en gång, med uppgifterna sammanslagna; grenens egna områden får
        /// grenens namn ("Materiel – Fält"), så att uppgifterna hamnar i grenens område — eller, när huvudplaneringen har en
        /// delplanering för grenen, i delplaneringen med sitt vanliga namn (<c>PlanningService.PlanTemplate</c>, N8).
        /// </summary>
        public static List<PrepArea> ForMany(IEnumerable<string?> disciplines, string? size)
        {
            var list = disciplines.Select(d => (d ?? "").Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count <= 1) return For(list.FirstOrDefault(), size);
            var merged = new List<PrepArea>();
            foreach (var d in list)
                foreach (var a in For(d, size))
                {
                    var common = Common.Contains(a.Area);
                    var name = common ? a.Area : $"{a.Area} – {DisciplineLabel(d)}";
                    var have = merged.FindIndex(m => string.Equals(m.Area, name, StringComparison.OrdinalIgnoreCase));
                    if (have < 0) { merged.Add(new PrepArea(name, a.Items.ToList(), common ? null : d, common ? null : a.Area)); continue; }
                    var items = merged[have].Items;
                    items.AddRange(a.Items.Where(i => !items.Any(x => string.Equals(x.Title, i.Title, StringComparison.OrdinalIgnoreCase))));
                }
            return merged;
        }

        private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        {
            "Tävlingsledning", "Förbundet", "Kretsen", AdminArea, "Inbjudan & anmälan", "Sekretariat & resultat",
            "Prisutdelning", "Säkerhet & sjukvård", "Funktionärer", "Ekonomi", "Parkering",
            "Servering", "Boende & logistik", "Marknad & media",
        };

        /// <summary>
        /// Stefan 2026-10-25 (Expertgruppen B12): mallens ord som arrangören använder. "Sanktion" och "domare" finns inte i
        /// pistolskyttet — området heter Ansökan & tillstånd och juryn utses bara där SHB kräver den (C.4.1.5).
        /// </summary>
        public const string AdminArea = "Ansökan & tillstånd";
        private const string JuryTitle = "Utse tävlingsjury";

        /// <summary>Fältets bygge av banan på plats — "Bygg station N" läggs här (<c>SeedStationTasks</c>).</summary>
        public const string BuildCourseArea = "Bygga banan";

        private static string DisciplineLabel(string d) => d.ToLowerInvariant() switch
        {
            "faltskytte" or "magnumfalt" => "Fält",
            "springskytte" => "Spring",
            "milsnabb" => "Milsnabb",
            _ => "Precision",
        };

        public static string NormalizeSize(string? size)
        {
            var s = (size ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Sizes, s) >= 0 ? s : "klubb";
        }

        /// <summary>
        /// Det som skiljer nivåerna åt enligt SHB (omgång 3, K10). Mallarna ovan bär SM:s förbundsområde; här läggs
        /// ansökan, inbjudan, jury och vapenkontroll in per nivå.
        /// <list type="bullet">
        /// <item>Kretstävling: ansökan till kretsstyrelsen på den tid kretsen föreskriver (C.3.5.4).</item>
        /// <item>Nationell tävling: ansökan via kretsen, hos förbundet före 1 oktober året före (C.3.5.3.2); inbjudan till
        /// förbundet för kännedom (C.3.5.2.1); i fält målförutsättningar och stationsbeskrivning till kretsstyrelsen
        /// (C.3.5.2.2).</item>
        /// <item>Landsdelsmästerskap: datumet i stomprogrammet (C.3.5.3.1), ansökan som nationell, inbjudan för
        /// godkännande och i fält banans underlag till förbundet 12 veckor före (C.3.5.2.1–2).</item>
        /// <item>Jury vid mästerskap och större nationella tävlingar (C.4.1.5); vapenkontroll vid mästerskap, nationella
        /// tävlingar och tävlingar med standardmedaljer (C.4.2.1).</item>
        /// </list>
        /// </summary>
        private static void ApplyLevel(List<PrepArea> list, string size, bool falt)
        {
            var admin = list.FirstOrDefault(a => a.Area == AdminArea);
            if (admin != null)
            {
                var items = admin.Items;
                // Mallarnas "stora" variant är SM:s — ett landsdelsmästerskap har ingen SM-ansvarig på förbundet.
                if (size != "sm") items.RemoveAll(x => x.Title == "Kontakta förbundets SM-ansvarige");
                var i = items.FindIndex(x => x.Title == "Sök sanktion (SPSF/krets)");
                if (i >= 0)
                {
                    if (size == "krets")
                        items[i] = M("Ansökan om kretstävling till kretsstyrelsen", 120,
                            "En kretstävling söks hos kretsstyrelsen på den tid kretsen föreskriver (SHB C.3.5.4). Sök under Tävlingen → "
                            + "Sök om tävlingen hos kretsen. Ändra sista dagen efter kretsens frist.");
                    else items.RemoveAt(i); // klubb: ingen ansökan; nationell/ldm: ansökan står under Förbundet
                }
                // Jury bara vid mästerskap och större nationella tävlingar (SHB C.4.1.5) — klubb- och kretstävlingar har ingen.
                var j = items.FindIndex(x => x.Title == JuryTitle);
                if (j >= 0)
                {
                    if (size is "nationell" or "ldm" or "sm")
                        items[j] = I("Utse tävlingsjury — tre i skyttefrågor väl förfarna personer (SHB C.4.1.5)", size == "nationell" ? 45 : 90);
                    else items.RemoveAt(j);
                }
                if (size != "klubb")
                    items.Add(I("Boka vapenkontrollant(er) — krävs vid mästerskap, nationella tävlingar och standardmedaljer (SHB C.4.2.1)",
                        size is "sm" or "ldm" ? 60 : 30));
            }
            if (size is not ("nationell" or "ldm")) return;

            // Förbundets område byggs för nationell tävling och landsdelsmästerskap i stället för SM:s.
            list.RemoveAll(a => a.Area == "Förbundet");
            var ansokan = M("Ansökan via kretsen till förbundet", null,
                "Ansökan om nationell tävling eller landsdelstävling ska ha kommit in till förbundsstyrelsen före 1 oktober året "
                + "innan (SHB C.3.5.3.2). Den skickas till kretsen, som yttrar sig och skickar den vidare — sista dagen är satt "
                + "en månad före; ändra den efter kretsens egen frist. Sök under Tävlingen → Sök om tävlingen hos kretsen.",
                false, d => new DateTime(d.Year - 1, 9, 1));
            var items2 = new List<PrepItem>();
            if (size == "ldm")
            {
                items2.Add(M("Förbundet fastställer datumet i stomprogrammet", null,
                    "Förbundsstyrelsen fastställer stomprogrammet med landsdelsmästerskapens datum senast 30 juni året före "
                    + "(SHB C.3.5.3.1). Kontrollera att datumet stämmer med planeringen.", false, d => new DateTime(d.Year - 1, 6, 30)));
                items2.Add(ansokan);
                items2.Add(M("Förslag till inbjudan till förbundet för godkännande", 120,
                    "Arrangören skickar i god tid förslaget till inbjudan till förbundet, som godkänner den (SHB C.3.5.2.1). "
                    + "Inbjudan publiceras först när förbundet har godkänt.", true));
                if (falt) items2.Add(M("Fältets underlag till förbundet", 84,
                    "Målförutsättningar och stationsbeskrivningar ska vara hos förbundet minst 12 veckor före tävlingen (SHB C.3.5.2.2).", true));
            }
            else
            {
                items2.Add(ansokan);
                items2.Add(M("Inbjudan till förbundet för kännedom", 45,
                    "Inbjudan till en nationell tävling skickas till förbundet för kännedom. Inget godkännande kommer tillbaka, "
                    + "men fel som upptäcks meddelas (SHB C.3.5.2.1).", true));
                if (falt) list.Insert(0, new PrepArea("Kretsen", new List<PrepItem>
                {
                    M("Målförutsättningar och stationsbeskrivning till kretsstyrelsen", 84,
                        "För en nationell fältskjutning skickas målförutsättningarna och stationsbeskrivningarna till kretsstyrelsen, "
                        + "som låter dem granskas (SHB C.3.5.2.2). Ändra sista dagen efter kretsens frist."),
                }));
            }
            list.Insert(0, new PrepArea("Förbundet", items2));
        }

        private static PrepItem I(string title, int? days = null) => new(title, days);
        private static PrepItem M(string title, int? days, string description, bool toForbundet = false, Func<DateTime, DateTime>? rule = null)
            => new(title, days, description, true, toForbundet, rule);

        /// <summary>
        /// SM: förbundets frister som milstolpar (beslut 9.1 och 10.1–10.2). SM är förbundets uppdrag — ingen sanktion att söka,
        /// och ingen ansökan 1 oktober: den gäller nationella tävlingar och landsdelstävlingar (SHB C.3.5.3.2), SM fördelas
        /// genom långtidsplanen. Stefan 2026-10-24 ("SHB har säkert rätt"): förbundet fastställer datumet i stomprogrammet
        /// (C.3.5.3.1) och godkänner inbjudan (C.3.5.2.1).
        /// <para>P4 (Stefan 2026-10-24): SM-annonsen ska med i förbundets tidning — den stora SM-veckan i nr 2, ett SM senare
        /// på året (springskytte) i nr 3. <see cref="NpIssues.ForCompetition"/> väljer numret ur tävlingens dag och
        /// uppskattar manusstoppet ur förbundets utgivningsplan. Titel och datum går att ändra på milstolpen.</para>
        /// </summary>
        private static PrepArea Forbundet(bool falt) => new("Förbundet", new List<PrepItem>
        {
            M("Förbundet fastställer SM-datumet i stomprogrammet", null,
                "Förbundsstyrelsen fastställer stomprogrammet med SM-datumen senast 30 juni året före (SHB C.3.5.3.1). "
                + "Kontrollera att datumet i stomprogrammet stämmer med planeringen.", false, d => new DateTime(d.Year - 1, 6, 30)),
            M("Förslag till inbjudan till förbundet för godkännande", null,
                "Arrangören skickar i god tid förslaget till inbjudan till förbundet, som godkänner den (SHB C.3.5.2.1). "
                + "Inbjudan och SM-annonsen går ut först när förbundet har godkänt. Sista dagen är satt fyra veckor före "
                + "tidningens manusstopp — ändra den om förbundet vill ha förslaget tidigare.",
                true, d => NpIssues.ForCompetition(d).Deadline.AddDays(-28)),
            M("SM-annonsen till Nationellt Pistolskytte", null,
                "SM-annonsen ska med i förbundets tidning i det nummer som kommer ut före tävlingen — den stora SM-veckan i nr 2 "
                + "(juni), ett SM senare på året, till exempel springskytte, i nr 3 (september). Numret och sista dagen (tidningens "
                + "manusstopp) är uppskattade ur förbundets utgivningsplan: kontrollera dem på pistolskytteforbundet.se under "
                + "Om oss → Medlemstidningen, och ändra milstolpen om de skiljer. Bifoga annonsen ur planeringens dokument.",
                true, d => NpIssues.ForCompetition(d).Deadline) with
            {
                TitleFromAnchor = d => { var n = NpIssues.ForCompetition(d); return $"SM-annonsen till Nationellt Pistolskytte nr {n.Issue}/{n.Year}"; },
            },
        }.Concat(falt ? new[] { M("Fältets underlag till förbundet", 84,
                "Banans underlag (målförutsättningar och stationsbeskrivningar) ska vara hos förbundet minst 12 veckor före tävlingen (SHB C.3.5.2.2).", true) }
            : Array.Empty<PrepItem>()).ToList());

        // --- Common områden shared by all disciplines ---
        private static PrepArea Admin(bool big) => new(AdminArea, new()
        {
            // SM är förbundets uppdrag; där står ansökan under området Förbundet. Ansökan per nivå läggs i ApplyLevel.
            big ? I("Kontakta förbundets SM-ansvarige", 365) : I("Sök sanktion (SPSF/krets)", 60),
            I(JuryTitle, big ? 90 : 45),
            I("Försäkring och tillstånd (markägare, polis vid behov)", big ? 90 : 45),
            I("Fastställ tidsplan och ansvarsfördelning", big ? 75 : 30),
        });
        private static PrepArea Kansli(bool big) => new("Inbjudan & anmälan", new()
        {
            big ? new PrepItem("Publicera inbjudan", 75, "Först när förbundet har godkänt inbjudan (SHB C.3.5.2.1).") : I("Publicera inbjudan", 35),
            I("Öppna anmälan", big ? 60 : 30),
            I("Stäng anmälan och bekräfta anmälningarna", big ? 14 : 7),
        });
        private static PrepArea Sekretariat(bool big, string lottning = "Publicera startlistan") => new("Sekretariat & resultat", new()
        {
            I(lottning, 3),
            I("Förbered resultatinmatning och resultattavla", 2),
            I("Publicera resultatet", 0),
        });
        private static PrepArea Prisutdelning(bool big) => new("Prisutdelning", new()
        {
            // Standardmedaljer delas aldrig ut på tävlingen (de vinns som poäng) och diplom delas inte ut; hederspriser kan delas ut.
            I("Beställ priserna", big ? 45 : 28),
            I("Beställ gravering av priserna", big ? 30 : 18),
            I("Hederspriser att dela ut (om ni har några)", big ? 14 : 7),
            I("Planera prisutdelningen — tid, plats och vem som delar ut", big ? 14 : 3),
        });
        private static PrepArea Sakerhet(bool big) => new("Säkerhet & sjukvård", new()
        {
            I("Säkerhetsplan och säkerhetsansvarig", big ? 60 : 21),
            I("Sjukvårdsansvarig, första hjälpen och larmrutin", big ? 30 : 7),
            I("Hjärtstartare på plats", big ? 21 : 7),
        });
        private static PrepArea Funktionarer(bool big) => new("Funktionärer", new()
        {
            I("Bemanna funktionärerna (under Bemanning)", big ? 45 : 21),
            I("Skicka information till funktionärerna", big ? 14 : 5),
            I("Genomgång med funktionärerna", 1),
        });
        private static PrepArea Ekonomi(bool big) => new("Ekonomi", new()
        {
            I("Budget och startavgift", big ? 120 : 45),
            I("Kontrollera att startavgifterna har kommit in", 3),
        });
        private static PrepArea Parkering(bool big) => new("Parkering", new()
        {
            I("Parkering och vägvisning till banan", big ? 21 : 7),
            I(big ? "Parkeringsvakter" : "Någon som visar till parkeringen", big ? 14 : 3),
        });

        /// <summary>De gemensamma områdena i den ordning arrangören tänker: före, under, efter. Klubbtävlingen hålls kort.</summary>
        private static void AddCommon(List<PrepArea> list, bool mid, bool big, bool falt, PrepArea? sekretariat = null, PrepArea? sakerhet = null)
        {
            list.Add(Funktionarer(big));
            list.Add(sakerhet ?? Sakerhet(big));
            list.Add(sekretariat ?? Sekretariat(big));
            list.Add(Prisutdelning(big));
            list.Add(Kansli(big));
            if (mid) { list.Add(Ekonomi(big)); list.Add(Parkering(big)); list.Add(Servering(big)); }
            if (big) { list.Insert(0, Forbundet(falt)); list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
        }
        private static PrepArea Servering(bool big) => new("Servering", new()
        {
            I("Planera kiosk/servering", big ? 30 : 10),
            I("Inköp", big ? 7 : 3),
            I("Bemanna servering", big ? 14 : 5),
        });
        private static PrepArea Logistik() => new("Boende & logistik", new()
        {
            I("Boka boende för funktionärer", 45),
            I("Transporter", 30),
            I("Måltider funktionärer", 21),
        });

        private static List<PrepArea> Precision(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Mark & bana", new()
            {
                I("Boka/säkra skjutbanan", big ? 90 : 45),
                I("Kontrollera antal eldplatser och kulfång", big ? 30 : 14),
                I("Ljudanläggning / skjutledarkommandon", 7),
                I("Skyltning och avspärrning", 3),
                I("Städ- och återställningsplan", 1),
            }));
            list.Add(new("Materiel", new()
            {
                I("Räkna och beställ tavlor (deltagare × serier)", big ? 45 : 21),
                I("Klister/tejp och markeringsutrustning", 7),
                I("Ladda batterier / kontrollera högtalare", 2),
            }));
            AddCommon(list, mid, big, falt: false);
            return list;
        }

        /// <summary>Milsnabb (omgång 3, persona 3): precisionens mall med snabbserierna — vändanordning och tidtagning.</summary>
        private static List<PrepArea> Milsnabb(bool mid, bool big)
        {
            var list = Precision(mid, big);
            var bana = list.First(a => a.Area == "Mark & bana");
            bana.Items.Insert(1, I("Vändanordning för figurerna och tidtagning för serierna 10, 8 och 6 sekunder", big ? 45 : 21));
            bana.Items.Insert(2, I("Prova vändningen och tiderna på banan", 7));
            return list;
        }

        private static List<PrepArea> Spring(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Bana", new()
            {
                I("Märk ut varv-slingan", big ? 30 : 14),
                I("Bygg straffrunde-slingan (~60 m)", big ? 30 : 14),
                I("Sprintfigurer och skjutplatser", 7),
                I("Start/mål + tidtagningsgrindar", 3),
                I("Skyltning, avspärrning och gångstråk", 2),
            }));
            list.Add(new("Materiel", new()
            {
                I("Tidtagningsutrustning (löpande klocka)", big ? 30 : 14),
                I("Varvräknar-blad och nummervästar", 7),
                I("Straffrunde-markörer och koner", 3),
                I("Reservutrustning för tidtagning", 7),
            }));
            var sakerhet = Sakerhet(big);
            sakerhet.Items.Add(I("Vätskestationer", 3));
            var sekretariat = Sekretariat(big, "Publicera startlistan (individuellt, masstart eller stafett)");
            sekretariat.Items.Insert(1, I("Ålders- och könsklasser", 5));
            AddCommon(list, mid, big, falt: false, sekretariat, sakerhet);
            return list;
        }

        private static List<PrepArea> Falt(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Banläggning", new()
            {
                I("Lägg banan — stationerna i Fältkonfiguratorn", big ? 60 : 30),
                I("Begär banläggarens godkännande i Fältkonfiguratorn", big ? 45 : 21),
                I("Bestäm patrullernas väg och tiden mellan patrullerna", 14),
                I("Mörkerbelysning (om mörkerfältskjutning)", 14),
            }));
            list.Add(new(BuildCourseArea, new()
            {
                I("Placera ut figurer på rätt avstånd per station", 3),
                I("Skärmar, säkerhetsvinklar och kulfång per station", 3),
                I("Gångstråk och skyltning mellan stationer", 2),
                I("Besiktiga banan före tävling", 1),
            }));
            list.Add(new("Materiel", new()
            {
                I("Beställ figurer per station (Figurkatalogen)", big ? 45 : 21),
                I("Markeringsmateriel per station", 7),
                I("Tidur och QR-kort per station", 3),
            }));
            AddCommon(list, mid, big, falt: true);
            return list;
        }

        private static PrepArea TavlingsledningArea(bool big) => new("Tävlingsledning", new()
        {
            I("Utse tävlingsledare", big ? 120 : 60),
            I("Ta fram tidsplanen för tävlingsdagarna", big ? 90 : 45),
            I("Fördela ansvar per område", big ? 75 : 30),
            I("Ackreditering funktionärer", big ? 21 : 10),
        });
        private static PrepArea MarknadArea() => new("Marknad & media", new()
        {
            I("Program och sponsorer", 60),
            I("Sociala medier", 30),
            I("Fotograf / press", 14),
        });
    }
}
