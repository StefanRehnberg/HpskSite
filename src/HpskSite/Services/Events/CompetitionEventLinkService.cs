using HpskSite.Models.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Events
{
    /// <summary>En tävling arrangörens händelse kan kopplas till.</summary>
    public sealed class CompetitionEventLinkCandidate
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public DateTime? Date { get; init; }
        public string HostName { get; init; } = "";
        public bool IsClubOnly { get; init; }
        /// <summary>Händelsen tävlingen redan är kopplad till, när det är en ANNAN händelse.</summary>
        public int? OtherEventId { get; init; }
        public string? OtherEventName { get; init; }
        public bool LinkedHere { get; init; }
    }

    /// <summary>
    /// Kopplingen tävling → händelse (sajtstrukturen fas 2, Stefans beslut 2026-10-09): arrangörens
    /// händelsesida med block och bilder beskriver en eller flera tävlingar, och tävlingssidan får en
    /// enda länkrad till den. <b>En tävling har högst en händelse</b> (primärnyckeln) — annars vet
    /// tävlingssidan inte vart den ska länka.
    ///
    /// <para><b>Vilka tävlingar som går att koppla:</b> en klubbs händelse → klubbens egna tävlingar,
    /// även de klubbinterna. En krets händelse → kretsens egna tävlingar och klubbarnas i kretsen,
    /// men inte klubbinterna (de är klubbens, inte kretsens).</para>
    ///
    /// <para>En koppling till en händelse som inte längre finns behandlas som ingen koppling — därför
    /// behöver borttagningen av en händelse inte städa här. Händelsernas id återanvänds aldrig
    /// (nod-id respektive identitet efter RESEED).</para>
    /// </summary>
    public class CompetitionEventLinkService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly InvoiceAdminService _invoiceAdmin;
        private readonly AdminAuthorizationService _auth;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly IClubEventStore _events;
        private readonly ILogger<CompetitionEventLinkService> _logger;

        /// <summary>Hur långt bakåt en tävling erbjuds (en händelse kan skapas strax efter).</summary>
        public const int PastDays = 60;

        public CompetitionEventLinkService(IScopeProvider scopeProvider, InvoiceAdminService invoiceAdmin,
            AdminAuthorizationService auth, IContentService contentService, ClubService clubService,
            IClubEventStore events, ILogger<CompetitionEventLinkService> logger)
        {
            _scopeProvider = scopeProvider;
            _invoiceAdmin = invoiceAdmin;
            _auth = auth;
            _contentService = contentService;
            _clubService = clubService;
            _events = events;
            _logger = logger;
        }

        private sealed class LinkRow
        {
            public int CompetitionId { get; set; }
            public int EventId { get; set; }
        }

        private List<LinkRow> Read(string where, params object[] args)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.Fetch<LinkRow>("SELECT CompetitionId, EventId FROM dbo.CompetitionEventLink " + where, args);
            }
            catch (Exception ex)
            {
                // En saknad tabell (okörd migrering) får inte ta ner tävlingssidan.
                _logger.LogWarning(ex, "CompetitionEventLink kunde inte läsas");
                return new List<LinkRow>();
            }
        }

        /// <summary>Den publicerade händelse tävlingen är kopplad till, eller null.</summary>
        public ClubEventRecord? EventForCompetition(int competitionId)
        {
            if (competitionId <= 0) return null;
            var row = Read("WHERE CompetitionId = @0", competitionId).FirstOrDefault();
            if (row == null) return null;
            var ev = _events.Get(row.EventId);
            return ev != null && ev.IsPublished ? ev : null;
        }

        public List<int> CompetitionsForEvent(int eventId)
            => eventId <= 0 ? new List<int>() : Read("WHERE EventId = @0", eventId).Select(r => r.CompetitionId).ToList();

        /// <summary>Alla tävlingar ägaren FÅR koppla, oavsett datum.</summary>
        private List<IContent> Allowed(ClubEventRecord ev)
        {
            if (ev.OwnerType == ClubEventRecord.OwnerClub)
                return _invoiceAdmin.GetCompetitionsHostedByClub(ev.OwnerId).Where(c => !c.Trashed).ToList();

            var regionCode = _contentService.GetById(ev.OwnerId)?.GetValue<string>("regionCode") ?? "";
            if (regionCode.Length == 0) return new List<IContent>();
            var clubIds = _auth.GetClubsInRegions(new List<string> { regionCode }).ToHashSet();
            return _invoiceAdmin.GetCompetitionsInRegion(regionCode, clubIds)
                .Where(c => !c.Trashed && !(c.GetValue<int>("clubId") > 0 && c.GetValue<bool>("isClubOnly")))
                .ToList();
        }

        // ⚠️ GetValue<DateTime?> ger DateTime.MinValue för en osatt egenskap, inte null.
        private static DateTime? DateOf(IContent c)
        {
            var d = c.GetValue<DateTime?>("competitionDate");
            return d is null || d.Value == DateTime.MinValue ? null : d;
        }

        /// <summary>
        /// Tävlingarna att välja bland: från <see cref="PastDays"/> dagar bakåt och framåt, plus de som
        /// redan är kopplade hit (oavsett datum, så att en koppling aldrig försvinner ur listan).
        /// </summary>
        public List<CompetitionEventLinkCandidate> Candidates(ClubEventRecord ev)
        {
            var allowed = Allowed(ev);
            var ids = allowed.Select(c => c.Id).ToList();
            var links = ids.Count == 0 ? new List<LinkRow>() : ReadForCompetitions(ids);
            var linkOf = links.ToDictionary(l => l.CompetitionId, l => l.EventId);
            var eventNames = new Dictionary<int, string?>();
            string? LiveEventName(int eventId)
            {
                if (!eventNames.TryGetValue(eventId, out var name))
                {
                    name = _events.Get(eventId)?.DisplayName;
                    eventNames[eventId] = name;
                }
                return name;
            }

            var from = DateTime.Today.AddDays(-PastDays);
            return allowed
                .Select(c => new { c, date = DateOf(c), linked = linkOf.TryGetValue(c.Id, out var e) ? e : (int?)null })
                .Where(x => x.linked == ev.Id || x.date == null || x.date >= from)
                .Select(x =>
                {
                    var clubId = x.c.GetValue<int>("clubId");
                    int? other = x.linked.HasValue && x.linked != ev.Id && LiveEventName(x.linked.Value) != null ? x.linked : null;
                    return new CompetitionEventLinkCandidate
                    {
                        Id = x.c.Id,
                        Name = x.c.Name ?? "",
                        Date = x.date,
                        HostName = clubId > 0 ? (_clubService.GetClubNameById(clubId) ?? "") : "Kretsen",
                        IsClubOnly = x.c.GetValue<bool>("isClubOnly"),
                        OtherEventId = other,
                        OtherEventName = other.HasValue ? LiveEventName(other.Value) : null,
                        LinkedHere = x.linked == ev.Id
                    };
                })
                .OrderBy(c => c.Date ?? DateTime.MaxValue).ThenBy(c => c.Name)
                .ToList();
        }

        private List<LinkRow> ReadForCompetitions(List<int> ids)
        {
            // IN (@0) tar slut kring 2100 parametrar — läs i portioner.
            var rows = new List<LinkRow>();
            foreach (var chunk in ids.Chunk(1000))
                rows.AddRange(Read("WHERE CompetitionId IN (@0)", chunk.ToList()));
            return rows;
        }

        /// <summary>
        /// Ersätter händelsens kopplingar med <paramref name="competitionIds"/>. Vägrar en tävling ägaren
        /// inte får koppla och en tävling som redan hör till en annan (levande) händelse — ingenting
        /// skrivs då. En koppling till en borttagen händelse tas över.
        /// </summary>
        public (bool Ok, string? Error) SetForEvent(ClubEventRecord ev, IReadOnlyCollection<int> competitionIds, int? actorMemberId)
        {
            var wanted = competitionIds.Where(id => id > 0).Distinct().ToList();
            var allowed = Allowed(ev).ToDictionary(c => c.Id, c => c.Name ?? "");
            var notAllowed = wanted.Where(id => !allowed.ContainsKey(id)).ToList();
            if (notAllowed.Count > 0)
                return (false, "En eller flera tävlingar går inte att koppla till den här händelsen.");

            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;
                var taken = wanted.Count == 0 ? new List<LinkRow>()
                    : db.Fetch<LinkRow>("SELECT CompetitionId, EventId FROM dbo.CompetitionEventLink WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId IN (@0) AND EventId <> @1", wanted, ev.Id);
                foreach (var t in taken)
                {
                    var other = _events.Get(t.EventId);
                    if (other != null)
                        return (false, $"{allowed[t.CompetitionId]} är redan kopplad till händelsen {other.DisplayName}. Ta bort kopplingen där först.");
                }

                db.Execute("DELETE FROM dbo.CompetitionEventLink WHERE EventId = @0", ev.Id);
                if (taken.Count > 0)
                    db.Execute("DELETE FROM dbo.CompetitionEventLink WHERE CompetitionId IN (@0)", taken.Select(t => t.CompetitionId).ToList());
                foreach (var id in wanted)
                    db.Execute("INSERT INTO dbo.CompetitionEventLink (CompetitionId, EventId, LinkedByMemberId) VALUES (@0, @1, @2)",
                        id, ev.Id, actorMemberId);
                scope.Complete();
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kopplingarna för händelse {EventId} kunde inte sparas", ev.Id);
                return (false, "Kopplingarna kunde inte sparas. Försök igen om en stund.");
            }
        }
    }
}
