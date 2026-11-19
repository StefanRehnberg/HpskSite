using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HpskSite.Extensions;
using NPoco;

namespace HpskSite.Services.SitePages
{
    public enum SitePostStatus : byte { Draft = 0, Published = 1 }

    /// <summary>En rad i <c>SitePost</c>: en nyhet som hör till en klubb eller krets (Nyheter, sorten "post"). Texten är sanerad HTML.</summary>
    public sealed class SitePostRow
    {
        public int Id { get; set; }
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Excerpt { get; set; }
        public string BodyHtml { get; set; } = "";
        public int? CoverFileId { get; set; }
        public string? CoverAlt { get; set; }
        public int? AuthorMemberId { get; set; }
        public string AuthorName { get; set; } = "";
        public byte Status { get; set; }
        public byte ReadVisibility { get; set; }
        public DateTime? PublishedDate { get; set; }
        public DateTime? FirstPublishedDate { get; set; }
        public int Revision { get; set; }
        public int? UpdatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }
        public DateTime? DeletedDate { get; set; }
        public DateTime? MailedDate { get; set; }
        public int? MailedCount { get; set; }

        public bool IsPublished => Status == (byte)SitePostStatus.Published;
        public SiteVisibility Visibility => (SiteVisibility)ReadVisibility;
    }

    /// <summary>
    /// Det skribenten fyller i. <see cref="BodyHtml"/> är rå HTML (saneras av lagret); <see cref="Excerpt"/> tom = klipps ur texten.
    /// <para><see cref="Gallery"/>: null = rör inte galleriet (och omslaget gäller som förut); en lista (även tom) ERSÄTTER galleriet, och omslaget blir då galleriets första bild
    /// (<see cref="SitePosts.EffectiveCover"/>) — EN skrivväg för omslaget. <see cref="ConsentConfirmed"/> = uppladdaren bekräftar att de som syns på bilder märkta "visar personer" har samtyckt.</para>
    /// </summary>
    public sealed record SitePostInput(string? Title, string? BodyHtml, string? Excerpt, int? CoverFileId, string? CoverAlt, SiteVisibility ReadVisibility, DateTime? PublishedDate = null,
        IReadOnlyList<SitePostGalleryItem>? Gallery = null, bool ConsentConfirmed = false);

    /// <summary>En bild i galleriet, i den ordning skribenten valt (första = den stora). Alt-texten läses upp för den som inte ser bilden; bildtexten visas.</summary>
    public sealed record SitePostGalleryItem(int FileId, string? Alt, string? Caption, bool ShowsPeople);

    /// <summary>En rad i <c>SitePostImage</c>.</summary>
    public sealed class SitePostImageRow
    {
        public int PostId { get; set; }
        public int FileId { get; set; }
        public int SortOrder { get; set; }
        public string? Caption { get; set; }
        public string? AltText { get; set; }
        public bool ShowsPeople { get; set; }
        public int? ConsentByMemberId { get; set; }
        public DateTime? ConsentDate { get; set; }
    }

    /// <summary>Vem som skriver: id (null för en flytt/systemet) och namnet som lagras vid första skrivningen.</summary>
    public sealed record SitePostAuthor(int? MemberId, string Name);

    public enum SitePostResult { Ok, NotFound, Conflict, Invalid }

    public sealed record SitePostWrite(SitePostResult Result, int Id = 0, int Revision = 0, string? Error = null);

    /// <summary>
    /// Vem som får skriva och ta bort nyheter (Stefan 2026-11-13): ägarens egen admin, sajtadmin och styrelsen skriver; bara ägarens egen admin och sajtadmin tar bort.
    /// Ren funktion: kretsadmin för en klubb är varken eget eller sajtadmin och skriver alltså inte (stödläget gäller Hemsidan, inte nyheterna).
    /// </summary>
    public static class SiteNewsRules
    {
        public static bool CanWrite(bool loggedIn, bool ownAdmin, bool siteAdmin, bool board) => loggedIn && (ownAdmin || siteAdmin || board);
        public static bool CanDelete(bool loggedIn, bool ownAdmin, bool siteAdmin) => loggedIn && (ownAdmin || siteAdmin);
    }

    /// <summary>Reglerna för en nyhet. Ren kod, ingen databas.</summary>
    public static class SitePostRules
    {
        public const int MaxTitle = 200;
        public const int MaxExcerpt = 400;
        public const int MaxSlug = 100;
        public const int MaxBodyChars = 400_000;
        public const int AutoExcerptChars = 200;
        public const string FallbackSlug = "nyhet";
        public const int MaxPostRefs = 60;
        /// <summary>Högst så många bilder i ett galleri (Stefan 2026-11-19). Räknas in i <see cref="MaxPostRefs"/> tillsammans med bilderna i texten.</summary>
        public const int MaxGallery = 12;
        public const int MaxImageText = 200;

        /// <summary>Adressdelen ur en rubrik: gemener, å/ä→a, ö→o, resten som inte är a–z/0–9 blir ett bindestreck. Aldrig tom.</summary>
        public static string Slugify(string? title)
        {
            var sb = new StringBuilder();
            var lastDash = true;
            foreach (var ch in (title ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                char? c = ch switch { 'ø' => 'o', 'æ' => 'a', 'ß' => 's', _ => ch };
                if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;   // combining ring/diaeresis efter å/ä/ö
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(c); lastDash = false; }
                else if (!lastDash) { sb.Append('-'); lastDash = true; }
            }
            var s = sb.ToString().Trim('-');
            if (s.Length > MaxSlug) s = s[..MaxSlug].Trim('-');
            return s.Length == 0 ? FallbackSlug : s;
        }

        /// <summary>Adressdelar som aldrig är en nyhets adress: skrivytan ligger på <c>/nyheter/skriv/</c>.</summary>
        public static readonly string[] ReservedSlugs = { "skriv" };

        public static bool IsReserved(string slug) => ReservedSlugs.Contains(slug, StringComparer.Ordinal);

        /// <summary>Slug med löpnummer när den är upptagen (<c>-2</c>, <c>-3</c> …), klippt så att helheten ryms.</summary>
        public static string WithSuffix(string slug, int n)
        {
            if (n <= 1) return slug;
            var suffix = "-" + n;
            var head = slug.Length + suffix.Length > MaxSlug ? slug[..(MaxSlug - suffix.Length)].TrimEnd('-') : slug;
            return head + suffix;
        }

        /// <summary>Utdraget ur en (redan sanerad) text: taggar bort, blanksteg ihop, klippt vid ett ordslut med …</summary>
        public static string AutoExcerpt(string? sanitizedHtml)
        {
            if (string.IsNullOrWhiteSpace(sanitizedHtml)) return "";
            var text = Regex.Replace(sanitizedHtml, @"</(p|h[2-4]|li|blockquote|tr)>|<br\s*/?>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]*>", "");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length <= AutoExcerptChars) return text;
            var cut = text[..AutoExcerptChars];
            var space = cut.LastIndexOf(' ');
            if (space > AutoExcerptChars / 2) cut = cut[..space];
            return cut.TrimEnd(' ', ',', '.', ';', ':', '-') + "…";
        }

        /// <summary>Rubrik, utdrag och nivå kontrollerade. Returnerar felet på svenska eller null. Ren funktion.</summary>
        public static string? Validate(SitePostInput i)
        {
            var title = (i.Title ?? "").Trim();
            if (title.Length == 0) return "Skriv en rubrik.";
            if (title.Length > MaxTitle) return $"Rubriken får vara högst {MaxTitle} tecken.";
            if (title.Any(char.IsControl) || title.Contains('<') || title.Contains('>')) return "Rubriken innehåller tecken som inte går att spara.";
            if (i.Excerpt != null && i.Excerpt.Trim().Length > MaxExcerpt) return $"Utdraget får vara högst {MaxExcerpt} tecken.";
            if (i.CoverAlt != null && i.CoverAlt.Length > 200) return "Bildtexten får vara högst 200 tecken.";
            if (!SiteAccess.IsSelectable(i.ReadVisibility)) return "Välj vem som får läsa nyheten.";
            if ((i.BodyHtml ?? "").Length > MaxBodyChars) return "Texten är för lång.";
            return ValidateGallery(i.Gallery);
        }

        /// <summary>Galleriet: högst <see cref="MaxGallery"/> bilder, ingen bild två gånger, texterna inom gränsen. Null = galleriet rörs inte. Ren funktion.</summary>
        public static string? ValidateGallery(IReadOnlyList<SitePostGalleryItem>? gallery)
        {
            if (gallery == null) return null;
            if (gallery.Count > MaxGallery) return $"Ett galleri får ha högst {MaxGallery} bilder.";
            if (gallery.Any(g => g.FileId <= 0)) return "En bild i galleriet är ogiltig.";
            if (gallery.Select(g => g.FileId).Distinct().Count() != gallery.Count) return "Samma bild ligger två gånger i galleriet.";
            if (gallery.Any(g => (g.Alt ?? "").Length > MaxImageText || (g.Caption ?? "").Length > MaxImageText)) return $"Bildtexter och beskrivningar får vara högst {MaxImageText} tecken.";
            return null;
        }

        /// <summary>
        /// Omslaget och dess beskrivning: galleriets första bild när galleriet skrivs (EN skrivväg — <c>CoverFileId</c> finns kvar bara för listkortet, blocket Nyheter och indexet),
        /// annars det som anropet bär. Ren funktion.
        /// </summary>
        public static (int? FileId, string? Alt) EffectiveCover(SitePostInput i) =>
            i.Gallery == null ? (i.CoverFileId, i.CoverAlt) : i.Gallery.Count == 0 ? (null, null) : (i.Gallery[0].FileId, i.Gallery[0].Alt);

        /// <summary>
        /// Visning av galleriet: den stora bilden, de (högst) två små och hur många som ligger bakom "+N" på den tredje. 1 bild = bara den stora, 2 = stor + en liten.
        /// Ren funktion så att layouten går att pröva utan vy.
        /// </summary>
        public static (int Big, int Small, int More) GalleryLayout(int count) =>
            count <= 0 ? (0, 0, 0) : (1, Math.Min(2, count - 1), Math.Max(0, count - 3));

        /// <summary>Nivåerna (0–4) som <paramref name="viewer"/> får läsa: EN källa, samma <see cref="SiteAccess.CanSee"/> som sidor och block.</summary>
        public static int[] ReadableLevels(SiteViewer viewer) =>
            new[] { SiteVisibility.Alla, SiteVisibility.Inloggade, SiteVisibility.Klubbmedlemmar, SiteVisibility.Styrelse, SiteVisibility.Dold }
                .Where(v => SiteAccess.CanSee(v, viewer)).Select(v => (int)v).ToArray();
    }

    /// <summary>
    /// Nyheterna i SQL (skriv/läs/ta bort). Varje fråga bär ägaren raden hör till (id-lagen: ett id utifrån prövas alltid mot den ägare anropet gäller).
    /// Skrivningarna prövas med <c>Revision</c> (compare-and-swap) och loggas i <c>SiteAudit</c> (aldrig text). Adressen (slug) ligger på raden.
    /// Läsnivån prövas HÄR, i frågan: en nyhet besökaren inte får läsa lämnar aldrig lagret (varken rubrik eller utdrag).
    /// </summary>
    public static class SitePosts
    {
        private const string Cols = "Id, OwnerType, OwnerId, Title, Slug, Excerpt, BodyHtml, CoverFileId, CoverAlt, AuthorMemberId, AuthorName, Status, ReadVisibility, PublishedDate, FirstPublishedDate, Revision, UpdatedByMemberId, CreatedDate, UpdatedDate, DeletedDate, MailedDate, MailedCount";

        /// <summary>Kolumnerna utan texten, för listor.</summary>
        private const string ListCols = "Id, OwnerType, OwnerId, Title, Slug, Excerpt, CAST(N'' AS NVARCHAR(MAX)) AS BodyHtml, CoverFileId, CoverAlt, AuthorMemberId, AuthorName, Status, ReadVisibility, PublishedDate, FirstPublishedDate, Revision, UpdatedByMemberId, CreatedDate, UpdatedDate, DeletedDate, MailedDate, MailedCount";

        /// <summary>Lägger in en ny nyhet som utkast. Ägarens rad låses (UPDLOCK) så att två samtidiga skrivningar inte får samma adress.</summary>
        public static SitePostWrite Create(IDatabase db, string ownerType, int ownerId, SitePostInput input, SitePostAuthor author, DateTime now, SiteActor actor)
        {
            var error = SitePostRules.Validate(input);
            if (error != null) return new SitePostWrite(SitePostResult.Invalid, Error: error);
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteOwner WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId) != 1)
                return new SitePostWrite(SitePostResult.NotFound);

            // En ny nyhet kan bara bära skribentens egen signatur (SiteSignatures): ingenting stod där förut.
            var body = SiteSignatures.Restrict(SiteHtmlSanitizer.Sanitize(input.BodyHtml), actor.MemberId, new HashSet<int>());
            var (coverId, coverAlt) = SitePostRules.EffectiveCover(input);
            var refError = CheckFiles(db, ownerType, ownerId, coverId, body, input.Gallery, out var fileIds);
            if (refError != null) return new SitePostWrite(SitePostResult.Invalid, Error: refError);
            var consentError = ConsentError(input.Gallery, new Dictionary<int, SitePostImageRow>(), input.ConsentConfirmed);
            if (consentError != null) return new SitePostWrite(SitePostResult.Invalid, Error: consentError);

            var slug = UniqueSlug(db, ownerType, ownerId, SitePostRules.Slugify(input.Title), exceptId: 0);
            var excerpt = string.IsNullOrWhiteSpace(input.Excerpt) ? SitePostRules.AutoExcerpt(body) : input.Excerpt!.Trim();
            var id = db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SitePost (OwnerType, OwnerId, Title, Slug, Excerpt, BodyHtml, CoverFileId, CoverAlt, AuthorMemberId, AuthorName, Status, ReadVisibility, Revision, UpdatedByMemberId, CreatedDate, UpdatedDate)
                  OUTPUT INSERTED.Id VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, 0, @10, 1, @8, @11, @11)",
                ownerType, ownerId, input.Title!.Trim(), slug, string.IsNullOrEmpty(excerpt) ? null : excerpt, body, coverId, Blank(coverAlt),
                author.MemberId, Clip(author.Name, 120), (int)input.ReadVisibility, now);
            SetRefs(db, id, ownerType, ownerId, fileIds);
            if (input.Gallery != null) WriteGallery(db, id, ownerType, ownerId, input.Gallery, new Dictionary<int, SitePostImageRow>(), actor.MemberId, now);
            Audit(db, ownerType, ownerId, actor, id, "post-create", now);
            return new SitePostWrite(SitePostResult.Ok, id, 1);
        }

        /// <summary>
        /// Sparar ändringar om raden fortfarande har <paramref name="expectedRevision"/> (annars Conflict: någon annan har sparat). Adressen byts bara så länge
        /// nyheten aldrig publicerats (en publicerad adress är permanent). Publiceringsstatusen rörs inte här.
        /// </summary>
        public static SitePostWrite Update(IDatabase db, int id, string ownerType, int ownerId, int expectedRevision, SitePostInput input, SiteActor actor, DateTime now)
        {
            var error = SitePostRules.Validate(input);
            if (error != null) return new SitePostWrite(SitePostResult.Invalid, Error: error);
            var cur = Get(db, id, ownerType, ownerId);
            if (cur == null) return new SitePostWrite(SitePostResult.NotFound);

            // Skribentens egen signatur, och de som redan stod i nyheten (en annan skribents bevaras när någon redigerar).
            var body = SiteSignatures.Restrict(SiteHtmlSanitizer.Sanitize(input.BodyHtml), actor.MemberId, SiteSignatures.Ids(SiteHtmlSanitizer.Sanitize(cur.BodyHtml)).ToHashSet());
            var (coverId, coverAlt) = SitePostRules.EffectiveCover(input);
            var refError = CheckFiles(db, ownerType, ownerId, coverId, body, input.Gallery, out var fileIds);
            if (refError != null) return new SitePostWrite(SitePostResult.Invalid, Error: refError);
            var existingGallery = input.Gallery == null ? new Dictionary<int, SitePostImageRow>() : GalleryRows(db, id, ownerType, ownerId).ToDictionary(r => r.FileId);
            var consentError = ConsentError(input.Gallery, existingGallery, input.ConsentConfirmed);
            if (consentError != null) return new SitePostWrite(SitePostResult.Invalid, Error: consentError);
            // Utan galleri i anropet rörs galleriets bilder inte, men de är fortfarande refererade (annars 404 i bildmotorn och ingen raderingsspärr).
            if (input.Gallery == null) fileIds = fileIds.Union(GalleryRows(db, id, ownerType, ownerId).Select(r => r.FileId)).ToList();

            var slug = cur.FirstPublishedDate == null ? UniqueSlug(db, ownerType, ownerId, SitePostRules.Slugify(input.Title), exceptId: id) : cur.Slug;
            var excerpt = string.IsNullOrWhiteSpace(input.Excerpt) ? SitePostRules.AutoExcerpt(body) : input.Excerpt!.Trim();
            var rows = db.Execute(
                @"UPDATE dbo.SitePost SET Title = @3, Slug = @4, Excerpt = @5, BodyHtml = @6, CoverFileId = @7, CoverAlt = @8, ReadVisibility = @9,
                         PublishedDate = CASE WHEN Status = 1 AND @12 IS NOT NULL THEN CAST(@12 AS DATETIME2(0)) ELSE PublishedDate END,
                         Revision = Revision + 1, UpdatedByMemberId = @10, UpdatedDate = @11
                  WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @13 AND DeletedDate IS NULL",
                id, ownerType, ownerId, input.Title!.Trim(), slug, string.IsNullOrEmpty(excerpt) ? null : excerpt, body, coverId, Blank(coverAlt),
                (int)input.ReadVisibility, actor.MemberId, now, input.PublishedDate, expectedRevision);
            if (rows != 1) return new SitePostWrite(SitePostResult.Conflict);
            SetRefs(db, id, ownerType, ownerId, fileIds);
            if (input.Gallery != null) WriteGallery(db, id, ownerType, ownerId, input.Gallery, existingGallery, actor.MemberId, now);
            Audit(db, ownerType, ownerId, actor, id, "post-update", now);
            return new SitePostWrite(SitePostResult.Ok, id, expectedRevision + 1);
        }

        /// <summary>Publicerar (eller drar tillbaka) nyheten. Första publiceringen sätter <c>FirstPublishedDate</c> för alltid; <c>PublishedDate</c> är det skribenten visar.</summary>
        public static SitePostWrite SetPublished(IDatabase db, int id, string ownerType, int ownerId, int expectedRevision, bool publish, DateTime? publishedDate, SiteActor actor, DateTime now)
        {
            var cur = Get(db, id, ownerType, ownerId);
            if (cur == null) return new SitePostWrite(SitePostResult.NotFound);
            var rows = publish
                ? db.Execute(
                    @"UPDATE dbo.SitePost SET Status = 1, PublishedDate = COALESCE(CAST(@3 AS DATETIME2(0)), PublishedDate, CAST(@5 AS DATETIME2(0))), FirstPublishedDate = COALESCE(FirstPublishedDate, CAST(@5 AS DATETIME2(0))),
                             Revision = Revision + 1, UpdatedByMemberId = @4, UpdatedDate = @5
                      WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @6 AND DeletedDate IS NULL",
                    id, ownerType, ownerId, publishedDate, actor.MemberId, now, expectedRevision)
                : db.Execute(
                    @"UPDATE dbo.SitePost SET Status = 0, Revision = Revision + 1, UpdatedByMemberId = @3, UpdatedDate = @4
                      WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Revision = @5 AND DeletedDate IS NULL",
                    id, ownerType, ownerId, actor.MemberId, now, expectedRevision);
            if (rows != 1) return new SitePostWrite(SitePostResult.Conflict);
            Audit(db, ownerType, ownerId, actor, id, publish ? "post-publish" : "post-unpublish", now);
            return new SitePostWrite(SitePostResult.Ok, id, expectedRevision + 1);
        }

        /// <summary>Tar bort nyheten (mjukt: raden står kvar med <c>DeletedDate</c>; adressen frigörs). Bildernas referenser släpps så att bilderna går att radera.</summary>
        public static SitePostResult Delete(IDatabase db, int id, string ownerType, int ownerId, SiteActor actor, DateTime now)
        {
            var rows = db.Execute(
                "UPDATE dbo.SitePost SET DeletedDate = @3, Revision = Revision + 1, UpdatedByMemberId = @4, UpdatedDate = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL",
                id, ownerType, ownerId, now, actor.MemberId);
            if (rows != 1) return SitePostResult.NotFound;
            db.Execute("DELETE FROM dbo.SitePostRef WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId);
            Audit(db, ownerType, ownerId, actor, id, "post-delete", now);
            return SitePostResult.Ok;
        }

        /// <summary>Ägarens nyhet (id utifrån prövas mot ägaren). Null om den inte finns, hör till någon annan eller är raderad. Prövar INTE läsnivån: använd <see cref="GetReadable"/> för besökare.</summary>
        public static SitePostRow? Get(IDatabase db, int id, string ownerType, int ownerId) =>
            db.FirstOrDefault<SitePostRow>($"SELECT {Cols} FROM dbo.SitePost WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL", id, ownerType, ownerId);

        /// <summary>
        /// Nyheten på adressen, bara om <paramref name="viewer"/> får läsa den. Ett utkast läses bara när <paramref name="includeDrafts"/> (skribenten);
        /// en nyhet besökaren inte får läsa ger null, precis som en som inte finns (ingen skillnad i svaret).
        /// </summary>
        public static SitePostRow? GetReadableBySlug(IDatabase db, string ownerType, int ownerId, string slug, SiteViewer viewer, bool includeDrafts)
        {
            var row = db.FirstOrDefault<SitePostRow>($"SELECT {Cols} FROM dbo.SitePost WHERE OwnerType = @0 AND OwnerId = @1 AND Slug = @2 AND DeletedDate IS NULL", ownerType, ownerId, slug);
            return row != null && CanRead(row, viewer, includeDrafts) ? row : null;
        }

        public static SitePostRow? GetReadable(IDatabase db, int id, string ownerType, int ownerId, SiteViewer viewer, bool includeDrafts)
        {
            var row = Get(db, id, ownerType, ownerId);
            return row != null && CanRead(row, viewer, includeDrafts) ? row : null;
        }

        /// <summary>EN regel för om en rad får läsas: publicerad + nivån, eller utkast för skribenten. Ren funktion.</summary>
        public static bool CanRead(SitePostRow row, SiteViewer viewer, bool includeDrafts) =>
            row.DeletedDate == null && (row.IsPublished ? SiteAccess.CanSee(row.Visibility, viewer) : includeDrafts);

        /// <summary>
        /// Nyheterna <paramref name="viewer"/> får se, nyast först (publiceringsdatum, sedan id), med <paramref name="offset"/>/<paramref name="take"/> och totalen.
        /// Nivåfiltret sitter i frågan; <paramref name="includeDrafts"/> tar med ägarens utkast (bara för skribenter). Listraderna bär inte texten.
        /// </summary>
        public static (List<SitePostRow> Rows, int Total) List(IDatabase db, string ownerType, int ownerId, SiteViewer viewer, bool includeDrafts, int offset, int take)
        {
            take = Math.Clamp(take, 1, 50);
            offset = Math.Max(0, offset);
            var levels = SitePostRules.ReadableLevels(viewer);
            var levelList = levels.Length == 0 ? "-1" : string.Join(",", levels);   // heltal ur en enum: aldrig text utifrån
            var where = $"OwnerType = @0 AND OwnerId = @1 AND DeletedDate IS NULL AND ((Status = 1 AND ReadVisibility IN ({levelList})) OR (@2 = 1 AND Status = 0))";
            var total = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.SitePost WHERE {where}", ownerType, ownerId, includeDrafts ? 1 : 0);
            var rows = db.Fetch<SitePostRow>(
                $@"SELECT {ListCols} FROM dbo.SitePost WHERE {where}
                   ORDER BY CASE WHEN Status = 0 THEN 0 ELSE 1 END, PublishedDate DESC, Id DESC
                   OFFSET @3 ROWS FETCH NEXT @4 ROWS ONLY", ownerType, ownerId, includeDrafts ? 1 : 0, offset, take);
            return (rows, total);
        }

        /// <summary>Ersätter nyhetens bildreferenser (omslag + bilder i texten).</summary>
        public static void SetRefs(IDatabase db, int postId, string ownerType, int ownerId, IReadOnlyCollection<int> fileIds)
        {
            db.Execute("DELETE FROM dbo.SitePostRef WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2", postId, ownerType, ownerId);
            foreach (var f in fileIds)
                db.Execute("INSERT INTO dbo.SitePostRef (PostId, FileId, OwnerType, OwnerId) VALUES (@0, @1, @2, @3)", postId, f, ownerType, ownerId);
        }

        /// <summary>Bilderna nyheten använder: omslaget och de i texten (ur den sanerade HTML:en), alla prövade mot ägaren. Felet på svenska, annars null.</summary>
        private static string? CheckFiles(IDatabase db, string ownerType, int ownerId, int? cover, string sanitizedBody, IReadOnlyList<SitePostGalleryItem>? gallery, out List<int> fileIds)
        {
            fileIds = SiteHtmlSanitizer.InlineFileIds(sanitizedBody).ToList();
            if (cover is int c && !fileIds.Contains(c)) fileIds.Add(c);
            // Galleribilderna är refererade av nyheten precis som omslaget: bildmotorn släpper bara ut en bild som någon nyhet pekar på (annars 404 för besökaren).
            if (gallery != null) foreach (var g in gallery) if (!fileIds.Contains(g.FileId)) fileIds.Add(g.FileId);
            if (fileIds.Count > SitePostRules.MaxPostRefs) return "Nyheten har för många bilder.";
            foreach (var f in fileIds)
                if (!SiteFiles.BelongsTo(db, f, ownerType, ownerId)) return "En bild i nyheten finns inte i föreningens bildbibliotek.";
            return null;
        }

        // ───── "Ta bort min bild" ─────

        /// <summary>En medlems förfrågan om att en galleribild tas bort ("Jag syns på den här bilden").</summary>
        public sealed class ImageRequestRow
        {
            public int Id { get; set; }
            public int PostId { get; set; }
            public int FileId { get; set; }
            public int? RequesterMemberId { get; set; }
            public string? Message { get; set; }
            public DateTime CreatedDate { get; set; }
        }

        /// <summary>
        /// Lägger in en förfrågan om att <paramref name="fileId"/> tas bort. Bilden måste ligga i nyhetens galleri (annars kan ingen be om något som inte finns). Samma medlem kan inte ha två
        /// obehandlade förfrågningar på samma bild. False = bilden finns inte i galleriet, eller förfrågan finns redan.
        /// </summary>
        public static bool AddImageRequest(IDatabase db, int postId, string ownerType, int ownerId, int fileId, int memberId, string? message, DateTime now)
        {
            if (!SocialTablesExist(db) || memberId <= 0) return false;
            var inGallery = db.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM dbo.SitePostImage i JOIN dbo.SitePost p ON p.Id = i.PostId AND p.OwnerType = i.OwnerType AND p.OwnerId = i.OwnerId
                  WHERE i.PostId = @0 AND i.OwnerType = @1 AND i.OwnerId = @2 AND i.FileId = @3 AND p.DeletedDate IS NULL", postId, ownerType, ownerId, fileId) == 1;
            if (!inGallery) return false;
            return db.Execute(
                @"IF NOT EXISTS (SELECT 1 FROM dbo.SitePostImageRequest WHERE PostId = @0 AND FileId = @3 AND RequesterMemberId = @4 AND HandledDate IS NULL)
                      INSERT INTO dbo.SitePostImageRequest (PostId, FileId, OwnerType, OwnerId, RequesterMemberId, Message, CreatedDate) VALUES (@0, @3, @1, @2, @4, @5, @6)",
                postId, ownerType, ownerId, fileId, memberId, Blank(message) is { } m ? Clip(m, 300) : null, now) == 1;
        }

        /// <summary>De obehandlade förfrågningarna på en nyhet (för skribenterna), äldst först, högst 50.</summary>
        public static List<ImageRequestRow> OpenImageRequests(IDatabase db, int postId, string ownerType, int ownerId) =>
            !SocialTablesExist(db) ? new List<ImageRequestRow>() : db.Fetch<ImageRequestRow>(
                @"SELECT TOP (50) Id, PostId, FileId, RequesterMemberId, Message, CreatedDate FROM dbo.SitePostImageRequest
                  WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2 AND HandledDate IS NULL ORDER BY Id", postId, ownerType, ownerId);

        /// <summary>Antalet obehandlade förfrågningar per nyhet, för arkivets märkning (en fråga för listans rader).</summary>
        public static Dictionary<int, int> OpenImageRequestCounts(IDatabase db, string ownerType, int ownerId) =>
            !SocialTablesExist(db) ? new Dictionary<int, int>() : db.Fetch<PostCountRow>(
                @"SELECT PostId, COUNT(*) AS N FROM dbo.SitePostImageRequest WHERE OwnerType = @0 AND OwnerId = @1 AND HandledDate IS NULL GROUP BY PostId", ownerType, ownerId)
                .ToDictionary(x => x.PostId, x => x.N);

        private sealed class PostCountRow { public int PostId { get; set; } public int N { get; set; } }

        /// <summary>Markerar förfrågan som hanterad (bilden är borttagen eller förfrågan avvisad). Prövas mot nyhetens ägare. False = finns inte eller redan hanterad.</summary>
        public static bool HandleImageRequest(IDatabase db, int requestId, string ownerType, int ownerId, int? handledByMemberId, DateTime now) =>
            SocialTablesExist(db) && db.Execute(
                "UPDATE dbo.SitePostImageRequest SET HandledDate = @3, HandledByMemberId = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND HandledDate IS NULL",
                requestId, ownerType, ownerId, now, handledByMemberId) == 1;

        // ───── Galleriet ─────

        private static volatile bool _socialTablesSeen;

        /// <summary>
        /// Finns galleri-, reaktions- och kommentarstabellerna? Schemat försvinner aldrig, så ett ja kommer ihåg (ingen rundtur per anrop). Saknas de (migreringen ej körd)
        /// visas nyheterna som förut utan galleri och reaktioner — aldrig ett 500.
        /// </summary>
        public static bool SocialTablesExist(IDatabase db)
        {
            if (_socialTablesSeen) return true;
            var ok = db.ExecuteScalar<int>(
                @"SELECT CASE WHEN OBJECT_ID(N'dbo.SitePostImage', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SitePostReaction', N'U') IS NOT NULL
                               AND OBJECT_ID(N'dbo.SitePostComment', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SitePostImageRequest', N'U') IS NOT NULL THEN 1 ELSE 0 END") == 1;
            if (ok) _socialTablesSeen = true;
            return ok;
        }

        /// <summary>Galleriets rader i visningsordning (första = den stora). Tom när tabellen saknas.</summary>
        public static List<SitePostImageRow> GalleryRows(IDatabase db, int postId, string ownerType, int ownerId) =>
            !SocialTablesExist(db) ? new List<SitePostImageRow>() : db.Fetch<SitePostImageRow>(
                @"SELECT PostId, FileId, SortOrder, Caption, AltText, ShowsPeople, ConsentByMemberId, ConsentDate FROM dbo.SitePostImage
                  WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2 ORDER BY SortOrder, FileId", postId, ownerType, ownerId);

        /// <summary>
        /// Bilderna att visa: galleriets rader, eller — för en nyhet som skrevs före galleriet och bara har ett omslag — omslaget som en enda bild. Då behöver en gammal nyhet
        /// ingen datamigrering: första sparningen i den nya skrivytan lägger omslaget i galleriet.
        /// </summary>
        public static List<SitePostImageRow> GalleryFor(IDatabase db, SitePostRow post)
        {
            var rows = GalleryRows(db, post.Id, post.OwnerType, post.OwnerId);
            if (rows.Count == 0 && post.CoverFileId is int cover)
                rows.Add(new SitePostImageRow { PostId = post.Id, FileId = cover, SortOrder = 0, AltText = post.CoverAlt });
            return rows;
        }

        /// <summary>Felet (på svenska) när en bild märkt "visar personer" saknar samtycke att stå på: nya markeringar kräver bekräftelsen, redan bekräftade behåller sin.</summary>
        private static string? ConsentError(IReadOnlyList<SitePostGalleryItem>? gallery, IReadOnlyDictionary<int, SitePostImageRow> existing, bool confirmed)
        {
            if (gallery == null || confirmed) return null;
            var missing = gallery.Any(g => g.ShowsPeople && !(existing.TryGetValue(g.FileId, out var e) && e.ShowsPeople && e.ConsentDate != null));
            return missing ? "Bekräfta att de som syns på bilderna har gett sitt samtycke (för barn under 18: vårdnadshavare)." : null;
        }

        /// <summary>
        /// Ersätter galleriet (i anroparens transaktion, under nyhetens revisionskontroll). Ordningen skrivs tätt 0..N-1 — aldrig ett unikt index på <c>SortOrder</c>, så att två bilder
        /// kan byta plats utan mellansteg. Samtycket behåller sin tid och person när bilden redan var bekräftad; annars är det nu och den som sparar.
        /// </summary>
        private static void WriteGallery(IDatabase db, int postId, string ownerType, int ownerId, IReadOnlyList<SitePostGalleryItem> gallery,
            IReadOnlyDictionary<int, SitePostImageRow> existing, int? actorMemberId, DateTime now)
        {
            db.Execute("DELETE FROM dbo.SitePostImage WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2", postId, ownerType, ownerId);
            for (var i = 0; i < gallery.Count; i++)
            {
                var g = gallery[i];
                int? by = null; DateTime? at = null;
                if (g.ShowsPeople)
                {
                    if (existing.TryGetValue(g.FileId, out var e) && e.ShowsPeople && e.ConsentDate != null) { by = e.ConsentByMemberId; at = e.ConsentDate; }
                    else { by = actorMemberId; at = now; }
                }
                db.Execute(
                    @"INSERT INTO dbo.SitePostImage (PostId, FileId, OwnerType, OwnerId, SortOrder, Caption, AltText, ShowsPeople, ConsentByMemberId, ConsentDate)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9)",
                    postId, g.FileId, ownerType, ownerId, i, Blank(g.Caption), Blank(g.Alt), g.ShowsPeople, by, at);
            }
        }

        private static string UniqueSlug(IDatabase db, string ownerType, int ownerId, string baseSlug, int exceptId)
        {
            for (var n = 1; n < 1000; n++)
            {
                var candidate = SitePostRules.WithSuffix(baseSlug, n);
                if (SitePostRules.IsReserved(candidate)) continue;
                var taken =db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePost WHERE OwnerType = @0 AND OwnerId = @1 AND Slug = @2 AND DeletedDate IS NULL AND Id <> @3", ownerType, ownerId, candidate, exceptId);
                if (taken == 0) return candidate;
            }
            return SitePostRules.WithSuffix(baseSlug, Random.Shared.Next(1000, 99999));
        }

        /// <summary>
        /// Tar utskicket: sätter <c>MailedDate</c> om nyheten är publicerad, inte raderad och aldrig mejlats. True = den här anroparen får skicka (claim FÖRE utskicket, så att två
        /// samtidiga klick eller en omladdning aldrig ger två utskick; ett misslyckat utskick skickas inte om, hellre ett mejl för lite än två).
        /// </summary>
        public static bool ClaimMail(IDatabase db, int id, string ownerType, int ownerId, DateTime now) =>
            db.Execute("UPDATE dbo.SitePost SET MailedDate = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Status = 1 AND DeletedDate IS NULL AND MailedDate IS NULL", id, ownerType, ownerId, now) == 1;

        public static void SetMailedCount(IDatabase db, int id, string ownerType, int ownerId, int count) =>
            db.Execute("UPDATE dbo.SitePost SET MailedCount = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId, count);

        /// <summary>Alla ägarens nyheter raderas för gott (referenserna följer med, CASCADE). Före bilderna: omslagets nyckel har ingen ON DELETE-regel.</summary>
        public static int DeleteAllForOwner(IDatabase db, string ownerType, int ownerId) =>
            db.Execute("IF OBJECT_ID(N'dbo.SitePost', N'U') IS NOT NULL DELETE FROM dbo.SitePost WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);

        /// <summary>Används bilden av en nyhet som inte är raderad?</summary>
        public static bool FileInUse(IDatabase db, int fileId, string ownerType, int ownerId) =>
            TablesExist(db) && db.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM dbo.SitePostRef r JOIN dbo.SitePost p ON p.Id = r.PostId
                  WHERE r.FileId = @0 AND p.OwnerType = @1 AND p.OwnerId = @2 AND p.DeletedDate IS NULL", fileId, ownerType, ownerId) > 0;

        /// <summary>Nyheterna som refererar bilden, för bildmotorn: (publicerad, nivå). Högst <see cref="SitePostRules.MaxPostRefs"/>. Saknas tabellerna (migreringen ej körd) finns inga.</summary>
        public static List<SitePostFileRef> RefsForFile(IDatabase db, int fileId, string ownerType, int ownerId) =>
            !TablesExist(db) ? new List<SitePostFileRef>() : db.Fetch<SitePostFileRef>(
                @"SELECT TOP (@3) p.Status, p.ReadVisibility FROM dbo.SitePostRef r JOIN dbo.SitePost p ON p.Id = r.PostId
                  WHERE r.FileId = @0 AND p.OwnerType = @1 AND p.OwnerId = @2 AND p.DeletedDate IS NULL", fileId, ownerType, ownerId, SitePostRules.MaxPostRefs);

        /// <summary>Lägger antalet icke raderade nyheter som använder varje bild till bibliotekets "används av".</summary>
        public static void AddPostUse(IDatabase db, List<SiteFiles.FileListItem> items, string ownerType, int ownerId)
        {
            if (items.Count == 0 || !TablesExist(db)) return;
            var counts = db.Fetch<PostUseRow>(
                @"SELECT r.FileId, COUNT(*) AS N FROM dbo.SitePostRef r JOIN dbo.SitePost p ON p.Id = r.PostId
                  WHERE p.OwnerType = @0 AND p.OwnerId = @1 AND p.DeletedDate IS NULL GROUP BY r.FileId", ownerType, ownerId).ToDictionary(x => x.FileId, x => x.N);
            foreach (var it in items) if (counts.TryGetValue(it.Id, out var n)) it.UsedBy += n;
        }

        private sealed class PostUseRow { public int FileId { get; set; } public int N { get; set; } }

        private static bool TablesExist(IDatabase db) =>
            db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.SitePost', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SitePostRef', N'U') IS NOT NULL THEN 1 ELSE 0 END") == 1;

        private static void Audit(IDatabase db, string ownerType, int ownerId, SiteActor actor, int postId, string action, DateTime now) =>
            db.Execute(
                @"INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, CreatedDate) VALUES (@0, @1, @2, @3, N'post', @4, @5, @6)",
                ownerType, ownerId, actor.MemberId, actor.AsSupport, postId, action, now);

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string Clip(string? s, int max) { var t = (s ?? "").Trim(); return t.Length > max ? t[..max] : t; }
    }

    /// <summary>En nyhets referens till en bild: bara det bildmotorn behöver veta.</summary>
    public sealed class SitePostFileRef
    {
        public byte Status { get; set; }
        public byte ReadVisibility { get; set; }
    }
}
