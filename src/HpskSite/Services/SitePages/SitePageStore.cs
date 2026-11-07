using System.Security.Cryptography;
using System.Text;
using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>En rad ur <c>dbo.SitePage</c> (listfrågor läser aldrig innehållet).</summary>
    public sealed class SitePageRow
    {
        public int Id { get; set; }
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public string? TemplateKey { get; set; }
        public int? TemplateVersion { get; set; }
        public string Title { get; set; } = "";
        public string? PublishedTitle { get; set; }
        public byte Visibility { get; set; }
        public int? PublishedVersionId { get; set; }
        public int? DraftVersionId { get; set; }
        public int Revision { get; set; }
        public int? UpdatedByMemberId { get; set; }
        public byte? LastVisibleLevel { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }
        public DateTime? DeletedDate { get; set; }

        public SiteVisibility VisibilityLevel => (SiteVisibility)Visibility;
        public bool IsFree => TemplateKey == null;
        public bool HasUnpublishedChanges => DraftVersionId != null;
    }

    /// <summary>En rad ur <c>dbo.SitePageVersion</c>.</summary>
    public sealed class SitePageVersionRow
    {
        public int Id { get; set; }
        public int PageId { get; set; }
        public int Revision { get; set; }
        public string Title { get; set; } = "";
        public string BlocksJson { get; set; } = "";
        public int SchemaVersion { get; set; }
        public byte[] ContentHash { get; set; } = Array.Empty<byte>();
        public bool IsBaseline { get; set; }
        public bool IsPublish { get; set; }
        public int? SavedByMemberId { get; set; }
        public bool SavedAsSupport { get; set; }
        public DateTime SavedDate { get; set; }
    }

    public enum SiteStoreOutcome
    {
        Ok,
        /// <summary>Någon annan hann före (revisionen stämde inte) — användarens text finns kvar, inget skrevs.</summary>
        Conflict,
        /// <summary>Sidan finns inte hos den ägaren (eller är raderad).</summary>
        NotFound,
        /// <summary>Taket för antal sidor per ägare är nått.</summary>
        TooManyPages,
        /// <summary>Det finns inget utkast att publicera.</summary>
        NothingToPublish,
    }

    public readonly record struct SiteStoreResult(SiteStoreOutcome Outcome, int PageId = 0, int Revision = 0);

    /// <summary>
    /// SQL för klubbens och kretsens egna sidor (klubbsidorna 2a). Allt tar <c>NPoco.IDatabase</c> och körs i ANROPARENS
    /// transaktion (en sparning = sida, version, adress och referenser i en transaktion, plan v2 §2); provas mot en riktig
    /// SQL Server i <c>SitePageStoreTests</c>.
    ///
    /// <para><b>Ägarprövning:</b> varje fråga bär (id, ownerType, ownerId) — ett sid-id utifrån prövas alltid mot radens egen ägare
    /// (id-lagen), aldrig mot ett ägar-id i anropet som saknar koppling till sidan.</para>
    ///
    /// <para><b>Revision (CAS):</b> <c>UPDATE … WHERE Id=@id AND Revision=@expected</c>; 0 rader = <see cref="SiteStoreOutcome.Conflict"/>.
    /// Revisionen höjs vid varje autospar och varje publicering. <b>Per-ägare-lås:</b> <c>UPDLOCK</c> på ägarens <c>SiteOwner</c>-rad
    /// serialiserar skapande (antal sidor) — skrivning av en enskild sida låses av själva CAS-raden.</para>
    ///
    /// <para><b>Utkast och publicerat:</b> utkastet är EN versionsrad som skrivs på plats (autospar); en ny versionsrad
    /// skapas bara vid Publicera. Efter Publicera finns inget utkast (<c>DraftVersionId</c> NULL) — sidan är "Publicerad".
    /// Innehållet som kommer hit är redan validerat och normaliserat av <see cref="SiteBlockSchema"/>.</para>
    /// </summary>
    public static class SitePageStore
    {
        public const int MaxFreePagesPerOwner = 50;

        /// <summary>
        /// Bara för proven: körs i <see cref="SaveDraft"/> mellan förkontrollen (läst revision) och själva UPDATE, så att ett prov kan
        /// låta en annan transaktion hinna emellan och därmed bevisa att <c>WHERE Revision = …</c> i UPDATE (CAS) gör jobbet, inte förkontrollen.
        /// Aldrig satt i drift. <c>[ThreadStatic]</c>: ett parallellt prov påverkar inte ett annat.
        /// </summary>
        [ThreadStatic] internal static Action? TestHookBeforeCas;

        private const string PageColumns =
            "Id, OwnerType, OwnerId, TemplateKey, TemplateVersion, Title, PublishedTitle, Visibility, PublishedVersionId, DraftVersionId, " +
            "Revision, UpdatedByMemberId, LastVisibleLevel, CreatedDate, UpdatedDate, DeletedDate";

        private const string VersionColumns =
            "Id, PageId, Revision, Title, BlocksJson, SchemaVersion, ContentHash, IsBaseline, IsPublish, SavedByMemberId, SavedAsSupport, SavedDate";

        /// <summary>Tomt innehåll för en ny sida: fri sida utan block, eller mallkopia utan slots.</summary>
        public static string EmptyContent(bool templateCopy) =>
            templateCopy ? "{\"schema\":1,\"slots\":{\"before\":[],\"after\":[]}}" : "{\"schema\":1,\"blocks\":[]}";

        public static byte[] Hash(string title, string blocksJson) =>
            SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + blocksJson));

        /// <summary>Tar ägarens lås (<c>UPDLOCK, HOLDLOCK</c> på SiteOwner-raden). False = ägaren har ingen rad.</summary>
        public static bool LockOwner(IDatabase db, string ownerType, int ownerId) =>
            db.ExecuteScalar<int?>("SELECT 1 FROM dbo.SiteOwner WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId) == 1;

        /// <summary>
        /// Skapar en sida (fri, eller en mallkopia när <paramref name="templateKey"/> anges) med ett tomt baslinjeutkast.
        /// Synligheten måste vara en av 0–4. Adressen (sluggen) sätts av anroparen i samma transaktion.
        /// </summary>
        public static SiteStoreResult Create(IDatabase db, string ownerType, int ownerId, string title, SiteVisibility visibility,
            string? templateKey, int? templateVersion, int? byMemberId, bool asSupport, DateTime now)
        {
            if (!SiteAccess.IsSelectable(visibility) || visibility > SiteVisibility.Dold)
                throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Ogiltig synlighet");
            if (string.IsNullOrWhiteSpace(title) || title.Length > 120) throw new ArgumentException("Titeln måste vara 1–120 tecken.", nameof(title));

            if (!LockOwner(db, ownerType, ownerId)) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (templateKey == null)
            {
                var n = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerType = @0 AND OwnerId = @1 AND TemplateKey IS NULL AND DeletedDate IS NULL", ownerType, ownerId);
                if (n >= MaxFreePagesPerOwner) return new SiteStoreResult(SiteStoreOutcome.TooManyPages);
            }

            var pageId = db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SitePage (OwnerType, OwnerId, TemplateKey, TemplateVersion, Title, Visibility, Revision, UpdatedByMemberId, CreatedDate, UpdatedDate)
                  VALUES (@0, @1, @2, @3, @4, @5, 1, @6, @7, @7);
                  SELECT CAST(SCOPE_IDENTITY() AS int)",
                ownerType, ownerId, templateKey, templateVersion, title.Trim(), (byte)visibility, byMemberId, now);

            var content = EmptyContent(templateKey != null);
            var versionId = InsertVersion(db, pageId, ownerType, ownerId, 1, title.Trim(), content, isBaseline: true, isPublish: false, byMemberId, asSupport, now);
            db.Execute("UPDATE dbo.SitePage SET DraftVersionId = @1 WHERE Id = @0", pageId, versionId);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, 1);
        }

        private static int InsertVersion(IDatabase db, int pageId, string ownerType, int ownerId, int revision, string title, string blocksJson,
            bool isBaseline, bool isPublish, int? byMemberId, bool asSupport, DateTime now) =>
            db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SitePageVersion (PageId, OwnerType, OwnerId, Revision, Title, BlocksJson, SchemaVersion, ContentHash, IsBaseline, IsPublish, SavedByMemberId, SavedAsSupport, SavedDate)
                  VALUES (@0, @1, @2, @3, @4, @5, 1, @6, @7, @8, @9, @10, @11);
                  SELECT CAST(SCOPE_IDENTITY() AS int)",
                pageId, ownerType, ownerId, revision, title, blocksJson, Hash(title, blocksJson), isBaseline, isPublish, byMemberId, asSupport, now);

        public static SitePageRow? GetPage(IDatabase db, int pageId, string ownerType, int ownerId, bool includeDeleted = false) =>
            db.FirstOrDefault<SitePageRow>(
                $"SELECT {PageColumns} FROM dbo.SitePage WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2" + (includeDeleted ? "" : " AND DeletedDate IS NULL"),
                pageId, ownerType, ownerId);

        public static SitePageVersionRow? GetVersion(IDatabase db, int versionId, int pageId) =>
            db.FirstOrDefault<SitePageVersionRow>($"SELECT {VersionColumns} FROM dbo.SitePageVersion WHERE Id = @0 AND PageId = @1", versionId, pageId);

        /// <summary>Det publicerade innehållet, eller null (aldrig publicerad, raderad, eller en annan ägares sida).</summary>
        public static SitePageVersionRow? GetPublished(IDatabase db, int pageId, string ownerType, int ownerId) =>
            db.FirstOrDefault<SitePageVersionRow>(
                @"SELECT v.Id, v.PageId, v.Revision, v.Title, v.BlocksJson, v.SchemaVersion, v.ContentHash, v.IsBaseline, v.IsPublish, v.SavedByMemberId, v.SavedAsSupport, v.SavedDate
                  FROM dbo.SitePage p JOIN dbo.SitePageVersion v ON v.Id = p.PublishedVersionId AND v.PageId = p.Id
                  WHERE p.Id = @0 AND p.OwnerType = @1 AND p.OwnerId = @2 AND p.DeletedDate IS NULL",
                pageId, ownerType, ownerId);

        /// <summary>Utkastet (det som redigeras), eller null när sidan inte har osparade ändringar.</summary>
        public static SitePageVersionRow? GetDraft(IDatabase db, int pageId, string ownerType, int ownerId) =>
            db.FirstOrDefault<SitePageVersionRow>(
                @"SELECT v.Id, v.PageId, v.Revision, v.Title, v.BlocksJson, v.SchemaVersion, v.ContentHash, v.IsBaseline, v.IsPublish, v.SavedByMemberId, v.SavedAsSupport, v.SavedDate
                  FROM dbo.SitePage p JOIN dbo.SitePageVersion v ON v.Id = p.DraftVersionId AND v.PageId = p.Id
                  WHERE p.Id = @0 AND p.OwnerType = @1 AND p.OwnerId = @2 AND p.DeletedDate IS NULL",
                pageId, ownerType, ownerId);

        /// <summary>Ägarens sidor för listan: aldrig innehåll, bara raden.</summary>
        public static List<SitePageRow> List(IDatabase db, string ownerType, int ownerId, bool deleted = false) =>
            db.Fetch<SitePageRow>(
                $"SELECT {PageColumns} FROM dbo.SitePage WHERE OwnerType = @0 AND OwnerId = @1 AND DeletedDate IS " + (deleted ? "NOT NULL" : "NULL") + " ORDER BY Title",
                ownerType, ownerId);

        /// <summary>
        /// Autospar: skriver utkastet på plats. Oförändrat innehåll (samma hash och titel) räknas inte som en ändring: revisionen
        /// höjs ändå inte och inget skrivs. <paramref name="expectedRevision"/> är revisionen klienten läste.
        /// </summary>
        public static SiteStoreResult SaveDraft(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision,
            string title, string blocksJson, SiteVisibility? visibility, int? byMemberId, bool asSupport, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(title) || title.Length > 120) throw new ArgumentException("Titeln måste vara 1–120 tecken.", nameof(title));
            title = title.Trim();
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);

            var vis = visibility ?? page.VisibilityLevel;
            if (!SiteAccess.IsSelectable(vis) || vis > SiteVisibility.Dold) throw new ArgumentOutOfRangeException(nameof(visibility), vis, "Ogiltig synlighet");

            // Oförändrat (samma titel, synlighet och hash som det som redan är sparat som utkast): ingenting skrivs, revisionen står kvar.
            var current = page.DraftVersionId is int cur ? GetVersion(db, cur, pageId) : null;
            if (current != null && !current.IsBaseline && vis == page.VisibilityLevel && current.Title == title
                && current.ContentHash.AsSpan().SequenceEqual(Hash(title, blocksJson)))
                return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, page.Revision);

            TestHookBeforeCas?.Invoke();

            // CAS: bara om revisionen fortfarande stämmer. 0 rader = någon annan hann före.
            var rows = db.Execute(
                @"UPDATE dbo.SitePage SET Revision = Revision + 1, Title = @3, Visibility = @4, UpdatedDate = @5, UpdatedByMemberId = @6
                  WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @7 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, title, (byte)vis, now, byMemberId, expectedRevision);
            if (rows == 0) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var revision = expectedRevision + 1;

            var hash = Hash(title, blocksJson);
            var draft = page.DraftVersionId is int did ? GetVersion(db, did, pageId) : null;
            // Baslinjen (sidans första, tomma utkast) skrivs aldrig över: första sparningen blir en ny utkastrad.
            if (draft != null && !draft.IsBaseline)
            {
                db.Execute(
                    @"UPDATE dbo.SitePageVersion SET Revision = @2, Title = @3, BlocksJson = @4, ContentHash = @5, SavedByMemberId = @6, SavedAsSupport = @7, SavedDate = @8
                      WHERE Id = @0 AND PageId = @1",
                    draft.Id, pageId, revision, title, blocksJson, hash, byMemberId, asSupport, now);
            }
            else
            {
                var vid = InsertVersion(db, pageId, ownerType, ownerId, revision, title, blocksJson, isBaseline: false, isPublish: false, byMemberId, asSupport, now);
                db.Execute("UPDATE dbo.SitePage SET DraftVersionId = @1 WHERE Id = @0", pageId, vid);
            }
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, revision);
        }

        /// <summary>
        /// Publicera utkastet: en NY versionsrad (<c>IsPublish</c>) blir det publicerade, titeln kopieras till
        /// <c>PublishedTitle</c> och utkastet försvinner (sidan är "Publicerad", utan osparade ändringar). Revisionen höjs.
        /// </summary>
        public static SiteStoreResult Publish(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision,
            int? byMemberId, bool asSupport, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var draft = GetDraft(db, pageId, ownerType, ownerId);
            if (draft == null) return new SiteStoreResult(SiteStoreOutcome.NothingToPublish, pageId, page.Revision);

            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, UpdatedDate = @3, UpdatedByMemberId = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, now, byMemberId, expectedRevision);
            if (rows == 0) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var revision = expectedRevision + 1;

            var publishedId = InsertVersion(db, pageId, ownerType, ownerId, revision, page.Title, draft.BlocksJson, isBaseline: false, isPublish: true, byMemberId, asSupport, now);
            // Ordning: peka om FÖRE utkastet raderas (pekar-FK mot samma sidas version). Utkastraden tas bort efteråt.
            db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1, PublishedTitle = @2, DraftVersionId = NULL WHERE Id = @0", pageId, publishedId, page.Title);
            db.Execute("DELETE FROM dbo.SitePageVersion WHERE Id = @0 AND PageId = @1 AND IsBaseline = 0", draft.Id, pageId);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, revision);
        }

        /// <summary>Kasta ändringarna: utkastet tas bort (en baslinje behålls), sidan visar det publicerade igen.</summary>
        public static SiteStoreResult DiscardDraft(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, int? byMemberId, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            if (page.DraftVersionId is not int draftId || page.PublishedVersionId == null)
                return new SiteStoreResult(SiteStoreOutcome.NothingToPublish, pageId, page.Revision); // inget att kasta, eller aldrig publicerad

            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, UpdatedDate = @3, UpdatedByMemberId = @4, DraftVersionId = NULL WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, now, byMemberId, expectedRevision);
            if (rows == 0) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            db.Execute("DELETE FROM dbo.SitePageVersion WHERE Id = @0 AND PageId = @1 AND IsBaseline = 0", draftId, pageId);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, expectedRevision + 1);
        }

        /// <summary>
        /// Mjukradera (papperskorg). Sidans synlighet just före raderingen sparas i <c>LastVisibleLevel</c> (301-regeln, v3 H3).
        /// Raden och versionerna står kvar tills städjobbet tar dem.
        /// </summary>
        public static SiteStoreResult SoftDelete(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, int? byMemberId, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, DeletedDate = @3, UpdatedDate = @3, UpdatedByMemberId = @4, LastVisibleLevel = Visibility WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, now, byMemberId, expectedRevision);
            return rows == 0
                ? new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision)
                : new SiteStoreResult(SiteStoreOutcome.Ok, pageId, expectedRevision + 1);
        }

        /// <summary>Ur papperskorgen (inom 30 dagar). Synligheten är den sidan hade; <c>LastVisibleLevel</c> nollas.</summary>
        public static SiteStoreResult Restore(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, int? byMemberId, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId, includeDeleted: true);
            if (page == null || page.DeletedDate == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, DeletedDate = NULL, LastVisibleLevel = NULL, UpdatedDate = @3, UpdatedByMemberId = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NOT NULL",
                pageId, ownerType, ownerId, now, byMemberId, expectedRevision);
            return rows == 0
                ? new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision)
                : new SiteStoreResult(SiteStoreOutcome.Ok, pageId, expectedRevision + 1);
        }
    }
}
