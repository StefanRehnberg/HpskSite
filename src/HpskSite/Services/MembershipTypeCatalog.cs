using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>En medlemstyp som klubben ser den: fast nyckel, klubbens namn, aktiv eller dold.</summary>
    public record MembershipTypeEntry(string Key, string Name, bool IsActive, bool IsCustom);

    /// <summary>
    /// Rena regler för klubbens medlemstyper. Tabellen <c>ClubMembershipType</c> är lat: ingen rad = standardnamnet,
    /// aktiv. Egna typer (nyckel <c>x-…</c>) finns bara som rad. Nyckeln ändras aldrig; bara namnet och aktiv-flaggan.
    ///
    /// <para>⚠️ Visa alltid <see cref="LabelFor"/>, aldrig den lagrade nyckeln. Importen känner igen NAMNET klubben har nu
    /// (<see cref="ResolveKey"/>) — inte ett gammalt standardnamn efter en omdöpning (Stefans beslut).</para>
    /// </summary>
    public static class MembershipTypeNames
    {
        public static List<MembershipTypeEntry> Effective(IEnumerable<ClubMembershipType> rows)
        {
            var byKey = new Dictionary<string, ClubMembershipType>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
                if (!string.IsNullOrWhiteSpace(r.TypeKey)) byKey[r.TypeKey.Trim()] = r;

            var result = new List<MembershipTypeEntry>();
            foreach (var k in MembershipTypeKeys.Standard)
            {
                byKey.TryGetValue(k, out var row);
                result.Add(new MembershipTypeEntry(k, NameOrDefault(row, k), row?.IsActive ?? true, false));
            }
            foreach (var row in byKey.Values.Where(r => MembershipTypeKeys.IsCustom(r.TypeKey)).OrderBy(r => r.Id))
                result.Add(new MembershipTypeEntry(row.TypeKey.Trim(), NameOrDefault(row, row.TypeKey), row.IsActive, true));
            return result;
        }

        private static string NameOrDefault(ClubMembershipType? row, string key)
            => row is not null && !string.IsNullOrWhiteSpace(row.Name) ? row.Name.Trim() : key;

        /// <summary>Klubbens namn för nyckeln. Okänd text (en gammal fritext) visas som den är.</summary>
        public static string LabelFor(IReadOnlyList<MembershipTypeEntry> entries, string? key)
        {
            var t = key?.Trim();
            if (string.IsNullOrEmpty(t)) return "";
            var e = entries.FirstOrDefault(x => MembershipTypeKeys.Same(x.Key, t));
            return e?.Name ?? t;
        }

        /// <summary>
        /// Nyckeln för en text (importen, formulär): klubbens nuvarande NAMN, skiftlägesokänsligt. Nyckeln själv duger bara
        /// när typen inte döpts om (annars skulle det gamla standardnamnet leva kvar som alias). Okänt = null.
        /// </summary>
        public static string? ResolveKey(IReadOnlyList<MembershipTypeEntry> entries, string? text)
        {
            var t = text?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            var byName = entries.FirstOrDefault(e => string.Equals(e.Name, t, StringComparison.OrdinalIgnoreCase));
            return byName?.Key;
        }
    }

    /// <summary>Läser klubbens medlemstyper. Saknas tabellen (migreringen inte körd) gäller standardnamnen.</summary>
    public class MembershipTypeCatalog
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<MembershipTypeCatalog> _logger;

        public MembershipTypeCatalog(IScopeProvider scopeProvider, ILogger<MembershipTypeCatalog> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public List<MembershipTypeEntry> Types(int clubId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var rows = scope.Database.Fetch<ClubMembershipType>(
                    "SELECT * FROM ClubMembershipType WHERE ClubId = @0", clubId);
                return MembershipTypeNames.Effective(rows);
            }
            catch (Exception ex)
            {
                // Tabellen saknas (före migreringen) eller databasen svarade inte: standardnamnen är det säkra läget.
                // Startkontrollen (MembershipFeeSchemaGuardHostedService) larmar om tabellen saknas.
                _logger.LogWarning(ex, "Klubbens medlemstyper kunde inte läsas för klubb {ClubId}; standardnamnen används.", clubId);
                return MembershipTypeNames.Effective(Array.Empty<ClubMembershipType>());
            }
        }

        public string LabelFor(int clubId, string? key) => MembershipTypeNames.LabelFor(Types(clubId), key);

        public string? ResolveKey(int clubId, string? text) => MembershipTypeNames.ResolveKey(Types(clubId), text);
    }
}
