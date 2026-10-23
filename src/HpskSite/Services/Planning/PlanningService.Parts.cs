using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Delarna som ORGANISATION (P3 steg D, 2026-10-23): en gren eller ett ansvar, med en ansvarig och en värd.
    /// Uppgifter och meddelanden kan märkas med en del. ⚠️ Bemanning och koppling per del finns inte (P4) —
    /// bemanningen och kopplingen gäller hela planeringen. Grunddelen (Root) är planeringen själv och visas aldrig som en del.
    /// </summary>
    public partial class PlanningService
    {
        public List<PlanningPartRow> Parts(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningPartRow>(
                "SELECT * FROM PlanningPart WHERE PlanningId = @0 AND DeletedAt IS NULL AND Kind <> N'Root' ORDER BY SortOrder, Id", planningId);
        }

        /// <summary>Hör delen till planeringen? Null/0 = hela planeringen, och det är alltid tillåtet.</summary>
        private static string? PartError(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int? partId)
        {
            if (!(partId > 0)) return null;
            var ok = db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningPart WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL AND Kind <> N'Root'",
                partId, planningId);
            return ok == 1 ? null : "Delen finns inte i planeringen.";
        }

        public sealed record PartInput(string? Name, string? Kind, string? Description, string? DisciplineKey,
            int? ResponsibleParticipantId, string? ResponsibleName, int? HostClubId, int? HostRegionId, string? HostText);

        /// <summary>Ny del (<paramref name="partId"/> = 0) eller ändrad.</summary>
        public (int PartId, string? Error) SavePart(int planningId, int partId, int byParticipantId, PartInput i)
        {
            var name = Clean(i.Name, 200);
            if (name == null) return (0, "Ge delen ett namn, till exempel \"Fält\" eller \"Sekretariatet\".");
            var kind = PlanningPartKind.Normalize(i.Kind);
            if (kind == null) return (0, "Välj om delen är en gren eller ett ansvar.");
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var err = AssigneeError(db, planningId, i.ResponsibleParticipantId);
            if (err != null) return (0, err);
            PlanningPartRow p;
            if (partId > 0)
            {
                p = db.SingleOrDefault<PlanningPartRow>(
                    "SELECT * FROM PlanningPart WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL AND Kind <> N'Root'", partId, planningId);
                if (p == null) return (0, "Delen finns inte.");
            }
            else
            {
                var root = db.ExecuteScalar<int?>("SELECT TOP 1 Id FROM PlanningPart WHERE PlanningId = @0 AND Kind = N'Root' AND DeletedAt IS NULL ORDER BY Id", planningId);
                p = new PlanningPartRow
                {
                    PlanningId = planningId, ParentPlanningPartId = root, CreatedAt = now, CreatedBy = byParticipantId,
                    SortOrder = db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) FROM PlanningPart WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId) + 1,
                };
            }
            p.Name = name; p.Kind = kind; p.Description = Clean(i.Description, 2000);
            p.DisciplineKey = kind == PlanningPartKind.Discipline ? Clean(i.DisciplineKey, 50) : null;
            p.ResponsibleParticipantId = i.ResponsibleParticipantId > 0 ? i.ResponsibleParticipantId : null;
            p.ResponsibleName = p.ResponsibleParticipantId.HasValue ? null : Clean(i.ResponsibleName, 200);
            // Värden är EN sak: en klubb, en krets eller fri text.
            p.HostClubId = i.HostClubId > 0 ? i.HostClubId : null;
            p.HostRegionId = p.HostClubId.HasValue ? null : (i.HostRegionId > 0 ? i.HostRegionId : null);
            p.HostText = p.HostClubId.HasValue || p.HostRegionId.HasValue ? null : Clean(i.HostText, 200);
            if (p.Id > 0) db.Update(p); else db.Insert(p);
            if (partId <= 0) InsertSystem(db, planningId, "part-new", $"Ny del: {p.Name}.", byParticipantId, null, ("part", p.Id), now);
            Bump(db, planningId);
            return (p.Id, null);
        }

        /// <summary>
        /// Tar bort en del. Uppgifterna och meddelandena står kvar och gäller hela planeringen — inget försvinner
        /// med delen.
        /// </summary>
        public (string? Error, int Tasks, int Messages) RemovePart(int planningId, int partId, int byParticipantId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = db.SingleOrDefault<PlanningPartRow>(
                "SELECT * FROM PlanningPart WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL AND Kind <> N'Root'", partId, planningId);
            if (p == null) return ("Delen finns inte.", 0, 0);
            p.DeletedAt = now;
            db.Update(p);
            var tasks = db.Execute("UPDATE PlanningTask SET PartId = NULL WHERE PlanningId = @0 AND PartId = @1", planningId, partId);
            var msgs = db.Execute("UPDATE PlanningMessage SET PartId = NULL WHERE PlanningId = @0 AND PartId = @1", planningId, partId);
            InsertSystem(db, planningId, "part-removed", $"Delen {p.Name} är borttagen. Dess uppgifter och meddelanden gäller nu hela planeringen.",
                byParticipantId, null, null, now);
            Bump(db, planningId);
            return (null, tasks, msgs);
        }

        /// <summary>Märker ett meddelande med en del (eller tar bort märkningen). Gäller hela tråden.</summary>
        public string? SetMessagePart(int planningId, int messageId, int? partId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (PartError(db, planningId, partId) is { } pe) return pe;
            var m = db.SingleOrDefault<PlanningMessageRow>("SELECT * FROM PlanningMessage WHERE Id = @0 AND PlanningId = @1 AND Kind = N'Message'", messageId, planningId);
            if (m == null) return "Meddelandet finns inte.";
            var threadId = m.ReplyToMessageId > 0 ? m.ReplyToMessageId!.Value : m.Id;
            db.Execute("UPDATE PlanningMessage SET PartId = @2 WHERE PlanningId = @0 AND (Id = @1 OR ReplyToMessageId = @1)",
                planningId, threadId, partId > 0 ? partId : null);
            Bump(db, planningId);
            return null;
        }
    }
}
