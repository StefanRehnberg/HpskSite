using HpskSite.Models.Planning;
using HpskSite.Services.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Omgång 3 steg 10 (Expertgruppen K7, GDPR art. 5.1 e): en borttagen planering rensas för gott efter
    /// <see cref="RetentionDays"/> dagar — deltagarna (även gäster och fritextfunktionärer), meddelandena, uppgifterna,
    /// bemanningen, dokumenten med filerna och loggarna. Fram till dess kan den tas tillbaka i databasen.
    /// <para>⚠️ Tabellistan nedan är den kompletta — en ny tabell med <c>PlanningId</c> ska läggas här, annars blir dess
    /// rader kvar för alltid efter rensningen (<c>PlanningPurgeCoverageTests</c> jämför med databasen).</para>
    /// </summary>
    public class PlanningPurgeService
    {
        public const int RetentionDays = 90;

        /// <summary>Tabellerna som bär <c>PlanningId</c>, i den ordning de rensas (barn före föräldrar).</summary>
        public static readonly string[] PlanningIdTables =
        {
            "StaffAssignment", "StaffAvailability", "StaffDay", "StaffPass", "StaffCrewNeed", "StaffHelpSlot", "StaffHelpSignup",
            "StaffingSourceScope", "StaffRequestLog",
            "PlanningDecisionResponse", "PlanningDecision",
            "PlanningDocumentRef", "PlanningDocument", "PlanningDocumentCategory",
            "PlanningTaskEvent", "PlanningTask",
            // Meddelandena före områdena: FK_PlanningMessage_Area (2026-10-25) pekar på området.
            "PlanningMessageMention", "PlanningMessage", "PlanningMessageRead", "PlanningNotifyLog",
            "PlanningStationResponsible", "PlanningShooterMode", "PlanningArea",
            "PlanningMeetingTopic", "PlanningRole",
            "PlanningRequest", "PlanningMember", "PlanningLink", "PlanningActivity",
        };

        private readonly IScopeProvider _scopes;
        private readonly PrepDocumentStorage _files;
        private readonly ILogger<PlanningPurgeService> _logger;

        public PlanningPurgeService(IScopeProvider scopes, PrepDocumentStorage files, ILogger<PlanningPurgeService> logger)
        {
            _scopes = scopes; _files = files; _logger = logger;
        }

        public sealed record PurgeResult(int Plannings, int Files, int Backups = 0);

        /// <summary>
        /// Säkerhetskopiorna av fältets startlista (en rad per ombyggnad, hela listan som JSON med skyttarnas namn och klubbar) behövs bara så länge en ombyggnad kan ångras och
        /// granskas. De gallras efter <see cref="RetentionDays"/> dagar — de hör till tävlingen och inte till en planering, så planeringsrensningen når dem inte.
        /// </summary>
        public int PurgeBackups(DateTime now)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Execute("IF OBJECT_ID('dbo.FaltskyttePatrolBackup') IS NOT NULL DELETE FROM FaltskyttePatrolBackup WHERE CreatedUtc < @0", now.ToUniversalTime().AddDays(-RetentionDays));
        }

        /// <summary>Rensar planeringar som tagits bort för mer än <see cref="RetentionDays"/> dagar sedan, och gamla säkerhetskopior av startlistan.</summary>
        public async Task<PurgeResult> PurgeAsync(DateTime now)
        {
            var backups = 0;
            try { backups = PurgeBackups(now); }
            catch (Exception ex) { _logger.LogError(ex, "Gallringen av startlistans säkerhetskopior föll — försöker igen nästa varv."); }
            if (backups > 0) _logger.LogInformation("Planeringsrensningen: {Backups} gamla säkerhetskopior av startlistan borttagna.", backups);
            List<int> ids;
            using (var s = _scopes.CreateScope(autoComplete: true))
                ids = s.Database.Fetch<int>("SELECT Id FROM Planning WHERE DeletedAt IS NOT NULL AND DeletedAt < @0 ORDER BY CASE WHEN ParentPlanningId IS NULL THEN 1 ELSE 0 END, Id",
                    now.AddDays(-RetentionDays));
            int plannings = 0, files = 0;
            foreach (var id in ids)
            {
                try { files += await PurgeOneAsync(id); plannings++; }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: rensningen föll — försöker igen nästa varv.", id); }
            }
            if (plannings > 0) _logger.LogInformation("Planeringsrensningen: {Plannings} planeringar och {Files} filer borttagna.", plannings, files);
            return new PurgeResult(plannings, files, backups);
        }

        private async Task<int> PurgeOneAsync(int id)
        {
            List<string> stored;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                // En delplanering som står kvar hindrar rensningen — den raderas först (samma regel som borttagningen).
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM Planning WHERE ParentPlanningId = @0 AND DeletedAt IS NULL", id) > 0)
                {
                    _logger.LogWarning("Planering {Id}: borttagen men har en kvarvarande delplanering — rensas inte.", id);
                    return 0;
                }
                stored = db.Fetch<string>("SELECT StoredFileName FROM PlanningDocument WHERE PlanningId = @0 AND StoredFileName IS NOT NULL", id);
                db.Execute("DELETE v FROM PlanningMeetingVote v JOIN PlanningMeeting m ON m.Id = v.PlanningMeetingId WHERE m.PlanningId = @0", id);
                db.Execute("DELETE o FROM PlanningMeetingOption o JOIN PlanningMeeting m ON m.Id = o.PlanningMeetingId WHERE m.PlanningId = @0", id);
                foreach (var t in PlanningIdTables) db.Execute($"IF OBJECT_ID('dbo.{t}') IS NOT NULL DELETE FROM {t} WHERE PlanningId = @0", id);
                db.Execute("DELETE FROM PlanningMeeting WHERE PlanningId = @0", id);
                db.Execute("DELETE FROM StaffRole WHERE OwnerType = N'Planning' AND OwnerId = @0", id);
                db.Execute("IF OBJECT_ID('dbo.PlanningShootIntent') IS NOT NULL DELETE FROM PlanningShootIntent WHERE RootPlanningId = @0 OR TargetPlanningId = @0", id);
                db.Execute("UPDATE Planning SET CopiedFromPlanningId = NULL WHERE CopiedFromPlanningId = @0", id);
                // Tävlingsansökan är föreningens (kretsens ärende), inte planeringens — den står kvar, utan kopplingen.
                db.Execute("IF OBJECT_ID('dbo.CompetitionApplication') IS NOT NULL UPDATE CompetitionApplication SET PlanningId = NULL WHERE PlanningId = @0", id);
                db.Execute("DELETE FROM Planning WHERE Id = @0", id);
            }
            // Filerna efter raderna: en fil som en annan rad fortfarande pekar på (en kopierad planering, tävlingens
            // förberedelser) står kvar.
            var removed = 0;
            foreach (var name in stored.Distinct())
            {
                int refs;
                using (var s = _scopes.CreateScope(autoComplete: true))
                    refs = s.Database.ExecuteScalar<int>("SELECT (SELECT COUNT(*) FROM PlanningDocument WHERE StoredFileName = @0) + (SELECT COUNT(*) FROM WorkLink WHERE StoredFileName = @0)", name);
                if (refs > 0) continue;
                try { await _files.DeleteAsync(name); removed++; }
                catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: filen {File} kunde inte tas bort.", id, name); }
            }
            return removed;
        }
    }
}
