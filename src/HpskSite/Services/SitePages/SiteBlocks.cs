using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HpskSite.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Sidans innehållsform: en fri sida har block, en systemsidas kopia (copy-on-write) har bara slots före/efter panelen,
    /// en OMVANDLAD systemsida (schema 2, konvertera-planen v2 K1) har block där paneler (<c>panel</c>) och egna block blandas.
    /// </summary>
    public enum SiteContentKind { FreePage, TemplateCopy, ConvertedPage }

    /// <summary>Ett block. <see cref="Data"/> är normaliserat (okända fält avvisade, text sanerad).</summary>
    public sealed record SiteBlock(string Id, string Type, SiteVisibility Visibility, JsonObject Data);

    /// <summary>Ett fel i sidans innehåll. <see cref="Path"/> pekar ut platsen ("blocks[2].html"), <see cref="Message"/> är klartext till admin.</summary>
    public sealed record SiteContentError(string Path, string Message);

    /// <summary>Resultatet av <see cref="SiteBlockSchema.Parse"/>: antingen innehåll eller fel, aldrig båda.</summary>
    public sealed class SiteContentResult
    {
        public SiteContentKind Kind { get; init; }
        public IReadOnlyList<SiteBlock> Blocks { get; init; } = Array.Empty<SiteBlock>();
        public IReadOnlyList<SiteBlock> Before { get; init; } = Array.Empty<SiteBlock>();
        public IReadOnlyList<SiteBlock> After { get; init; } = Array.Empty<SiteBlock>();
        public IReadOnlyList<SiteContentError> Errors { get; init; } = Array.Empty<SiteContentError>();
        public bool Ok => Errors.Count == 0;

        /// <summary>Alla block i sidan, oavsett form.</summary>
        public IEnumerable<SiteBlock> AllBlocks => Blocks.Concat(Before).Concat(After);
    }

    /// <summary>
    /// Schemat för klubbens och kretsens egna sidor (klubbsidorna steg 2, plan v2 §3 och v3 A4). Servern omvaliderar vid
    /// skrivning OCH rendering (en lagrad rad är data från en tidigare version av koden, inte en betrodd källa).
    ///
    /// <para>Former: fri sida <c>{"schema":1,"blocks":[…]}</c>, mallkopia <c>{"schema":1,"slots":{"before":[…],"after":[…]}}</c>.
    /// Ett block är <c>{"id","type","visibility","data":{…}}</c>. Okända typer, okända fält, <c>style</c>, <c>&lt;script</c>,
    /// svagare synlighet än sidans, personblock satt till Alla och för stort djup avvisas.</para>
    ///
    /// <para>Ren kod, ingen databas. Id:n som blocken refererar (<c>doc:12</c>) prövas mot radens ägare i tjänsten
    /// (databasen), aldrig här — här kontrolleras bara formen. Bilder, nyheter och inbäddning kommer inte i 2a
    /// (inbäddning byggs inte alls, Stefans beslut 2026-11-07).</para>
    /// </summary>
    public static class SiteBlockSchema
    {
        public const int SchemaVersion = 1;

        /// <summary>Schema 2: en omvandlad systemsida, <c>{"schema":2,"blocks":[…]}</c> där <c>panel</c>-block är tillåtna. Schema 1 är oförändrat.</summary>
        public const int ConvertedSchemaVersion = 2;

        /// <summary>Schemanumret en lagrad form har.</summary>
        public static int SchemaOf(SiteContentKind kind) => kind == SiteContentKind.ConvertedPage ? ConvertedSchemaVersion : SchemaVersion;

        /// <summary>
        /// Formen en lagrad version har, ur sidan (fri eller mallkopia) och versionens JSON. EN plats som alla tolkningsställen använder:
        /// en mallkopia (sida med mallnyckel) vars JSON har <c>"schema":2</c> är en omvandlad sida; allt annat följer sidan som förut.
        /// En fri sida är aldrig omvandlad — schema 2 på en fri sida ger ett fel vid tolkningen, inte en tyst ny form.
        /// </summary>
        public static SiteContentKind KindOf(bool pageIsFree, string? json)
        {
            if (pageIsFree) return SiteContentKind.FreePage;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth + 4 }) is JsonObject o && Int(o["schema"]) == ConvertedSchemaVersion)
                        return SiteContentKind.ConvertedPage;
                }
                catch (JsonException) { /* Parse rapporterar felet */ }
            }
            return SiteContentKind.TemplateCopy;
        }

        /// <summary>Schemanumret i lagrad JSON (kolumnen <c>SitePageVersion.SchemaVersion</c> ska alltid säga samma som innehållet). Oläsligt = 1; Parse avvisar det.</summary>
        public static int SchemaNumberOf(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return SchemaVersion;
            try { return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth + 4 }) is JsonObject o ? Int(o["schema"]) ?? SchemaVersion : SchemaVersion; }
            catch (JsonException) { return SchemaVersion; }
        }

        public const int MaxBlocks = 40;
        public const int MaxJsonBytes = 400_000;
        public const int MaxDepth = 8;
        public const int MaxHtmlChars = 100_000;
        public const int MaxLinks = 20;
        public const int MaxDocuments = 50;

        private static readonly Regex IdRx = new("^[a-z0-9]{4,12}$", RegexOptions.Compiled);
        private static readonly Regex DocRefRx = new(@"^doc:[1-9]\d{0,8}$", RegexOptions.Compiled);
        private static readonly Regex FileRefRx = new(@"^file:[1-9]\d{0,8}$", RegexOptions.Compiled);

        /// <summary>Bildblockets fil-id (<c>file:12</c> → 12), eller null om blocket inte är ett giltigt bildblock.</summary>
        /// <summary>Alla bildfiler ett block visar: ett bildblocks fil, eller de bilder en text bäddar in.</summary>
        public static IReadOnlyList<int> FileIdsOf(SiteBlock b)
        {
            if (b.Type == "image") return FileIdOf(b) is int id ? new[] { id } : Array.Empty<int>();
            if (b.Type == "text") return SiteHtmlSanitizer.InlineFileIds(b.Data["html"]?.GetValue<string>());
            return Array.Empty<int>();
        }

        public static int? FileIdOf(SiteBlock b) =>
            b.Type == "image" && b.Data["ref"] is JsonValue v && v.TryGetValue<string>(out var r) && FileRefRx.IsMatch(r) ? int.Parse(r["file:".Length..]) : null;

        /// <param name="Retired">Utgången blocktyp: en sida som redan har den läses och sparas som förut (nyckeln finns kvar), men den ritas aldrig och erbjuds inte i väljaren.</param>
        public sealed record BlockType(string Name, bool Personal, bool AllowedInSlot, IReadOnlySet<string> Fields, bool RegionOnly = false, bool ConvertedOnly = false, bool Retired = false);

        /// <summary>Är typen utgången? EN fråga för ritningen, väljaren och State (<see cref="IsRetired(BlockType?)"/> för typen själv).</summary>
        public static bool IsRetired(string typeName) => Types.TryGetValue(typeName, out var t) && IsRetired(t);
        public static bool IsRetired(BlockType? type) => type is { Retired: true };

        /// <summary>
        /// GOLV-TABELLEN: det öppnaste en blocktyp får vara, per ägartyp. Ett personblock aldrig öppnare än Inloggade, övriga Alla (sidans egen nivå sätter taket).
        /// Skrivlagret (<c>Parse</c>), ritningen (<c>Effective</c>) och editorn (State.blockFloors) läser ALLA härifrån; ingen kopia någon annanstans.
        /// En typ som inte får finnas hos ägartypen (kretsblock på en klubb) saknar rad.
        /// </summary>
        public static IReadOnlyDictionary<string, SiteVisibility> FloorTable(string ownerType) =>
            Types.Values.Where(t => !t.ConvertedOnly && !t.Retired && (!t.RegionOnly || ownerType == SiteTemplates.Region))
                .ToDictionary(t => t.Name, t => IsPersonalBlock(t.Name, ownerType, null) ? SiteAccess.CapPersonal(SiteVisibility.Alla) : SiteVisibility.Alla, StringComparer.Ordinal);

        /// <summary>
        /// Panelblockets nyckel (<c>data.key</c>), eller null. Personuppgiftsnivån hör till PANELEN (registret), inte till blocktypen:
        /// <c>panel</c> är personblock bara när panelen visar personuppgifter.
        /// </summary>
        public static string? PanelKeyOf(JsonObject? data) => Str(data?["key"]);

        /// <summary>Visar blocket personuppgifter (kan aldrig bli öppnare än Inloggade)? Gäller blocktypen, och för <c>panel</c> panelens registerpost.</summary>
        public static bool IsPersonalBlock(string typeName, string ownerType, JsonObject? data)
        {
            if (typeName == "panel") return PanelKeyOf(data) is string k && PanelCatalog.Find(ownerType, k)?.PersonalData == true;
            return Types.TryGetValue(typeName, out var t) && t.Personal;
        }

        /// <summary>Panelens zon i lagrad form.</summary>
        public static string ZoneName(PanelZone z) => z switch { PanelZone.Main => "main", PanelZone.Side => "side", _ => "full" };

        public static readonly IReadOnlyDictionary<string, BlockType> Types = new[]
        {
            new BlockType("text", Personal: false, AllowedInSlot: true, Set("html")),
            new BlockType("heading", false, false, Set("text", "level")),
            new BlockType("links", false, true, Set("items")),
            new BlockType("documents", false, false, Set("title", "refs")),
            new BlockType("contact", Personal: true, false, Set("title")),   // klubbens kontaktperson: personuppgift, aldrig Alla
            new BlockType("events", false, false, Set("title", "count", "type")),
            new BlockType("pagelink", false, false, Set("key", "label")),
            new BlockType("image", false, false, Set("ref", "alt", "decorative", "caption")),
            new BlockType("region-clubs", false, false, Set("title"), RegionOnly: true),   // kretsens klubbar (live): namn och länk
            new BlockType("region-ranges", false, false, Set("title"), RegionOnly: true),   // skjutbanor kretsens klubbar använder (live, ur ClubRangeLink; begränsade banor utelämnas)
            new BlockType("region-series", false, false, Set("title", "count"), RegionOnly: true),   // kretsens egna serier (live)
            new BlockType("region-stats", false, false, Set("title"), RegionOnly: true),   // kretsens siffror: klubbar, banor, serier, kommande tävlingar
            new BlockType("news", false, true, Set("title", "count")),   // klubbens senaste nyheter (live, lagras inte); även före/efter en systemsida
            new BlockType("competitions", false, false, Set("title", "count")),   // föreningens kommande tävlingar (live ur tävlingslagringen, lagras inte)
            new BlockType("records", false, false, Set("title")),   // föreningens gällande rekord (live ur rekordtabellen, lagras inte)
            new BlockType("ranges", false, false, Set("title")),   // skjutbanor (klubbens egna respektive kretsens klubbars), utan karta; begränsade banor utelämnas
            new BlockType("champions", Personal: true, false, Set("title")),   // regerande mästare (live): namn på personer, aldrig Alla (som mästarpanelen, bara inloggade)
            new BlockType("stats", false, false, Set("title")),   // föreningens siffror (live): klubbens medlemmar, banor och tävlingar, eller kretsens klubbar, banor, serier och tävlingar
            new BlockType("about", false, false, Set("title")),   // föreningens presentationstext (live ur nodens uppgifter, lagras inte): första biblioteksblocket, samma text som Om-panelen
            new BlockType("panel", Personal: false, AllowedInSlot: false, Set("key", "zone"), ConvertedOnly: true),   // en systempanel på en omvandlad sida (schema 2); ritas av vyn ur registret, personnivån ur registret
        }.ToDictionary(t => t.Name, StringComparer.Ordinal);

        /// <summary>Händelsetyper ett händelseblock får filtrera på (klubbens vanliga typer; tävlingar har egna sidor).</summary>
        public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal) { "Möte", "Städning", "Socialt", "Nyhet" };

        private static IReadOnlySet<string> Set(params string[] f) => new HashSet<string>(f, StringComparer.Ordinal);

        /// <summary>
        /// Tolka och validera lagrad/inskickad JSON. <paramref name="pageVisibility"/> är sidans synlighet (blocket får inte
        /// vara öppnare), <paramref name="template"/> avgör vilka systemsidor ett <c>pagelink</c>-block får peka på.
        /// </summary>
        public static SiteContentResult Parse(string? json, SiteContentKind kind, SiteVisibility pageVisibility, SiteTemplate template, string? pageKey = null)
        {
            var errors = new List<SiteContentError>();
            JsonObject? root = null;

            if (string.IsNullOrWhiteSpace(json)) errors.Add(new("", "Innehållet saknas."));
            else if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) errors.Add(new("", "Sidan är för stor. Ta bort något innehåll."));
            else
            {
                try
                {
                    root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth + 4 }) as JsonObject;
                    if (root == null) errors.Add(new("", "Innehållet har fel form."));
                }
                catch (JsonException) { errors.Add(new("", "Innehållet kunde inte läsas.")); }
            }
            if (root == null) return Fail(kind, errors);

            var hasBlocks = kind != SiteContentKind.TemplateCopy;   // fri sida och omvandlad sida har block; mallkopian har platser
            var allowedRoot = hasBlocks ? new[] { "schema", "blocks" } : new[] { "schema", "slots" };
            foreach (var key in root.Select(p => p.Key).Where(k => !allowedRoot.Contains(k)))
                errors.Add(new(key, $"Okänt fält \"{key}\"."));
            if (Int(root["schema"]) != SchemaOf(kind))
                errors.Add(new("schema", "Okänd version av innehållet."));

            var blocks = new List<SiteBlock>();
            var before = new List<SiteBlock>();
            var after = new List<SiteBlock>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (kind == SiteContentKind.ConvertedPage && (pageKey == null || template.PageByKey(pageKey) is not { IsAdmin: false }))
                errors.Add(new("", "Sidan som omvandlats är okänd."));
            if (hasBlocks)
            {
                ReadBlocks(root["blocks"], "blocks", slot: false, pageVisibility, template, blocks, seen, errors, kind, pageKey);
            }
            else
            {
                if (root["slots"] is not JsonObject slots) errors.Add(new("slots", "Platserna före och efter saknas."));
                else
                {
                    foreach (var key in slots.Select(p => p.Key).Where(k => k != "before" && k != "after"))
                        errors.Add(new("slots." + key, $"Okänd plats \"{key}\"."));
                    ReadBlocks(slots["before"], "slots.before", slot: true, pageVisibility, template, before, seen, errors, kind, pageKey);
                    ReadBlocks(slots["after"], "slots.after", slot: true, pageVisibility, template, after, seen, errors, kind, pageKey);
                }
            }

            if (blocks.Count + before.Count + after.Count > MaxBlocks)
                errors.Add(new("", $"Högst {MaxBlocks} block per sida."));

            return new SiteContentResult { Kind = kind, Blocks = blocks, Before = before, After = after, Errors = errors };
        }

        private static SiteContentResult Fail(SiteContentKind kind, List<SiteContentError> errors) =>
            new() { Kind = kind, Errors = errors };

        private static void ReadBlocks(JsonNode? node, string path, bool slot, SiteVisibility pageVisibility, SiteTemplate template,
            List<SiteBlock> into, HashSet<string> seen, List<SiteContentError> errors, SiteContentKind kind, string? pageKey)
        {
            if (node == null) { return; } // en tom lista får utelämnas
            if (node is not JsonArray arr) { errors.Add(new(path, "Förväntade en lista med block.")); return; }
            if (arr.Count > MaxBlocks) { errors.Add(new(path, $"Högst {MaxBlocks} block per sida.")); return; }

            for (var i = 0; i < arr.Count; i++)
            {
                var p = $"{path}[{i}]";
                if (arr[i] is not JsonObject b) { errors.Add(new(p, "Blocket har fel form.")); continue; }
                var block = ReadBlock(b, p, slot, pageVisibility, template, seen, errors, kind, pageKey);
                if (block != null) into.Add(block);
            }
        }

        private static SiteBlock? ReadBlock(JsonObject b, string p, bool slot, SiteVisibility pageVisibility, SiteTemplate template,
            HashSet<string> seen, List<SiteContentError> errors, SiteContentKind kind, string? pageKey)
        {
            var before = errors.Count;
            foreach (var key in b.Select(x => x.Key).Where(k => k is not ("id" or "type" or "visibility" or "data")))
                errors.Add(new($"{p}.{key}", $"Okänt fält \"{key}\"."));

            var id = Str(b["id"]);
            if (id == null || !IdRx.IsMatch(id)) errors.Add(new($"{p}.id", "Blocket saknar ett giltigt id."));
            else if (!seen.Add(id)) errors.Add(new($"{p}.id", "Två block har samma id."));

            var typeName = Str(b["type"]);
            BlockType? type = null;
            if (typeName == null || !Types.TryGetValue(typeName, out type))
                errors.Add(new($"{p}.type", $"Okänd blocktyp \"{typeName}\"."));
            else if (type.RegionOnly && template.OwnerType != SiteTemplates.Region)
                errors.Add(new($"{p}.type", $"Blocktypen \"{typeName}\" finns bara på kretsens sidor."));
            else if (slot && !type.AllowedInSlot)
                errors.Add(new($"{p}.type", $"Blocktypen \"{typeName}\" får inte ligga före eller efter en systemsida."));
            else if (type.ConvertedOnly && kind != SiteContentKind.ConvertedPage)
                errors.Add(new($"{p}.type", $"Blocktypen \"{typeName}\" finns bara på en omvandlad systemsida."));

            // Panelens registerpost avgör personnivå och om den är en systempanel (login-prompt: lagringsbar, aldrig valbar).
            var bData = b["data"] as JsonObject;
            var panelDef = type?.Name == "panel" && PanelKeyOf(bData) is string pk ? PanelCatalog.Find(template.OwnerType, pk) : null;
            var personal = type != null && IsPersonalBlock(type.Name, template.OwnerType, bData);

            SiteVisibility visibility = SiteVisibility.Dold;
            if (Int(b["visibility"]) is not int v) errors.Add(new($"{p}.visibility", "Blocket saknar synlighet."));
            else
            {
                var outOnly = panelDef != null && pageKey != null && template.IsLoggedOutOnly(pageKey, panelDef.Key);   // mallen avgör vilka paneler som bara finns för utloggade
                var systemLoggedOut = outOnly && v == (int)SiteVisibility.Utloggad;
                if (outOnly && !systemLoggedOut) errors.Add(new($"{p}.visibility", "Systemrutan visas bara för den som inte är inloggad."));
                else if (!systemLoggedOut && (v < 0 || v > 4 || !SiteAccess.IsSelectable((SiteVisibility)v))) errors.Add(new($"{p}.visibility", "Ogiltig synlighet."));
                else
                {
                    visibility = (SiteVisibility)v;
                    if (SiteAccess.Strictest(visibility, pageVisibility) != visibility)
                        errors.Add(new($"{p}.visibility", "Ett block kan inte vara öppnare än sidan."));
                    if (personal && visibility == SiteVisibility.Alla)
                        errors.Add(new($"{p}.visibility", "Ett block med personuppgifter kan inte vara öppet för alla."));
                    // En panel visar data som klubben redan har valt hur öppen den är (mallen): ägaren kan göra den slutnare, aldrig öppnare än förut.
                    if (panelDef != null && pageKey != null && !outOnly)
                    {
                        var floor = SiteConversion.PanelFloor(template.OwnerType, pageKey, panelDef.Key);
                        if (SiteAccess.Strictest(visibility, floor) != visibility)
                            errors.Add(new($"{p}.visibility", $"\"{panelDef.Name}\" kan inte vara öppnare än den är idag."));
                    }
                }
            }

            JsonObject? data = null;
            if (b["data"] is not JsonObject d) errors.Add(new($"{p}.data", "Blocket saknar innehåll."));
            else if (type != null)
            {
                foreach (var key in d.Select(x => x.Key).Where(k => !type.Fields.Contains(k)))
                    errors.Add(new($"{p}.data.{key}", $"Okänt fält \"{key}\"."));
                data = NormalizeData(type, d, $"{p}.data", template, errors, pageKey, seen);
            }

            return errors.Count == before && id != null && type != null && data != null
                ? new SiteBlock(id, type.Name, visibility, data)
                : null;
        }

        private static JsonObject? NormalizeData(BlockType type, JsonObject d, string p, SiteTemplate template, List<SiteContentError> errors,
            string? pageKey = null, HashSet<string>? seen = null)
        {
            var before = errors.Count;
            var o = new JsonObject();
            switch (type.Name)
            {
                case "panel":
                {
                    var key = Str(d["key"]);
                    var def = key == null ? null : PanelCatalog.Find(template.OwnerType, key);
                    if (def == null) { errors.Add(new($"{p}.key", "Okänd panel.")); break; }
                    if (pageKey == null || !PanelCatalog.AllowedOn(template.OwnerType, pageKey, key!))
                    { errors.Add(new($"{p}.key", $"Panelen \"{def.Name}\" hör inte hemma på den här sidan.")); break; }
                    // En panel finns högst en gång på sidan. Nyckeln prefixas så att den aldrig kan krocka med ett block-id (id:n är bara a–z, 0–9).
                    if (seen != null && !seen.Add("panel:" + key)) { errors.Add(new($"{p}.key", $"Panelen \"{def.Name}\" finns redan på sidan.")); break; }
                    var zone = d["zone"] == null ? ZoneName(def.Zone) : Str(d["zone"]);
                    if (zone is not ("main" or "side" or "full")) { errors.Add(new($"{p}.zone", "Okänd plats på sidan.")); break; }
                    o["key"] = key;
                    o["zone"] = zone;
                    break;
                }
                case "text":
                {
                    var html = Str(d["html"]);
                    if (html == null) { errors.Add(new($"{p}.html", "Texten saknas.")); break; }
                    if (html.Length > MaxHtmlChars) { errors.Add(new($"{p}.html", "Texten är för lång.")); break; }
                    if (HasScript(html)) { errors.Add(new($"{p}.html", "Texten innehåller otillåtet innehåll.")); break; }
                    o["html"] = SiteHtmlSanitizer.Sanitize(html);
                    break;
                }
                case "heading":
                {
                    var text = Short(d["text"], $"{p}.text", 120, required: true, errors);
                    var level = d["level"] == null ? 2 : Int(d["level"]) ?? -1;
                    if (level is not (2 or 3)) errors.Add(new($"{p}.level", "Rubriknivån är 2 eller 3."));
                    if (text != null) o["text"] = text;
                    o["level"] = level;
                    break;
                }
                case "links":
                {
                    if (d["items"] is not JsonArray items || items.Count == 0) { errors.Add(new($"{p}.items", "Lägg till minst en länk.")); break; }
                    if (items.Count > MaxLinks) { errors.Add(new($"{p}.items", $"Högst {MaxLinks} länkar.")); break; }
                    var outItems = new JsonArray();
                    for (var i = 0; i < items.Count; i++)
                    {
                        var ip = $"{p}.items[{i}]";
                        if (items[i] is not JsonObject it) { errors.Add(new(ip, "Länken har fel form.")); continue; }
                        foreach (var key in it.Select(x => x.Key).Where(k => k is not ("label" or "url")))
                            errors.Add(new($"{ip}.{key}", $"Okänt fält \"{key}\"."));
                        var label = Short(it["label"], $"{ip}.label", 80, required: true, errors);
                        var url = Str(it["url"])?.Trim();
                        if (url == null || url.Length > 500 || !SiteHtmlSanitizer.IsAllowedLink(url))
                            errors.Add(new($"{ip}.url", "Länken måste börja med https://, mailto:, tel: eller /."));
                        else if (HasScript(url)) errors.Add(new($"{ip}.url", "Länken är inte tillåten."));
                        if (label != null && url != null) outItems.Add(new JsonObject { ["label"] = label, ["url"] = url });
                    }
                    o["items"] = outItems;
                    break;
                }
                case "documents":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    if (d["refs"] is not JsonArray refs || refs.Count == 0) { errors.Add(new($"{p}.refs", "Välj minst ett dokument.")); break; }
                    if (refs.Count > MaxDocuments) { errors.Add(new($"{p}.refs", $"Högst {MaxDocuments} dokument.")); break; }
                    var outRefs = new JsonArray();
                    var seenRefs = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < refs.Count; i++)
                    {
                        var r = Str(refs[i]);
                        if (r == null || !DocRefRx.IsMatch(r)) { errors.Add(new($"{p}.refs[{i}]", "Dokumentet anges som doc:<nummer>.")); continue; }
                        if (seenRefs.Add(r)) outRefs.Add(r);
                    }
                    if (title != null) o["title"] = title;
                    o["refs"] = outRefs;
                    break;
                }
                case "contact":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    if (title != null) o["title"] = title;
                    break;
                }
                case "events":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    var count = d["count"] == null ? 5 : Int(d["count"]) ?? -1;
                    if (count < 1 || count > 10) errors.Add(new($"{p}.count", "Antalet händelser är 1 till 10."));
                    // Valfri filtrering på händelsetyp ("Möte"): mallen "Möten och kallelser" visar bara möten. Fri text vore en ny inmatning att validera, så typen väljs ur en fast lista.
                    var evType = Str(d["type"]);
                    if (d["type"] != null && (evType == null || !EventTypes.Contains(evType))) errors.Add(new($"{p}.type", "Okänd händelsetyp."));
                    if (title != null) o["title"] = title;
                    if (evType != null) o["type"] = evType;
                    o["count"] = count;
                    break;
                }
                case "news":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    var count = d["count"] == null ? 5 : Int(d["count"]) ?? -1;
                    if (count < 1 || count > 10) errors.Add(new($"{p}.count", "Antalet nyheter är 1 till 10."));
                    if (title != null) o["title"] = title;
                    o["count"] = count;
                    break;
                }
                case "about":
                case "region-clubs":
                case "region-ranges":
                case "region-stats":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    if (title != null) o["title"] = title;
                    break;
                }
                case "competitions":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    var count = d["count"] == null ? 5 : Int(d["count"]) ?? -1;
                    if (count < 1 || count > 10) errors.Add(new($"{p}.count", "Antalet tävlingar är 1 till 10."));
                    if (title != null) o["title"] = title;
                    o["count"] = count;
                    break;
                }
                case "records":
                case "champions":
                case "stats":
                case "ranges":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    if (title != null) o["title"] = title;
                    break;
                }
                case "region-series":
                {
                    var title = Short(d["title"], $"{p}.title", 120, required: false, errors);
                    var count = d["count"] == null ? 10 : Int(d["count"]) ?? -1;
                    if (count < 1 || count > 20) errors.Add(new($"{p}.count", "Antalet serier är 1 till 20."));
                    if (title != null) o["title"] = title;
                    o["count"] = count;
                    break;
                }
                case "image":
                {
                    var r = Str(d["ref"]);
                    if (r == null || !FileRefRx.IsMatch(r)) { errors.Add(new($"{p}.ref", "Välj en bild.")); break; }
                    // Beskrivningen får vara tom i ett utkast (autosparningen) — editorn varnar vid publicering. "Bara dekoration" = ingen beskrivning behövs.
                    var alt = Short(d["alt"], $"{p}.alt", 200, required: false, errors);
                    var caption = Short(d["caption"], $"{p}.caption", 200, required: false, errors);
                    bool? decorative = d["decorative"] == null ? false : (d["decorative"] is JsonValue dv && dv.TryGetValue<bool>(out var dflag) ? dflag : null);
                    if (decorative == null) { errors.Add(new($"{p}.decorative", "Bara dekoration är ja eller nej.")); break; }
                    o["ref"] = r;
                    if (alt != null && decorative == false) o["alt"] = alt;
                    if (caption != null) o["caption"] = caption;
                    o["decorative"] = decorative.Value;
                    break;
                }
                case "pagelink":
                {
                    var key = Str(d["key"]);
                    var page = key == null ? null : template.PageByKey(key);
                    if (page == null || page.IsAdmin) { errors.Add(new($"{p}.key", "Välj en av sajtens sidor.")); break; }
                    var label = Short(d["label"], $"{p}.label", 80, required: false, errors);
                    o["key"] = page.Key;
                    if (label != null) o["label"] = label;
                    break;
                }
            }
            return errors.Count == before ? o : null;
        }

        /// <summary>Ett heltal, annars null (1.5, text och null ger null).</summary>
        private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static string? Short(JsonNode? n, string path, int max, bool required, List<SiteContentError> errors)
        {
            var s = Str(n)?.Trim();
            if (string.IsNullOrEmpty(s))
            {
                if (required) errors.Add(new(path, "Fältet får inte vara tomt."));
                return null;
            }
            if (s.Length > max) { errors.Add(new(path, $"Högst {max} tecken.")); return null; }
            if (s.Any(c => char.IsControl(c) || c is '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩'))
            { errors.Add(new(path, "Texten innehåller otillåtna tecken.")); return null; }
            if (s.Contains('<') || s.Contains('>')) { errors.Add(new(path, "Texten får inte innehålla < eller >.")); return null; }
            return s;
        }

        private static bool HasScript(string s) =>
            s.Contains("<script", StringComparison.OrdinalIgnoreCase) || s.Contains("javascript:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Id:n blocken refererar, som <c>(sort, id)</c>: underlaget för <c>SitePageRef</c> ("används dokumentet?") och för
        /// tjänstens ägarprövning. Sorten står alltid med (id-lagen).
        /// </summary>
        public static IReadOnlyList<(string Kind, int Id)> ExtractRefs(SiteContentResult content)
        {
            var refs = new HashSet<(string, int)>();
            foreach (var b in content.AllBlocks.Where(b => b.Type == "documents"))
                foreach (var r in b.Data["refs"]!.AsArray())
                    refs.Add(("doc", int.Parse(r!.GetValue<string>()["doc:".Length..])));
            foreach (var b in content.AllBlocks)
                foreach (var fid in FileIdsOf(b)) refs.Add(("file", fid));
            return refs.OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToList();
        }

        /// <summary>Normaliserad JSON att lagra: samma form som <see cref="Parse"/> läser.</summary>
        public static string ToJson(SiteContentResult content)
        {
            static JsonArray Arr(IEnumerable<SiteBlock> bs) => new(bs.Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id, ["type"] = b.Type, ["visibility"] = (int)b.Visibility, ["data"] = JsonNode.Parse(b.Data.ToJsonString())
            }).ToArray());

            var root = content.Kind != SiteContentKind.TemplateCopy
                ? new JsonObject { ["schema"] = SchemaOf(content.Kind), ["blocks"] = Arr(content.Blocks) }
                : new JsonObject { ["schema"] = SchemaVersion, ["slots"] = new JsonObject { ["before"] = Arr(content.Before), ["after"] = Arr(content.After) } };
            return root.ToJsonString();
        }
    }
}
