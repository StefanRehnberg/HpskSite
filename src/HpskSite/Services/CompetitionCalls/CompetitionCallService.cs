using HpskSite.Models.CompetitionCalls;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Utlysningarna (kretsutlysningen F2): lagring, tillstånd och händelselogg.
    ///
    /// <para><b>⚠️ ALLA tillståndsbyten är ETT villkorat UPDATE</b> (<c>… WHERE Id=@id AND Status=…</c>) vars radantal avgör om
    /// handlingen lyckades, i samma transaktion som händelseraden. Två som utser samtidigt: den ena får 1 rad, den andra 0
    /// och svaret "Någon hann före dig. Ladda om sidan." — aldrig en tyst överskrivning. Radantalet kommer ur
    /// <c>Execute()</c>, aldrig <c>@@ROWCOUNT</c> (som NPoco gör om till en parameter).</para>
    ///
    /// <para><b>Behörighet hör INTE hit</b> (<see cref="CompetitionCallAccess"/> i kontrollern); tjänsten prövar bara att
    /// läget tillåter handlingen — så att samma regler gäller varje väg in.</para>
    /// </summary>
    public class CompetitionCallService
    {
        public const string LostRace = "Någon hann före dig. Ladda om sidan.";

        private static readonly string[] Tables = { "CompetitionCall", "CompetitionCallWish", "CompetitionCallEvent" };

        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CompetitionCallService> _logger;

        public CompetitionCallService(IScopeProvider scopeProvider, ILogger<CompetitionCallService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public readonly record struct CallResult(CompetitionCall? Call, string? Error);

        /// <summary>Saknade tabeller som text, eller null när alla finns. Kastar när frågan inte kan ställas (aldrig "saknas" av misstag).</summary>
        public string? SchemaProblem()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var have = scope.Database.Fetch<string>("SELECT name FROM sys.tables WHERE name IN ('CompetitionCall','CompetitionCallWish','CompetitionCallEvent')");
            var missing = Tables.Where(t => !have.Contains(t)).ToList();
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        public bool TablesExist() => SchemaProblem() == null;

        // ── Läsning ─────────────────────────────────────────────────────────────────────────

        public CompetitionCall? Get(int id)
        {
            if (id <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<CompetitionCall>(id);
        }

        /// <summary>Kretsens utlysningar ett år, i datumordning (datumlösa sist).</summary>
        public List<CompetitionCall> ForRegion(int regionId, int? year = null)
        {
            if (regionId <= 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var sql = "SELECT * FROM CompetitionCall WHERE RegionId = @0";
            if (year.HasValue) sql += " AND [Year] = @1";
            sql += " ORDER BY CASE WHEN CompetitionDate IS NULL THEN 1 ELSE 0 END, CompetitionDate, Name, Id";
            return scope.Database.Fetch<CompetitionCall>(sql, regionId, year ?? 0);
        }

        /// <summary>Åren kretsen har utlysningar för, nyaste först.</summary>
        public List<int> YearsFor(int regionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>("SELECT DISTINCT [Year] FROM CompetitionCall WHERE RegionId = @0 ORDER BY [Year] DESC", regionId);
        }

        public List<CompetitionCallEvent> Events(int callId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCallEvent>("SELECT * FROM CompetitionCallEvent WHERE CallId = @0 ORDER BY At, Id", callId);
        }

        public int ActiveWishCount(int callId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM CompetitionCallWish WHERE CallId = @0 AND Status = 'Aktiv'", callId);
        }

        // ── Skrivvägar ──────────────────────────────────────────────────────────────────────

        /// <summary>Lägger till en utlysning. <paramref name="publish"/> släpper den direkt till klubbarna; annars börjar den i planering.</summary>
        public CallResult Create(CompetitionCall draft, bool publish, int actorId, string actorName)
        {
            var err = CompetitionCallRules.ValidateContent(draft, DateTime.Today);
            if (err != null) return new(null, err);

            var now = DateTime.Now;
            draft.Id = 0;
            draft.Name = draft.Name.Trim();
            draft.Level = "Krets";
            draft.Status = publish ? CompetitionCallStatus.Utlyst : CompetitionCallStatus.Planering;
            draft.AwardedClubId = null; draft.AwardedAt = null; draft.AwardedByMemberId = null;
            draft.CompetitionId = null; draft.CreatingAt = null;
            draft.Version = 1;
            draft.CreatedAt = now; draft.UpdatedAt = now; draft.CreatedByMemberId = actorId > 0 ? actorId : null;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            db.Insert(draft);
            AddEvent(db, draft.Id, actorId, actorName, CompetitionCallEventKind.Created, null);
            if (publish) AddEvent(db, draft.Id, actorId, actorName, CompetitionCallEventKind.Published, null);
            scope.Complete();
            return new(draft, null);
        }

        /// <summary>
        /// Ändrar uppgifterna. <paramref name="expectedVersion"/> är versionen sidan läste — har någon annan ändrat sedan dess
        /// vägras sparningen (0 rader), så en annan styrelseledamots ändring aldrig skrivs över i tysthet.
        /// </summary>
        public CallResult Update(int id, CompetitionCall edit, int expectedVersion, int actorId, string actorName)
        {
            var cur = Get(id);
            if (cur == null) return new(null, "Utlysningen hittades inte.");
            if (!CompetitionCallRules.CanEdit(cur.Status)) return new(null, CompetitionCallRules.WhyNot("edit", cur.Status, cur.CompetitionId));

            var next = new CompetitionCall
            {
                Id = cur.Id, RegionId = cur.RegionId, Level = "Krets", Status = cur.Status,
                SeriesId = edit.SeriesId, Year = edit.Year, Name = (edit.Name ?? "").Trim(), Description = Clean(edit.Description),
                Discipline = Clean(edit.Discipline), ChampionshipScope = Clean(edit.ChampionshipScope),
                CompetitionDate = edit.CompetitionDate?.Date, LockedByKrets = edit.LockedByKrets, WishDeadline = edit.WishDeadline?.Date,
                WishesVisibleToClubs = edit.WishesVisibleToClubs,
            };
            var err = CompetitionCallRules.ValidateContent(next, DateTime.Today);
            if (err != null) return new(null, err);

            var dateChanged = cur.CompetitionDate?.Date != next.CompetitionDate?.Date || cur.LockedByKrets != next.LockedByKrets;
            var now = DateTime.Now;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var rows = db.Execute(
                "UPDATE CompetitionCall SET SeriesId=@0, [Year]=@1, Name=@2, Description=@3, Discipline=@4, ChampionshipScope=@5, "
                + "CompetitionDate=@6, LockedByKrets=@7, WishDeadline=@8, WishesVisibleToClubs=@9, "
                + "DateChangedAt=CASE WHEN @10=1 THEN @11 ELSE DateChangedAt END, DateChangedByMemberId=CASE WHEN @10=1 THEN @12 ELSE DateChangedByMemberId END, "
                + "UpdatedAt=@11, Version=Version+1 "
                + "WHERE Id=@13 AND Version=@14 AND Status IN ('Planering','Utlyst','Tilldelad')",
                next.SeriesId, next.Year, next.Name, next.Description, next.Discipline, next.ChampionshipScope,
                next.CompetitionDate, next.LockedByKrets, next.WishDeadline, next.WishesVisibleToClubs,
                dateChanged ? 1 : 0, now, actorId, id, expectedVersion);
            if (rows == 0) return new(null, "Någon har ändrat utlysningen sedan du öppnade den. Ladda om sidan och försök igen.");
            AddEvent(db, id, actorId, actorName, CompetitionCallEventKind.Edited, null);
            if (dateChanged)
                AddEvent(db, id, actorId, actorName, CompetitionCallEventKind.DateChanged,
                    $"{Fmt(cur.CompetitionDate)} → {Fmt(next.CompetitionDate)}{(next.LockedByKrets ? " (fastställt av kretsen)" : "")}");
            scope.Complete();
            return new(Get(id), null);
        }

        /// <summary>Utlys: släpp utlysningen till klubbarna.</summary>
        public CallResult Publish(int id, int actorId, string actorName) =>
            Transition(id, actorId, actorName, CompetitionCallEventKind.Published, null,
                "UPDATE CompetitionCall SET Status='Utlyst', UpdatedAt=@0 WHERE Id=@1 AND Status='Planering'", DateTime.Now, id);

        /// <summary>Dra tillbaka till planering — bara när ingen klubb har önskat.</summary>
        public CallResult Withdraw(int id, int actorId, string actorName) =>
            Transition(id, actorId, actorName, CompetitionCallEventKind.Withdrawn, null,
                "UPDATE CompetitionCall SET Status='Planering', UpdatedAt=@0 WHERE Id=@1 AND Status='Utlyst' "
                + "AND NOT EXISTS (SELECT 1 FROM CompetitionCallWish w WHERE w.CallId=@1 AND w.Status='Aktiv')", DateTime.Now, id);

        /// <summary>
        /// Utse en klubb. ETT villkorat UPDATE: bara när ingen redan är utsedd — den som kommer sist får "Någon hann före dig".
        /// Att klubben hör till kretsen prövar kontrollern (den har kretsens klubblista).
        /// </summary>
        public CallResult Award(int id, int clubId, string clubName, int actorId, string actorName)
        {
            if (clubId <= 0) return new(null, "Välj en klubb.");
            return Transition(id, actorId, actorName, CompetitionCallEventKind.Awarded, clubName,
                "UPDATE CompetitionCall SET Status='Tilldelad', AwardedClubId=@0, AwardedAt=@1, AwardedByMemberId=@2, UpdatedAt=@1 "
                + "WHERE Id=@3 AND Status IN ('Planering','Utlyst') AND AwardedClubId IS NULL", clubId, DateTime.Now, actorId, id);
        }

        /// <summary>Ta tillbaka en utnämning (tillbaka till Utlyst) — bara när klubben inte skapat tävlingen.</summary>
        public CallResult Unaward(int id, int actorId, string actorName) =>
            Transition(id, actorId, actorName, CompetitionCallEventKind.Unawarded, null,
                "UPDATE CompetitionCall SET Status='Utlyst', AwardedClubId=NULL, AwardedAt=NULL, AwardedByMemberId=NULL, UpdatedAt=@0 "
                + "WHERE Id=@1 AND Status='Tilldelad' AND CompetitionId IS NULL", DateTime.Now, id);

        /// <summary>Ställ in utlysningen. En skapad tävling berörs inte.</summary>
        public CallResult Cancel(int id, int actorId, string actorName) =>
            Transition(id, actorId, actorName, CompetitionCallEventKind.Cancelled, null,
                "UPDATE CompetitionCall SET Status='Installd', UpdatedAt=@0 WHERE Id=@1 AND Status<>'Installd' AND CompetitionId IS NULL", DateTime.Now, id);

        // ── Önskemål (F3) ───────────────────────────────────────────────────────────────────

        public const string AlreadyWished = "Ni har redan önskat att arrangera den här tävlingen.";

        public readonly record struct WishResult(CompetitionCallWish? Wish, string? Error);

        /// <summary>Alla önskemål på en utlysning (aktiva först).</summary>
        public List<CompetitionCallWish> Wishes(int callId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCallWish>(
                "SELECT * FROM CompetitionCallWish WHERE CallId = @0 ORDER BY CASE WHEN Status='Aktiv' THEN 0 ELSE 1 END, CreatedAt, Id", callId);
        }

        public CompetitionCallWish? WishOf(int callId, int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<CompetitionCallWish>("SELECT * FROM CompetitionCallWish WHERE CallId=@0 AND ClubId=@1", callId, clubId);
        }

        /// <summary>De utlysningar en klubbs krets har släppt till klubbarna (utlysta, utsedda, skapade) ett år — aldrig planering eller inställda.</summary>
        public List<CompetitionCall> VisibleToClubsIn(int regionId, int year)
        {
            if (regionId <= 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCall>(
                "SELECT * FROM CompetitionCall WHERE RegionId=@0 AND [Year]=@1 AND Status IN ('Utlyst','Tilldelad','Skapad') "
                + "ORDER BY CASE WHEN CompetitionDate IS NULL THEN 1 ELSE 0 END, CompetitionDate, Name, Id", regionId, year);
        }

        /// <summary>Åren kretsen har släppt något för till klubbarna, stigande.</summary>
        public List<int> VisibleYearsIn(int regionId)
        {
            if (regionId <= 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>("SELECT DISTINCT [Year] FROM CompetitionCall WHERE RegionId=@0 AND Status IN ('Utlyst','Tilldelad','Skapad') ORDER BY [Year]", regionId);
        }

        /// <summary>Klubbens egna önskemål på de angivna utlysningarna (aktiva), nycklade på utlysningen.</summary>
        public Dictionary<int, CompetitionCallWish> ActiveWishesOf(int clubId, IReadOnlyCollection<int> callIds)
        {
            if (callIds.Count == 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCallWish>(
                "SELECT * FROM CompetitionCallWish WHERE ClubId=@0 AND Status='Aktiv' AND CallId IN (@1)", clubId, callIds.ToArray())
                .ToDictionary(w => w.CallId);
        }

        /// <summary>
        /// Klubben önskar att arrangera. Ett önskemål per (utlysning, klubb): finns ett återkallat återupplivas det, finns ett aktivt
        /// svarar vi "Ni har redan önskat" — aldrig ett 500 från det unika indexet. Villkoret att utlysningen fortfarande är UTLYST
        /// ligger i själva INSERT/UPDATE:t, så ett önskemål som kommer samtidigt med att kretsen utser aldrig sparas.
        /// Behörigheten (klubbens admin, eller kretsen när <paramref name="onBehalf"/>) och att klubben hör till kretsen prövar kontrollern.
        /// </summary>
        public WishResult Express(int callId, int clubId, DateTime? proposedDate, string? note, int contactMemberId,
            int actorId, string actorName, string clubName, bool onBehalf)
        {
            var call = Get(callId);
            if (call == null) return new(null, "Utlysningen hittades inte.");
            var why = CompetitionCallRules.WhyNotWish(call.Status, call.WishDeadline, DateTime.Today, onBehalf);
            if (why != null) return new(null, why);
            var bad = CompetitionCallRules.ValidateWish(call, proposedDate?.Date, note);
            if (bad != null) return new(null, bad);

            var now = DateTime.Now;
            var cleanNote = Clean(note);
            var contact = contactMemberId > 0 ? (int?)contactMemberId : null;
            var by = actorId > 0 ? (int?)actorId : null;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var rows = db.Execute(
                "UPDATE w SET Status='Aktiv', ProposedDate=@2, Note=@3, ContactMemberId=@4, RegisteredByMemberId=@5, OnBehalf=@6, UpdatedAt=@7 "
                + "FROM CompetitionCallWish w WHERE w.CallId=@0 AND w.ClubId=@1 AND w.Status='Aterkallad' "
                + "AND EXISTS (SELECT 1 FROM CompetitionCall c WHERE c.Id=@0 AND c.Status='Utlyst')",
                callId, clubId, proposedDate?.Date, cleanNote, contact, by, onBehalf, now);
            if (rows == 0)
            {
                rows = db.Execute(
                    "INSERT INTO CompetitionCallWish (CallId, ClubId, ProposedDate, Note, Status, ContactMemberId, RegisteredByMemberId, OnBehalf, CreatedAt, UpdatedAt) "
                    + "SELECT @0, @1, @2, @3, 'Aktiv', @4, @5, @6, @7, @7 "
                    + "WHERE EXISTS (SELECT 1 FROM CompetitionCall c WHERE c.Id=@0 AND c.Status='Utlyst') "
                    + "AND NOT EXISTS (SELECT 1 FROM CompetitionCallWish w WHERE w.CallId=@0 AND w.ClubId=@1)",
                    callId, clubId, proposedDate?.Date, cleanNote, contact, by, onBehalf, now);
            }
            if (rows == 0)
            {
                var again = Get(callId);
                if (again != null && again.Status != CompetitionCallStatus.Utlyst) return new(null, "Kretsen hann utse en klubb. Ladda om sidan.");
                return new(null, AlreadyWished);
            }
            AddEvent(db, callId, actorId, actorName, CompetitionCallEventKind.Wished,
                onBehalf ? $"{clubName} (registrerat av kretsen)" : clubName);
            scope.Complete();
            return new(WishOf(callId, clubId), null);
        }

        /// <summary>Ångra ett önskemål — bara medan utlysningen är utlyst (en utsedd klubb ångrar sig hos kretsen).</summary>
        public WishResult WithdrawWish(int callId, int clubId, int actorId, string actorName, string clubName)
        {
            if (Get(callId) == null) return new(null, "Utlysningen hittades inte.");
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var rows = db.Execute(
                "UPDATE w SET Status='Aterkallad', UpdatedAt=@2 FROM CompetitionCallWish w WHERE w.CallId=@0 AND w.ClubId=@1 AND w.Status='Aktiv' "
                + "AND EXISTS (SELECT 1 FROM CompetitionCall c WHERE c.Id=@0 AND c.Status='Utlyst')", callId, clubId, DateTime.Now);
            if (rows == 0)
            {
                var c = Get(callId);
                return new(null, c != null && c.Status != CompetitionCallStatus.Utlyst
                    ? "Kretsen har redan utsett en klubb. Hör av dig till kretsen om ni inte vill arrangera."
                    : "Ni har inget önskemål att ångra.");
            }
            AddEvent(db, callId, actorId, actorName, CompetitionCallEventKind.WishWithdrawn, clubName);
            scope.Complete();
            return new(WishOf(callId, clubId), null);
        }

        /// <summary>Skriver att utseendet aviserats (och hur många mejl som gick). Egen rad, aldrig i utseendets transaktion.</summary>
        public void RecordAwardMailed(int callId, int mailed, int actorId, string actorName)
        {
            using var scope = _scopeProvider.CreateScope();
            AddEvent(scope.Database, callId, actorId, actorName, CompetitionCallEventKind.AwardMailed, mailed.ToString());
            scope.Complete();
        }

        /// <summary>
        /// Utsedda utlysningar i kretsen (inte skapade) där ingen mejlades — det kretsen ska ta tag i: klubben saknar aktiv
        /// administratör, eller mejlet gick inte. Hämtas ur händelsen <c>AwardMailed</c> med värdet 0 efter senaste utseendet.
        /// </summary>
        public List<CompetitionCall> AwardedWithoutNotice(int regionId)
        {
            if (regionId <= 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCall>(
                "SELECT c.* FROM CompetitionCall c WHERE c.RegionId=@0 AND c.Status='Tilldelad' AND EXISTS ("
                + "SELECT 1 FROM CompetitionCallEvent e WHERE e.CallId=c.Id AND e.Kind='AwardMailed' AND e.[Text]='0' "
                + "AND e.Id = (SELECT MAX(e2.Id) FROM CompetitionCallEvent e2 WHERE e2.CallId=c.Id AND e2.Kind='AwardMailed')) "
                + "ORDER BY c.CompetitionDate, c.Id", regionId);
        }

        // ── Internt ─────────────────────────────────────────────────────────────────────────

        /// <summary>Det villkorade UPDATE:t + händelseraden i EN transaktion. 0 rader = läget tillät det inte (eller någon hann före).</summary>
        private CallResult Transition(int id, int actorId, string actorName, string eventKind, string? eventText, string sql, params object[] args)
        {
            var before = Get(id);
            if (before == null) return new(null, "Utlysningen hittades inte.");

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var rows = db.Execute(sql, args);
            if (rows == 0)
            {
                var why = WhyRefused(eventKind, before);
                return new(null, why ?? LostRace);
            }
            AddEvent(db, id, actorId, actorName, eventKind, eventText);
            scope.Complete();
            return new(Get(id), null);
        }

        /// <summary>Varför 0 rader: läget var fel (förklaring) eller någon hann före (null → <see cref="LostRace"/>).</summary>
        private string? WhyRefused(string eventKind, CompetitionCall seen)
        {
            var action = eventKind switch
            {
                CompetitionCallEventKind.Published => "publish",
                CompetitionCallEventKind.Withdrawn => "withdraw",
                CompetitionCallEventKind.Awarded => "award",
                CompetitionCallEventKind.Unawarded => "unaward",
                CompetitionCallEventKind.Cancelled => "cancel",
                _ => ""
            };
            var fresh = Get(seen.Id) ?? seen;
            return CompetitionCallRules.WhyNot(action, fresh.Status, fresh.CompetitionId, fresh.Status == CompetitionCallStatus.Utlyst ? ActiveWishCount(fresh.Id) : 0);
        }

        private static void AddEvent(NPoco.IDatabase db, int callId, int byMemberId, string byName, string kind, string? text) =>
            db.Insert(new CompetitionCallEvent { CallId = callId, At = DateTime.Now, ByMemberId = byMemberId > 0 ? byMemberId : null, ByName = byName, Kind = kind, Text = text });

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string Fmt(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "inget datum";
    }
}
