using HpskSite.Models;
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

        /// <summary>
        /// Vapenklassen tillfället gäller, eller <c>null</c>.
        ///
        /// <para><b>⚠️ TREDJE DELEN AV NYCKELN, för en tävling.</b> Resultatlistan grupperar en
        /// officiell tävling per (tävling, vapenklass), så en skytt anmäld i både A1 och C1 har två
        /// rader med samma <see cref="SourceId"/>. Utan klassen i nyckeln skriver en taggning av
        /// A-vapnet TYST över taggningen av C-vapnet, och skytten kan aldrig ange mer än ett vapen
        /// per tävlingsdag. Samma nyckelform som resten av kodbasen: en start är per (skytt, klass).</para>
        ///
        /// <para><b>⚠️ NULL för en träningsrad</b>, med flit — <c>TrainingScores.Id</c> är redan ett
        /// tillfälle i sig. SQL Server behandlar NULL som lika i ett unikt index, så spärren håller
        /// även där.</para>
        ///
        /// <para>Lagras som klassens <b>visningsnamn</b> (<c>ShootingClasses.ToCanonicalName</c>),
        /// samma konvention som resultatraderna.</para>
        /// </summary>
        public string? SourceClass { get; set; }

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
        public string? SetUsage(
            int memberId, string sourceKind, int sourceId, int firearmId, DateTime occurredOn,
            string? sourceClass = null)
        {
            if (memberId <= 0) return "Ogiltig medlem.";
            if (!ValidSources.Contains((sourceKind ?? "").Trim(), StringComparer.Ordinal))
                return $"Okänd källa '{sourceKind}'.";
            if (sourceId <= 0) return "Ogiltigt tillfälle.";

            var kind = sourceKind.Trim();
            var cls = NormaliseClass(kind, sourceClass);

            // ⚠️ Klassen måste ligga i WHERE i BÅDA riktningarna. Utan den i DELETE:n raderar en
            // taggning av A-vapnet också C-vapnets rad på samma tävling — alltså precis den tysta
            // överskrivning som kolumnen finns för att förhindra. Och NULL-fallet måste skrivas ut:
            // `SourceClass = NULL` matchar ingenting, så en träningsrad hade aldrig gått att tagga om.
            const string where =
                "WHERE MemberId = @0 AND SourceKind = @1 AND SourceId = @2 " +
                "AND ((SourceClass IS NULL AND @3 IS NULL) OR SourceClass = @3)";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            if (firearmId <= 0)
            {
                db.Execute("DELETE FROM FirearmUsage " + where, memberId, kind, sourceId, cls);
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
            db.Execute("DELETE FROM FirearmUsage " + where, memberId, kind, sourceId, cls);

            db.Insert(new FirearmUsage
            {
                FirearmId = firearmId,
                MemberId = memberId,
                SourceKind = kind,
                SourceId = sourceId,
                SourceClass = cls,
                OccurredOn = occurredOn.Date,
                CreatedAt = DateTime.Now,
            });

            return null;
        }

        /// <summary>
        /// Klassdelen av nyckeln, normaliserad.
        ///
        /// <para><b>⚠️ Bara en TÄVLING bär en klass.</b> En träningsrad tvingas till null oavsett vad
        /// anroparen skickar — annars kunde samma träningspass få två rader (en med klass, en utan)
        /// beroende på vilken yta som taggade det, och "använt vid N tillfällen" skulle dubbelräkna.</para>
        ///
        /// <para><b>⚠️ Går via <c>ShootingClasses.ToCanonicalName</c>.</b> Klassen finns i två
        /// strängformer — id (<c>C_Vet_Y</c>) och visningsnamn (<c>C Vet Y</c>) — och de är IDENTISKA
        /// för C1/C2/A1 men olika för varje klass med ändelse. En rak jämförelse skulle alltså se ut
        /// att fungera i all testning och dela veteran-, dam-, junior- och optikklasserna i två
        /// tillfällen. Okänd indata behålls trimmad, aldrig kastad.</para>
        /// </summary>
        private static string? NormaliseClass(string kind, string? sourceClass)
        {
            if (!string.Equals(kind, SourceCompetition, StringComparison.Ordinal)) return null;

            var canonical = ShootingClasses.ToCanonicalName(sourceClass);
            if (string.IsNullOrEmpty(canonical)) return null;

            // Kolumnen är NVARCHAR(20). Ingen känd klass är i närheten, men en okänd sträng som
            // släpps igenom oavkortad hade gett ett trunkeringsfel i stället för en taggning.
            return canonical.Length > 20 ? canonical.Substring(0, 20) : canonical;
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
                    .ToDictionary(u => Key(u.SourceKind, u.SourceId, u.SourceClass), u => u.FirearmId);
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

        /// <summary>
        /// Den sammansatta nyckeln som EN sträng. Använd den, bygg den inte för hand.
        ///
        /// <para><b>⚠️ Klassdelen är GEMEN och Id/Namn-foldad</b> (<c>ShootingClasses.NormalizeKey</c>),
        /// medan kolumnen lagrar visningsnamnet. Nyckeln är en uppslagsnyckel, inte lagringsformen.
        /// Klienten bygger samma nyckel via <c>window.getShootingClassName(...).toLowerCase()</c> —
        /// håll de två i takt, annars hittar resultatlistan aldrig sin egen taggning för just de
        /// klasser där formerna skiljer sig.</para>
        ///
        /// <para>Utan klass blir nyckeln oförändrad <c>kind:id</c>, så träningsraderna behåller sin
        /// gamla form.</para>
        /// </summary>
        public static string Key(string sourceKind, int sourceId, string? sourceClass = null)
        {
            var head = $"{sourceKind}:{sourceId}";
            var cls = ShootingClasses.NormalizeKey(sourceClass);
            return string.IsNullOrEmpty(cls) ? head : $"{head}:{cls}";
        }

        private class CountRow
        {
            public int FirearmId { get; set; }
            public int Count { get; set; }
        }
    }
}
