using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Ett ärende som en styrelseledamot vill ta upp på ett styrelsemöte (2026-10-08).
    ///
    /// <para><b>Ärendekön:</b> ledamoten anmäler, sekreteraren eller ordföranden (eller
    /// administratören) placerar ärendet som en UNDERPUNKT på ett kommande möte. Ett ärende kan
    /// också komma från en motion (styrelsen ska yttra sig) eller vara en inkommen skrivelse som
    /// sekreteraren lägger in för hand — se <see cref="SourceKind"/>.</para>
    ///
    /// <para><b>⚠️⚠️ "Placerat" och "behandlat" lagras INTE.</b> Läget härleds ur den aktiva
    /// dagordningspunkt som bär <c>IssueId</c> och ur dess mötes status (<see cref="BoardIssueRules.StateOf"/>).
    /// En lagrad status hade behövt skrivas om på tre ställen — vid placering, vid borttagen punkt
    /// och vid justering — och en missad skrivning är en tyst lögn om vad som hänt med ärendet.
    /// Det enda som lagras är det som INTE går att härleda: att ärendet återkallats eller avvisats.</para>
    /// </summary>
    [TableName("BoardIssues")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardIssue
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Body { get; set; }
        /// <summary><see cref="BoardIssueKinds"/>: Beslut / Information / Diskussion.</summary>
        public string Kind { get; set; } = BoardIssueKinds.Decision;
        /// <summary><see cref="BoardIssueSources"/>: Ledamot / Motion / Skrivelse.</summary>
        public string SourceKind { get; set; } = BoardIssueSources.Member;
        public int? SourceRefId { get; set; }
        public int SubmittedByMemberId { get; set; }
        public DateTime SubmittedDate { get; set; }
        /// <summary>Det möte ledamoten önskar. null = nästa möte / ingen särskild önskan.</summary>
        public int? WishMeetingId { get; set; }
        /// <summary>null = öppet. Annars <see cref="BoardIssueClosed"/>.</summary>
        public string? ClosedStatus { get; set; }
        public string? ClosedReason { get; set; }
        public int? ClosedByMemberId { get; set; }
        public DateTime? ClosedDate { get; set; }
        /// <summary>När den som anmälde fick besked om beslutet. Spärren mot ett andra besked.</summary>
        public DateTime? DecisionNotifiedDate { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public static class BoardIssueKinds
    {
        public const string Decision = "Beslut";
        public const string Information = "Information";
        public const string Discussion = "Diskussion";
        public static readonly string[] All = { Decision, Information, Discussion };

        public static string Normalize(string? k) =>
            All.FirstOrDefault(x => string.Equals(x, (k ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Decision;

        /// <summary>Hur sorten läses i en mening: "Vad gäller det? Ett beslut."</summary>
        public static string Label(string? k) => Normalize(k) switch
        {
            Information => "Information",
            Discussion => "Diskussion",
            _ => "Beslut"
        };
    }

    public static class BoardIssueSources
    {
        public const string Member = "Ledamot";
        public const string Motion = "Motion";
        public const string Letter = "Skrivelse";
    }

    public static class BoardIssueClosed
    {
        /// <summary>Den som anmälde drog tillbaka ärendet innan det placerades.</summary>
        public const string Withdrawn = "Aterkallad";
        /// <summary>Sekreteraren tog bort det ur kön, med ett skäl som den som anmälde får läsa.</summary>
        public const string Rejected = "Avvisad";
    }

    /// <summary>Ett ärendes härledda läge.</summary>
    public enum BoardIssueState
    {
        /// <summary>I kön, inte på något möte.</summary>
        Waiting,
        /// <summary>Underpunkt på ett möte som ännu inte är justerat.</summary>
        Placed,
        /// <summary>Underpunkt på ett möte vars protokoll är justerat.</summary>
        Handled,
        Withdrawn,
        Rejected
    }

    /// <summary>
    /// Reglerna för ärendekön som rena funktioner — ingen databas, så de går att pröva.
    /// </summary>
    public static class BoardIssueRules
    {
        /// <summary>
        /// Läget, härlett. <paramref name="placedMeetingStatus"/> är status på mötet där ärendets
        /// aktiva punkt ligger, eller null om det inte ligger på något aktivt möte.
        /// </summary>
        public static BoardIssueState StateOf(string? closedStatus, string? placedMeetingStatus)
        {
            // En placerad punkt vinner över en stängning: har ärendet behandlats står det kvar som
            // behandlat, även om någon senare försöker återkalla det.
            if (placedMeetingStatus != null)
                return placedMeetingStatus == "Justerat" ? BoardIssueState.Handled : BoardIssueState.Placed;
            return closedStatus switch
            {
                BoardIssueClosed.Withdrawn => BoardIssueState.Withdrawn,
                BoardIssueClosed.Rejected => BoardIssueState.Rejected,
                _ => BoardIssueState.Waiting
            };
        }

        public static string StateLabel(BoardIssueState s) => s switch
        {
            BoardIssueState.Waiting => "Väntar på placering",
            BoardIssueState.Placed => "Placerad",
            BoardIssueState.Handled => "Behandlad",
            BoardIssueState.Withdrawn => "Återkallad",
            BoardIssueState.Rejected => "Avvisad",
            _ => ""
        };

        /// <summary>
        /// Underpunktens bokstav: 0 → "a", 25 → "z", 26 → "aa". En dagordning med fler än 26
        /// underpunkter under samma punkt är osannolik, men den får inte krascha.
        /// </summary>
        public static string Letter(int index)
        {
            if (index < 0) index = 0;
            var s = "";
            int n = index;
            do
            {
                s = (char)('a' + n % 26) + s;
                n = n / 26 - 1;
            } while (n >= 0);
            return s;
        }

        /// <summary>Paragrafnummer för en punkt eller underpunkt: "§7" eller "§7b".</summary>
        public static string ParagraphLabel(int topNumber, int? subIndex) =>
            subIndex.HasValue ? $"§{topNumber}{Letter(subIndex.Value)}" : $"§{topNumber}";

        /// <summary>
        /// Dagordningen i läsordning med varje punkts paragraf: huvudpunkterna numreras 1, 2, 3 …
        /// och varje huvudpunkts underpunkter direkt efter den med a, b, c. Numren HÄRLEDS här och
        /// lagras aldrig — en flyttad eller borttagen punkt ger rätt numrering vid nästa läsning.
        ///
        /// <para>⚠️ En underpunkt vars huvudpunkt är borttagen (eller inte finns i listan) visas som
        /// en egen huvudpunkt i stället för att försvinna. Ett ärende som tyst faller ur protokollet
        /// är värre än ett som hamnar på fel nivå.</para>
        /// </summary>
        public static List<(BoardMeetingAgendaItem Item, string Label, bool IsSub)> Ordered(
            IEnumerable<BoardMeetingAgendaItem> items)
        {
            var all = items.ToList();
            var ids = new HashSet<int>(all.Select(a => a.Id));
            // Bara EN nivå: en punkt vars förälder själv är en underpunkt räknas som huvudpunkt,
            // annars skulle den aldrig skrivas ut.
            var parentLevel = new HashSet<int>(all
                .Where(a => !a.ParentItemId.HasValue || !ids.Contains(a.ParentItemId.Value))
                .Select(a => a.Id));
            bool IsTop(BoardMeetingAgendaItem a) => !a.ParentItemId.HasValue || !parentLevel.Contains(a.ParentItemId.Value);

            var result = new List<(BoardMeetingAgendaItem, string, bool)>();
            int n = 0;
            foreach (var top in all.Where(IsTop).OrderBy(a => a.SortOrder).ThenBy(a => a.Id))
            {
                n++;
                result.Add((top, ParagraphLabel(n, null), false));
                int s = 0;
                foreach (var sub in all.Where(a => a.ParentItemId == top.Id).OrderBy(a => a.SortOrder).ThenBy(a => a.Id))
                    result.Add((sub, ParagraphLabel(n, s++), true));
            }
            return result;
        }

        /// <summary>
        /// Får ett ärende placeras på det här mötet? Bara möten som går att ändra: ett protokoll
        /// som skickats för justering eller är justerat är låst.
        /// </summary>
        public static string? PlaceRefusal(string meetingStatus, bool meetingActive)
        {
            if (!meetingActive) return "Mötet finns inte längre.";
            if (meetingStatus is "VantarJustering" or "Justerat")
                return "Mötets protokoll är låst för justering. Välj ett annat möte.";
            return null;
        }
    }
}
