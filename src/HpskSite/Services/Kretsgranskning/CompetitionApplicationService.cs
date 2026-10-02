using HpskSite.Models.Kretsgranskning;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Tävlingsansökan (kretsgranskning fas 4): lagring, tillstånd och händelselogg.
    ///
    /// <para><b>⚠️ ALLA tillståndsbyten går genom <see cref="Transition"/></b>, som skriver raden och
    /// händelsen i samma transaktion. En ändring utan händelse är en ansökan där ingen kan svara på
    /// "vem tillstyrkte?" när Förbundet frågar ett halvår senare.</para>
    ///
    /// <para><b>Behörighet hör INTE hit.</b> Kontrollern avgör vem som får göra vad (klubbens admin
    /// för klubbsidan, kretsens tävlingsansvarige för kretssidan); tjänsten prövar bara att
    /// tillståndet tillåter handlingen. Det gör att samma regler gäller den inloggade vägen och
    /// länkläget.</para>
    /// </summary>
    public class CompetitionApplicationService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CompetitionApplicationService> _logger;

        public CompetitionApplicationService(IScopeProvider scopeProvider, ILogger<CompetitionApplicationService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public bool TablesExist()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name IN ('CompetitionApplication','CompetitionApplicationEvent','RegionApplicationDeadline','RegionCalendarSettings','KretsgranskningReminder')") == 5;
        }

        public CompetitionApplication? Get(int id)
        {
            if (id <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<CompetitionApplication>(id);
        }

        public List<CompetitionApplication> ForClub(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionApplication>(
                "SELECT * FROM CompetitionApplication WHERE ClubId = @0 ORDER BY CompetitionDate DESC, Id DESC", clubId);
        }

        /// <summary>Kretsens ansökningar, valfritt inom ett datumintervall (kalendern).</summary>
        public List<CompetitionApplication> ForRegion(int regionId, DateTime? from = null, DateTime? to = null)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var sql = "SELECT * FROM CompetitionApplication WHERE RegionId = @0";
            if (from.HasValue) sql += " AND COALESCE(EndDate, GrantedDate, CompetitionDate) >= @1";
            if (to.HasValue) sql += " AND COALESCE(GrantedDate, CompetitionDate) <= @2";
            sql += " ORDER BY CompetitionDate, Id";
            return scope.Database.Fetch<CompetitionApplication>(sql, regionId, from ?? DateTime.MinValue, to ?? DateTime.MaxValue);
        }

        /// <summary>Flera kretsars ansökningar i ett intervall — grannkretsarna i kalendern.</summary>
        public List<CompetitionApplication> ForRegions(IReadOnlyCollection<int> regionIds, DateTime from, DateTime to)
        {
            if (regionIds.Count == 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionApplication>(
                "SELECT * FROM CompetitionApplication WHERE RegionId IN (@0) "
                + "AND COALESCE(EndDate, GrantedDate, CompetitionDate) >= @1 AND COALESCE(GrantedDate, CompetitionDate) <= @2 "
                + "ORDER BY CompetitionDate, Id",
                regionIds.ToArray(), from, to);
        }

        /// <summary>Ansökan som är kopplad till en tävling, om någon.</summary>
        public CompetitionApplication? ForCompetition(int competitionId)
        {
            if (competitionId <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<CompetitionApplication>(
                "SELECT TOP 1 * FROM CompetitionApplication WHERE CompetitionId = @0 ORDER BY Id DESC", competitionId);
        }

        public List<CompetitionApplicationEvent> Events(int applicationId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionApplicationEvent>(
                "SELECT * FROM CompetitionApplicationEvent WHERE ApplicationId = @0 ORDER BY At, Id", applicationId);
        }

        // ── Skrivvägar ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Skapar en ansökan. <paramref name="submit"/> skickar in den direkt (vanligast); annars
        /// sparas den som utkast. En krets egen ansökan börjar alltid inskickad — det finns ingen
        /// annan part att vänta på.
        /// </summary>
        public (CompetitionApplication? App, string? Error) Create(CompetitionApplication draft, bool submit, int actorId, string actorName)
        {
            var err = CompetitionApplicationRules.ValidateContent(draft, DateTime.Today);
            if (err != null) return (null, err);

            var now = DateTime.Now;
            draft.Id = 0;
            draft.Status = submit || draft.IsRegionOwn ? CompetitionApplicationStatus.Inskickad : CompetitionApplicationStatus.Utkast;
            draft.SubmittedAt = draft.Status == CompetitionApplicationStatus.Inskickad ? now : null;
            draft.CreatedAt = now;
            draft.UpdatedAt = now;
            draft.CreatedByMemberId = actorId;
            draft.CompetitionId = null;
            draft.KretsOpinion = null;
            draft.DecisionText = null;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            db.Insert(draft);
            AddEvent(db, draft.Id, actorId, actorName, CompetitionApplicationEventKind.Created, null);
            if (draft.Status == CompetitionApplicationStatus.Inskickad)
                AddEvent(db, draft.Id, actorId, actorName, CompetitionApplicationEventKind.Submitted, null);
            scope.Complete();
            return (draft, null);
        }

        /// <summary>
        /// Klubben ändrar innehållet (före beslut). Status, yttrande, beslut och koppling rörs
        /// aldrig här — de har egna vägar.
        /// </summary>
        public (CompetitionApplication? App, string? Error) UpdateContent(int id, CompetitionApplication edit, int actorId, string actorName)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (!CompetitionApplicationRules.ClubCanEdit(a.Status))
                return (null, $"En ansökan som är {CompetitionApplicationStatus.Label(a.Status).ToLowerInvariant()} kan inte ändras.");

            a.Level = edit.Level;
            a.Discipline = edit.Discipline;
            a.Name = (edit.Name ?? "").Trim();
            a.CompetitionDate = edit.CompetitionDate;
            a.EndDate = edit.EndDate;
            a.ReserveDate = edit.ReserveDate;
            a.RangeId = edit.RangeId;
            a.Place = edit.Place;
            a.ContactMemberId = edit.ContactMemberId;
            a.ContactName = edit.ContactName;
            a.ContactEmail = edit.ContactEmail;
            a.ContactPhone = edit.ContactPhone;
            a.Classes = edit.Classes;
            a.Note = edit.Note;
            a.NumberOfSeries = edit.NumberOfSeries;
            a.ChampionshipScope = edit.ChampionshipScope;
            a.AwardsStandardMedals = edit.AwardsStandardMedals;

            var err = CompetitionApplicationRules.ValidateContent(a, DateTime.Today);
            if (err != null) return (null, err);

            return Transition(a, a.Status, actorId, actorName, CompetitionApplicationEventKind.Edited, null);
        }

        public (CompetitionApplication? App, string? Error) Submit(int id, int actorId, string actorName)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (!CompetitionApplicationRules.ClubCanSubmit(a.Status)) return (null, "Ansökan är redan inskickad.");
            a.SubmittedAt = DateTime.Now;
            return Transition(a, CompetitionApplicationStatus.Inskickad, actorId, actorName, CompetitionApplicationEventKind.Submitted, null);
        }

        public (CompetitionApplication? App, string? Error) Withdraw(int id, int actorId, string actorName, string? reason)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (!CompetitionApplicationRules.ClubCanWithdraw(a.Status)) return (null, "Ansökan är redan avslutad.");
            return Transition(a, CompetitionApplicationStatus.Aterkallad, actorId, actorName, CompetitionApplicationEventKind.Withdrawn, Trim(reason));
        }

        /// <summary>Kretsen ber om komplettering. Texten är obligatorisk — den är vad klubben ska göra.</summary>
        public (CompetitionApplication? App, string? Error) RequestCompletion(int id, string text, KretsDecider who)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (!CompetitionApplicationRules.KretsCanAct(a.Status)) return (null, "Ansökan väntar inte på kretsen.");
            text = Trim(text) ?? "";
            if (text.Length == 0) return (null, "Skriv vad klubben behöver komplettera.");
            a.CompletionRequest = text;
            a.CompletionReply = null;
            Stamp(a, who);
            return Transition(a, CompetitionApplicationStatus.Komplettering, who.MemberId, who.Name, CompetitionApplicationEventKind.CompletionRequested, text);
        }

        /// <summary>Klubben svarar på kompletteringen; ansökan går tillbaka till kretsen.</summary>
        public (CompetitionApplication? App, string? Error) Reply(int id, string text, int actorId, string actorName)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (a.Status != CompetitionApplicationStatus.Komplettering) return (null, "Kretsen har inte bett om komplettering.");
            text = Trim(text) ?? "";
            if (text.Length == 0) return (null, "Skriv ditt svar.");
            a.CompletionReply = text;
            return Transition(a, CompetitionApplicationStatus.Inskickad, actorId, actorName, CompetitionApplicationEventKind.Replied, text);
        }

        /// <summary>
        /// Kretsens beslut om en KRETSTÄVLING: godkänn eller avslå. Avslaget kräver en motivering.
        /// En nationell eller landsdelsansökan beslutas inte av kretsen — se <see cref="Opinion"/>.
        /// </summary>
        public (CompetitionApplication? App, string? Error) Decide(int id, bool approve, string? text, KretsDecider who)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (!CompetitionApplicationRules.KretsCanAct(a.Status)) return (null, "Ansökan väntar inte på kretsen.");
            if (a.NeedsForbundet) return (null, "En nationell tävling och en landsdelstävling beslutas av Förbundet. Kretsen tillstyrker eller avstyrker.");
            text = Trim(text);
            if (!approve && string.IsNullOrEmpty(text)) return (null, "Skriv varför ansökan avslås — klubben ser motiveringen.");
            a.DecisionText = text;
            a.DecidedAt = DateTime.Now;
            a.DecidedByMemberId = who.MemberId > 0 ? who.MemberId : null;
            Stamp(a, who);
            return Transition(a, approve ? CompetitionApplicationStatus.Beviljad : CompetitionApplicationStatus.Avslagen,
                who.MemberId, who.Name, approve ? CompetitionApplicationEventKind.Approved : CompetitionApplicationEventKind.Rejected, text);
        }

        /// <summary>
        /// Kretsens yttrande om en NATIONELL eller LANDSDELS-ansökan. Ansökan går till "Hos
        /// Förbundet": den ingår i kretsens sammanställning och kretsen registrerar sedan beslutet.
        /// </summary>
        public (CompetitionApplication? App, string? Error) Opinion(int id, string opinion, string? text, KretsDecider who)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (!CompetitionApplicationRules.KretsCanAct(a.Status)) return (null, "Ansökan väntar inte på kretsen.");
            if (!a.NeedsForbundet) return (null, "En kretstävling beslutas av kretsen — godkänn eller avslå i stället.");
            if (!CompetitionApplicationOpinion.IsValid(opinion)) return (null, "Välj tillstyrker eller avstyrker.");
            text = Trim(text);
            if (opinion == CompetitionApplicationOpinion.Avstyrker && string.IsNullOrEmpty(text))
                return (null, "Skriv varför kretsen avstyrker — både klubben och Förbundet läser motiveringen.");
            a.KretsOpinion = opinion;
            a.KretsOpinionText = text;
            Stamp(a, who);
            return Transition(a, CompetitionApplicationStatus.HosForbundet, who.MemberId, who.Name,
                CompetitionApplicationEventKind.Opinion,
                opinion == CompetitionApplicationOpinion.Tillstyrker ? $"Tillstyrker{(text != null ? ": " + text : "")}" : $"Avstyrker: {text}");
        }

        /// <summary>Kretsen markerar att sammanställningen med de här ansökningarna skickats till Förbundet.</summary>
        public int MarkSentToForbundet(IEnumerable<int> ids, int actorId, string actorName)
        {
            int n = 0;
            foreach (var id in ids.Distinct())
            {
                var a = Get(id);
                if (a == null || a.Status != CompetitionApplicationStatus.HosForbundet) continue;
                a.SentToForbundetAt = DateTime.Now;
                var (ok, _) = Transition(a, a.Status, actorId, actorName, CompetitionApplicationEventKind.SentToForbundet, null);
                if (ok != null) n++;
            }
            return n;
        }

        /// <summary>
        /// Förbundets beslut, registrerat av kretsen. Förbundet kan flytta dagen
        /// (<paramref name="grantedDate"/>); en redan skapad tävling flyttas inte av sig själv.
        /// </summary>
        public (CompetitionApplication? App, string? Error) RegisterForbundetDecision(int id, bool granted,
            DateTime decisionDate, DateTime? grantedDate, string? text, KretsDecider who)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (a.Status != CompetitionApplicationStatus.HosForbundet) return (null, "Ansökan ligger inte hos Förbundet.");
            a.ForbundetDecisionDate = decisionDate.Date;
            a.GrantedDate = granted && grantedDate.HasValue && grantedDate.Value.Date != a.CompetitionDate.Date ? grantedDate.Value.Date : null;
            a.DecisionText = Trim(text);
            a.DecidedAt = DateTime.Now;
            a.DecidedByMemberId = who.MemberId > 0 ? who.MemberId : null;
            var note = granted
                ? (a.GrantedDate.HasValue ? $"Beviljad, men med datumet {a.GrantedDate:yyyy-MM-dd}" : "Beviljad")
                : "Avslagen";
            if (!string.IsNullOrEmpty(a.DecisionText)) note += ": " + a.DecisionText;
            return Transition(a, granted ? CompetitionApplicationStatus.Beviljad : CompetitionApplicationStatus.Avslagen,
                who.MemberId, who.Name, CompetitionApplicationEventKind.ForbundetDecision, note);
        }

        /// <summary>
        /// Kopplar den skapade tävlingen till ansökan. Kallas av tävlingsguiden när en tävling
        /// skapas ur en ansökan. Vägrar om ansökan redan har en tävling.
        /// </summary>
        public (CompetitionApplication? App, string? Error) LinkCompetition(int id, int competitionId, int actorId, string actorName)
        {
            var a = Get(id);
            if (a == null) return (null, "Ansökan hittades inte.");
            if (a.CompetitionId is > 0 && a.CompetitionId != competitionId) return (null, "Ansökan har redan en tävling.");
            if (!CompetitionApplicationRules.CanCreateCompetition(a) && a.CompetitionId != competitionId)
                return (null, "Ansökan är inte godkänd ännu.");
            a.CompetitionId = competitionId;
            return Transition(a, a.Status, actorId, actorName, CompetitionApplicationEventKind.Linked, $"Tävling {competitionId}");
        }

        /// <summary>
        /// Länkläget: vart ärendets länk skickades. Ingen tillståndsövergång — bara uppgiften, så
        /// att påminnelserna kan räkna "ni har skickat N länkar dit i år".
        /// </summary>
        public void SetLinkSentTo(int id, string email)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("UPDATE CompetitionApplication SET LinkSentTo = @1 WHERE Id = @0", id,
                email.Length > 200 ? email[..200] : email);
        }

        // ── Påminnelser ──────────────────────────────────────────────────────────────────

        /// <summary>Öppna och godkända ansökningar vars tävling inte passerat — påminnelsernas underlag.</summary>
        public List<CompetitionApplication> ForReminders(DateTime today)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionApplication>(
                "SELECT * FROM CompetitionApplication WHERE Status IN (@0) AND COALESCE(EndDate, GrantedDate, CompetitionDate) >= @1",
                new[] { CompetitionApplicationStatus.Inskickad, CompetitionApplicationStatus.Komplettering,
                        CompetitionApplicationStatus.HosForbundet, CompetitionApplicationStatus.Beviljad },
                today.Date);
        }

        /// <summary>
        /// Gör anspråk på en påminnelse INNAN den skickas. Det unika indexet är spärren: en krasch
        /// mellan anspråk och utskick kostar en missad påminnelse, motsatt ordning kostar spam.
        /// </summary>
        public bool TryClaimReminder(string key)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                scope.Database.Execute(
                    "INSERT INTO KretsgranskningReminder (ReminderKey, SentAt, Recipients) VALUES (@0, @1, 0)", key, DateTime.Now);
                return true;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            {
                return false;
            }
        }

        public void SetReminderRecipients(string key, int n)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("UPDATE KretsgranskningReminder SET Recipients = @1 WHERE ReminderKey = @0", key, n);
        }

        // ── Kretsens inställningar ───────────────────────────────────────────────────────

        public List<RegionApplicationDeadline> Deadlines(int regionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<RegionApplicationDeadline>(
                "SELECT * FROM RegionApplicationDeadline WHERE RegionId = @0 ORDER BY [Year], [Level]", regionId);
        }

        public DateTime? DeadlineFor(int regionId, int year, string level)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<DateTime?>(
                "SELECT LastDate FROM RegionApplicationDeadline WHERE RegionId = @0 AND [Year] = @1 AND [Level] = @2",
                regionId, year, level);
        }

        /// <summary>Sätter (eller tar bort, med null) kretsens sista ansökningsdag för ett år och en nivå.</summary>
        public string? SetDeadline(int regionId, int year, string level, DateTime? lastDate)
        {
            if (!CompetitionApplicationRules.IsApplicableLevel(level)) return "Okänd nivå.";
            if (year < 2000 || year > 2100) return "Orimligt år.";
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            db.Execute("DELETE FROM RegionApplicationDeadline WHERE RegionId = @0 AND [Year] = @1 AND [Level] = @2", regionId, year, level);
            if (lastDate.HasValue)
                db.Insert(new RegionApplicationDeadline { RegionId = regionId, Year = year, Level = level, LastDate = lastDate.Value.Date });
            scope.Complete();
            return null;
        }

        public RegionCalendarSettings? Settings(int regionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<RegionCalendarSettings>(regionId);
        }

        public void SaveSettings(RegionCalendarSettings s, int actorId)
        {
            s.UpdatedAt = DateTime.Now;
            s.UpdatedByMemberId = actorId;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var exists = db.ExecuteScalar<int>("SELECT COUNT(*) FROM RegionCalendarSettings WHERE RegionId = @0", s.RegionId) > 0;
            if (exists) db.Update(s); else db.Insert(s);
            scope.Complete();
        }

        // ── Internt ──────────────────────────────────────────────────────────────────────

        private (CompetitionApplication? App, string? Error) Transition(CompetitionApplication a, string newStatus,
            int actorId, string actorName, string eventKind, string? text)
        {
            a.Status = newStatus;
            a.UpdatedAt = DateTime.Now;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            db.Update(a);
            AddEvent(db, a.Id, actorId, actorName, eventKind, text);
            scope.Complete();
            return (a, null);
        }

        private static void Stamp(CompetitionApplication a, KretsDecider who)
        {
            a.Channel = who.Channel;
            a.DeciderName = who.Name;
            a.DeciderRole = who.Role;
            if (who.Channel == KretsChannel.Lank) a.LinkSentTo = who.LinkSentTo;
        }

        private static void AddEvent(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int appId, int byId, string byName, string kind, string? text)
            => db.Insert(new CompetitionApplicationEvent
            {
                ApplicationId = appId, At = DateTime.Now, ByMemberId = byId,
                ByName = string.IsNullOrWhiteSpace(byName) ? null : byName.Trim(),
                Kind = kind, Text = text
            });

        private static string? Trim(string? s)
        {
            var t = (s ?? "").Trim();
            if (t.Length > 4000) t = t[..4000];
            return t.Length == 0 ? null : t;
        }
    }

    /// <summary>Kanalen ett kretsbeslut kom genom (länkläget, fas 1).</summary>
    public static class KretsChannel
    {
        public const string Inloggad = "Inloggad";
        public const string Lank = "Lank";
        public const string Manuell = "Manuell";
    }

    /// <summary>Vem som beslutade på kretsens vägnar, och genom vilken väg.</summary>
    public record KretsDecider(int MemberId, string Name, string Role, string Channel, string? LinkSentTo = null);
}
