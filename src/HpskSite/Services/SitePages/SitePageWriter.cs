using HpskSite.Services.Addresses;
using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>Vem som skriver. <see cref="AsSupport"/> = stödläge (kretsadmin/sajtadmin), loggas och märks på versionen.</summary>
    public sealed record SiteActor(int? MemberId, bool AsSupport = false);

    /// <summary>
    /// Det skrivlagret behöver veta utifrån, och som inte ligger i SQL-tabellerna: dokumentarkivet (ägare) och Umbraco-trädet
    /// (barnnoder, kretsens klubbar). Läses FÖRE transaktionen i en tjänst (Umbraco-värden läses inte i skrivtransaktionen,
    /// plan v2 §2); här är de en enkel yta så att skrivlagret kan provas mot en riktig SQL Server utan Umbraco.
    /// </summary>
    public interface ISiteOwnerFacts
    {
        /// <summary>Tillhör dokumentet <paramref name="docId"/> den här ägaren? (Dokumentarkivets ägare, aldrig ett id från anropet.)</summary>
        bool DocumentBelongsTo(int docId, string ownerType, int ownerId);

        /// <summary>Url-segmenten för ägarens barnnoder i trädet.</summary>
        IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId);

        /// <summary>Url-segmenten för kretsens klubbar (tom för en klubb).</summary>
        IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId);
    }

    public enum SiteWriteStatus
    {
        Ok,
        Conflict,
        NotFound,
        TooManyPages,
        NothingToPublish,
        /// <summary>Innehållet bryter mot schemat (<see cref="SiteWriteResult.Errors"/> säger var) eller refererar något ägaren inte har.</summary>
        Invalid,
        /// <summary>Adressen är upptagen eller otillåten (<see cref="SiteWriteResult.Message"/> säger varför).</summary>
        SlugTaken,
        /// <summary>Texten ser ut att innehålla ett personnummer (<see cref="SiteWriteResult.Match"/>); admin väljer Ändra eller publicera ändå.</summary>
        PersonalNumber,
    }

    public sealed record SiteWriteResult(SiteWriteStatus Status, int PageId = 0, int Revision = 0, string? Slug = null,
        IReadOnlyList<SiteContentError>? Errors = null, string? Message = null, string? Match = null)
    {
        public bool Ok => Status == SiteWriteStatus.Ok;
    }

    /// <summary>
    /// Skrivlagret för egna sidor (klubbsidorna 2a): en sparning är EN transaktion — sida, version, adress, referenser och
    /// audit — i anroparens databas. Auktorisering (ägarens admin, stödläge) prövas FÖRE, i controllern; här prövas allt som rör
    /// innehållet: schema, personnummer, att refererade dokument tillhör ägaren, adressens namnrymd, och att varje sid-id hör
    /// till den ägare anropet gäller (via <see cref="SitePageStore"/>).
    ///
    /// <para>Innehållet valideras OM vid publicering mot sidans aktuella synlighet: en sida som gjorts stängdare efter att ett
    /// block sparats kan inte publiceras med ett öppnare block.</para>
    /// </summary>
    public static class SitePageWriter
    {
        /// <summary>Alla textfält i innehållet (titel + blockens text), för personnummerspärren. Länkadresser räknas inte som text.</summary>
        internal static IEnumerable<string?> TextOf(string title, SiteContentResult content)
        {
            yield return title;
            foreach (var b in content.AllBlocks)
            {
                switch (b.Type)
                {
                    case "text": yield return b.Data["html"]?.GetValue<string>(); break;
                    case "heading": yield return b.Data["text"]?.GetValue<string>(); break;
                    case "documents": case "contact": case "events": yield return b.Data["title"]?.GetValue<string>(); break;
                    case "pagelink": yield return b.Data["label"]?.GetValue<string>(); break;
                    case "links":
                        foreach (var it in b.Data["items"]!.AsArray()) yield return it!["label"]?.GetValue<string>();
                        break;
                }
            }
        }

        /// <summary>Skapa en fri sida med adress. Sluggen utelämnad = ur titeln (numrerad om upptagen).</summary>
        public static SiteWriteResult CreatePage(IDatabase db, ISiteOwnerFacts facts, string ownerType, int ownerId, string title,
            string? requestedSlug, SiteVisibility visibility, SiteActor actor, DateTime now)
        {
            title = (title ?? "").Trim();
            if (title.Length is 0 or > 120)
                return Invalid("title", "Titeln måste vara 1–120 tecken.");
            var hit = SitePersonalNumberGuard.FindInAny(new[] { title });
            if (hit != null) return new SiteWriteResult(SiteWriteStatus.PersonalNumber, Match: hit, Message: "Titeln ser ut att innehålla ett personnummer.");

            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return new SiteWriteResult(SiteWriteStatus.NotFound);

            var taken = TakenAddressSlugs(db, ownerType, ownerId);
            var children = facts.ChildNodeSegments(ownerType, ownerId);
            var clubs = facts.RegionClubSegments(ownerType, ownerId);
            string slug;
            if (string.IsNullOrWhiteSpace(requestedSlug))
            {
                var suggested = SitePageSlugRules.Suggest(SitePageSlugRules.SlugFrom(title), ownerType, taken, children, clubs);
                if (suggested == null) return new SiteWriteResult(SiteWriteStatus.SlugTaken, Message: "Välj ett annat namn eller en annan adress: adressen ur titeln kan inte användas.");
                slug = suggested;
            }
            else
            {
                slug = requestedSlug.Trim();
                var check = SitePageSlugRules.Check(slug, ownerType, taken, children, clubs);
                if (!check.Ok) return new SiteWriteResult(SiteWriteStatus.SlugTaken, Message: check.Reason);
            }

            var created = SitePageStore.Create(db, ownerType, ownerId, title, visibility, null, null, actor.MemberId, actor.AsSupport, now);
            if (created.Outcome != SiteStoreOutcome.Ok) return From(created);

            var addr = PageAddressService.SetCurrentFlat(db, PageAddressKinds.SitePage, created.PageId, new PageAddressOwner(ownerType, ownerId), slug);
            if (!addr.Ok) throw new InvalidOperationException("Adressen blev upptagen mitt i skapandet: " + addr.Conflict); // låset på SiteOwner gör det omöjligt; rulla tillbaka
            Audit(db, ownerType, ownerId, actor, "page", created.PageId, "page-create", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, created.PageId, created.Revision, slug);
        }

        /// <summary>
        /// Skapar ägarens mallkopia (egen text före/efter) för en systemsida — idempotent: finns den redan returneras den. Titeln är systemsidans (fast), synligheten får aldrig
        /// vara öppnare än systemsidans öppnaste block (<see cref="SiteTemplate.MostOpenLevel"/>) och aldrig Utloggad. Loggas som <c>slot-create</c>.
        /// </summary>
        public static SiteWriteResult CreateTemplateCopy(IDatabase db, string ownerType, int ownerId, string templateKey, SiteVisibility visibility, SiteActor actor, DateTime now)
        {
            var template = SiteTemplates.For(ownerType);
            var open = template.MostOpenLevel(templateKey);
            if (open == null) return Invalid("templateKey", "Den sidan kan inte få egen text före eller efter.");
            if (!SiteAccess.IsSelectable(visibility) || visibility > SiteVisibility.Dold) return Invalid("visibility", "Ogiltig synlighet.");
            if (visibility < open.Value) return Invalid("visibility", $"Texten får inte vara öppnare än sidan själv ({SlotLevelLabel(open.Value)} eller strängare).");

            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var existing = SitePageStore.GetTemplateCopy(db, ownerType, ownerId, templateKey);
            if (existing != null) return new SiteWriteResult(SiteWriteStatus.Ok, existing.Id, existing.Revision);

            var title = template.PageByKey(templateKey)!.Label;
            var created = SitePageStore.Create(db, ownerType, ownerId, title, visibility, templateKey, template.Version, actor.MemberId, actor.AsSupport, now);
            if (created.Outcome != SiteStoreOutcome.Ok) return From(created);
            Audit(db, ownerType, ownerId, actor, "page", created.PageId, "slot-create", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, created.PageId, created.Revision);
        }

        private static string SlotLevelLabel(SiteVisibility v) => v switch
        {
            SiteVisibility.Alla => "alla", SiteVisibility.Inloggade => "inloggade", SiteVisibility.Klubbmedlemmar => "medlemmar", SiteVisibility.Styrelse => "styrelsen", _ => "administratörer",
        };

        /// <summary>
        /// Autospar av utkastet. <paramref name="allowPersonalNumber"/> = admin har svarat "Det är inget personnummer" (loggas).
        /// </summary>
        public static SiteWriteResult SaveDraft(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, int expectedRevision,
            string title, string blocksJson, SiteVisibility? visibility, SiteActor actor, DateTime now, bool allowPersonalNumber = false)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (!page.IsFree) title = page.Title;                              // en mallkopia heter alltid som systemsidan; titeln kan inte ändras
            title = (title ?? "").Trim();
            if (title.Length is 0 or > 120) return Invalid("title", "Titeln måste vara 1–120 tecken.");
            var vis = visibility ?? page.VisibilityLevel;
            if (!SiteAccess.IsSelectable(vis) || vis > SiteVisibility.Dold) return Invalid("visibility", "Ogiltig synlighet.");
            if (!page.IsFree && SiteTemplates.For(ownerType).MostOpenLevel(page.TemplateKey!) is { } floor && vis < floor)
                return Invalid("visibility", "Texten får inte vara öppnare än sidan själv.");   // ett slot kan aldrig vara bredare än systemsidan

            var v = Validate(db, facts, page, ownerType, ownerId, title, blocksJson, vis, allowPersonalNumber, out var content);
            if (v != null) return v;

            var json = SiteBlockSchema.ToJson(content!);
            var saved = SitePageStore.SaveDraft(db, pageId, ownerType, ownerId, expectedRevision, title, json, vis, actor.MemberId, actor.AsSupport, now);
            if (saved.Outcome != SiteStoreOutcome.Ok) return From(saved);

            RebuildRefs(db, facts, page, ownerType, ownerId, vis, includeDraft: true);
            if (allowPersonalNumber && SitePersonalNumberGuard.FindInAny(TextOf(title, content!)) != null)
                Audit(db, ownerType, ownerId, actor, "page", pageId, "personal-number-override", SitePageStore.Hash(title, json), now);
            if (vis != page.VisibilityLevel)
                Audit(db, ownerType, ownerId, actor, "page", pageId, "page-visibility", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, saved.Revision);
        }

        /// <summary>Publicera utkastet (valideras om mot sidans aktuella synlighet).</summary>
        public static SiteWriteResult Publish(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, int expectedRevision,
            SiteActor actor, DateTime now, bool allowPersonalNumber = false)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var draft = SitePageStore.GetDraft(db, pageId, ownerType, ownerId);
            if (draft == null) return new SiteWriteResult(SiteWriteStatus.NothingToPublish, pageId, page.Revision);

            var v = Validate(db, facts, page, ownerType, ownerId, page.Title, draft.BlocksJson, page.VisibilityLevel, allowPersonalNumber, out _);
            if (v != null) return v;

            var published = SitePageStore.Publish(db, pageId, ownerType, ownerId, expectedRevision, actor.MemberId, actor.AsSupport, now);
            if (published.Outcome != SiteStoreOutcome.Ok) return From(published);
            RebuildRefs(db, facts, page, ownerType, ownerId, page.VisibilityLevel, includeDraft: false);
            Audit(db, ownerType, ownerId, actor, "page", pageId, "page-publish", SitePageStore.Hash(page.Title, draft.BlocksJson), now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, published.Revision);
        }

        /// <summary>
        /// "Använd den här versionen": versionens titel och innehåll kopieras in som NYTT utkast (aldrig en pekarflytt), och valideras om mot sidans
        /// aktuella synlighet precis som vilken sparning som helst. Versionen måste höra till just den här sidan.
        /// </summary>
        public static SiteWriteResult RestoreVersion(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, int versionId,
            int expectedRevision, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var version = SitePageStore.GetVersion(db, versionId, pageId);   // (id, pageId): en annan sidas version hittas aldrig
            if (version == null || version.IsBaseline) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var r = SaveDraft(db, facts, pageId, ownerType, ownerId, expectedRevision, version.Title, version.BlocksJson, null, actor, now);   // spärren gäller även här: en gammal version med personnummer kan inte tas tillbaka tyst
            if (r.Ok) Audit(db, ownerType, ownerId, actor, "page", pageId, "page-restore-version", SitePageStore.Hash(version.Title, version.BlocksJson), now);
            return r;
        }

        /// <summary>"Spara en återställningspunkt" (loggas inte: den ändrar ingenting synligt).</summary>
        public static SiteWriteResult SaveCheckpoint(IDatabase db, int pageId, string ownerType, int ownerId, SiteActor actor, DateTime now) =>
            From(SitePageStore.SaveCheckpoint(db, pageId, ownerType, ownerId, actor.MemberId, actor.AsSupport, now));

        /// <summary>
        /// Byt sidans adress. <b>Sluggen ändras aldrig automatiskt</b> — bara här, på uttrycklig begäran. Den gamla adressen blir historik och
        /// svarar 301 till den nya (findern, för en sida som var öppen för minst medlemmar). Sidans EGNA tidigare adresser räknas som lediga;
        /// alla andra regler är de vanliga (<see cref="SitePageSlugRules"/>). Körs under ägarens lås så att två byten inte tar samma adress.
        /// </summary>
        public static SiteWriteResult SetSlug(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, string newSlug, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null || !page.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            newSlug = (newSlug ?? "").Trim();
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return new SiteWriteResult(SiteWriteStatus.NotFound);

            // Namnrymden utan sidans egna adresser (en adress sidan själv haft är ledig att gå tillbaka till).
            var taken = db.Fetch<string>(
                "SELECT Slug FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL AND NOT (TargetKind = @2 AND TargetId = @3)",
                ownerType, ownerId, PageAddressKinds.SitePage, pageId);
            var check = SitePageSlugRules.Check(newSlug, ownerType, taken, facts.ChildNodeSegments(ownerType, ownerId), facts.RegionClubSegments(ownerType, ownerId));
            if (!check.Ok) return new SiteWriteResult(SiteWriteStatus.SlugTaken, pageId, page.Revision, Message: check.Reason);

            var r = PageAddressService.SetCurrentFlat(db, PageAddressKinds.SitePage, pageId, new PageAddressOwner(ownerType, ownerId), newSlug);
            if (!r.Ok) return new SiteWriteResult(SiteWriteStatus.SlugTaken, pageId, page.Revision, Message: r.Conflict);
            if (r.Changed) Audit(db, ownerType, ownerId, actor, "page", pageId, "page-slug", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, page.Revision, newSlug);
        }

        /// <summary>Lägg en egen sida i menyn eller ta bort den därifrån (loggas). Sidan måste höra till ägaren.</summary>
        public static SiteWriteResult SetInMenu(IDatabase db, int pageId, string ownerType, int ownerId, bool inMenu, SiteActor actor, DateTime now)
        {
            if (!SitePageStore.SetInMenu(db, pageId, ownerType, ownerId, inMenu, now)) return new SiteWriteResult(SiteWriteStatus.NotFound);
            Audit(db, ownerType, ownerId, actor, "menu", pageId, inMenu ? "menu-add" : "menu-remove", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId);
        }

        public static SiteWriteResult SoftDelete(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, SiteActor actor, DateTime now)
        {
            // En mallkopia (egen text på en systemsida) raderas aldrig: tömmer man den är systemsidan som förut. Bara fria sidor går att radera.
            var existing = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (existing == null || !existing.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var r = SitePageStore.SoftDelete(db, pageId, ownerType, ownerId, expectedRevision, actor.MemberId, now);
            if (r.Outcome == SiteStoreOutcome.Ok) Audit(db, ownerType, ownerId, actor, "page", pageId, "page-delete", null, now);
            return From(r);
        }

        // ── Hjälpare ──────────────────────────────────────────────────────────────

        private static SiteWriteResult? Validate(IDatabase db, ISiteOwnerFacts facts, SitePageRow page, string ownerType, int ownerId,
            string title, string blocksJson, SiteVisibility vis, bool allowPersonalNumber, out SiteContentResult? content)
        {
            var kind = page.IsFree ? SiteContentKind.FreePage : SiteContentKind.TemplateCopy;
            content = SiteBlockSchema.Parse(blocksJson, kind, vis, SiteTemplates.For(ownerType));
            if (!content.Ok)
                return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision, Errors: content.Errors, Message: content.Errors[0].Message);

            // Refererade dokument måste tillhöra RADENS ägare (id utifrån är aldrig betrott).
            var foreign = SiteBlockSchema.ExtractRefs(content).Where(r => r.Kind == "doc" && !facts.DocumentBelongsTo(r.Id, ownerType, ownerId)).ToList();
            if (foreign.Count > 0)
                return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision,
                    Errors: new[] { new SiteContentError("refs", "Ett valt dokument finns inte hos den här föreningen.") },
                    Message: "Ett valt dokument finns inte hos den här föreningen.");

            if (!allowPersonalNumber)
            {
                var hit = SitePersonalNumberGuard.FindInAny(TextOf(title, content));
                if (hit != null)
                    return new SiteWriteResult(SiteWriteStatus.PersonalNumber, page.Id, page.Revision, Match: hit,
                        Message: $"Det här ser ut som ett personnummer: {hit}");
            }
            return null;
        }

        /// <summary>Bygger om <c>SitePageRef</c> för sidan: utkastets och det publicerade innehållets refererade id:n.</summary>
        private static void RebuildRefs(IDatabase db, ISiteOwnerFacts facts, SitePageRow page, string ownerType, int ownerId, SiteVisibility vis, bool includeDraft)
        {
            var refs = new HashSet<(string, int)>();
            void Add(SitePageVersionRow? v)
            {
                if (v == null) return;
                var c = SiteBlockSchema.Parse(v.BlocksJson, page.IsFree ? SiteContentKind.FreePage : SiteContentKind.TemplateCopy, vis, SiteTemplates.For(ownerType));
                if (c.Ok) foreach (var r in SiteBlockSchema.ExtractRefs(c)) refs.Add(r);
            }
            if (includeDraft) Add(SitePageStore.GetDraft(db, page.Id, ownerType, ownerId));
            Add(SitePageStore.GetPublished(db, page.Id, ownerType, ownerId));

            db.Execute("DELETE FROM dbo.SitePageRef WHERE PageId = @0", page.Id);
            foreach (var (kind, id) in refs.OrderBy(r => r.Item1).ThenBy(r => r.Item2))
                db.Execute("INSERT INTO dbo.SitePageRef (PageId, RefKind, RefId) VALUES (@0, @1, @2)", page.Id, kind, id);
        }

        /// <summary>Ägarens årslösa adresser av ALLA sorter (en egen sida delar namnrymd med dem).</summary>
        private static List<string> TakenAddressSlugs(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<string>("SELECT Slug FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL", ownerType, ownerId);

        private static void Audit(IDatabase db, string ownerType, int ownerId, SiteActor actor, string objectKind, int objectId, string action, byte[]? hash, DateTime now) =>
            db.Execute(
                @"INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, ContentHash, CreatedDate)
                  VALUES (@0, @1, @2, @3, @4, @5, @6, CONVERT(BINARY(32), @7), @8)",
                ownerType, ownerId, actor.MemberId, actor.AsSupport, objectKind, objectId, action, hash, now);

        private static SiteWriteResult Invalid(string path, string message) =>
            new(SiteWriteStatus.Invalid, Errors: new[] { new SiteContentError(path, message) }, Message: message);

        private static SiteWriteResult From(SiteStoreResult r) => r.Outcome switch
        {
            SiteStoreOutcome.Ok => new(SiteWriteStatus.Ok, r.PageId, r.Revision),
            SiteStoreOutcome.Conflict => new(SiteWriteStatus.Conflict, r.PageId, r.Revision),
            SiteStoreOutcome.NotFound => new(SiteWriteStatus.NotFound, r.PageId, r.Revision),
            SiteStoreOutcome.TooManyPages => new(SiteWriteStatus.TooManyPages, r.PageId, r.Revision),
            SiteStoreOutcome.NothingToPublish => new(SiteWriteStatus.NothingToPublish, r.PageId, r.Revision),
            _ => throw new ArgumentOutOfRangeException(nameof(r)),
        };
    }
}
