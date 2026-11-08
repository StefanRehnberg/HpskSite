using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HpskSite.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>Sidans innehållsform: en fri sida har block, en systemsidas kopia (copy-on-write) har bara slots före/efter panelen.</summary>
    public enum SiteContentKind { FreePage, TemplateCopy }

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
        public static int? FileIdOf(SiteBlock b) =>
            b.Type == "image" && b.Data["ref"] is JsonValue v && v.TryGetValue<string>(out var r) && FileRefRx.IsMatch(r) ? int.Parse(r["file:".Length..]) : null;

        public sealed record BlockType(string Name, bool Personal, bool AllowedInSlot, IReadOnlySet<string> Fields);

        public static readonly IReadOnlyDictionary<string, BlockType> Types = new[]
        {
            new BlockType("text", Personal: false, AllowedInSlot: true, Set("html")),
            new BlockType("heading", false, false, Set("text", "level")),
            new BlockType("links", false, true, Set("items")),
            new BlockType("documents", false, false, Set("title", "refs")),
            new BlockType("contact", Personal: true, false, Set("title")),   // klubbens kontaktperson: personuppgift, aldrig Alla
            new BlockType("events", false, false, Set("title", "count")),
            new BlockType("pagelink", false, false, Set("key", "label")),
            new BlockType("image", false, false, Set("ref", "alt", "decorative", "caption")),
            new BlockType("news", false, true, Set("title", "count")),   // klubbens senaste nyheter (live, lagras inte); även före/efter en systemsida
        }.ToDictionary(t => t.Name, StringComparer.Ordinal);

        private static IReadOnlySet<string> Set(params string[] f) => new HashSet<string>(f, StringComparer.Ordinal);

        /// <summary>
        /// Tolka och validera lagrad/inskickad JSON. <paramref name="pageVisibility"/> är sidans synlighet (blocket får inte
        /// vara öppnare), <paramref name="template"/> avgör vilka systemsidor ett <c>pagelink</c>-block får peka på.
        /// </summary>
        public static SiteContentResult Parse(string? json, SiteContentKind kind, SiteVisibility pageVisibility, SiteTemplate template)
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

            var allowedRoot = kind == SiteContentKind.FreePage ? new[] { "schema", "blocks" } : new[] { "schema", "slots" };
            foreach (var key in root.Select(p => p.Key).Where(k => !allowedRoot.Contains(k)))
                errors.Add(new(key, $"Okänt fält \"{key}\"."));
            if (Int(root["schema"]) != SchemaVersion)
                errors.Add(new("schema", "Okänd version av innehållet."));

            var blocks = new List<SiteBlock>();
            var before = new List<SiteBlock>();
            var after = new List<SiteBlock>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (kind == SiteContentKind.FreePage)
            {
                ReadBlocks(root["blocks"], "blocks", slot: false, pageVisibility, template, blocks, seen, errors);
            }
            else
            {
                if (root["slots"] is not JsonObject slots) errors.Add(new("slots", "Platserna före och efter saknas."));
                else
                {
                    foreach (var key in slots.Select(p => p.Key).Where(k => k != "before" && k != "after"))
                        errors.Add(new("slots." + key, $"Okänd plats \"{key}\"."));
                    ReadBlocks(slots["before"], "slots.before", slot: true, pageVisibility, template, before, seen, errors);
                    ReadBlocks(slots["after"], "slots.after", slot: true, pageVisibility, template, after, seen, errors);
                }
            }

            if (blocks.Count + before.Count + after.Count > MaxBlocks)
                errors.Add(new("", $"Högst {MaxBlocks} block per sida."));

            return new SiteContentResult { Kind = kind, Blocks = blocks, Before = before, After = after, Errors = errors };
        }

        private static SiteContentResult Fail(SiteContentKind kind, List<SiteContentError> errors) =>
            new() { Kind = kind, Errors = errors };

        private static void ReadBlocks(JsonNode? node, string path, bool slot, SiteVisibility pageVisibility, SiteTemplate template,
            List<SiteBlock> into, HashSet<string> seen, List<SiteContentError> errors)
        {
            if (node == null) { return; } // en tom lista får utelämnas
            if (node is not JsonArray arr) { errors.Add(new(path, "Förväntade en lista med block.")); return; }
            if (arr.Count > MaxBlocks) { errors.Add(new(path, $"Högst {MaxBlocks} block per sida.")); return; }

            for (var i = 0; i < arr.Count; i++)
            {
                var p = $"{path}[{i}]";
                if (arr[i] is not JsonObject b) { errors.Add(new(p, "Blocket har fel form.")); continue; }
                var block = ReadBlock(b, p, slot, pageVisibility, template, seen, errors);
                if (block != null) into.Add(block);
            }
        }

        private static SiteBlock? ReadBlock(JsonObject b, string p, bool slot, SiteVisibility pageVisibility, SiteTemplate template,
            HashSet<string> seen, List<SiteContentError> errors)
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
            else if (slot && !type.AllowedInSlot)
                errors.Add(new($"{p}.type", $"Blocktypen \"{typeName}\" får inte ligga före eller efter en systemsida."));

            SiteVisibility visibility = SiteVisibility.Dold;
            if (Int(b["visibility"]) is not int v) errors.Add(new($"{p}.visibility", "Blocket saknar synlighet."));
            else
            {
                if (v < 0 || v > 4 || !SiteAccess.IsSelectable((SiteVisibility)v)) errors.Add(new($"{p}.visibility", "Ogiltig synlighet."));
                else
                {
                    visibility = (SiteVisibility)v;
                    if (SiteAccess.Strictest(visibility, pageVisibility) != visibility)
                        errors.Add(new($"{p}.visibility", "Ett block kan inte vara öppnare än sidan."));
                    if (type is { Personal: true } && visibility == SiteVisibility.Alla)
                        errors.Add(new($"{p}.visibility", "Ett block med personuppgifter kan inte vara öppet för alla."));
                }
            }

            JsonObject? data = null;
            if (b["data"] is not JsonObject d) errors.Add(new($"{p}.data", "Blocket saknar innehåll."));
            else if (type != null)
            {
                foreach (var key in d.Select(x => x.Key).Where(k => !type.Fields.Contains(k)))
                    errors.Add(new($"{p}.data.{key}", $"Okänt fält \"{key}\"."));
                data = NormalizeData(type, d, $"{p}.data", template, errors);
            }

            return errors.Count == before && id != null && type != null && data != null
                ? new SiteBlock(id, type.Name, visibility, data)
                : null;
        }

        private static JsonObject? NormalizeData(BlockType type, JsonObject d, string p, SiteTemplate template, List<SiteContentError> errors)
        {
            var before = errors.Count;
            var o = new JsonObject();
            switch (type.Name)
            {
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
                    if (title != null) o["title"] = title;
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
            foreach (var b in content.AllBlocks.Where(b => b.Type == "image"))
                if (FileIdOf(b) is int fid) refs.Add(("file", fid));
            return refs.OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToList();
        }

        /// <summary>Normaliserad JSON att lagra: samma form som <see cref="Parse"/> läser.</summary>
        public static string ToJson(SiteContentResult content)
        {
            static JsonArray Arr(IEnumerable<SiteBlock> bs) => new(bs.Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id, ["type"] = b.Type, ["visibility"] = (int)b.Visibility, ["data"] = JsonNode.Parse(b.Data.ToJsonString())
            }).ToArray());

            var root = content.Kind == SiteContentKind.FreePage
                ? new JsonObject { ["schema"] = SchemaVersion, ["blocks"] = Arr(content.Blocks) }
                : new JsonObject { ["schema"] = SchemaVersion, ["slots"] = new JsonObject { ["before"] = Arr(content.Before), ["after"] = Arr(content.After) } };
            return root.ToJsonString();
        }
    }
}
