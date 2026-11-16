using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace HpskSite.Services.SitePages
{
    /// <summary>En tittare man kan se sidan "som". Fasta personer — aldrig den som tittar själv (adminens egen roll och medlemskap får inte påverka bilden).</summary>
    public sealed record PreviewPersona(string Key, string Label, SiteViewer Viewer);

    /// <summary>Vad "Se som" visar: beslutet för tittaren, en förklaring på svenska och (när sidan visas) ett komplett HTML-dokument att lägga i en sandlådad iframe.</summary>
    public sealed record PreviewResult(SitePageDecision Decision, string Notice, string Document);

    /// <summary>
    /// "Se som" (klubbsidorna 2b, plan v2 §5): serverrenderad förhandsvisning av en sida för en vald tittare — Utloggad, Inloggad (inte medlem), Medlem, Styrelse. Renderas
    /// med samma <see cref="SiteBlockRenderer"/> och samma <see cref="SitePageAccessRules"/> som besökarna får, mot de FASTA personerna nedan. Aldrig en parameter på den publika
    /// adressen: förhandsvisningen går genom en POST i Hemsidan och svaret är <c>no-store</c>. Ren kod.
    /// </summary>
    public static class SitePagePreview
    {
        /// <summary>Personerna för ägartypen. Kretsen har även "Medlem i en klubb i kretsen" (2d).</summary>
        public static IReadOnlyList<PreviewPersona> Personas(string ownerType) => ownerType == SiteTemplates.Region
            ? new[]
            {
                new PreviewPersona("anon", "Utloggad besökare", SiteViewer.Anonymous),
                new PreviewPersona("logged", "Inloggad, inte medlem i någon klubb i kretsen", new SiteViewer(true, false, false, false)),
                new PreviewPersona("member", "Medlem i en klubb i kretsen", new SiteViewer(true, true, false, false)),
                new PreviewPersona("board", "Kretsstyrelse", new SiteViewer(true, false, true, false)),
            }
            : new[]
            {
                new PreviewPersona("anon", "Utloggad besökare", SiteViewer.Anonymous),
                new PreviewPersona("logged", "Inloggad, inte medlem", new SiteViewer(true, false, false, false)),
                new PreviewPersona("member", "Medlem i klubben", new SiteViewer(true, true, false, false)),
                new PreviewPersona("board", "Styrelseledamot", new SiteViewer(true, true, true, false)),
            };

        public static PreviewPersona? Persona(string ownerType, string? key) => Personas(ownerType).FirstOrDefault(p => p.Key == key);

        private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

        /// <summary>
        /// Bygger förhandsvisningen. <paramref name="content"/> är redan tolkat mot sidans synlighet. Utloggad på en stängd sida: inloggning eller "finns inte" precis som
        /// besökaren; synlig sida: dokumentet, eller (om inget block syns) ett besked om det.
        /// </summary>
        /// <summary>Vad en tittare får när sidan är låst: sidan finns i menyn med ett hänglås och visar en ruta i stället för innehållet.</summary>
        private static string LockedNotice(PreviewPersona persona) => persona.Viewer.LoggedIn
            ? $"{persona.Label} ser sidan i menyn med ett hänglås och får beskedet att sidan är för medlemmar."
            : $"{persona.Label} ser sidan i menyn med ett hänglås och får en ruta med Logga in i stället för innehållet.";

        public static PreviewResult Build(string ownerType, string title, SiteVisibility pageVisibility, SiteContentResult content, PreviewPersona persona, ISiteBlockData data, string ownerUrl, PreviewStyle? style = null)
        {
            var template = SiteTemplates.For(ownerType);
            var def = new SitePageDef(SitePageRequest.FreePageKey, "x", title, 0, new[] { new SiteBlockDef("page", pageVisibility) });
            var decision = SitePageAccessRules.Decide(template, def, persona.Viewer);
            switch (decision)
            {
                case SitePageDecision.Locked:
                    return new PreviewResult(decision, LockedNotice(persona), "");
                case SitePageDecision.NotFound:
                    return new PreviewResult(decision, $"{persona.Label} får beskedet att sidan inte finns. Ingen kan se att den finns.", "");
            }
            var ctx = new SiteRenderContext(persona.Viewer, ownerUrl, template, data);
            var body = SiteBlockRenderer.RenderAll(content.Blocks, pageVisibility, ctx);
            if (body.Length == 0)
                return new PreviewResult(decision, $"{persona.Label} kan öppna sidan men ser inget av innehållet: alla block är för någon annan.", Document(title, "", style));
            return new PreviewResult(decision, $"Så ser {persona.Label.ToLowerInvariant()} sidan.", Document(title, body, style));
        }

        /// <summary>
        /// Förhandsvisning av en systemsidas EGEN TEXT före och efter sidans vanliga innehåll (slots). Sidans vanliga innehåll är levande data med session och ritas inte här: det visas som ett
        /// namnkort mellan texterna. Åtkomsten prövas mot systemsidan själv (samma regel som besökarna), blocken mot textens egen nivå.
        /// </summary>
        public static PreviewResult BuildSlots(string ownerType, string templateKey, SiteVisibility copyVisibility, SiteContentResult content, PreviewPersona persona, ISiteBlockData data, string ownerUrl, PreviewStyle? style = null)
        {
            var template = SiteTemplates.For(ownerType);
            var def = template.PageByKey(templateKey) ?? throw new ArgumentException("Okänd sida: " + templateKey, nameof(templateKey));
            switch (SitePageAccessRules.Decide(template, def, persona.Viewer))
            {
                case SitePageDecision.Locked: return new PreviewResult(SitePageDecision.Locked, LockedNotice(persona), "");
                case SitePageDecision.NotFound: return new PreviewResult(SitePageDecision.NotFound, $"{persona.Label} får beskedet att sidan inte finns. Ingen kan se att den finns.", "");
            }
            var ctx = new SiteRenderContext(persona.Viewer, ownerUrl, template, data);
            var before = SiteBlockRenderer.RenderAll(content.Before, copyVisibility, ctx);
            var after = SiteBlockRenderer.RenderAll(content.After, copyVisibility, ctx);
            var card = "<div class=\"card mb-3\"><div class=\"card-body\"><div class=\"fw-semibold\">" + Encoder.Encode(def.Label) + "</div><div class=\"text-muted small\">Sidans vanliga innehåll visas här. Det ändras inte.</div></div></div>";
            var notice = before.Length + after.Length == 0
                ? $"{persona.Label} ser sidans vanliga innehåll, men ingen av texterna före eller efter (de är för någon annan eller tomma)."
                : $"Så ser {persona.Label.ToLowerInvariant()} sidan. Sidans vanliga innehåll visas bara som ett namnkort.";
            return new PreviewResult(SitePageDecision.Serve, notice, Document(def.Label, before + card + after, style));
        }

        /// <summary>
        /// Förhandsvisning av en OMGJORD vanlig sida (K5a). Åtkomsten prövas mot mallen med sidan utbytt mot den omgjorda (samma regel som besökarna), blocken ritas genom
        /// <see cref="SiteConvertedPages.Plan"/> — samma loop och samma <see cref="SiteBlockRenderer.PanelVisible"/> som den riktiga sidan. En panel är en delvy med
        /// levande data och ritas inte här: den visas som ett kort med sitt namn och vad den visar, men bara för den tittare som får se den.
        /// </summary>
        public static PreviewResult BuildConverted(string ownerType, string title, string templateKey, SiteVisibility pageVisibility, SiteContentResult content,
            PreviewPersona persona, ISiteBlockData data, string ownerUrl, PreviewStyle? style = null)
        {
            var converted = new ConvertedPage(templateKey, ConvertedState.Ok, pageVisibility, false, content);
            var template = SiteConvertedPages.Apply(SiteTemplates.For(ownerType), new Dictionary<string, ConvertedPage> { [templateKey] = converted });
            var def = template.PageByKey(templateKey) ?? throw new ArgumentException("Okänd sida: " + templateKey, nameof(templateKey));
            var decision = SitePageAccessRules.Decide(template, def, persona.Viewer);
            switch (decision)
            {
                case SitePageDecision.Locked:
                    return new PreviewResult(decision, LockedNotice(persona), "");
                case SitePageDecision.NotFound:
                    return new PreviewResult(decision, $"{persona.Label} får beskedet att sidan inte finns. Ingen kan se att den finns.", "");
            }
            var plan = SiteConvertedPages.Plan(converted, new SiteRenderContext(persona.Viewer, ownerUrl, template, data));
            var sb = new System.Text.StringBuilder();
            foreach (var item in plan.Items)
            {
                if (item.PanelKey == null) { sb.Append(item.Html); continue; }
                var d = PanelCatalog.Find(ownerType, item.PanelKey);
                sb.Append("<div class=\"card mb-3\"><div class=\"card-body\"><div class=\"fw-semibold\">").Append(Encoder.Encode(d?.Name ?? item.PanelKey))
                  .Append("</div><div class=\"text-muted small\">").Append(Encoder.Encode(d?.Description ?? "")).Append("</div></div></div>");
            }
            if (sb.Length == 0)
                return new PreviewResult(decision, $"{persona.Label} kan öppna sidan men ser inget av innehållet: alla block är för någon annan.", Document(title, "", style));
            return new PreviewResult(decision, $"Så ser {persona.Label.ToLowerInvariant()} sidan. Rutor med automatiskt innehåll visas här bara med namn.", Document(title, sb.ToString(), style));
        }

        /// <summary>En kolumn i "Vem ser sidan?": en av de fasta tittarna.</summary>
        public sealed record VisibilityColumn(string Key, string Label);

        /// <summary>En rad: sidan i menyn, en panel eller ett eget block. <c>States</c> har ett tillstånd per kolumn: "see" (ser), "lock" (ser menyvalet men får en låsruta) eller "no" (ser inte).</summary>
        public sealed record VisibilityRow(string Kind, string Label, string Detail, IReadOnlyList<string> States);

        public sealed record VisibilityMatrix(IReadOnlyList<VisibilityColumn> Columns, IReadOnlyList<VisibilityRow> Rows, string Summary);

        private static string BlockDetail(SiteBlock b)
        {
            string? s = b.Type switch
            {
                "heading" => b.Data["text"]?.GetValue<string>(),
                "text" => System.Text.RegularExpressions.Regex.Replace(b.Data["html"]?.GetValue<string>() ?? "", "<[^>]+>", " "),
                "image" => b.Data["caption"]?.GetValue<string>() ?? b.Data["alt"]?.GetValue<string>(),
                "gallery" => b.Data["title"]?.GetValue<string>() is { Length: > 0 } gt ? gt : SiteBlockSchema.GalleryItems(b).Count + " bilder",
                _ => null,
            };
            s = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > 48 ? s[..48].TrimEnd() + "…" : s;
        }

        /// <summary>
        /// "Vem ser sidan?" (ersätter förhandsvisningen med namnkort): för varje fast tittare, vad hen får se av sidan. Sidans egen rad kommer ur
        /// <see cref="SitePageAccessRules.Decide"/> och varje block/panel ur <see cref="SiteBlockRenderer.BlockVisible"/> — samma regler som besökarna möter, utan att rita något.
        /// <paramref name="templateKey"/> satt = en vanlig sida (omgjord eller standard); null = en fri sida. Ren kod.
        /// </summary>
        public static VisibilityMatrix Matrix(string ownerType, string title, string? templateKey, SiteVisibility pageVisibility, SiteContentResult content)
        {
            var personas = Personas(ownerType);
            SiteTemplate template; SitePageDef def;
            if (templateKey != null)
            {
                var converted = new ConvertedPage(templateKey, ConvertedState.Ok, pageVisibility, false, content);
                template = SiteConvertedPages.Apply(SiteTemplates.For(ownerType), new Dictionary<string, ConvertedPage> { [templateKey] = converted });
                def = template.PageByKey(templateKey) ?? throw new ArgumentException("Okänd sida: " + templateKey, nameof(templateKey));
            }
            else
            {
                template = SiteTemplates.For(ownerType);
                def = new SitePageDef(SitePageRequest.FreePageKey, "x", title, 0, new[] { new SiteBlockDef("page", pageVisibility) });
            }
            var decisions = personas.Select(p => SitePageAccessRules.Decide(template, def, p.Viewer)).ToArray();
            var rows = new List<VisibilityRow>
            {
                new("page", "Sidan i menyn", "", decisions.Select(d => d switch { SitePageDecision.Serve => "see", SitePageDecision.Locked => "lock", _ => "no" }).ToList()),
            };
            foreach (var b in content.Blocks)
            {
                var isPanel = b.Type == "panel";
                var label = isPanel ? (PanelCatalog.Find(ownerType, SiteBlockSchema.PanelKeyOf(b.Data) ?? "")?.Name ?? "Del av sidan") : SiteBlockCatalog.LabelOf(b.Type, ownerType);   // ett namn överallt: samma register som väljaren och redigeraren
                rows.Add(new(isPanel ? "panel" : "block", label, isPanel ? "" : BlockDetail(b),
                    personas.Select((p, i) => decisions[i] == SitePageDecision.Serve && SiteBlockRenderer.BlockVisible(b, pageVisibility, ownerType, p.Viewer) ? "see" : "no").ToList()));
            }
            var anon = decisions[0];
            var summary = anon switch
            {
                SitePageDecision.Serve => "Utloggade kan öppna sidan.",
                SitePageDecision.Locked => "Utloggade ser sidan i menyn med ett hänglås och en ruta med Logga in i stället för innehållet.",
                _ => "Utloggade ser inte sidan alls.",
            };
            return new VisibilityMatrix(personas.Select(p => new VisibilityColumn(p.Key, p.Label)).ToList(), rows, summary);
        }

        /// <summary>
        /// Ett komplett dokument för en <c>&lt;iframe sandbox srcdoc&gt;</c>. Titeln kodas; brödtexten är redan renderarens (kodad och sanerad). Utan <paramref name="style"/> laddas bara
        /// bootstrap-stilen; med den även sajtens tema, ikoner och föreningens färg. Inga skript. Redigerarens iframe har <c>sandbox="allow-same-origin"</c> (utan allow-scripts, allow-forms och
        /// allow-popups): det ger bilderna sin inloggade session men inget skript, inget formulär och ingen navigering kan köras i den.
        /// </summary>
        public static string Document(string title, string bodyHtml, PreviewStyle? style = null)
        {
            // Sajtens egna stilar (theme.css har bland annat .site-block) och klubbens valda färg (samma CSS som besökarsidan får, ur en vitlista) så att förhandsvisningen ser ut som sidan.
            // Ljust eller mörkt läge följer redigeraren. Inga skript: iframen har aldrig allow-scripts.
            var theme = style?.Theme == "dark" ? "dark" : "light";
            var css = style == null || string.IsNullOrEmpty(style.PaletteCss) ? "" : "<style>" + style.PaletteCss + "</style>";
            var bodyClass = "bg-body" + (style != null && !string.IsNullOrEmpty(style.BodyClass) ? " " + style.BodyClass : "");
            return $"<!doctype html><html lang=\"sv\" data-bs-theme=\"{theme}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                "<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.8/dist/css/bootstrap.min.css\" rel=\"stylesheet\">" +
                (style == null ? "" : "<link href=\"https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css\" rel=\"stylesheet\"><link href=\"/css/theme.css\" rel=\"stylesheet\">") +
                css + $"</head><body class=\"{bodyClass} p-3\"><main class=\"container\" style=\"max-width:60rem\">" +
                $"<h1 class=\"h2 mb-4\">{Encoder.Encode(title)}</h1>{bodyHtml}</main></body></html>";
        }
    }

    /// <summary>Utseendet förhandsvisningen ritas med: klubbens palett-CSS (<see cref="SiteAppearance.Css"/>), klassen på &lt;body&gt; som CSS:en hänger på, och ljust/mörkt läge ("light"/"dark").</summary>
    public sealed record PreviewStyle(string PaletteCss, string BodyClass, string Theme);
}
