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
        /// <summary>Sajtadmin har stoppat sidan (nödbromsen): den kan inte publiceras förrän den hävs.</summary>
        Suspended,
        /// <summary>Brytare 3 (omvandling tillåten) är AV för ägaren, eller brytare 1 eller 2 är det.</summary>
        ConvertOff,
    }

    /// <param name="Affected">Antal andra sidor åtgärden också ändrade (undersidor som fick strängare nivå, undersidor som togs ur menyn …), för meddelandet till användaren.</param>
    public sealed record SiteWriteResult(SiteWriteStatus Status, int PageId = 0, int Revision = 0, string? Slug = null,
        IReadOnlyList<SiteContentError>? Errors = null, string? Message = null, string? Match = null, int Affected = 0)
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
        private sealed class OwnerFlags { public bool IsEnabled { get; set; } public bool EditingEnabled { get; set; } public bool ConvertEnabled { get; set; } }

        /// <summary>
        /// Gör om en systemsida till en blocksida på samma adress (K2). Skapar ett UTKAST i schema 2: standardpanelerna i dagens ordning och nivåer, med ägarens
        /// egen text före/efter (om den finns publicerad) som vanliga block. Den levande sidan är oförändrad tills utkastet publiceras; "Avbryt" kastar utkastet.
        /// <para>Grindar, alla prövade här under ägarens lås: ägaren har brytare 1, 2 och 3 PÅ; sidan är en systemsida som kan göras om; mallkopian har inga
        /// osparade ändringar (de skulle gå förlorade); idempotent — är sidan redan omgjord returneras den. Aktören prövas av anroparen (ägarens EGEN admin, inte stödläge).</para>
        /// </summary>
        public static SiteWriteResult ConvertTemplatePage(IDatabase db, ISiteOwnerFacts facts, string ownerType, int ownerId, string templateKey, SiteActor actor, DateTime now)
        {
            var template = SiteTemplates.For(ownerType);
            if (template.PageByKey(templateKey) is not { IsAdmin: false } def) return Invalid("templateKey", "Den sidan kan inte göras om.");
            if (PanelCatalog.Standard(ownerType, templateKey, true).Count == 0) return Invalid("templateKey", "Den sidan kan inte göras om.");

            var flags = db.FirstOrDefault<OwnerFlags>(
                "SELECT IsEnabled, EditingEnabled, ConvertEnabled FROM dbo.SiteOwner WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            if (flags == null) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (!flags.IsEnabled || !flags.EditingEnabled || !flags.ConvertEnabled) return new SiteWriteResult(SiteWriteStatus.ConvertOff);

            var open = SitePageStore.GetTemplateCopy(db, ownerType, ownerId, templateKey);
            SiteContentResult? slots = null;
            if (open == null)
            {
                var created = CreateTemplateCopy(db, ownerType, ownerId, templateKey, SiteConversion.PageVisibility(ownerType, templateKey), actor, now);
                if (!created.Ok) return created;
                open = SitePageStore.GetPage(db, created.PageId, ownerType, ownerId)!;
            }
            else
            {
                if (SitePageStore.IsConverted(db, open.Id)) return new SiteWriteResult(SiteWriteStatus.Ok, open.Id, open.Revision);   // idempotent: dubbelklick, två flikar
                var draft = SitePageStore.GetDraft(db, open.Id, ownerType, ownerId);
                if (draft != null && !draft.IsBaseline)
                    return new SiteWriteResult(SiteWriteStatus.Invalid, open.Id, open.Revision, Message: "Sidan har ändringar som inte är publicerade. Publicera eller släng dem först.");
                var published = SitePageStore.GetPublished(db, open.Id, ownerType, ownerId);
                if (published != null)
                {
                    slots = SiteBlockSchema.Parse(published.BlocksJson, SiteContentKind.TemplateCopy, open.VisibilityLevel, template);
                    if (!slots.Ok) return new SiteWriteResult(SiteWriteStatus.Invalid, open.Id, open.Revision, Message: "Sidans egen text kan inte läsas, så sidan kan inte göras om. Kontakta pistol.nu.");
                }
            }

            var content = SiteConversion.Build(ownerType, templateKey, slots);
            var vis = SiteConversion.PageVisibility(ownerType, templateKey);
            var json = SiteBlockSchema.ToJson(content);
            // Lägre nivå än SaveDraft i Writer: Validate vägrar medvetet schema 2 på en sida som ännu inte är omgjord.
            var saved = SitePageStore.SaveDraft(db, open.Id, ownerType, ownerId, open.Revision, open.Title, json, vis, actor.MemberId, actor.AsSupport, now);
            if (saved.Outcome != SiteStoreOutcome.Ok) return From(saved);
            RebuildRefs(db, facts, open, ownerType, ownerId, vis, includeDraft: true);
            Audit(db, ownerType, ownerId, actor, "page", open.Id, "page-convert", SitePageStore.Hash(open.Title, json), now);
            return new SiteWriteResult(SiteWriteStatus.Ok, open.Id, saved.Revision);
        }

        private const string HasChildrenMessage = "Sidan har undersidor och kan inte läggas tillbaka förrän de är flyttade eller raderade.";

        /// <summary>
        /// Avbryt omvandlingen: kastar utkastet i schema 2 på en sida vars omvandling inte är publicerad. Den levande sidan rörs inte.
        /// En publicerad omvandling ångras med <see cref="RevertConversion"/>.
        /// </summary>
        public static SiteWriteResult CancelConversion(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, int expectedRevision, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null || page.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var draft = SitePageStore.GetDraft(db, pageId, ownerType, ownerId);
            var published = SitePageStore.GetPublished(db, pageId, ownerType, ownerId);
            if (published != null && published.SchemaVersion == SiteBlockSchema.ConvertedSchemaVersion)
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, page.Revision, Message: "Sidan är redan omgjord. Använd Lägg tillbaka för att gå tillbaka till den vanliga sidan.");
            if (draft == null || draft.SchemaVersion != SiteBlockSchema.ConvertedSchemaVersion)
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, page.Revision, Message: "Ingen omvandling pågår.");
            if (SitePageStore.ChildCount(db, pageId) > 0)
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, page.Revision, Message: HasChildrenMessage);

            var r = published != null
                ? SitePageStore.DiscardDraft(db, pageId, ownerType, ownerId, expectedRevision, actor.MemberId, now)
                : SitePageStore.ResetToBaseline(db, pageId, ownerType, ownerId, expectedRevision, actor.MemberId, now);
            if (r.Outcome != SiteStoreOutcome.Ok) return From(r);
            RebuildRefs(db, facts, page, ownerType, ownerId, page.VisibilityLevel, includeDraft: false);
            Audit(db, ownerType, ownerId, actor, "page", pageId, "page-convert-cancel", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, r.Revision);
        }

        /// <summary>
        /// Lägg tillbaka (Återgå): sidan blir en vanlig systemsida igen. Publicerar sidans senaste version i schema 1 — det den hade före omvandlingen, med ägarens
        /// egen text — annars en tom mallkopia, och kastar ett pågående utkast. Alla versioner finns kvar. Ingen brytare krävs: det ska alltid gå att gå tillbaka.
        /// </summary>
        public static SiteWriteResult RevertConversion(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, int expectedRevision, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null || page.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (!SitePageStore.IsConverted(db, pageId))
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, page.Revision, Message: "Sidan är inte omgjord.");
            var published = SitePageStore.GetPublished(db, pageId, ownerType, ownerId);
            if (published == null || published.SchemaVersion != SiteBlockSchema.ConvertedSchemaVersion)
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, page.Revision, Message: "Omvandlingen är inte publicerad. Använd Avbryt.");
            if (SitePageStore.ChildCount(db, pageId) > 0)
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, page.Revision, Message: HasChildrenMessage);

            var template = SiteTemplates.For(ownerType);
            var json = SitePageStore.EmptyContent(templateCopy: true);
            if (SitePageStore.LatestSchema1Version(db, pageId) is { } previous)
            {
                var c = SiteBlockSchema.Parse(previous.BlocksJson, SiteContentKind.TemplateCopy, page.VisibilityLevel, template);
                if (c.Ok) json = SiteBlockSchema.ToJson(c);   // en gammal version som inte längre går att läsa ersätts av en tom, aldrig av ett fel
            }
            var r = SitePageStore.PublishContent(db, pageId, ownerType, ownerId, expectedRevision, page.Title, json, actor.MemberId, actor.AsSupport, now);
            if (r.Outcome != SiteStoreOutcome.Ok) return From(r);
            RebuildRefs(db, facts, page, ownerType, ownerId, page.VisibilityLevel, includeDraft: false);
            Audit(db, ownerType, ownerId, actor, "page", pageId, "page-convert-revert", SitePageStore.Hash(page.Title, json), now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, r.Revision);
        }

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
                    case "documents": case "contact": case "events": case "news": case "about": case "competitions": case "records": case "ranges": case "champions": case "stats": yield return b.Data["title"]?.GetValue<string>(); break;
                    case "pagelink": yield return b.Data["label"]?.GetValue<string>(); break;
                    case "image": yield return b.Data["alt"]?.GetValue<string>(); yield return b.Data["caption"]?.GetValue<string>(); break;
                    case "links":
                        foreach (var it in b.Data["items"]!.AsArray()) yield return it!["label"]?.GetValue<string>();
                        break;
                }
            }
        }

        /// <summary>Skapa en fri sida med adress. Sluggen utelämnad = ur titeln (numrerad om upptagen).</summary>
        public static SiteWriteResult CreatePage(IDatabase db, ISiteOwnerFacts facts, string ownerType, int ownerId, string title,
            string? requestedSlug, SiteVisibility visibility, SiteActor actor, DateTime now, int? parentPageId = null)
        {
            title = (title ?? "").Trim();
            if (title.Length is 0 or > 120)
                return Invalid("title", "Titeln måste vara 1–120 tecken.");
            var hit = SitePersonalNumberGuard.FindInAny(new[] { title });
            if (hit != null) return new SiteWriteResult(SiteWriteStatus.PersonalNumber, Match: hit, Message: "Titeln ser ut att innehålla ett personnummer.");

            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return new SiteWriteResult(SiteWriteStatus.NotFound);

            // En undersida: föräldern måste vara en egen sida på egen nivå hos samma förening, och sidan får inte vara öppnare än den (ParentPageId kommer utifrån och prövas mot radens ägare).
            if (parentPageId != null)
            {
                var parent = SitePageStore.GetPage(db, parentPageId.Value, ownerType, ownerId);
                var parentError = SitePageTree.NewChildError(ownerType, ownerId, parent, parent == null ? 0 : SitePageStore.ChildCount(db, parent.Id), parent != null && SitePageStore.IsConverted(db, parent.Id));
                if (parentError != null) return Invalid("parent", parentError);
                if (!SitePageTree.ChildLevelOk(visibility, parent!.VisibilityLevel))
                    return Invalid("visibility", $"Sidan får inte vara öppnare än sidan den ligger under ({SlotLevelLabel(parent.VisibilityLevel)} eller strängare).");
            }

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

            var created = SitePageStore.Create(db, ownerType, ownerId, title, visibility, null, null, actor.MemberId, actor.AsSupport, now, parentPageId);
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
            if (!page.IsFree && (SiteBlockSchema.KindOf(false, blocksJson) == SiteContentKind.ConvertedPage ? SiteConversion.PageFloor(ownerType, page.TemplateKey!) : SiteTemplates.For(ownerType).MostOpenLevel(page.TemplateKey!)) is { } floor && vis < floor)
                return Invalid("visibility", "Texten får inte vara öppnare än sidan själv.");   // ett slot kan aldrig vara bredare än systemsidan

            // En undersida är aldrig öppnare än sin förälder (föräldern läses färskt: nivån gäller direkt, inte först vid publicering).
            if (page.IsFree && page.ParentPageId is int parentId)
            {
                var parent = SitePageStore.GetPage(db, parentId, ownerType, ownerId);
                if (parent != null && !SitePageTree.ChildLevelOk(vis, parent.VisibilityLevel))
                    return Invalid("visibility", $"Sidan får inte vara öppnare än sidan den ligger under ({SlotLevelLabel(parent.VisibilityLevel)} eller strängare).");
            }

            var v = Validate(db, facts, page, ownerType, ownerId, title, blocksJson, vis, allowPersonalNumber, out var content);
            if (v != null) return v;

            var json = SiteBlockSchema.ToJson(content!);
            var saved = SitePageStore.SaveDraft(db, pageId, ownerType, ownerId, expectedRevision, title, json, vis, actor.MemberId, actor.AsSupport, now);
            if (saved.Outcome != SiteStoreOutcome.Ok) return From(saved);

            RebuildRefs(db, facts, page, ownerType, ownerId, vis, includeDraft: true);
            if (allowPersonalNumber && SitePersonalNumberGuard.FindInAny(TextOf(title, content!)) != null)
                Audit(db, ownerType, ownerId, actor, "page", pageId, "personal-number-override", SitePageStore.Hash(title, json), now);
            var raised = 0;
            if (vis != page.VisibilityLevel)
            {
                Audit(db, ownerType, ownerId, actor, "page", pageId, "page-visibility", null, now);
                // Gör man en förälder strängare får undersidorna samma nivå (de kan inte vara öppnare än den). Klienten har frågat först; servern gör det alltid så att trädet aldrig bryter regeln.
                if (SitePageStore.ChildCount(db, pageId) > 0)   // en fri sida eller en omgjord Om-sida
                {
                    raised = SitePageStore.RaiseChildren(db, ownerType, ownerId, pageId, vis, actor.MemberId, now);
                    if (raised > 0) Audit(db, ownerType, ownerId, actor, "page", pageId, "children-visibility", null, now);
                }
            }
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, saved.Revision, Affected: raised);
        }

        /// <summary>
        /// Lägger en egen sida under en annan egen sida (<paramref name="newParentId"/>), eller gör den till egen nivå (null). Reglerna i <see cref="SitePageTree.ParentError"/>. Sidan läggs sist bland
        /// sina nya syskon; är den öppnare än den nya föräldern får den förälderns nivå (en undersida är aldrig öppnare), och ligger den i menyn men inte föräldern tas den ur menyn.
        /// Adressen rörs aldrig. <see cref="SiteWriteResult.Affected"/> = 1 när sidans nivå eller menyplats ändrades.
        /// </summary>
        public static SiteWriteResult SetParent(IDatabase db, int pageId, string ownerType, int ownerId, int? newParentId, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null || !page.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (newParentId == page.ParentPageId) return new SiteWriteResult(SiteWriteStatus.Ok, pageId, page.Revision);

            var affected = 0;
            if (newParentId != null)
            {
                var parent = SitePageStore.GetPage(db, newParentId.Value, ownerType, ownerId);
                var error = SitePageTree.ParentError(page, parent, parent == null ? 0 : SitePageStore.ChildCount(db, parent.Id, exceptPageId: pageId), SitePageStore.ChildCount(db, pageId) > 0, parent != null && SitePageStore.IsConverted(db, parent.Id));
                if (error != null) return Invalid("parent", error);
                if (!SitePageTree.ChildLevelOk(page.VisibilityLevel, parent!.VisibilityLevel))
                {
                    db.Execute("UPDATE dbo.SitePage SET Visibility = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", pageId, ownerType, ownerId, (byte)parent.Visibility);
                    Audit(db, ownerType, ownerId, actor, "page", pageId, "page-visibility", null, now);
                    affected++;
                }
                if (!ParentIsInMenu(db, ownerType, ownerId, parent) && SitePageStore.IsInMenu(db, ownerType, ownerId, pageId))
                {
                    SitePageStore.SetInMenu(db, pageId, ownerType, ownerId, false, now);
                    Audit(db, ownerType, ownerId, actor, "menu", pageId, "menu-remove", null, now);
                    affected++;
                }
            }
            // Till egen nivå går alltid: sidan är då en undersida (utan egna undersidor), och dess nivå och menyplats kan stå kvar som de är.
            SitePageStore.SetParent(db, pageId, ownerType, ownerId, newParentId, actor.MemberId, now);
            Audit(db, ownerType, ownerId, actor, "page", pageId, "page-move", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, page.Revision + 1, Affected: affected);
        }

        /// <summary>Flyttar sidan ett steg upp (<paramref name="direction"/> -1) eller ner (+1) bland sina syskon (samma förälder). Ligger den redan först eller sist ändras inget.</summary>
        public static SiteWriteResult MoveInOrder(IDatabase db, int pageId, string ownerType, int ownerId, int direction, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null || !page.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return new SiteWriteResult(SiteWriteStatus.NotFound);
            var ids = SitePageStore.Siblings(db, ownerType, ownerId, page.ParentPageId).Select(s => s.Id).ToList();
            var at = ids.IndexOf(pageId);
            var to = at + Math.Sign(direction);
            if (at < 0 || to < 0 || to >= ids.Count) return new SiteWriteResult(SiteWriteStatus.Ok, pageId, page.Revision);
            (ids[at], ids[to]) = (ids[to], ids[at]);
            SitePageStore.SetOrder(db, ownerType, ownerId, ids, now);
            Audit(db, ownerType, ownerId, actor, "page", pageId, "page-order", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, page.Revision);
        }

        /// <summary>Publicera utkastet (valideras om mot sidans aktuella synlighet).</summary>
        public static SiteWriteResult Publish(IDatabase db, ISiteOwnerFacts facts, int pageId, string ownerType, int ownerId, int expectedRevision,
            SiteActor actor, DateTime now, bool allowPersonalNumber = false)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null) return new SiteWriteResult(SiteWriteStatus.NotFound);
            if (page.SuspendedDate != null) return new SiteWriteResult(SiteWriteStatus.Suspended, pageId, page.Revision);
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
        /// Nödbromsen (sajtadmin): stoppar en sida eller häver stoppet. Kräver inget stödläge, ändrar inget innehåll och loggas i SiteAudit
        /// (<c>page-suspend</c> / <c>page-release</c>) i samma transaktion. Aktören är alltid sajtadmin (<c>AsSupport</c>).
        /// </summary>
        public static SiteWriteResult SetSuspended(IDatabase db, int pageId, string ownerType, int ownerId, bool suspended, SiteActor actor, DateTime now)
        {
            var before = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            var r = SitePageStore.SetSuspended(db, pageId, ownerType, ownerId, suspended, actor.MemberId, now);
            if (r.Outcome != SiteStoreOutcome.Ok) return From(r);
            if (before != null && (before.SuspendedDate != null) == suspended) return new SiteWriteResult(SiteWriteStatus.Ok, pageId, r.Revision);   // redan i det läget: ingen ny loggrad
            Audit(db, ownerType, ownerId, actor with { AsSupport = true }, "page", pageId, suspended ? "page-suspend" : "page-release", null, now);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, r.Revision);
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

        /// <summary>Ligger föräldern i menyn? En omgjord systemsida (Om) är alltid med i menyn (mallen äger den); en egen sida ligger där bara om ägaren lagt den dit.</summary>
        private static bool ParentIsInMenu(IDatabase db, string ownerType, int ownerId, SitePageRow parent) =>
            !parent.IsFree || SitePageStore.IsInMenu(db, ownerType, ownerId, parent.Id);

        /// <summary>Lägg en egen sida i menyn eller ta bort den därifrån (loggas). Sidan måste höra till ägaren.</summary>
        public static SiteWriteResult SetInMenu(IDatabase db, int pageId, string ownerType, int ownerId, bool inMenu, SiteActor actor, DateTime now)
        {
            var page = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (page == null || !page.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            // En undersida kan bara ligga i menyn om sidan den ligger under gör det (annars vore den onåbar i menyn).
            if (inMenu && page.ParentPageId is int parentId && SitePageStore.GetPage(db, parentId, ownerType, ownerId) is { } menuParent && !ParentIsInMenu(db, ownerType, ownerId, menuParent))
                return Invalid("menu", "Lägg först sidan den ligger under i menyn.");
            if (!SitePageStore.SetInMenu(db, pageId, ownerType, ownerId, inMenu, now)) return new SiteWriteResult(SiteWriteStatus.NotFound);
            Audit(db, ownerType, ownerId, actor, "menu", pageId, inMenu ? "menu-add" : "menu-remove", null, now);
            // Tas en förälder ur menyn tas dess undersidor ur den med (de hänger på föräldern i menyn).
            var removed = inMenu ? 0 : SitePageStore.RemoveChildrenFromMenu(db, ownerType, ownerId, pageId);
            return new SiteWriteResult(SiteWriteStatus.Ok, pageId, Affected: removed);
        }

        public static SiteWriteResult SoftDelete(IDatabase db, int pageId, string ownerType, int ownerId, int expectedRevision, SiteActor actor, DateTime now)
        {
            // En mallkopia (egen text på en systemsida) raderas aldrig: tömmer man den är systemsidan som förut. Bara fria sidor går att radera.
            var existing = SitePageStore.GetPage(db, pageId, ownerType, ownerId);
            if (existing == null || !existing.IsFree) return new SiteWriteResult(SiteWriteStatus.NotFound);
            // En sida med undersidor raderas inte förrän undersidorna är flyttade eller raderade (inget försvinner tyst med sin förälder).
            var kids = SitePageStore.ChildCount(db, pageId);
            if (kids > 0)
                return new SiteWriteResult(SiteWriteStatus.Invalid, pageId, existing.Revision, Errors: new[] { new SiteContentError("children", "Sidan har undersidor.") },
                    Message: $"Sidan har {kids} undersid{(kids == 1 ? "a" : "or")}. Flytta eller radera {(kids == 1 ? "den" : "dem")} först, så kan du radera sidan.", Affected: kids);
            var r = SitePageStore.SoftDelete(db, pageId, ownerType, ownerId, expectedRevision, actor.MemberId, now);
            if (r.Outcome == SiteStoreOutcome.Ok) Audit(db, ownerType, ownerId, actor, "page", pageId, "page-delete", null, now);
            return From(r);
        }

        // ── Hjälpare ──────────────────────────────────────────────────────────────

        private static SiteWriteResult? Validate(IDatabase db, ISiteOwnerFacts facts, SitePageRow page, string ownerType, int ownerId,
            string title, string blocksJson, SiteVisibility vis, bool allowPersonalNumber, out SiteContentResult? content)
        {
            var kind = SiteBlockSchema.KindOf(page.IsFree, blocksJson);
            content = null;
            // En vanlig sparning varken omvandlar eller gör tillbaka en sida: schema 2 får bara skrivas på en sida som är omvandlad just nu, och schema 1 aldrig på en som är det.
            // Omvandlingen och Lägg tillbaka har sina egna grindar (ConvertTemplatePage, RevertConversion).
            if (!page.IsFree)
            {
                var converted = SitePageStore.IsConverted(db, page.Id);
                if (kind == SiteContentKind.ConvertedPage && !converted)
                    return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision,
                        Errors: new[] { new SiteContentError("schema", "Sidan har inte gjorts om.") }, Message: "Sidan har inte gjorts om.");
                if (kind != SiteContentKind.ConvertedPage && converted)
                    return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision,
                        Errors: new[] { new SiteContentError("schema", "Sidan är omgjord. Använd Lägg tillbaka för att gå tillbaka till den vanliga sidan.") },
                        Message: "Sidan är omgjord. Använd Lägg tillbaka för att gå tillbaka till den vanliga sidan.");
            }
            content = SiteBlockSchema.Parse(blocksJson, kind, vis, SiteTemplates.For(ownerType), page.TemplateKey);
            if (!content.Ok)
                return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision, Errors: content.Errors, Message: content.Errors[0].Message);

            // En omgjord sida får aldrig tappa en obligatorisk panel (motioner, kalender, tävlingar, dokument, kretsens styrelse, inloggningsrutan …): den kan flyttas, inte tas bort.
            if (kind == SiteContentKind.ConvertedPage && page.TemplateKey != null)
            {
                var present = content.Blocks.Where(b => b.Type == "panel").Select(b => SiteBlockSchema.PanelKeyOf(b.Data)).ToHashSet();
                var missing = SiteConversion.MustKeep(ownerType, page.TemplateKey).Where(d => !present.Contains(d.Key)).ToList();
                if (missing.Count > 0)
                {
                    var msg = missing.Count == 1 ? $"\"{missing[0].Name}\" kan flyttas men inte tas bort från sidan."
                        : "Följande kan flyttas men inte tas bort från sidan: " + string.Join(", ", missing.Select(d => "\"" + d.Name + "\"")) + ".";
                    return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision, Errors: new[] { new SiteContentError("panel", msg) }, Message: msg);
                }
            }

            // Refererade dokument måste tillhöra RADENS ägare (id utifrån är aldrig betrott).
            var foreign = SiteBlockSchema.ExtractRefs(content).Where(r => r.Kind == "doc" && !facts.DocumentBelongsTo(r.Id, ownerType, ownerId)).ToList();
            if (foreign.Count > 0)
                return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision,
                    Errors: new[] { new SiteContentError("refs", "Ett valt dokument finns inte hos den här föreningen.") },
                    Message: "Ett valt dokument finns inte hos den här föreningen.");

            // Refererade bilder likaså: ett fil-id utifrån är aldrig betrott (en annan förenings bild, en raderad bild).
            var foreignFiles = SiteBlockSchema.ExtractRefs(content).Where(r => r.Kind == "file" && !SiteFiles.BelongsTo(db, r.Id, ownerType, ownerId)).ToList();
            if (foreignFiles.Count > 0)
                return new SiteWriteResult(SiteWriteStatus.Invalid, page.Id, page.Revision,
                    Errors: new[] { new SiteContentError("refs", "En vald bild finns inte längre hos den här föreningen. Välj en annan bild.") },
                    Message: "En vald bild finns inte längre hos den här föreningen. Välj en annan bild.");

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
                var c = SiteBlockSchema.Parse(v.BlocksJson, SiteBlockSchema.KindOf(page.IsFree, v.BlocksJson), vis, SiteTemplates.For(ownerType), page.TemplateKey);
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

        internal static void Audit(IDatabase db, string ownerType, int ownerId, SiteActor actor, string objectKind, int objectId, string action, byte[]? hash, DateTime now) =>
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
