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
    /// <see cref="DueFromAnchor"/> räknar en förbundsfrist ur tävlingens dag ("1 oktober året före").
    /// </summary>
    internal record PrepItem(string Title, int? DaysBeforeComp, string? Description = null, bool Milestone = false,
        bool ToForbundet = false, Func<DateTime, DateTime>? DueFromAnchor = null, Func<DateTime, string>? TitleFromAnchor = null);
    internal record PrepArea(string Area, List<PrepItem> Items);

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
        public static List<PrepArea> For(string? discipline, string? size)
        {
            var s = (size ?? "klubb").ToLowerInvariant();
            var big = s == "sm";
            var mid = s == "krets" || big;
            var d = (discipline ?? "").ToLowerInvariant();

            if (d is "faltskytte" or "magnumfalt") return Falt(mid, big);
            if (d == "springskytte") return Spring(mid, big);
            return Precision(mid, big);
        }

        private static PrepItem I(string title, int? days = null) => new(title, days);
        private static PrepItem M(string title, int? days, string description, bool toForbundet = false, Func<DateTime, DateTime>? rule = null)
            => new(title, days, description, true, toForbundet, rule);

        /// <summary>
        /// SM: förbundets frister som milstolpar (beslut 9.1 och 10.1–10.2). SM är förbundets uppdrag — ingen sanktion att söka.
        /// <para>P4 (Stefan 2026-10-24): SM-annonsen ska med i förbundets tidning — den stora SM-veckan i nr 2, ett SM senare
        /// på året (springskytte) i nr 3. <see cref="NpIssues.ForCompetition"/> väljer numret ur tävlingens dag och
        /// uppskattar manusstoppet ur förbundets utgivningsplan. Titel och datum går att ändra på milstolpen.</para>
        /// </summary>
        private static PrepArea Forbundet(bool falt) => new("Förbundet", new List<PrepItem>
        {
            M("Ansökan om tävlingen till förbundet", null,
                "Förbundets frist: 1 oktober året före tävlingsåret. Ansökan går genom kretsen.", false, d => new DateTime(d.Year - 1, 10, 1)),
            M("Tävlingen med i förbundets stomprogram", null,
                "Förbundets frist: 30 juni året före tävlingsåret.", false, d => new DateTime(d.Year - 1, 6, 30)),
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
                "Banans underlag (målförutsättningar och stationsbeskrivningar) ska vara hos förbundet minst 12 veckor före tävlingen.", true) }
            : Array.Empty<PrepItem>()).ToList());

        // --- Common områden shared by all disciplines ---
        private static PrepArea Admin(bool big) => new("Sanktion & administration", new()
        {
            // SM är förbundets uppdrag; där ersätts sanktionen av området Förbundet.
            big ? I("Kontakta förbundets SM-ansvarige", 365) : I("Sök sanktion (SPSF/krets)", 60),
            I("Boka domare / jury", big ? 90 : 45),
            I("Försäkring och tillstånd", big ? 90 : 45),
            I("Fastställ tidsplan och ansvarsfördelning", big ? 75 : 30),
        });
        private static PrepArea Kansli(bool big) => new("Kansli & inbjudan", new()
        {
            I("Publicera inbjudan", big ? 75 : 35),
            I("Öppna anmälan", big ? 60 : 30),
            I("Bekräfta anmälningar / seedning", big ? 14 : 7),
            I("Publicera startlista", 3),
        });
        private static PrepArea Sekretariat(bool big) => new("Sekretariat", new()
        {
            I("Startlistor och lottning", 3),
            I("Förbered resultatinmatning / live-tavla", 2),
            I("Priser, medaljer och diplom", big ? 30 : 10),
            I("Standardmedaljer / märken att dela ut", big ? 30 : 14),
        });
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
                I("Första hjälpen", 7),
            }));
            list.Add(Sekretariat(big));
            list.Add(Kansli(big));
            if (mid) list.Add(Servering(big));
            if (big) { list.Insert(0, Forbundet(false)); list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
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
            list.Add(new("Sjukvård", new()
            {
                I("Sjukvårdsansvarig och första hjälpen", big ? 30 : 14),
                I("Hjärtstartare på plats", big ? 21 : 10),
                I("Vätskestationer", 3),
            }));
            list.Add(new("Materiel", new()
            {
                I("Tidtagningsutrustning (löpande klocka)", big ? 30 : 14),
                I("Varvräknar-blad och nummervästar", 7),
                I("Straffrunde-markörer och koner", 3),
                I("Reservutrustning för tidtagning", 7),
            }));
            list.Add(new("Sekretariat", new()
            {
                I("Startlistor (individuellt + mass-start/stafett)", 3),
                I("Ålders- och könsklasser", 5),
                I("Tid → resultat och live-tavla", 2),
                I("Priser och medaljer", big ? 30 : 10),
            }));
            list.Add(Kansli(big));
            if (mid) list.Add(Servering(big));
            if (big) { list.Insert(0, Forbundet(false)); list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
            return list;
        }

        private static List<PrepArea> Falt(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Bankonfiguration", new()
            {
                I("Färdigställ stationskonfiguration (Fältkonfigurator)", big ? 60 : 30),
                I("Begär Banläggare-godkännande av konfigurationen", big ? 45 : 21),
                I("Fastställ patrullflöde och utsläppsintervall", 14),
                I("Mörkerbelysning (om mörkerfältskjutning)", 14),
            }));
            list.Add(new("Stationsbygge", new()
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
                I("Sjukvård och första hjälpen", 7),
            }));
            list.Add(Sekretariat(big));
            list.Add(Kansli(big));
            if (mid) list.Add(Servering(big));
            if (big) { list.Insert(0, Forbundet(true)); list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
            return list;
        }

        private static PrepArea TavlingsledningArea(bool big) => new("Tävlingsledning", new()
        {
            I("Utse tävlingsledare och säkerhetschef", big ? 120 : 60),
            I("Ta fram tidsplan och säkerhetsplan", big ? 90 : 45),
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
