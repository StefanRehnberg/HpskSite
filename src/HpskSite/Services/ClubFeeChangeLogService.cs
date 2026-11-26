using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Ändringsloggen för medlemsavgiftens inställningar (K4). EN skrivväg: varje ändpunkt som ändrar taxa, tillägg, förbund eller typer
    /// anropar <see cref="Record"/> efter en lyckad ändring. Ett fel i loggen får aldrig stoppa ändringen, men det loggas som fel
    /// (en revisionslogg som tyst tappar rader är värre än ingen), och saknas tabellen står det i loggen vilket skript som ska köras.
    /// </summary>
    public class ClubFeeChangeLogService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<ClubFeeChangeLogService> _logger;

        public ClubFeeChangeLogService(IScopeProvider scopeProvider, ILogger<ClubFeeChangeLogService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public void Record(int clubId, int? year, string entity, string? key, string action, string detail, int? byMemberId, string? basis)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                scope.Database.Insert(new ClubFeeChangeLogEntry
                {
                    ClubId = clubId,
                    Year = year,
                    Entity = entity,
                    EntityKey = Trim(key, 100),
                    Action = action,
                    Detail = Trim(detail, 500) ?? "",
                    ByMemberId = byMemberId is > 0 ? byMemberId : null,
                    Basis = Trim(basis, 30),
                    ChangedUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ändringsloggen för medlemsavgiften kunde inte skrivas (klubb {ClubId}, {Entity}, {Detail}). Saknas tabellen: kör Migrations/create-club-fee-change-log.sql.", clubId, entity, detail);
            }
        }

        /// <summary>De senaste ändringarna för en klubb, nyast först.</summary>
        public List<ClubFeeChangeLogEntry> Recent(int clubId, int take = 50)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubFeeChangeLogEntry>(
                "SELECT TOP (@1) * FROM ClubFeeChangeLog WHERE ClubId = @0 ORDER BY ChangedUtc DESC, Id DESC", clubId, Math.Clamp(take, 1, 500));
        }

        private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
    }
}
