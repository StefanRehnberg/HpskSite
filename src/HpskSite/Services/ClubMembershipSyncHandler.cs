using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Håller <c>ClubMembership</c> i takt med medlemmens klubbar (<c>primaryClubId</c>/<c>memberClubIds</c>)
    /// vid VARJE sparning av en medlem (skalplanen B3, 2026-10-12).
    ///
    /// <para><b>Varför en notis och inte anrop i skrivvägarna:</b> klubbtillhörigheten skrivs på ett
    /// dussintal ställen (registrering, import, medlemsdialogen, klubbadmin, sammanslagning, seedrar).
    /// Mätt i dev 2026-10-12: 125 av 275 (medlem, klubb)-par saknade rad, för att flera av vägarna
    /// aldrig skapade den. En notis täcker alla, också de som skrivs i morgon.</para>
    ///
    /// <para><b>Två regler:</b></para>
    /// <list type="bullet">
    /// <item><b>En klubb i egenskaperna</b> → raden skapas om den saknas (<see cref="ClubMembershipService.EnsureMembership"/>,
    ///   som aldrig sätter datum eller rör en befintlig rad).</item>
    /// <item><b>En klubb som TOGS BORT ur egenskaperna i den här sparningen</b> → medlemskapet sätts till
    ///   <c>Utträdd</c> med dagens datum, om det var aktivt (Stefans beslut 2026-10-12). Samma sak som
    ///   klubbadmins "Avsluta medlemskap" gör; personen och all data finns kvar och det går att
    ///   återaktivera. Bara en FAKTISK borttagning räknas — klubbarna före sparningen läses ur databasen
    ///   i Saving-steget, så en sparning som inte rör klubbarna ändrar ingenting.</item>
    /// <item><b>En klubb som LADES TILL i den här sparningen</b> och har ett <c>Utträdd</c> medlemskap →
    ///   aktivt igen, <c>MemberUntil</c> tömt.</item>
    /// </list>
    ///
    /// <para>Ett fel här får aldrig fälla sparningen; det loggas.</para>
    /// </summary>
    public sealed class ClubMembershipSyncHandler :
        INotificationHandler<MemberSavingNotification>,
        INotificationHandler<MemberSavedNotification>
    {

        private readonly ClubMembershipService _memberships;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<ClubMembershipSyncHandler> _logger;

        public ClubMembershipSyncHandler(ClubMembershipService memberships, IScopeProvider scopeProvider,
            ILogger<ClubMembershipSyncHandler> logger)
        {
            _memberships = memberships;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>
        /// Borttagna och återtillagda klubbar avgörs HÄR: medlemmen bär de nya värdena och databasen
        /// fortfarande de gamla. Körs i sparningens egen transaktion, så en sparning som misslyckas tar
        /// statusändringen med sig.
        /// </summary>
        public void Handle(MemberSavingNotification notification)
        {
            foreach (var member in notification.SavedEntities)
            {
                if (member.Id <= 0) continue; // ny medlem — inga klubbar att tappa
                try
                {
                    var old = StoredClubIds(member.Id);
                    var now = ClubIds(member);
                    foreach (var removed in Removed(old, now))
                        if (_memberships.EndIfActive(member.Id, removed))
                            _logger.LogInformation("Medlem {MemberId} togs bort ur klubb {ClubId} — medlemskapet satt till Utträdd.", member.Id, removed);
                    // En klubb som läggs TILL igen gör ett utträtt medlemskap aktivt — annars står
                    // medlemmen i egenskaperna men syns inte i klubbens listor.
                    foreach (var added in Removed(now, old))
                        if (_memberships.ReactivateIfEnded(member.Id, added))
                            _logger.LogInformation("Medlem {MemberId} lades till i klubb {ClubId} igen — medlemskapet aktivt igen.", member.Id, added);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Klubbmedlemskapet kunde inte synkas före sparningen av medlem {MemberId}.", member.Id);
                }
            }
        }

        /// <summary>Raden skapas EFTER sparningen — en ny medlem har inget id förrän då.</summary>
        public void Handle(MemberSavedNotification notification)
        {
            foreach (var member in notification.SavedEntities)
            {
                try
                {
                    foreach (var clubId in ClubIds(member))
                        _memberships.EnsureMembership(member.Id, clubId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Klubbmedlemskapet kunde inte synkas för medlem {MemberId}.", member.Id);
                }
            }
        }

        /// <summary>Klubbar som fanns före sparningen men inte efter. Ren funktion.</summary>
        public static IReadOnlyList<int> Removed(IReadOnlyList<int> before, IReadOnlyList<int> after)
            => before.Where(c => !after.Contains(c)).ToList();

        /// <summary>Klubbarna i egenskaperna (huvudklubben och övriga), utan dubbletter. Ren funktion.</summary>
        public static IReadOnlyList<int> ClubIds(IMember member)
            => ClubIds(member.GetValue("primaryClubId")?.ToString(), member.GetValue("memberClubIds")?.ToString());

        public static IReadOnlyList<int> ClubIds(string? primary, string? others)
        {
            var result = new List<int>();
            if (int.TryParse((primary ?? "").Trim(), out var p) && p > 0) result.Add(p);
            foreach (var part in (others ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), out var id) && id > 0 && !result.Contains(id)) result.Add(id);
            return result;
        }

        /// <summary>Klubbarna som är SPARADE i databasen just nu — före sparningen som pågår.</summary>
        private IReadOnlyList<int> StoredClubIds(int memberId)
        {
            // ⚠️ Går med i sparningens scope — en egen anslutning medan Umbracos sparning håller sin
            // transaktion öppen är formen som har låst förut (egen anslutning i någon annans scope).
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<PropRow>(
                @"SELECT pt.alias AS Alias,
                         COALESCE(CAST(pd.intValue AS NVARCHAR(20)), pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) AS Value
                    FROM umbracoPropertyData pd
                    JOIN cmsPropertyType pt       ON pt.id = pd.propertyTypeId
                    JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.[current] = 1
                   WHERE cv.nodeId = @0 AND pt.alias IN ('primaryClubId', 'memberClubIds')", memberId);
            // ⚠️ Aliaset heter "memberClubIDs" i databasen (versalt IDs). SQL Server jämför utan skiftläge,
            // C# gör det inte — en literal jämförelse gav tyst bara huvudklubben (mätt 2026-10-12).
            return ClubIds(rows.FirstOrDefault(r => string.Equals(r.Alias, "primaryClubId", StringComparison.OrdinalIgnoreCase))?.Value,
                           rows.FirstOrDefault(r => string.Equals(r.Alias, "memberClubIds", StringComparison.OrdinalIgnoreCase))?.Value);
        }

        private sealed class PropRow
        {
            public string Alias { get; set; } = "";
            public string? Value { get; set; }
        }
    }
}
