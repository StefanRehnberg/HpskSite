using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Vad som hänger kvar på en tävling</b> och därför stoppar borttagningen (C3-planen S5c). Förut
    /// stoppade bara anmälningarna; lag, resultat, betalningar och bemanning togs bort med tävlingen eller
    /// blev hängande utan den.
    ///
    /// <para>Anmälningarna prövas av anroparen (anmälningslagringen). Rekord och mästartitlar bär bara
    /// tävlingens NAMN, inte dess id, och står därför kvar oberoende av den.</para>
    ///
    /// <para>En tabell som saknas (en miljö utan migreringen) räknas som tom — kontrollen ska inte göra en
    /// borttagning omöjlig för att en funktion aldrig byggts där.</para>
    /// </summary>
    public class CompetitionDeleteGuard
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CompetitionDeleteGuard> _logger;

        public CompetitionDeleteGuard(IScopeProvider scopeProvider, ILogger<CompetitionDeleteGuard> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        private static readonly string[] ResultTables =
        {
            "PrecisionResultEntry", "MilsnabbResultEntry", "DuellResultEntry", "NationellHelmatchResultEntry",
            "MagnumPrecisionResultEntry", "SpringskytteResultEntry", "SpringskytteStafettResultEntry",
            "FaltskytteResultEntry", "StandardpistolResultEntry", "SportpistolResultEntry",
        };

        /// <summary>Det som stoppar borttagningen, som läsbara rader ("3 lag"). Tom lista = får tas bort.</summary>
        public IReadOnlyList<string> Blockers(int competitionId)
        {
            var found = new List<string>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            int Count(string sql, params object[] args)
            {
                try { return db.ExecuteScalar<int>(sql, args); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Borttagningsspärren: frågan gick inte ({Sql})", sql);
                    return 0;
                }
            }
            void Add(int n, string one, string many) { if (n > 0) found.Add($"{n} {(n == 1 ? one : many)}"); }

            Add(Count("SELECT COUNT(*) FROM dbo.CompetitionTeam WHERE CompetitionId = @0", competitionId), "lag", "lag");
            Add(ResultTables.Sum(t => Count($"SELECT COUNT(*) FROM dbo.{t} WHERE CompetitionId = @0", competitionId)),
                "inmatat resultat", "inmatade resultat");

            const string ledger = "SELECT COUNT(*) FROM {0}.LedgerPayment WHERE SourceId = @0 AND SourceType IN (@1, @2) AND VoidedUtc IS NULL";
            Add(Count(string.Format(ledger, "dbo"), competitionId, LedgerSourceType.CompetitionRegistration, LedgerSourceType.TeamFee)
                + Count(string.Format(ledger, "sbx"), competitionId, LedgerSourceType.CompetitionRegistration, LedgerSourceType.TeamFee),
                "avgift eller betalning i liggaren", "avgifter eller betalningar i liggaren");

            Add(Count(@"SELECT COUNT(*) FROM umbracoNode n
                        JOIN umbracoContent c ON c.nodeId = n.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                        WHERE ct.alias = 'registrationInvoice' AND n.trashed = 0 AND n.path LIKE @0",
                    $"%,{competitionId},%"),
                "faktura (det gamla sättet)", "fakturor (det gamla sättet)");

            Add(Count("SELECT COUNT(*) FROM dbo.StandardMedalAward WHERE CompetitionId = @0", competitionId), "standardmedalj", "standardmedaljer");
            Add(Count("SELECT COUNT(*) FROM dbo.MarkenSeries WHERE SourceCompetitionId = @0", competitionId), "märkesserie", "märkesserier");
            Add(Count("SELECT COUNT(*) FROM dbo.StaffAssignment WHERE CompetitionId = @0", competitionId), "funktionärsuppdrag", "funktionärsuppdrag");
            Add(Count("SELECT COUNT(*) FROM dbo.CompetitionEventLink WHERE CompetitionId = @0", competitionId), "koppling till ett evenemang", "kopplingar till evenemang");
            Add(Count("SELECT COUNT(*) FROM dbo.CompetitionApplication WHERE CompetitionId = @0", competitionId), "tävlingsansökan", "tävlingsansökningar");
            return found;
        }

        /// <summary>Meddelandet till den som försöker ta bort, eller null när inget stoppar.</summary>
        public string? Refusal(int competitionId)
        {
            var b = Blockers(competitionId);
            return b.Count == 0
                ? null
                : "Tävlingen kan inte tas bort — den har " + string.Join(", ", b) + ". Ta bort dem först, eller låt tävlingen vara kvar.";
        }
    }
}
