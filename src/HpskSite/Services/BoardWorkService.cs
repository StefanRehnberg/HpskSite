using System.Text.Json;
using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Ärendekön och motionerna i styrelsearbetet (2026-10-08).
    ///
    /// <para>Ett ärende och en motion hamnar på ett möte på SAMMA sätt: som en underpunkt
    /// (<see cref="BoardMeetingAgendaItem.ParentItemId"/>) med en pekare tillbaka
    /// (<see cref="BoardMeetingAgendaItem.IssueId"/> / <see cref="BoardMeetingAgendaItem.MotionId"/>).
    /// <see cref="PlaceOnMeeting"/> är den ENDA vägen dit, så de två kan inte glida isär.</para>
    ///
    /// <para><b>⚠️ Läget härleds, det lagras inte.</b> Ett ärende är "placerat" så länge det finns en
    /// AKTIV punkt på ett AKTIVT möte som pekar på det, och "behandlat" när det mötet är justerat. Tas
    /// mötet eller punkten bort är ärendet tillbaka i kön utan att någon behöver komma ihåg det.</para>
    /// </summary>
    public class BoardWorkService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly ILogger<BoardWorkService> _logger;

        public BoardWorkService(IScopeProvider scopeProvider, IMemberService memberService,
            IContentService contentService, ClubService clubService, ILogger<BoardWorkService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _contentService = contentService;
            _clubService = clubService;
            _logger = logger;
        }

        // =====================================================================
        //  Placering — gemensam för ärenden och motioner
        // =====================================================================

        /// <summary>Var en punkt som pekar på ett ärende eller en motion ligger.</summary>
        public class Placement
        {
            public int AgendaItemId { get; set; }
            public int MeetingId { get; set; }
            public string MeetingTitle { get; set; } = "";
            public DateTime MeetingDate { get; set; }
            public string MeetingStatus { get; set; } = "";
            public string? Decision { get; set; }
            public string Paragraph { get; set; } = "";
        }

        private class PlacementRow
        {
            public int AgendaItemId { get; set; }
            public int RefId { get; set; }
            public int MeetingId { get; set; }
            public string MeetingTitle { get; set; } = "";
            public DateTime MeetingDate { get; set; }
            public string MeetingStatus { get; set; } = "";
            public string? Decision { get; set; }
        }

        /// <summary>
        /// Aktiva placeringar för en mängd ärenden eller motioner, i EN fråga. Paragrafen räknas ut
        /// per berört möte (en fråga per möte, och det är få möten).
        /// </summary>
        private Dictionary<int, Placement> GetPlacements(string column, IEnumerable<int> refIds)
        {
            var ids = refIds.Distinct().ToList();
            var result = new Dictionary<int, Placement>();
            if (ids.Count == 0) return result;

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rows = new List<PlacementRow>();
            // IN (@0) har ett tak kring 2100 parametrar — dela upp.
            foreach (var chunk in ids.Chunk(1000))
            {
                rows.AddRange(db.Fetch<PlacementRow>(
                    $"SELECT a.Id AS AgendaItemId, a.{column} AS RefId, m.Id AS MeetingId, m.Title AS MeetingTitle, " +
                    "m.MeetingDate, m.Status AS MeetingStatus, a.Decision " +
                    "FROM BoardMeetingAgendaItems a JOIN BoardMeetings m ON m.Id = a.MeetingId " +
                    $"WHERE a.IsActive = 1 AND m.IsActive = 1 AND a.{column} IN (@0) ORDER BY m.MeetingDate DESC, a.Id DESC",
                    chunk));
            }

            var labelCache = new Dictionary<int, Dictionary<int, string>>();
            foreach (var r in rows)
            {
                if (result.ContainsKey(r.RefId)) continue;   // nyaste placeringen vinner
                if (!labelCache.TryGetValue(r.MeetingId, out var labels))
                {
                    var agenda = db.Fetch<BoardMeetingAgendaItem>(
                        "SELECT * FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1", r.MeetingId);
                    labels = BoardIssueRules.Ordered(agenda).ToDictionary(x => x.Item.Id, x => x.Label);
                    labelCache[r.MeetingId] = labels;
                }
                result[r.RefId] = new Placement
                {
                    AgendaItemId = r.AgendaItemId,
                    MeetingId = r.MeetingId,
                    MeetingTitle = r.MeetingTitle,
                    MeetingDate = r.MeetingDate,
                    MeetingStatus = r.MeetingStatus,
                    Decision = r.Decision,
                    Paragraph = labels.TryGetValue(r.AgendaItemId, out var l) ? l : ""
                };
            }
            return result;
        }

        public Placement? GetIssuePlacement(int issueId) =>
            GetPlacements("IssueId", new[] { issueId }).GetValueOrDefault(issueId);

        public Placement? GetMotionPlacement(int motionId) =>
            GetPlacements("MotionId", new[] { motionId }).GetValueOrDefault(motionId);

        /// <summary>
        /// Lägger en punkt för ett ärende eller en motion på ett möte.
        ///
        /// <para><paramref name="parentItemId"/> = huvudpunkten den ska ligga under (blir en
        /// underpunkt med bokstav). null = som en egen huvudpunkt, före mötets avslutande.</para>
        ///
        /// <para><b>⚠️ Ligger den redan på ett annat möte flyttas den</b> — den gamla punkten tas bort i
        /// samma scope. Ett ärende på två möten samtidigt hade gett två beslut om samma sak. Men en
        /// punkt på ett LÅST protokoll flyttas aldrig: det som beslutats står kvar.</para>
        /// </summary>
        public (bool Ok, string? Message, int AgendaItemId) PlaceOnMeeting(
            string column, int refId, string heading, string itemType, int meetingId, int? parentItemId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var meeting = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (meeting == null) return (false, "Mötet hittades inte.", 0);
            var refusal = BoardIssueRules.PlaceRefusal(meeting.Status, meeting.IsActive);
            if (refusal != null) return (false, refusal, 0);

            // Befintliga placeringar. Låst = får inte flyttas; olåst = tas bort här.
            var existing = db.Fetch<BoardMeetingAgendaItem>(
                $"SELECT a.* FROM BoardMeetingAgendaItems a JOIN BoardMeetings m ON m.Id = a.MeetingId " +
                $"WHERE a.IsActive = 1 AND m.IsActive = 1 AND a.{column} = @0", refId);
            foreach (var ex in existing)
            {
                var exMeeting = db.SingleOrDefaultById<BoardMeeting>(ex.MeetingId);
                if (exMeeting != null && exMeeting.Status is "VantarJustering" or "Justerat")
                    return (false, $"Den ligger redan på {exMeeting.Title} ({exMeeting.MeetingDate:yyyy-MM-dd}), vars protokoll är låst.", 0);
            }
            foreach (var ex in existing)
            {
                ex.IsActive = false;
                db.Update(ex);
            }

            if (parentItemId.HasValue)
            {
                var parent = db.SingleOrDefaultById<BoardMeetingAgendaItem>(parentItemId.Value);
                if (parent == null || !parent.IsActive || parent.MeetingId != meetingId)
                    return (false, "Punkten finns inte på det mötet.", 0);
                // Bara EN nivå: en underpunkt kan inte ha egna underpunkter.
                if (parent.ParentItemId.HasValue)
                    return (false, "Välj en huvudpunkt, inte en underpunkt.", 0);

                var nextSort = db.ExecuteScalar<int?>(
                    "SELECT MAX(SortOrder) FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 AND ParentItemId = @1",
                    meetingId, parentItemId.Value) ?? -1;
                var item = NewItem(meetingId, heading, itemType, nextSort + 1, parentItemId, column, refId);
                db.Insert(item);
                return (true, null, item.Id);
            }

            // Egen huvudpunkt: före "Mötets avslutande" om sista punkten är den, annars sist.
            var tops = db.Fetch<BoardMeetingAgendaItem>(
                "SELECT * FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 AND ParentItemId IS NULL ORDER BY SortOrder, Id",
                meetingId);
            int insertAt = tops.Count;
            if (tops.Count > 0 && tops[^1].Heading.Contains("avslut", StringComparison.OrdinalIgnoreCase))
                insertAt = tops.Count - 1;

            var newTop = NewItem(meetingId, heading, itemType, insertAt, null, column, refId);
            db.Insert(newTop);
            tops.Insert(insertAt, newTop);
            for (int i = 0; i < tops.Count; i++)
            {
                if (tops[i].SortOrder == i) continue;
                tops[i].SortOrder = i;
                db.Update(tops[i]);
            }
            return (true, null, newTop.Id);
        }

        private static BoardMeetingAgendaItem NewItem(int meetingId, string heading, string itemType, int sort,
            int? parentId, string column, int refId)
        {
            var item = new BoardMeetingAgendaItem
            {
                MeetingId = meetingId,
                SortOrder = sort,
                Heading = heading.Length > 300 ? heading.Substring(0, 300) : heading,
                ItemType = itemType == "note" ? "note" : "text",
                ElectionCount = 1,
                ElectionSource = "attendees",
                ParentItemId = parentId,
                IsActive = true
            };
            if (column == "IssueId") item.IssueId = refId; else item.MotionId = refId;
            return item;
        }

        /// <summary>Tar en placering tillbaka till kön. Vägrar på ett låst protokoll.</summary>
        public (bool Ok, string? Message) Unplace(string column, int refId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var items = db.Fetch<BoardMeetingAgendaItem>(
                $"SELECT a.* FROM BoardMeetingAgendaItems a JOIN BoardMeetings m ON m.Id = a.MeetingId " +
                $"WHERE a.IsActive = 1 AND m.IsActive = 1 AND a.{column} = @0", refId);
            if (items.Count == 0) return (false, "Den ligger inte på något möte.");
            foreach (var it in items)
            {
                var m = db.SingleOrDefaultById<BoardMeeting>(it.MeetingId);
                if (m != null && m.Status is "VantarJustering" or "Justerat")
                    return (false, "Protokollet är låst för justering. Återöppna det först.");
            }
            foreach (var it in items) { it.IsActive = false; db.Update(it); }
            return (true, null);
        }

        /// <summary>Kommande, olåsta möten att placera på, med deras huvudpunkter.</summary>
        public List<(BoardMeeting Meeting, List<(BoardMeetingAgendaItem Item, string Label)> Tops)> GetPlaceableMeetings(
            int ownerType, int ownerId, bool annualOnly = false)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var meetings = db.Fetch<BoardMeeting>(
                "SELECT * FROM BoardMeetings WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 " +
                "AND Status NOT IN ('VantarJustering','Justerat') AND MeetingDate >= @2 ORDER BY MeetingDate",
                ownerType, ownerId, DateTime.Today);
            if (annualOnly) meetings = meetings.Where(m => m.IsAnnualMeeting).ToList();

            var result = new List<(BoardMeeting, List<(BoardMeetingAgendaItem, string)>)>();
            foreach (var m in meetings)
            {
                var agenda = db.Fetch<BoardMeetingAgendaItem>(
                    "SELECT * FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1", m.Id);
                var tops = BoardIssueRules.Ordered(agenda).Where(x => !x.IsSub)
                    .Select(x => (x.Item, x.Label)).ToList();
                result.Add((m, tops));
            }
            return result;
        }

        // =====================================================================
        //  Ärenden
        // =====================================================================

        public BoardIssue? GetIssue(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var i = scope.Database.SingleOrDefaultById<BoardIssue>(id);
            return i != null && i.IsActive ? i : null;
        }

        public BoardIssue CreateIssue(int ownerType, int ownerId, string title, string? body, string kind,
            int? wishMeetingId, int submittedBy, string sourceKind = BoardIssueSources.Member, int? sourceRefId = null)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var issue = new BoardIssue
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                Title = Trunc(title.Trim(), 300),
                Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
                Kind = BoardIssueKinds.Normalize(kind),
                SourceKind = sourceKind,
                SourceRefId = sourceRefId,
                SubmittedByMemberId = submittedBy,
                SubmittedDate = DateTime.Now,
                WishMeetingId = wishMeetingId > 0 ? wishMeetingId : null,
                IsActive = true
            };
            scope.Database.Insert(issue);
            return issue;
        }

        /// <summary>Ändra ett ärende — bara så länge det väntar i kön.</summary>
        public (bool Ok, string? Message) UpdateIssue(int id, string title, string? body, string kind, int? wishMeetingId)
        {
            if (GetIssuePlacement(id) != null) return (false, "Ärendet ligger redan på ett möte och kan inte ändras här.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var i = db.SingleOrDefaultById<BoardIssue>(id);
            if (i == null || !i.IsActive || i.ClosedStatus != null) return (false, "Ärendet går inte att ändra.");
            i.Title = Trunc(title.Trim(), 300);
            i.Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
            i.Kind = BoardIssueKinds.Normalize(kind);
            i.WishMeetingId = wishMeetingId > 0 ? wishMeetingId : null;
            db.Update(i);
            return (true, null);
        }

        /// <summary>
        /// Stänger ett ärende som väntar: återkallat av den som anmälde det, eller avvisat av
        /// sekreteraren med ett skäl. Ett PLACERAT ärende stängs aldrig så här — det tas först
        /// tillbaka till kön, så att stängningen inte lämnar en punkt kvar på dagordningen.
        /// </summary>
        public (bool Ok, string? Message) CloseIssue(int id, string closedStatus, string? reason, int byMemberId)
        {
            if (GetIssuePlacement(id) != null)
                return (false, "Ärendet ligger på ett möte. Ta tillbaka det till kön först.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var i = db.SingleOrDefaultById<BoardIssue>(id);
            if (i == null || !i.IsActive) return (false, "Ärendet hittades inte.");
            if (i.ClosedStatus != null) return (false, "Ärendet är redan stängt.");
            i.ClosedStatus = closedStatus;
            i.ClosedReason = string.IsNullOrWhiteSpace(reason) ? null : Trunc(reason.Trim(), 1000);
            i.ClosedByMemberId = byMemberId;
            i.ClosedDate = DateTime.Now;
            db.Update(i);
            return (true, null);
        }

        public class IssueView
        {
            public BoardIssue Issue { get; set; } = new();
            public string SubmittedByName { get; set; } = "";
            public BoardIssueState State { get; set; }
            public Placement? Placement { get; set; }
            public string? WishMeetingLabel { get; set; }
        }

        public List<IssueView> GetIssues(int ownerType, int ownerId, int? onlySubmittedBy = null)
        {
            List<BoardIssue> issues;
            List<BoardMeeting> meetings;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                issues = onlySubmittedBy.HasValue
                    ? db.Fetch<BoardIssue>("SELECT * FROM BoardIssues WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 AND SubmittedByMemberId = @2 ORDER BY SubmittedDate DESC",
                        ownerType, ownerId, onlySubmittedBy.Value)
                    : db.Fetch<BoardIssue>("SELECT * FROM BoardIssues WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 ORDER BY SubmittedDate DESC",
                        ownerType, ownerId);
                meetings = db.Fetch<BoardMeeting>("SELECT * FROM BoardMeetings WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            }
            var placements = GetPlacements("IssueId", issues.Select(i => i.Id));
            var names = ResolveNames(issues.Select(i => i.SubmittedByMemberId));
            var meetingById = meetings.ToDictionary(m => m.Id);

            return issues.Select(i =>
            {
                placements.TryGetValue(i.Id, out var p);
                string? wish = null;
                if (i.WishMeetingId.HasValue && meetingById.TryGetValue(i.WishMeetingId.Value, out var wm))
                    wish = $"{wm.Title} {wm.MeetingDate:yyyy-MM-dd}";
                return new IssueView
                {
                    Issue = i,
                    SubmittedByName = names.GetValueOrDefault(i.SubmittedByMemberId, ""),
                    State = BoardIssueRules.StateOf(i.ClosedStatus, p?.MeetingStatus),
                    Placement = p,
                    WishMeetingLabel = wish
                };
            }).ToList();
        }

        /// <summary>
        /// Väntande ärenden som önskats till just det här mötet (eller "nästa möte" när det här är
        /// nästa kommande). Visas i mötets förberedelse, så sekreteraren ser dem där dagordningen görs.
        /// </summary>
        public List<IssueView> GetWaitingIssuesForMeeting(BoardMeeting meeting)
        {
            var waiting = GetIssues(meeting.OwnerType, meeting.OwnerId).Where(v => v.State == BoardIssueState.Waiting).ToList();
            int? nextMeetingId;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                nextMeetingId = scope.Database.ExecuteScalar<int?>(
                    "SELECT TOP 1 Id FROM BoardMeetings WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 " +
                    "AND Status NOT IN ('VantarJustering','Justerat') AND MeetingDate >= @2 ORDER BY MeetingDate",
                    meeting.OwnerType, meeting.OwnerId, DateTime.Today);
            }
            return waiting.Where(v => v.Issue.WishMeetingId == meeting.Id
                || (!v.Issue.WishMeetingId.HasValue && nextMeetingId == meeting.Id)).ToList();
        }

        // =====================================================================
        //  Motioner
        // =====================================================================

        public BoardMotion? GetMotion(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var m = scope.Database.SingleOrDefaultById<BoardMotion>(id);
            return m != null && m.IsActive ? m : null;
        }

        // ---- Bilagor ----------------------------------------------------------------------------

        public const int MaxAttachmentsPerMotion = 5;

        public BoardMotionAttachment? GetAttachment(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var a = scope.Database.SingleOrDefaultById<BoardMotionAttachment>(id);
            return a != null && a.IsActive ? a : null;
        }

        public List<BoardMotionAttachment> GetAttachments(int motionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardMotionAttachment>(
                "SELECT * FROM BoardMotionAttachments WHERE IsActive = 1 AND MotionId = @0 ORDER BY UploadedDate", motionId);
        }

        public int CountAttachments(int motionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM BoardMotionAttachments WHERE IsActive = 1 AND MotionId = @0", motionId);
        }

        public BoardMotionAttachment AddAttachment(int motionId, string fileName, string storedFileName, long size, int byMemberId)
        {
            var a = new BoardMotionAttachment
            {
                MotionId = motionId, FileName = Trunc(fileName, 260), StoredFileName = storedFileName,
                FileSize = size, UploadedByMemberId = byMemberId, UploadedDate = DateTime.Now, IsActive = true
            };
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Insert(a);
            return a;
        }

        /// <summary>Gömmer bilagan. Filen ligger kvar — raden kan redan ha lästs ut till kallelsen.</summary>
        public void RemoveAttachment(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("UPDATE BoardMotionAttachments SET IsActive = 0 WHERE Id = @0", id);
        }

        /// <summary>Nästa kommande ordinarie årsmöte för en klubb eller krets, om det är inlagt.</summary>
        public BoardMeeting? GetUpcomingAnnualMeeting(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<BoardMeeting>(
                "SELECT TOP 1 * FROM BoardMeetings WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 " +
                "AND MeetingType = 'Arsmote' AND MeetingDate >= @2 ORDER BY MeetingDate",
                ownerType, ownerId, DateTime.Today);
        }

        public bool SetMotionDeadline(int meetingId, DateTime? deadline)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (m == null || !m.IsAnnualMeeting) return false;
            m.MotionDeadline = deadline?.Date;
            db.Update(m);
            return true;
        }

        /// <summary>
        /// Lämnar in en motion. Numret tas under lås (UPDLOCK/HOLDLOCK) så att två samtidiga
        /// inlämningar inte får samma nummer; det unika indexet är spärren bakom.
        /// </summary>
        public BoardMotion SubmitMotion(BoardMotion m)
        {
            m.Title = Trunc(m.Title.Trim(), 300);
            m.Background = string.IsNullOrWhiteSpace(m.Background) ? null : m.Background.Trim();
            m.SubmittedDate = DateTime.Now;
            m.Year = m.SubmittedDate.Year;
            m.IsActive = true;
            if (m.MeetingId == null)
                m.MeetingId = GetUpcomingAnnualMeeting(m.OwnerType, m.OwnerId)?.Id;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var scope = _scopeProvider.CreateScope(autoComplete: true);
                    var db = scope.Database;
                    m.Number = db.ExecuteScalar<int>(
                        "SELECT ISNULL(MAX(Number), 0) + 1 FROM BoardMotions WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1 AND [Year] = @2",
                        m.OwnerType, m.OwnerId, m.Year);
                    m.Snapshot = JsonSerializer.Serialize(new
                    {
                        number = BoardMotionRules.NumberLabel(m.OwnerType, m.Year, m.Number),
                        motioner = m.MotionerName,
                        signedBy = m.SignedByName,
                        title = m.Title,
                        background = m.Background,
                        proposals = m.ProposalList,
                        reference = m.SourceReference,
                        submitted = m.SubmittedDate.ToString("yyyy-MM-dd HH:mm")
                    });
                    db.Insert(m);
                    return m;
                }
                catch (Exception ex) when (attempt < 2 && ex.Message.Contains("UX_BoardMotions_Number"))
                {
                    _logger.LogWarning("Motionsnumret krockade för {Owner}/{Id} — försöker igen.", m.OwnerType, m.OwnerId);
                    m.Id = 0;
                }
            }
            throw new InvalidOperationException("Motionen kunde inte numreras.");
        }

        /// <summary>
        /// Inkomstdagen för en motion som sekreteraren för in i efterhand (kom på papper eller i ett
        /// mejl). Det är inkomstdagen som avgör om motionen kom i tid, inte dagen den skrevs in.
        /// </summary>
        public void SetSubmittedDate(int motionId, DateTime submitted)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMotion>(motionId);
            if (m == null) return;
            m.SubmittedDate = submitted;
            db.Update(m);
        }

        public (bool Ok, string? Message) SaveOpinion(int motionId, string? opinion, string? proposal, int byMemberId)
        {
            if (!string.IsNullOrWhiteSpace(proposal) && !BoardMotionProposals.IsValid(proposal))
                return (false, "Okänt förslag.");
            var placement = GetMotionPlacement(motionId);
            if (placement?.MeetingStatus is "VantarJustering" or "Justerat")
                return (false, "Årsmötets protokoll är låst. Yttrandet kan inte ändras nu.");

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMotion>(motionId);
            if (m == null || !m.IsActive) return (false, "Motionen hittades inte.");
            if (m.WithdrawnDate.HasValue) return (false, "Motionen är återkallad.");
            m.BoardOpinion = string.IsNullOrWhiteSpace(opinion) ? null : opinion.Trim();
            m.BoardProposal = string.IsNullOrWhiteSpace(proposal) ? null : proposal;
            m.OpinionDate = m.BoardOpinion == null && m.BoardProposal == null ? null : DateTime.Now;
            m.OpinionByMemberId = m.OpinionDate.HasValue ? byMemberId : null;
            db.Update(m);
            return (true, null);
        }

        public (bool Ok, string? Message) WithdrawMotion(int motionId)
        {
            var placement = GetMotionPlacement(motionId);
            if (placement?.MeetingStatus == "Justerat") return (false, "Motionen är redan behandlad.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMotion>(motionId);
            if (m == null || !m.IsActive) return (false, "Motionen hittades inte.");
            if (m.WithdrawnDate.HasValue) return (false, "Motionen är redan återkallad.");
            m.WithdrawnDate = DateTime.Now;
            db.Update(m);
            return (true, null);
        }

        /// <summary>
        /// Sätter, byter eller tar bort en medlems stöd. <paramref name="kind"/> null = ta bort.
        /// En rad per medlem och motion (unikt index), så ett byte från tumme upp till medmotionär
        /// är en uppdatering och aldrig två rader.
        /// </summary>
        public void SetSupport(int motionId, int memberId, string? kind)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.FirstOrDefault<BoardMotionSupport>(
                "SELECT * FROM BoardMotionSupport WHERE MotionId = @0 AND MemberId = @1", motionId, memberId);
            if (kind == null)
            {
                if (row != null) db.Delete(row);
                return;
            }
            if (row == null)
                db.Insert(new BoardMotionSupport { MotionId = motionId, MemberId = memberId, Kind = kind, CreatedDate = DateTime.Now });
            else if (row.Kind != kind)
            {
                row.Kind = kind;
                row.CreatedDate = DateTime.Now;
                db.Update(row);
            }
        }

        public class MotionView
        {
            public BoardMotion Motion { get; set; } = new();
            public BoardMotionState State { get; set; }
            public Placement? Placement { get; set; }
            public bool IsLate { get; set; }
            public DateTime? Deadline { get; set; }
            public string? MeetingLabel { get; set; }
            public List<string> CoSigners { get; set; } = new();
            /// <summary>Namnen bakom tummarna — en reaktion är aldrig anonym (Stefan 2026-10-08).</summary>
            public List<string> UpNames { get; set; } = new();
            public List<string> DownNames { get; set; } = new();
            public int Up { get; set; }
            public int Down { get; set; }
            /// <summary>Den inloggades eget stöd: null, Medmotionar, Upp, Ner.</summary>
            public string? MySupport { get; set; }
            /// <summary>Årsmötets beslut — bara när protokollet är justerat.</summary>
            public string? Decision { get; set; }
            public List<BoardMotionAttachment> Attachments { get; set; } = new();
        }

        private class SupportRow
        {
            public int MotionId { get; set; }
            public int MemberId { get; set; }
            public string Kind { get; set; } = "";
        }

        /// <summary>
        /// Motioner med härlett läge. <paramref name="ownerType"/>/<paramref name="ownerId"/> =
        /// mottagaren; eller, med <paramref name="motionerClubId"/>, en klubbs EGNA motioner till kretsen.
        /// </summary>
        public List<MotionView> GetMotions(int? ownerType, int? ownerId, int viewerMemberId,
            int? motionerClubId = null, int? motionerMemberId = null)
        {
            List<BoardMotion> motions;
            List<SupportRow> support;
            var attachments = new List<BoardMotionAttachment>();
            Dictionary<int, BoardMeeting> meetings;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                if (motionerClubId.HasValue)
                    motions = db.Fetch<BoardMotion>("SELECT * FROM BoardMotions WHERE IsActive = 1 AND MotionerKind = 'Club' AND MotionerClubId = @0 ORDER BY SubmittedDate DESC", motionerClubId.Value);
                else if (motionerMemberId.HasValue)
                    motions = db.Fetch<BoardMotion>("SELECT * FROM BoardMotions WHERE IsActive = 1 AND MotionerKind = 'Member' AND MotionerMemberId = @0 ORDER BY SubmittedDate DESC", motionerMemberId.Value);
                else
                    motions = db.Fetch<BoardMotion>("SELECT * FROM BoardMotions WHERE IsActive = 1 AND OwnerType = @0 AND OwnerId = @1 ORDER BY [Year] DESC, Number DESC", ownerType ?? 0, ownerId ?? 0);

                var ids = motions.Select(m => m.Id).ToList();
                support = new List<SupportRow>();
                foreach (var chunk in ids.Chunk(1000))
                    support.AddRange(db.Fetch<SupportRow>("SELECT MotionId, MemberId, Kind FROM BoardMotionSupport WHERE MotionId IN (@0)", chunk));

                foreach (var chunk in ids.Chunk(1000))
                    attachments.AddRange(db.Fetch<BoardMotionAttachment>("SELECT * FROM BoardMotionAttachments WHERE IsActive = 1 AND MotionId IN (@0) ORDER BY UploadedDate", chunk));

                var meetingIds = motions.Where(m => m.MeetingId.HasValue).Select(m => m.MeetingId!.Value).Distinct().ToList();
                meetings = new Dictionary<int, BoardMeeting>();
                foreach (var chunk in meetingIds.Chunk(1000))
                    foreach (var bm in db.Fetch<BoardMeeting>("SELECT * FROM BoardMeetings WHERE Id IN (@0)", chunk))
                        meetings[bm.Id] = bm;
            }

            var placements = GetPlacements("MotionId", motions.Select(m => m.Id));
            var coSignerNames = ResolveNames(support.Select(s => s.MemberId));

            return motions.Select(m =>
            {
                placements.TryGetValue(m.Id, out var p);
                meetings.TryGetValue(m.MeetingId ?? 0, out var meeting);
                var mine = support.Where(s => s.MotionId == m.Id).ToList();
                var state = BoardMotionRules.StateOf(m.WithdrawnDate.HasValue,
                    !string.IsNullOrWhiteSpace(m.BoardOpinion) || !string.IsNullOrWhiteSpace(m.BoardProposal), p?.MeetingStatus);
                return new MotionView
                {
                    Motion = m,
                    State = state,
                    Placement = p,
                    Deadline = meeting?.MotionDeadline,
                    IsLate = BoardMotionRules.IsLate(m.SubmittedDate, meeting?.MotionDeadline),
                    MeetingLabel = meeting != null ? $"{meeting.Title} {meeting.MeetingDate:yyyy-MM-dd}" : null,
                    CoSigners = mine.Where(s => s.Kind == BoardMotionSupportKinds.CoSigner)
                        .Select(s => coSignerNames.GetValueOrDefault(s.MemberId, "")).Where(n => n.Length > 0).ToList(),
                    UpNames = mine.Where(s => s.Kind == BoardMotionSupportKinds.Up)
                        .Select(s => coSignerNames.GetValueOrDefault(s.MemberId, "")).Where(n => n.Length > 0).ToList(),
                    DownNames = mine.Where(s => s.Kind == BoardMotionSupportKinds.Down)
                        .Select(s => coSignerNames.GetValueOrDefault(s.MemberId, "")).Where(n => n.Length > 0).ToList(),
                    Up = mine.Count(s => s.Kind == BoardMotionSupportKinds.Up || s.Kind == BoardMotionSupportKinds.CoSigner),
                    Down = mine.Count(s => s.Kind == BoardMotionSupportKinds.Down),
                    MySupport = viewerMemberId > 0 ? mine.FirstOrDefault(s => s.MemberId == viewerMemberId)?.Kind : null,
                    Decision = state == BoardMotionState.Decided ? p?.Decision : null,
                    Attachments = attachments.Where(a => a.MotionId == m.Id).ToList()
                };
            }).ToList();
        }

        // =====================================================================
        //  Besked efter justering
        // =====================================================================

        /// <summary>
        /// Ärenden och motioner som behandlats på ett justerat möte och vars avsändare inte fått
        /// besked än. Markeras som aviserade i samma anrop — spärren mot ett andra besked.
        /// </summary>
        public (List<(BoardIssue Issue, string Paragraph, string? Decision)> Issues,
                List<(BoardMotion Motion, string Paragraph, string? Decision)> Motions)
            ClaimDecisionNotices(int meetingId)
        {
            var issues = new List<(BoardIssue, string, string?)>();
            var motions = new List<(BoardMotion, string, string?)>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var meeting = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (meeting == null || meeting.Status != "Justerat") return (issues, motions);

            var agenda = db.Fetch<BoardMeetingAgendaItem>(
                "SELECT * FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1", meetingId);
            var labels = BoardIssueRules.Ordered(agenda).ToDictionary(x => x.Item.Id, x => x.Label);

            foreach (var a in agenda.Where(a => a.IssueId.HasValue))
            {
                var i = db.SingleOrDefaultById<BoardIssue>(a.IssueId!.Value);
                if (i == null || !i.IsActive || i.DecisionNotifiedDate.HasValue) continue;
                i.DecisionNotifiedDate = DateTime.Now;
                db.Update(i);
                issues.Add((i, labels.GetValueOrDefault(a.Id, ""), a.Decision));
            }
            foreach (var a in agenda.Where(a => a.MotionId.HasValue))
            {
                var m = db.SingleOrDefaultById<BoardMotion>(a.MotionId!.Value);
                if (m == null || !m.IsActive || m.DecisionNotifiedDate.HasValue) continue;
                m.DecisionNotifiedDate = DateTime.Now;
                db.Update(m);
                motions.Add((m, labels.GetValueOrDefault(a.Id, ""), a.Decision));
            }
            return (issues, motions);
        }

        // =====================================================================
        //  Översikten
        // =====================================================================

        /// <summary>Möten som väntar på DIN justering.</summary>
        public List<BoardMeeting> GetMeetingsAwaitingMyApproval(int ownerType, int ownerId, int memberId)
        {
            if (memberId <= 0) return new List<BoardMeeting>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardMeeting>(
                "SELECT m.* FROM BoardMeetings m JOIN BoardMeetingAttendees a ON a.MeetingId = m.Id " +
                "WHERE m.OwnerType = @0 AND m.OwnerId = @1 AND m.IsActive = 1 AND m.Status = 'VantarJustering' " +
                "AND a.MemberId = @2 AND a.ApprovedDate IS NULL AND (a.IsChairman = 1 OR a.IsSecretary = 1 OR a.IsAdjuster = 1) " +
                "ORDER BY m.MeetingDate", ownerType, ownerId, memberId);
        }

        public BoardMeeting? GetNextMeeting(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<BoardMeeting>(
                "SELECT TOP 1 * FROM BoardMeetings WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 " +
                "AND MeetingDate >= @2 ORDER BY MeetingDate", ownerType, ownerId, DateTime.Today);
        }

        public (int TopItems, int PlacedIssues, int PlacedMotions) CountAgenda(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return (
                db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 AND ParentItemId IS NULL", meetingId),
                db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 AND IssueId IS NOT NULL", meetingId),
                db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 AND MotionId IS NOT NULL", meetingId));
        }

        // =====================================================================
        //  Hjälpare
        // =====================================================================

        /// <summary>Föreningens namn och kontaktadress (klubbnoden eller kretsnoden).</summary>
        public (string Name, string? Email) OwnerContact(int ownerType, int ownerId)
        {
            try
            {
                var node = _contentService.GetById(ownerId);
                if (node == null) return (ownerType == DocumentOwnerType.Club ? (_clubService.GetClubNameById(ownerId) ?? "") : "", null);
                var name = ownerType == DocumentOwnerType.Club
                    ? (_clubService.GetClubNameById(ownerId) ?? node.Name ?? "")
                    : (node.GetValue<string>("regionName") ?? node.Name ?? "");
                var email = node.GetValue<string>("contactEmail");
                return (name, string.IsNullOrWhiteSpace(email) ? null : email.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa föreningens kontaktuppgifter ({Type}/{Id}).", ownerType, ownerId);
                return ("", null);
            }
        }

        public Dictionary<int, string> ResolveNames(IEnumerable<int> memberIds)
        {
            var byId = new Dictionary<int, string>();
            foreach (var id in memberIds.Distinct())
            {
                if (id <= 0) continue;
                var mem = _memberService.GetById(id);
                if (mem == null) continue;
                var nm = $"{mem.GetValue<string>("firstName")} {mem.GetValue<string>("lastName")}".Trim();
                byId[id] = string.IsNullOrEmpty(nm) ? mem.Name ?? "" : nm;
            }
            return byId;
        }

        public (string Name, string? Email) MemberContact(int memberId)
        {
            var mem = memberId > 0 ? _memberService.GetById(memberId) : null;
            if (mem == null) return ("", null);
            var nm = $"{mem.GetValue<string>("firstName")} {mem.GetValue<string>("lastName")}".Trim();
            return (string.IsNullOrEmpty(nm) ? mem.Name ?? "" : nm, string.IsNullOrWhiteSpace(mem.Email) ? null : mem.Email);
        }

        /// <summary>
        /// Det som saknas i databasen av det koden kräver. Tom lista = allt finns. ⚠️ KASTAR om
        /// frågan inte går att ställa — "kunde inte fråga" får aldrig översättas till ett svar.
        /// </summary>
        public List<string> MissingSchema()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var missing = new List<string>();
            foreach (var t in new[] { "BoardIssues", "BoardMotions", "BoardMotionSupport", "BoardMotionAttachments" })
                if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + t) == 0)
                    missing.Add(t);
            foreach (var (table, col) in new[] { ("BoardMeetingAgendaItems", "ParentItemId"), ("BoardMeetingAgendaItems", "IssueId"),
                                                 ("BoardMeetingAgendaItems", "MotionId"), ("BoardMeetings", "MotionDeadline") })
                if (db.ExecuteScalar<int>("SELECT CASE WHEN COL_LENGTH(@0, @1) IS NULL THEN 0 ELSE 1 END", "dbo." + table, col) == 0)
                    missing.Add($"{table}.{col}");
            return missing;
        }

        private static string Trunc(string s, int max) => s.Length > max ? s.Substring(0, max) : s;
    }
}
