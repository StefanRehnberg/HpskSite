using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Klubbens åldersgränser per medlemstyp (steg 3b) och medlemmarnas födelseår. Läs- och skrivvägen för stegen
    /// (<see cref="ClubMembershipTypeAge"/>); reglerna ligger i <see cref="AgeTypeRules"/>.
    ///
    /// <para>⚠️ <see cref="Steps"/> KASTAR när tabellen saknas eller databasen inte svarar. Ett tyst "inga gränser" skulle öppna
    /// betalsidans åldersgrind för alla (regeln är AV utan steg) — anroparna väljer själva hur ett fel ska hanteras
    /// (betalsidan stänger valet, ytan visar felet). Startkontrollen larmar om tabellen saknas.</para>
    ///
    /// <para>Födelseåret läses i en SQL-fråga för många medlemmar (<see cref="MemberPropertyReader"/>), aldrig <c>GetById</c> per
    /// medlem. ⚠️ Läsaren öppnar en EGEN anslutning — anropa den aldrig inuti ett skrivande scope.</para>
    /// </summary>
    public class MembershipAgeLimits
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly MembershipTypeCatalog _types;
        private readonly MemberPropertyReader _reader;

        public MembershipAgeLimits(IScopeProvider scopeProvider, MembershipTypeCatalog types, MemberPropertyReader reader)
        {
            _scopeProvider = scopeProvider;
            _types = types;
            _reader = reader;
        }

        /// <summary>Klubbens steg, lägsta gräns först. Tom lista = regeln är av. Kastar vid fel.</summary>
        public List<AgeStep> Steps(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembershipTypeAge>(
                    "SELECT * FROM ClubMembershipTypeAge WHERE ClubId = @0 ORDER BY FromAge", clubId)
                .Select(r => new AgeStep(r.TypeKey, r.FromAge))
                .ToList();
        }

        /// <summary>Innevarande år (svensk tid) — för århundradesgissningen och "framtid", inte avgiftsåret.</summary>
        public static int CurrentYear => DateTime.Now.Year;

        /// <summary>Födelseår och status per medlem. En medlem som inte finns i registret får <see cref="BirthYearStatus.Missing"/>.</summary>
        public Dictionary<int, BirthYearResult> BirthYears(IEnumerable<int> memberIds)
        {
            var ids = memberIds.Distinct().ToList();
            var result = ids.ToDictionary(i => i, _ => new BirthYearResult(null, BirthYearStatus.Missing));
            if (ids.Count == 0) return result;
            var year = CurrentYear;
            foreach (var row in _reader.Read(new[] { "personNumber", "birthDate" }, ids))
                result[row.Id] = MemberBirthYear.From(row.Get("birthDate"), row.Get("personNumber"), year);
            return result;
        }

        /// <summary>Medlemmens ålder avgiftsåret, eller null när födelseåret inte är känt (säkert eller osäkert).</summary>
        public int? AgeFor(int memberId, int feeYear)
        {
            var b = BirthYears(new[] { memberId }).GetValueOrDefault(memberId);
            return b.IsUsable ? AgeTypeRules.Age(feeYear, b.Year!.Value) : null;
        }

        /// <summary>
        /// Sparar HELA stegen (ersätter allt). Tom lista stänger av regeln. Returnerar ett felmeddelande eller null.
        /// Typerna prövas mot klubbens egna: okänd typ, dold typ och typer som aldrig åldersstyrs avvisas.
        /// </summary>
        public string? SaveSteps(int clubId, IReadOnlyList<AgeStep> requested)
        {
            var types = _types.Types(clubId);
            var steps = new List<AgeStep>();
            foreach (var s in requested)
            {
                var t = types.FirstOrDefault(x => MembershipTypeKeys.Same(x.Key, s.Key));
                if (t is null) return "Okänd medlemstyp bland åldersgränserna.";
                steps.Add(new AgeStep(t.Key, s.FromAge));
            }

            var error = AgeTypeRules.ValidateSteps(steps, types.Select(t => t.Key).ToList());
            if (error is not null) return error;
            var hidden = steps.Select(s => types.First(t => MembershipTypeKeys.Same(t.Key, s.Key))).FirstOrDefault(t => !t.IsActive);
            if (hidden is not null) return $"Typen {hidden.Name} är dold. Visa den först — en dold typ kan inte vara mål för en åldersgräns.";

            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            db.Execute("DELETE FROM ClubMembershipTypeAge WHERE ClubId = @0", clubId);
            var now = DateTime.UtcNow;
            foreach (var s in steps)
                db.Insert(new ClubMembershipTypeAge { ClubId = clubId, TypeKey = s.Key, FromAge = s.FromAge, CreatedUtc = now, UpdatedUtc = now });
            scope.Complete();
            return null;
        }

        /// <summary>Har typen en åldersgräns? (Då får den varken raderas eller döljas innan gränsen tagits bort.) Kastar vid fel.</summary>
        public bool HasLimit(int clubId, string typeKey)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ClubMembershipTypeAge WHERE ClubId = @0 AND TypeKey = @1", clubId, typeKey) > 0;
        }
    }
}
