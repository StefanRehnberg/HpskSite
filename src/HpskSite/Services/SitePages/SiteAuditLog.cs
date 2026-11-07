using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>En rad i klubbens ändringslogg, klar att visa. Aldrig innehåll: loggen bär bara hash.</summary>
    public sealed class SiteAuditRow
    {
        public long Id { get; set; }
        public DateTime CreatedDate { get; set; }
        public int? ActorMemberId { get; set; }
        public bool ActedAsSupport { get; set; }
        public string ObjectKind { get; set; } = "";
        public int? ObjectId { get; set; }
        public string Action { get; set; } = "";
        /// <summary>Sidans titel just nu (en sida som tagits bort för gott har ingen).</summary>
        public string? PageTitle { get; set; }
    }

    /// <summary>
    /// Klubbens ändringslogg (klubbsidorna 2b): vem gjorde vad och när, för ägarens egen admin. Läser <c>dbo.SiteAudit</c> (append-only, bevaras 24 månader, bär bara hash och
    /// aldrig text). Sidans titel hämtas live, bara för en sida som hör till SAMMA ägare (ett objekt-id prövas mot ägaren, id-lagen).
    /// </summary>
    public static class SiteAuditLog
    {
        /// <summary>De senaste raderna för ägaren, senaste först. <paramref name="take"/> begränsas till 1–200.</summary>
        public static List<SiteAuditRow> List(IDatabase db, string ownerType, int ownerId, int take = 100) =>
            db.Fetch<SiteAuditRow>(
                @"SELECT TOP (@2) a.Id, a.CreatedDate, a.ActorMemberId, a.ActedAsSupport, a.ObjectKind, a.ObjectId, a.Action,
                         CASE WHEN a.ObjectKind IN ('page', 'menu') THEN p.Title END AS PageTitle
                  FROM dbo.SiteAudit a
                  LEFT JOIN dbo.SitePage p ON a.ObjectKind IN ('page', 'menu') AND p.Id = a.ObjectId AND p.OwnerType = a.OwnerType AND p.OwnerId = a.OwnerId
                  WHERE a.OwnerType = @0 AND a.OwnerId = @1
                  ORDER BY a.Id DESC",
                ownerType, ownerId, Math.Clamp(take, 1, 200));

        /// <summary>Vad en loggad åtgärd heter på vanlig svenska. En okänd åtgärd visas som den är, aldrig som ett fel.</summary>
        public static string Label(string action) => action switch
        {
            "page-create" => "skapade sidan",
            "page-publish" => "publicerade",
            "page-delete" => "raderade",
            "page-slug" => "bytte adress på",
            "page-visibility" => "ändrade vem som ser",
            "page-restore-version" => "använde en tidigare version av",
            "menu-add" => "lade i menyn:",
            "menu-remove" => "tog ur menyn:",
            "terms-accepted" => "godkände villkoren för klubbens sidor",
            "support-start" => "öppnade ett stödläge",
            "support-end" => "avslutade stödläget",
            "personal-number-override" => "publicerade trots varning om personnummer:",
            _ => action,
        };

        /// <summary>Hela raden som en mening utan namn: "publicerade <titel>". Titeln utelämnas när sidan inte finns längre.</summary>
        public static string Describe(SiteAuditRow r)
        {
            var label = Label(r.Action);
            return string.IsNullOrWhiteSpace(r.PageTitle) ? label : $"{label} \"{r.PageTitle}\"";
        }
    }
}
