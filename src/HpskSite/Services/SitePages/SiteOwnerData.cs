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
        /// <summary>
        /// Villkorens version. Höjs en ändring av villkoren som kräver en ny accept. ⚠️ Texten är ett UTKAST tills juristen granskat
        /// (BBS-lagen, ansvar för klubbens innehåll, personuppgifter): ingen riktig klubb får skrivytan före dess.
        /// </summary>
        public const int CurrentTermsVersion = 1;

        /// <summary>Har ägarens egen admin accepterat den aktuella versionen av villkoren? Läses färskt ur SQL (ingen cache).</summary>
        public static bool TermsAccepted(IDatabase db, string ownerType, int ownerId) =>
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.SiteOwner WHERE OwnerType = @0 AND OwnerId = @1 AND TermsAcceptedDate IS NOT NULL AND TermsVersion >= @2",
                ownerType, ownerId, CurrentTermsVersion) == 1;

        /// <summary>Brytare 3 (får ägaren göra om vanliga sidor?) läst färskt ur SQL. Saknad rad = false. Bara en visning: skrivlagret prövar den på nytt under ägarens lås.</summary>
        public static bool ConvertEnabled(IDatabase db, string ownerType, int ownerId) =>
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.SiteOwner WHERE OwnerType = @0 AND OwnerId = @1 AND IsEnabled = 1 AND EditingEnabled = 1 AND ConvertEnabled = 1", ownerType, ownerId) == 1;

        /// <summary>Registrerar accepten (vem, när, vilken version). Returnerar false om ägaren saknar rad. En ny accept ersätter den gamla.</summary>
        public static bool AcceptTerms(IDatabase db, string ownerType, int ownerId, int memberId, DateTime now) =>
            db.Execute(
                "UPDATE dbo.SiteOwner SET TermsAcceptedDate = @2, TermsAcceptedByMemberId = @3, TermsVersion = @4, UpdatedDate = @2 WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId, now, memberId, CurrentTermsVersion) == 1;

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
            SiteFiles.DeleteAllForOwner(db, ownerType, ownerId);   // filerna i lagringen tas bort av SiteOwnerCleanupHandler
            db.Execute("DELETE FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);   // anmälningarna är personuppgifter: de följer med ägaren
            return db.Execute("DELETE FROM dbo.SiteOwner WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
        }
    }
}
