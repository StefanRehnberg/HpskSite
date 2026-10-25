using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Omgång 2 steg 4 (2026-10-24): DELARNA ÄR BORTA — områdena (<see cref="PlanningAreaRow"/>) har tagit över. Ett område
    /// grupperar uppgifter OCH meddelanden och har ansvarig, värd och beskrivning (se <c>SaveArea</c> i Prep-filen).
    /// <c>PlanningPart</c> finns kvar bara som grunddelen (Root), som kopplingen till tävlingen pekar på.
    /// </summary>
    public partial class PlanningService
    {
        /// <summary>Märker ett meddelande med ett område (eller tar bort märkningen). Gäller hela tråden.</summary>
        public string? SetMessageArea(int planningId, int messageId, int? areaId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (AreaError(db, planningId, areaId) is { } ae) return ae;
            var m = db.SingleOrDefault<PlanningMessageRow>("SELECT * FROM PlanningMessage WHERE Id = @0 AND PlanningId = @1 AND Kind = N'Message'", messageId, planningId);
            if (m == null) return "Meddelandet finns inte.";
            var threadId = m.ReplyToMessageId > 0 ? m.ReplyToMessageId!.Value : m.Id;
            db.Execute("UPDATE PlanningMessage SET AreaId = @2 WHERE PlanningId = @0 AND (Id = @1 OR ReplyToMessageId = @1)",
                planningId, threadId, areaId > 0 ? areaId : null);
            Bump(db, planningId);
            return null;
        }

        /// <summary>
        /// Omgång 3 steg 10 (progressivt avslöjande): slår på eller av en del (<see cref="PlanningParts"/>). En del med
        /// innehåll visas ändå — det här styr bara om den syns innan något finns i den.
        /// </summary>
        public string? SetPart(int planningId, string part, bool on)
        {
            if (!PlanningParts.All.Contains(part, StringComparer.OrdinalIgnoreCase)) return "Okänd del.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var current = db.ExecuteScalar<string?>("SELECT Parts FROM Planning WITH (UPDLOCK) WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            var next = PlanningParts.With(current, part.ToLowerInvariant(), on);
            db.Execute("UPDATE Planning SET Parts = @1 WHERE Id = @0", planningId, next.Length == 0 ? null : next);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Har planeringen förslag och beslut? (Visas då oavsett <see cref="PlanningRow.Parts"/>.)</summary>
        public bool DecisionsHaveContent(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningDecision WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId) > 0;
        }

        /// <summary>Markerar att planeringens roller i bemanningen är valda (rutnätet visar då hela katalogen).</summary>
        public void MarkRolesChosen(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET RolesChosen = 1 WHERE Id = @0", planningId);
            Bump(s.Database, planningId);
        }
    }
}
