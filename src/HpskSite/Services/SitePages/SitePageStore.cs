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
        /// <summary>Nödbromsen: satt av sajtadmin. Sidan visas inte och kan inte publiceras förrän den hävs.</summary>
        public DateTime? SuspendedDate { get; set; }
        /// <summary>Den egna sida den här sidan ligger under (null = egen nivå). Två nivåer: se <see cref="SitePageTree"/>.</summary>
        public int? ParentPageId { get; set; }
        /// <summary>Ordningen bland syskon (samma förälder). Lägre först.</summary>
        public int SortOrder { get; set; }

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

    /// <summary>Listrad: sidans rad plus dess aktuella adress (sluggen).</summary>
    public sealed class SitePageListItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string? PublishedTitle { get; set; }
        public byte Visibility { get; set; }
        public int? PublishedVersionId { get; set; }
        public int? DraftVersionId { get; set; }
        public int Revision { get; set; }
        public DateTime UpdatedDate { get; set; }
        public DateTime? DeletedDate { get; set; }
        public DateTime? SuspendedDate { get; set; }
        public string? Slug { get; set; }
        public bool InMenu { get; set; }
        public int? ParentPageId { get; set; }
        public int SortOrder { get; set; }
        /// <summary>Antal undersidor (ej raderade). Bara <see cref="SitePageStore.ListFree"/> fyller det.</summary>
        public int ChildCount { get; set; }
        /// <summary>Schemat i den publicerade respektive utkastversionen (1 = vanlig, 2 = omgjord sida); null = ingen sådan version. Bara <see cref="SitePageStore.ListTemplateCopies"/> fyller dem.</summary>
        public int? PublishedSchema { get; set; }
        public int? DraftSchema { get; set; }

        /// <summary>Det användaren ser: "Publicerad", "Publicerad, med osparade ändringar" eller "Utkast".</summary>
        public string Status => SuspendedDate != null ? "Stoppad av pistol.nu" : PublishedVersionId == null ? "Utkast" : DraftVersionId != null ? "Publicerad, med osparade ändringar" : "Publicerad";
    }

    /// <summary>En egen sida i ägarens meny (bara publicerade, ej raderade sidor med en aktuell adress).</summary>
    public sealed class SiteMenuPageRow
    {
        public int PageId { get; set; }
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public byte Visibility { get; set; }
        public int SortOrder { get; set; }
        public int? ParentPageId { get; set; }
        /// <summary>Mallnyckeln när föräldern är en omgjord systemsida (Om klubben), annars null: undersidan ritas då under systemposten i menyn.</summary>
        public string? ParentTemplateKey { get; set; }
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
            "Revision, UpdatedByMemberId, LastVisibleLevel, CreatedDate, UpdatedDate, DeletedDate, SuspendedDate, ParentPageId, SortOrder";

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
            string? templateKey, int? templateVersion, int? byMemberId, bool asSupport, DateTime now, int? parentPageId = null)
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

            // En ny egen sida läggs sist bland sina syskon (egen nivå eller under samma förälder); en mallkopia har ingen ordning.
            var sort = templateKey == null ? NextSortOrder(db, ownerType, ownerId, parentPageId) : 0;
            var pageId = db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SitePage (OwnerType, OwnerId, TemplateKey, TemplateVersion, Title, Visibility, Revision, UpdatedByMemberId, CreatedDate, UpdatedDate, ParentPageId, SortOrder)
                  VALUES (@0, @1, @2, @3, @4, @5, 1, @6, @7, @7, @8, @9);
                  SELECT CAST(SCOPE_IDENTITY() AS int)",
                ownerType, ownerId, templateKey, templateVersion, title.Trim(), (byte)visibility, byMemberId, now, parentPageId, sort);

            var content = EmptyContent(templateKey != null);
            var versionId = InsertVersion(db, pageId, ownerType, ownerId, 1, title.Trim(), content, isBaseline: true, isPublish: false, byMemberId, asSupport, now);
            db.Execute("UPDATE dbo.SitePage SET DraftVersionId = @1 WHERE Id = @0", pageId, versionId);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, 1);
        }

        private static int InsertVersion(IDatabase db, int pageId, string ownerType, int ownerId, int revision, string title, string blocksJson,
            bool isBaseline, bool isPublish, int? byMemberId, bool asSupport, DateTime now) =>
            db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SitePageVersion (PageId, OwnerType, OwnerId, Revision, Title, BlocksJson, SchemaVersion, ContentHash, IsBaseline, IsPublish, SavedByMemberId, SavedAsSupport, SavedDate)
                  VALUES (@0, @1, @2, @3, @4, @5, @12, @6, @7, @8, @9, @10, @11);
                  SELECT CAST(SCOPE_IDENTITY() AS int)",
                pageId, ownerType, ownerId, revision, title, blocksJson, Hash(title, blocksJson), isBaseline, isPublish, byMemberId, asSupport, now,
                SiteBlockSchema.SchemaNumberOf(blocksJson));   // kolumnen följer innehållet (schema 2 = omvandlad sida)

        public static SitePageRow? GetPage(IDatabase db, int pageId, string ownerType, int ownerId, bool includeDeleted = false) =>
            db.FirstOrDefault<SitePageRow>(
                $"SELECT {PageColumns} FROM dbo.SitePage WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2" + (includeDeleted ? "" : " AND DeletedDate IS NULL"),
                pageId, ownerType, ownerId);

        /// <summary>
        /// Är sidan omvandlad just nu: pekar utkastet eller det publicerade på en version i schema 2? Äldre versioner i schema 2 räknas inte (efter
        /// Lägg tillbaka är sidan en vanlig systemsida igen). Vanlig sparning får bara skriva schema 2 på en sida som redan är det, och aldrig schema 1 på
        /// en som är det; själva omvandlingen och återgången går bara genom sina egna grindar (<see cref="SitePageWriter.ConvertTemplatePage"/>).
        /// </summary>
        public static bool IsConverted(IDatabase db, int pageId) =>
            db.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM dbo.SitePage p JOIN dbo.SitePageVersion v ON v.PageId = p.Id AND v.Id IN (p.PublishedVersionId, p.DraftVersionId)
                  WHERE p.Id = @0 AND v.SchemaVersion = @1", pageId, SiteBlockSchema.ConvertedSchemaVersion) > 0;

        /// <summary>Den senaste versionen i schema 1 (inte baslinjen): det sidan hade innan den gjordes om, eller det som publicerades före. Null = ingen.</summary>
        public static SitePageVersionRow? LatestSchema1Version(IDatabase db, int pageId) =>
            db.FirstOrDefault<SitePageVersionRow>(
                $"SELECT TOP 1 {VersionColumns} FROM dbo.SitePageVersion WHERE PageId = @0 AND SchemaVersion = 1 AND IsBaseline = 0 ORDER BY Id DESC", pageId);

        /// <summary>
        /// Publicerar angivet innehåll direkt (en ny publiceringsversion; utkastet kastas). Används av Lägg tillbaka, som publicerar sidans senaste schema-1-version
        /// utan att gå via ett utkast. Revisionen höjs med CAS.
        /// </summary>
        public static SiteStoreResult PublishContent(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, string title, string blocksJson,
            int? byMemberId, bool asSupport, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, UpdatedDate = @3, UpdatedByMemberId = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, now, byMemberId, expectedRevision);
            if (rows == 0) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var revision = expectedRevision + 1;
            var publishedId = InsertVersion(db, pageId, ownerType, ownerId, revision, title, blocksJson, isBaseline: false, isPublish: true, byMemberId, asSupport, now);
            db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1, PublishedTitle = @2, DraftVersionId = NULL WHERE Id = @0", pageId, publishedId, title);
            if (page.DraftVersionId is int draftId) db.Execute("DELETE FROM dbo.SitePageVersion WHERE Id = @0 AND PageId = @1 AND IsBaseline = 0", draftId, pageId);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, revision);
        }

        /// <summary>
        /// Kastar utkastet på en sida som aldrig publicerats: utkastraden tas bort och sidan pekar på sin baslinje (det tomma första utkastet) igen.
        /// Revisionen höjs med CAS. (På en publicerad sida används <see cref="DiscardDraft"/>.)
        /// </summary>
        public static SiteStoreResult ResetToBaseline(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, int? byMemberId, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if (page.Revision != expectedRevision) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            var baseline = db.ExecuteScalar<int?>("SELECT TOP 1 Id FROM dbo.SitePageVersion WHERE PageId = @0 AND IsBaseline = 1 ORDER BY Id", pageId);
            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, UpdatedDate = @3, UpdatedByMemberId = @4, DraftVersionId = @6 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, now, byMemberId, expectedRevision, baseline);
            if (rows == 0) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            if (page.DraftVersionId is int draftId && draftId != baseline)
                db.Execute("DELETE FROM dbo.SitePageVersion WHERE Id = @0 AND PageId = @1 AND IsBaseline = 0", draftId, pageId);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, expectedRevision + 1);
        }

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

        /// <summary>En rad i versionshistoriken (aldrig innehållet).</summary>
        public sealed class VersionListItem
        {
            public int Id { get; set; }
            public int Revision { get; set; }
            public string Title { get; set; } = "";
            public bool IsPublish { get; set; }
            public int? SavedByMemberId { get; set; }
            public bool SavedAsSupport { get; set; }
            public DateTime SavedDate { get; set; }
            /// <summary>Det som visas för användaren: "Publicerad" eller "Återställningspunkt".</summary>
            public string Kind => IsPublish ? "Publicerad" : "Återställningspunkt";
        }

        /// <summary>
        /// Sidans tidigare versioner: publiceringar och återställningspunkter, senaste först, högst <paramref name="take"/>. Baslinjen (det tomma
        /// startutkastet) och det pågående utkastet ingår inte. Bara versioner av just den här sidan hos den här ägaren.
        /// </summary>
        public static List<VersionListItem> ListVersions(IDatabase db, int pageId, string ownerType, int ownerId, int take = 10) =>
            db.Fetch<VersionListItem>(
                @"SELECT TOP (@3) v.Id, v.Revision, v.Title, v.IsPublish, v.SavedByMemberId, v.SavedAsSupport, v.SavedDate
                  FROM dbo.SitePageVersion v JOIN dbo.SitePage p ON p.Id = v.PageId AND p.OwnerType = v.OwnerType AND p.OwnerId = v.OwnerId
                  WHERE v.PageId = @0 AND v.OwnerType = @1 AND v.OwnerId = @2 AND v.IsBaseline = 0
                    AND (v.IsPublish = 1 OR v.Id <> ISNULL(p.DraftVersionId, 0))
                  ORDER BY v.Id DESC",
                pageId, ownerType, ownerId, Math.Clamp(take, 1, 50));

        /// <summary>
        /// "Spara en återställningspunkt": en ny versionsrad med det pågående utkastets (annars det publicerades) innehåll, utan att röra utkast,
        /// publicering eller revision. Samma hash som senaste punkten = ingen ny rad.
        /// </summary>
        public static SiteStoreResult SaveCheckpoint(IDatabase db, int pageId, string ownerType, int ownerId, int? byMemberId, bool asSupport, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            var src = GetDraft(db, pageId, ownerType, ownerId) ?? GetPublished(db, pageId, ownerType, ownerId);
            if (src == null) return new SiteStoreResult(SiteStoreOutcome.NothingToPublish, pageId, page.Revision);
            var last = db.FirstOrDefault<byte[]>(
                "SELECT TOP 1 ContentHash FROM dbo.SitePageVersion WHERE PageId = @0 AND IsBaseline = 0 AND Id <> ISNULL((SELECT DraftVersionId FROM dbo.SitePage WHERE Id = @0), 0) ORDER BY Id DESC", pageId);
            var hash = Hash(src.Title, src.BlocksJson);
            if (last != null && last.AsSpan().SequenceEqual(hash)) return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, page.Revision);   // samma som senaste: ingen ny rad
            InsertVersion(db, pageId, ownerType, ownerId, page.Revision, src.Title, src.BlocksJson, isBaseline: false, isPublish: false, byMemberId, asSupport, now);
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, page.Revision);
        }

        /// <summary>Ägarens FRIA sidor för redigeringslistan, med aktuell adress. Aldrig innehåll. Mallkopior (systemsidor) ingår inte.</summary>
        public static List<SitePageListItem> ListFree(IDatabase db, string ownerType, int ownerId, bool deleted = false) =>
            db.Fetch<SitePageListItem>(
                @"SELECT p.Id, p.Title, p.PublishedTitle, p.Visibility, p.PublishedVersionId, p.DraftVersionId, p.Revision, p.UpdatedDate, p.DeletedDate, p.SuspendedDate,
                         (SELECT TOP 1 a.Slug FROM dbo.PageAddress a WHERE a.TargetKind = 'sitepage' AND a.TargetId = p.Id AND a.IsCurrent = 1) AS Slug,
                         CASE WHEN EXISTS (SELECT 1 FROM dbo.SiteMenuItem m WHERE m.Kind = 1 AND m.PageId = p.Id AND m.IsRemoved = 0) THEN 1 ELSE 0 END AS InMenu,
                         p.ParentPageId, p.SortOrder,
                         (SELECT COUNT(*) FROM dbo.SitePage c WHERE c.ParentPageId = p.Id AND c.DeletedDate IS NULL) AS ChildCount
                  FROM dbo.SitePage p
                  WHERE p.OwnerType = @0 AND p.OwnerId = @1 AND p.TemplateKey IS NULL AND p.DeletedDate IS " + (deleted ? "NOT NULL" : "NULL") + @"
                  ORDER BY p.SortOrder, p.Title",
                ownerType, ownerId);

        /// <summary>Nästa ordningstal bland syskon: sist, i steg om 10 så att en flytt kan lägga en sida emellan utan att numrera om.</summary>
        public static int NextSortOrder(IDatabase db, string ownerType, int ownerId, int? parentPageId) =>
            (db.ExecuteScalar<int?>(
                @"SELECT MAX(SortOrder) FROM dbo.SitePage WHERE OwnerType = @0 AND OwnerId = @1 AND TemplateKey IS NULL AND DeletedDate IS NULL
                    AND ((@2 IS NULL AND ParentPageId IS NULL) OR ParentPageId = @2)", ownerType, ownerId, parentPageId) ?? 0) + 10;

        /// <summary>Antal undersidor (ej raderade). <paramref name="exceptPageId"/> räknas inte med (sidan som flyttas).</summary>
        public static int ChildCount(IDatabase db, int parentPageId, int exceptPageId = 0) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePage WHERE ParentPageId = @0 AND DeletedDate IS NULL AND Id <> @1", parentPageId, exceptPageId);

        /// <summary>Syskonen (samma förälder, ägare och sort) i ordning, för att byta plats. Bara egna, ej raderade sidor.</summary>
        public static List<(int Id, int SortOrder)> Siblings(IDatabase db, string ownerType, int ownerId, int? parentPageId) =>
            db.Fetch<SiblingRow>(
                @"SELECT Id, SortOrder FROM dbo.SitePage WHERE OwnerType = @0 AND OwnerId = @1 AND TemplateKey IS NULL AND DeletedDate IS NULL
                    AND ((@2 IS NULL AND ParentPageId IS NULL) OR ParentPageId = @2) ORDER BY SortOrder, Title, Id", ownerType, ownerId, parentPageId)
              .Select(r => (r.Id, r.SortOrder)).ToList();

        private sealed class SiblingRow { public int Id { get; set; } public int SortOrder { get; set; } }

        /// <summary>Skriver ordningen: sidorna i listan får 10, 20, 30 … (bara de som ändras). Ägarprövat i frågan.</summary>
        public static void SetOrder(IDatabase db, string ownerType, int ownerId, IReadOnlyList<int> orderedIds, DateTime now)
        {
            for (var i = 0; i < orderedIds.Count; i++)
                db.Execute("UPDATE dbo.SitePage SET SortOrder = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND TemplateKey IS NULL AND SortOrder <> @3",
                    orderedIds[i], ownerType, ownerId, (i + 1) * 10);
        }

        /// <summary>Lägger en sida under en förälder (null = egen nivå) sist bland sina nya syskon. Revisionen höjs så att en öppen redigerare ser att något hänt. Prövningen gör anroparen (<see cref="SitePageTree"/>).</summary>
        public static void SetParent(IDatabase db, int pageId, string ownerType, int ownerId, int? parentPageId, int? byMemberId, DateTime now)
        {
            var sort = NextSortOrder(db, ownerType, ownerId, parentPageId);
            db.Execute("UPDATE dbo.SitePage SET ParentPageId = @3, SortOrder = @4, Revision = Revision + 1, UpdatedDate = @5, UpdatedByMemberId = @6 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND TemplateKey IS NULL",
                pageId, ownerType, ownerId, parentPageId, sort, now, byMemberId);
        }

        /// <summary>Gör varje undersida till minst nivån <paramref name="level"/> (en undersida är aldrig öppnare än sin förälder). Returnerar antal ändrade. Revisionen höjs.</summary>
        public static int RaiseChildren(IDatabase db, string ownerType, int ownerId, int parentPageId, SiteVisibility level, int? byMemberId, DateTime now) =>
            db.Execute("UPDATE dbo.SitePage SET Visibility = @3, Revision = Revision + 1, UpdatedDate = @4, UpdatedByMemberId = @5 WHERE OwnerType = @0 AND OwnerId = @1 AND ParentPageId = @2 AND DeletedDate IS NULL AND Visibility < @3",
                ownerType, ownerId, parentPageId, (byte)level, now, byMemberId);

        private sealed class OwnerRef { public string OwnerType { get; set; } = ""; public int OwnerId { get; set; } }

        /// <summary>Vem en egen (fri) sida hör till, bara på id: för en publik inskickning som bara känner sidans id. Null om sidan inte finns, är en mallkopia eller är raderad.</summary>
        public static (string OwnerType, int OwnerId)? OwnerOfFreePage(IDatabase db, int pageId)
        {
            var r = db.FirstOrDefault<OwnerRef>("SELECT OwnerType, OwnerId FROM dbo.SitePage WHERE Id = @0 AND TemplateKey IS NULL AND DeletedDate IS NULL", pageId);
            return r == null ? null : (r.OwnerType, r.OwnerId);
        }

        private sealed class OwnerKeyRef { public string OwnerType { get; set; } = ""; public int OwnerId { get; set; } public string? TemplateKey { get; set; } }

        /// <summary>
        /// Vem en sida hör till, bara på id, för en publik inskickning: en egen sida eller en omgjord systemsida (<c>TemplateKey</c> sätts då). Null om sidan inte finns eller är raderad.
        /// En mallkopia i den gamla formen (platser före/efter) har inga formulär: den hittar inget Anmälan-block och avvisas av anroparen.
        /// </summary>
        public static (string OwnerType, int OwnerId, string? TemplateKey)? OwnerOfPage(IDatabase db, int pageId)
        {
            var r = db.FirstOrDefault<OwnerKeyRef>("SELECT OwnerType, OwnerId, TemplateKey FROM dbo.SitePage WHERE Id = @0 AND DeletedDate IS NULL", pageId);
            return r == null ? null : (r.OwnerType, r.OwnerId, r.TemplateKey);
        }

        public static bool IsInMenu(IDatabase db, string ownerType, int ownerId, int pageId) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 1 AND PageId = @2 AND IsRemoved = 0", ownerType, ownerId, pageId) > 0;

        /// <summary>Tar undersidorna ur menyn (när föräldern tas ur den): en undersida i menyn kräver en förälder i menyn. Returnerar antal.</summary>
        public static int RemoveChildrenFromMenu(IDatabase db, string ownerType, int ownerId, int parentPageId) =>
            db.Execute(@"DELETE m FROM dbo.SiteMenuItem m JOIN dbo.SitePage c ON c.Id = m.PageId
                         WHERE m.OwnerType = @0 AND m.OwnerId = @1 AND m.Kind = 1 AND c.ParentPageId = @2", ownerType, ownerId, parentPageId);

        /// <summary>
        /// Ägarens egna sidor som ligger i menyn, i menyns ordning. Bara PUBLICERADE sidor utan raderingsdatum med en aktuell adress: en sida som
        /// inte går att öppna ska inte synas som en länk. Etiketten är sidans publicerade titel (följer med ett namnbyte, ingen gammal kopia).
        /// </summary>
        public static List<SiteMenuPageRow> MenuPages(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteMenuPageRow>(
                @"SELECT p.Id AS PageId, COALESCE(p.PublishedTitle, p.Title) AS Title, a.Slug AS Slug, p.Visibility AS Visibility, p.SortOrder AS SortOrder, p.ParentPageId AS ParentPageId, pp.TemplateKey AS ParentTemplateKey
                  FROM dbo.SiteMenuItem m
                  JOIN dbo.SitePage p ON p.Id = m.PageId AND p.OwnerType = m.OwnerType AND p.OwnerId = m.OwnerId
                  LEFT JOIN dbo.SitePage pp ON pp.Id = p.ParentPageId AND pp.OwnerType = p.OwnerType AND pp.OwnerId = p.OwnerId
                  JOIN dbo.PageAddress a ON a.TargetKind = 'sitepage' AND a.TargetId = p.Id AND a.IsCurrent = 1
                  WHERE m.OwnerType = @0 AND m.OwnerId = @1 AND m.Kind = 1 AND m.IsRemoved = 0 AND p.DeletedDate IS NULL AND p.SuspendedDate IS NULL AND p.PublishedVersionId IS NOT NULL
                  ORDER BY p.SortOrder, p.Id",
                ownerType, ownerId);

        /// <summary>
        /// Lägger en egen sida i menyn eller tar bort den därifrån. Sidan måste höra till ägaren (id-lagen). Läggs sist; en sida som redan ligger
        /// där rörs inte. Returnerar false om sidan inte finns hos ägaren. Menyn är bara en lista: sidans synlighet gäller alltid (Strictest).
        /// </summary>
        public static bool SetInMenu(IDatabase db, int pageId, string ownerType, int ownerId, bool inMenu, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null || !page.IsFree) return false;
            if (!inMenu)
            {
                db.Execute("DELETE FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 1 AND PageId = @2", ownerType, ownerId, pageId);
                return true;
            }
            if (!LockOwner(db, ownerType, ownerId)) return false;
            var exists = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 1 AND PageId = @2", ownerType, ownerId, pageId);
            if (exists > 0) { db.Execute("UPDATE dbo.SiteMenuItem SET IsRemoved = 0, UpdatedDate = @3 WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 1 AND PageId = @2", ownerType, ownerId, pageId, now); return true; }
            var sort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId) ?? 0;
            var label = page.Title.Length > 80 ? page.Title[..80] : page.Title;
            db.Execute(
                @"INSERT INTO dbo.SiteMenuItem (OwnerType, OwnerId, Kind, PageId, Label, SortOrder, Visibility, CreatedDate, UpdatedDate)
                  VALUES (@0, @1, 1, @2, @3, @4, @5, @6, @6)", ownerType, ownerId, pageId, label, sort + 10, page.Visibility, now);
            return true;
        }

        /// <summary>Ägarens mallkopia (slots) för en systemsida, eller null. En kopia per systemsida och ägare (unikt index, raderade räknas också).</summary>
        public static SitePageRow? GetTemplateCopy(IDatabase db, string ownerType, int ownerId, string templateKey) =>
            db.FirstOrDefault<SitePageRow>(
                $"SELECT {PageColumns} FROM dbo.SitePage WHERE OwnerType = @0 AND OwnerId = @1 AND TemplateKey = @2 AND DeletedDate IS NULL", ownerType, ownerId, templateKey);

        /// <summary>Alla ägarens mallkopior (för Hemsidans lista över systemsidor).</summary>
        public static List<SitePageListItem> ListTemplateCopies(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SitePageListItem>(
                @"SELECT p.Id, p.Title, p.PublishedTitle, p.Visibility, p.PublishedVersionId, p.DraftVersionId, p.Revision, p.UpdatedDate, p.DeletedDate, p.TemplateKey AS Slug, 0 AS InMenu,
                         pv.SchemaVersion AS PublishedSchema, dv.SchemaVersion AS DraftSchema
                  FROM dbo.SitePage p
                  LEFT JOIN dbo.SitePageVersion pv ON pv.Id = p.PublishedVersionId
                  LEFT JOIN dbo.SitePageVersion dv ON dv.Id = p.DraftVersionId
                  WHERE p.OwnerType = @0 AND p.OwnerId = @1 AND p.TemplateKey IS NOT NULL AND p.DeletedDate IS NULL ORDER BY p.Id", ownerType, ownerId);

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

        /// <summary>
        /// Nödbromsen: sätter eller hävar <c>SuspendedDate</c>. Höjer revisionen (en öppen redigerare får konflikt och ser att något hänt).
        /// Oförändrat läge ger Ok utan skrivning. Sidan måste höra till ägaren och får inte vara raderad.
        /// </summary>
        public static SiteStoreResult SetSuspended(IDatabase db, int pageId, string ownerType, int ownerId, bool suspended, int? byMemberId, DateTime now)
        {
            var page = GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteStoreResult(SiteStoreOutcome.NotFound);
            if ((page.SuspendedDate != null) == suspended) return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, page.Revision);
            var rows = db.Execute(
                "UPDATE dbo.SitePage SET Revision = Revision + 1, SuspendedDate = @3, UpdatedDate = @4, UpdatedByMemberId = @5 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL",
                pageId, ownerType, ownerId, suspended ? now : (DateTime?)null, now, byMemberId);
            return rows == 0 ? new SiteStoreResult(SiteStoreOutcome.NotFound) : new SiteStoreResult(SiteStoreOutcome.Ok, pageId, page.Revision + 1);
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
            if (rows == 0) return new SiteStoreResult(SiteStoreOutcome.Conflict, pageId, page.Revision);
            // En undersida vars förälder är raderad kommer tillbaka som egen sida (sist bland sidorna på egen nivå), aldrig under en raderad sida.
            if (page.ParentPageId != null)
                db.Execute(@"UPDATE c SET ParentPageId = NULL, SortOrder = @1 FROM dbo.SitePage c JOIN dbo.SitePage p ON p.Id = c.ParentPageId
                             WHERE c.Id = @0 AND p.DeletedDate IS NOT NULL", pageId, NextSortOrder(db, ownerType, ownerId, null));
            return new SiteStoreResult(SiteStoreOutcome.Ok, pageId, expectedRevision + 1);
        }
    }
}
