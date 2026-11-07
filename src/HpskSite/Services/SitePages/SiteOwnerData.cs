using HpskSite.Services.Addresses;
using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Allt sidstrukturen lagrar PER ÄGARE (klubbsidorna 2a). Används när en klubb eller krets raderas för gott
    /// (<see cref="SiteOwnerCleanupHandler"/>): ett återanvänt nod-id får aldrig ärva en gammal brytare, en gammal sida eller en adress.
    ///
    /// <para>Tas bort, i EN transaktion och i den här ordningen: menyrader (pekar på sidor), sidornas adresser (aldrig efter sidorna:
    /// en 301 får aldrig peka på en borttagen sida, v3 H3), sidorna (versioner och referenser följer med CASCADE), stödlägen och
    /// brytarraden. <b>Kvar:</b> <c>SiteAudit</c> (bevaras 24 månader, bär bara hash och aldrig text; gallras av sitt eget jobb).</para>
    /// </summary>
    public static class SiteOwnerData
    {
        /// <summary>Raderar ägarens alla sidstrukturrader. Returnerar antalet borttagna <c>SiteOwner</c>-rader (0 eller 1).</summary>
        public static int DeleteAll(IDatabase db, string ownerType, int ownerId)
        {
            db.Execute("DELETE FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            db.Execute(
                "DELETE a FROM dbo.PageAddress a JOIN dbo.SitePage p ON p.Id = a.TargetId WHERE a.TargetKind = @2 AND p.OwnerType = @0 AND p.OwnerId = @1",
                ownerType, ownerId, PageAddressKinds.SitePage);
            // Pekarna nollas först så att inget beror på ordningen mellan versioner och sida (CASCADE tar versionerna och referenserna).
            db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = NULL, DraftVersionId = NULL WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            db.Execute("DELETE FROM dbo.SitePage WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            db.Execute("DELETE FROM dbo.SiteSupportGrant WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            return db.Execute("DELETE FROM dbo.SiteOwner WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
        }
    }
}
