using HpskSite.Models.Firearms;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    [TableName("FirearmUsage")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FirearmUsage
    {
        public int Id { get; set; }
        public int FirearmId { get; set; }
        public int MemberId { get; set; }

        /// <summary>
        /// <c>"training"</c> eller <c>"comp"</c> — samma vokabulär som
        /// <c>MemberActivityEntry.SourceKind</c>. Halva nyckeln, inte metadata.
        /// </summary>
        public string SourceKind { get; set; } = string.Empty;
        public int SourceId { get; set; }
        public DateTime OccurredOn { get; set; }
        public DateTime CreatedAt { get; set; }

        [ResultColumn] public string? FirearmAlias { get; set; }
    }

    /// <summary>
    /// Vilket vapen som användes vid ett tillfälle.
    ///
    /// <para><b>Syftet är skyttens eget</b> — se vad som faktiskt fungerar. Tabellen bär inga
    /// intygspåståenden: kravet "tränat två gånger med vapnet" är struket, eftersom en
    /// förstagångssökande tränat med lånevapen och den historiken aldrig kan finnas.</para>
    ///
    /// <para><b>⚠️ Taggningen sker ALDRIG i funktionärens sifferpanel.</b> Tävlingsresultat matas in
    /// av funktionärer, som inte vet vilket vapen skytten använde. Därför är tävlingstaggningen en
    /// efterhandsåtgärd på medlemmens egen sida — och den delade <c>.sp-*</c>-komponenten behöver
    /// inte öppnas.</para>
    /// </summary>
    public class FirearmUsageService
    {
        public const string SourceTraining = "training";
        public const string SourceCompetition = "comp";

        private static readonly string[] ValidSources = { SourceTraining, SourceCompetition };

        private readonly IScopeProvider _scopeProvider;
        private readonly FirearmService _firearms;
        private readonly ILogger<FirearmUsageService> _logger;

        public FirearmUsageService(
            IScopeProvider scopeProvider,
            FirearmService firearms,
            ILogger<FirearmUsageService> logger)
        {
            _scopeProvider = scopeProvider;
            _firearms = firearms;
            _logger = logger;
        }

        /// <summary>
        /// Taggar (eller om-taggar) ett tillfälle med ett vapen. <paramref name="firearmId"/> = 0
        /// tar bort taggningen.
        ///
        /// <para><b>⚠️ Vapnet måste vara medlemmens EGET eller ett lånebart klubbvapen.</b> Utan
        /// kontrollen kunde en medlem tagga någon annans vapen på sitt resultat — vilket vore ett
        /// sätt att få ett alias hen inte äger att synas i sin egen lista.</para>
        /// </summary>
        public string? SetUsage(int memberId, string sourceKind, int sourceId, int firearmId, DateTime occurredOn)
        {
            if (memberId <= 0) return "Ogiltig medlem.";
            if (!ValidSources.Contains((sourceKind ?? "").Trim(), StringComparer.Ordinal))
                return $"Okänd källa '{sourceKind}'.";
            if (sourceId <= 0) return "Ogiltigt tillfälle.";

            var kind = sourceKind.Trim();

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            if (firearmId <= 0)
            {
                db.Execute(
                    "DELETE FROM FirearmUsage WHERE MemberId = @0 AND SourceKind = @1 AND SourceId = @2",
                    memberId, kind, sourceId);
                return null;
            }

            var firearm = _firearms.GetById(firearmId);
            if (firearm is null) return "Vapnet hittades inte.";

            var ownsIt = firearm.Scope == FirearmScope.Member(memberId);
            var isLoanable = firearm.Scope.Kind == FirearmOwnerKind.Club && firearm.IsLoanable;
            if (!ownsIt && !isLoanable)
                return "Du kan bara ange dina egna vapen eller klubbens lånevapen.";

            // Ersätt, inte lägg till: ett tillfälle bär ett vapen. Delete+insert i stället för en
            // MERGE eftersom det unika indexet redan är spärren och mängden är en rad.
            db.Execute(
                "DELETE FROM FirearmUsage WHERE MemberId = @0 AND SourceKind = @1 AND SourceId = @2",
                memberId, kind, sourceId);

            db.Insert(new FirearmUsage
            {
                FirearmId = firearmId,
                MemberId = memberId,
                SourceKind = kind,
                SourceId = sourceId,
                OccurredOn = occurredOn.Date,
                CreatedAt = DateTime.Now,
            });

            return null;
        }

        /// <summary>Antal tillfällen per vapen för en medlem — "Använt vid N tillfällen" på kortet.</summary>
        public Dictionary<int, int> CountsForMember(int memberId)
        {
            if (memberId <= 0) return new Dictionary<int, int>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.Fetch<CountRow>(
                        "SELECT FirearmId, COUNT(*) AS [Count] FROM FirearmUsage " +
                        "WHERE MemberId = @0 GROUP BY FirearmId", memberId)
                    .ToDictionary(r => r.FirearmId, r => r.Count);
            }
            catch (Exception ex)
            {
                // Tabellen saknas = migreringen inte körd. Noll är rätt svar då, och kortet visar
                // ingen rad om användning.
                _logger.LogDebug(ex, "Kunde inte räkna vapenanvändning för medlem {MemberId}.", memberId);
                return new Dictionary<int, int>();
            }
        }

        /// <summary>
        /// Taggningen per tillfälle för en medlem, som en uppslagning på den SAMMANSATTA nyckeln.
        /// Läses av resultatlistan för att kunna visa vilket vapen som är angivet.
        /// </summary>
        public Dictionary<string, int> UsageBySourceForMember(int memberId)
        {
            if (memberId <= 0) return new Dictionary<string, int>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.Fetch<FirearmUsage>(
                        "SELECT * FROM FirearmUsage WHERE MemberId = @0", memberId)
                    .ToDictionary(u => Key(u.SourceKind, u.SourceId), u => u.FirearmId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa vapenanvändning för medlem {MemberId}.", memberId);
                return new Dictionary<string, int>();
            }
        }

        /// <summary>De senaste tillfällena för ett vapen.</summary>
        public List<FirearmUsage> RecentForFirearm(int firearmId, int max = 10)
        {
            if (firearmId <= 0) return new List<FirearmUsage>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.Fetch<FirearmUsage>(
                    "SELECT TOP (@0) * FROM FirearmUsage WHERE FirearmId = @1 ORDER BY OccurredOn DESC, Id DESC",
                    Math.Clamp(max, 1, 100), firearmId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa användning för vapen {FirearmId}.", firearmId);
                return new List<FirearmUsage>();
            }
        }

        /// <summary>Den sammansatta nyckeln som EN sträng. Använd den, bygg den inte för hand.</summary>
        public static string Key(string sourceKind, int sourceId) => $"{sourceKind}:{sourceId}";

        private class CountRow
        {
            public int FirearmId { get; set; }
            public int Count { get; set; }
        }
    }
}
