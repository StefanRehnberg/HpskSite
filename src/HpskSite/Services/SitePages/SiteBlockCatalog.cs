using System.Text.Json.Nodes;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Ett blocks metadata för väljaren och redigeraren (ENDA källan: editorn har ingen egen lista). <see cref="Group"/> är ett index i <see cref="SiteBlockCatalog.Groups"/>,
    /// -1 = finns kvar för äldre sidor men erbjuds inte. <see cref="NeedsImage"/> = bildvalet görs före tillägget (bild, galleri). <see cref="Defaults"/> = blockets första innehåll,
    /// null när editorn måste bygga det (bild/galleri väljer fil, länk till en sida väljer sida).
    /// </summary>
    public sealed record SiteBlockMeta(string Type, int Group, string Icon, string Label, string Description, bool Slot, bool NeedsImage, JsonObject? Defaults, bool RegionOnly)
    {
        /// <summary>Hämtar blocket sitt innehåll vid varje besök (händelser, tävlingar, rekord …) i stället för att lagra det i sidan? Märket "Hämtas automatiskt" i redigeraren.</summary>
        public bool Live => Type is not ("text" or "heading" or "links" or "documents" or "signup" or "image" or "gallery" or "pagelink");
    }

    /// <summary>Ett uppgiftskort i väljaren ("Vad vill du göra?"): lägger ett eller flera block. <see cref="Note"/> säger när uppgiften egentligen görs på ett annat ställe än på sidan.</summary>
    public sealed record SiteBlockTask(string Key, string Title, string Description, string Icon, IReadOnlyList<string> Blocks, string? Note = null);

    /// <summary>
    /// Blockregistret: grupper, etiketter, ikoner, beskrivningar, standardinnehåll och uppgiftskort, per ägartyp (klubb eller krets). Skickas som <c>State.blockTypes</c> /
    /// <c>blockGroups</c> / <c>blockTasks</c>, och förhandsvisningens/"Vem ser sidan?"-tabellens blocknamn läses härifrån, så ett block heter samma sak överallt.
    /// <see cref="SiteBlockSchema.Types"/> bestämmer vad som FÅR finnas (fält, nivå, plats); registret bestämmer hur det PRESENTERAS. Ett prov binder dem ihop: en typ utan
    /// registerrad (eller tvärtom) fäller bygget. Ren kod.
    /// </summary>
    public static class SiteBlockCatalog
    {
        /// <summary>Grupperna följer besökarens vy (vad besökaren ser), inte hur blocket är byggt. Sista gruppen heter olika för klubb och krets.</summary>
        public static IReadOnlyList<string> Groups(string ownerType) => new[]
        {
            "Text och bilder", "Kontakt", "Händelser", "Länkar och dokument", "Tävlingar och resultat", ownerType == SiteTemplates.Region ? "Kretsen" : "Klubben",
        };

        private static JsonObject O(params (string Key, JsonNode? Value)[] f)
        {
            var o = new JsonObject();
            foreach (var (k, v) in f) o[k] = v;
            return o;
        }

        /// <summary>Alla blocktyper (utom panelen, som hör till omgjorda sidor och har sin egen katalog), i väljarens ordning, för ägartypen. Kretsblock saknas för en klubb.</summary>
        public static IReadOnlyList<SiteBlockMeta> For(string ownerType)
        {
            var region = ownerType == SiteTemplates.Region;
            var who = region ? "kretsens" : "klubbens";
            var whoU = region ? "Kretsens" : "Klubbens";
            var all = new List<SiteBlockMeta>
            {
                new("text", 0, "bi-text-paragraph", "Text", "Egen text med rubriker, listor och bilder.", true, false, O(("html", "")), false),
                new("heading", 0, "bi-type-h2", "Rubrik", "En mellanrubrik som delar upp sidan.", false, false, O(("text", "Ny rubrik"), ("level", 2)), false),   // servern vägrar en tom rubrik vid autospar: en färdig text som markeras när blocket får fokus, så man skriver över den
                new("image", 0, "bi-image", "Bild", "En bild med bildtext.", false, true, null, false),
                new("gallery", 0, "bi-images", "Bildgalleri", "Flera bilder som miniatyrer. Ett klick öppnar bilden i full storlek.", false, true, null, false),
                new("about", 0, "bi-info-circle", region ? "Om kretsen" : "Om klubben", $"Presentationstexten. Den skrivs under {whoU} uppgifter och hämtas vid varje besök.", false, false, O(("title", "")), false),
                new("contact", 1, "bi-person-lines-fill", "Kontaktuppgifter", "Kontaktperson, e-post och telefon. Visas aldrig för alla.", false, false, O(("title", "Kontakt")), false),
                new("signup", 1, "bi-clipboard-check", "Anmälan / ansökan", "Ett formulär där besökare anmäler sig till en kurs eller ansöker om medlemskap. Ni hanterar anmälningarna under Anmälningar.", false, false,
                    O(("title", "Anmälan"), ("intro", ""), ("choices", new JsonArray())), false),
                new("events", 2, "bi-calendar-event", "Kommande händelser", "De närmaste händelserna i kalendern.", false, false, O(("title", "Kommande händelser"), ("count", 5)), false),
                new("news", 2, "bi-newspaper", "Senaste nyheterna", "De senaste nyheterna, med länk till alla.", true, false, O(("title", "Senaste nyheterna"), ("count", 5)), false),
                new("links", 3, "bi-link-45deg", "Länkar", "Knappar som leder till andra webbplatser.", true, false, O(("items", new JsonArray(O(("label", ""), ("url", ""))))), false),
                new("documents", 3, "bi-file-earmark-text", "Dokument", "Utvalda dokument ur dokumentarkivet.", false, false, O(("title", ""), ("refs", new JsonArray())), false),
                new("pagelink", 3, "bi-box-arrow-up-right", "Länk till en sida", $"En knapp till en av {who} andra sidor.", false, false, null, false),
                new("competitions", 4, "bi-trophy", "Kommande tävlingar", "De närmaste tävlingarna med datum och länk.", false, false, O(("title", "Kommande tävlingar"), ("count", 5)), false),
                new("staff-seeking", 4, "bi-clipboard-plus", "Funktionärer sökes", $"Tävlingar som söker funktionärer bland {(region ? "kretsens klubbar" : "klubbens medlemmar")}, med länk till anmälan.", false, false, O(("title", "Funktionärer sökes"), ("count", 5)), false),
                new("records", 4, "bi-award", region ? "Kretsrekord" : "Klubbrekord", "Gällande rekord per gren och klass.", false, false, O(("title", region ? "Kretsrekord" : "Klubbrekord")), false),
                new("champions", 4, "bi-trophy-fill", region ? "Regerande kretsmästare" : "Regerande klubbmästare", "Senaste årets mästare per gren och klass. Visas aldrig för alla, bara för inloggade eller strängare.", false, false,
                    O(("title", region ? "Regerande kretsmästare" : "Regerande klubbmästare")), false),
                new("stats", 5, "bi-bar-chart", region ? "Kretsen i siffror" : "Klubben i siffror", region ? "Antal klubbar, banor, serier och kommande tävlingar." : "Antal medlemmar, banor och kommande tävlingar.", false, false, O(("title", "")), false),
                new("ranges", 5, "bi-geo-alt", "Skjutbanor", region ? "Banorna kretsens klubbar använder." : "Klubbens banor, utan karta.", false, false, O(("title", "Skjutbanor")), false),
                new("region-clubs", 5, "bi-people", "Klubbar i kretsen", "Kretsens klubbar med länk till deras sidor.", false, false, O(("title", "Klubbar i kretsen")), true),
                new("region-series", 5, "bi-trophy", "Serier", "Kretsens egna serier.", false, false, O(("title", "Serier"), ("count", 10)), true),
                // Äldre block: nya sidor väljer Skjutbanor/Kretsen i siffror ovan; typerna finns kvar så att befintliga block ritas och får ett namn.
                new("region-ranges", -1, "bi-geo-alt", "Skjutbanor", "", false, false, O(("title", "Skjutbanor")), true),
                new("region-stats", -1, "bi-bar-chart", "Kretsen i siffror", "", false, false, O(("title", "")), true),
            };
            return all.Where(m => !m.RegionOnly || region).ToList();
        }

        /// <summary>Blockets namn i väljaren, redigeraren och "Vem ser sidan?" (ett namn överallt). Okänd typ: typnamnet.</summary>
        public static string LabelOf(string type, string ownerType) => For(ownerType).FirstOrDefault(m => m.Type == type)?.Label ?? type;

        /// <summary>
        /// Uppgiftskorten överst i väljaren. En uppgift lägger ett eller flera block med rätt förinställningar och går genom samma väg som ett vanligt tillägg (golv, plats, tillgänglighet).
        /// Uppgifter som egentligen görs på ett annat ställe (nyheter skrivs under Nyheter) säger det i <see cref="SiteBlockTask.Note"/>.
        /// </summary>
        public static IReadOnlyList<SiteBlockTask> Tasks(string ownerType)
        {
            var region = ownerType == SiteTemplates.Region;
            var list = new List<SiteBlockTask>
            {
                new("write", "Skriva egen text", "Rubriker, stycken, listor och bilder i egen text.", "bi-text-paragraph", new[] { "text" }),
                new("news", "Visa de senaste nyheterna", "De senaste nyheterna visas på sidan.", "bi-newspaper", new[] { "news" },
                    "Nyheterna skriver ni under Nyheter på " + (region ? "kretsens" : "klubbens") + " sida. Här visas de."),
                new("competitions", "Visa kommande tävlingar och händelser", "Datum och länkar till det som är på gång.", "bi-trophy", new[] { "competitions", "events" }),
                new("staff", "Söka funktionärer", "Visar tävlingar som söker funktionärer, med länk till anmälan.", "bi-clipboard-plus", new[] { "staff-seeking" },
                    "Listan visas när en tävling söker funktionärer. Ni öppnar anmälan under Hitta funktionärer i tävlingens planering."),
                new("signup", "Ta emot anmälningar eller nya medlemmar", "Ett formulär för kurser eller medlemskap.", "bi-clipboard-check", new[] { "signup" }),
                new("contact", "Visa kontaktuppgifter", "Kontaktperson, e-post och telefon (visas aldrig för alla).", "bi-person-lines-fill", new[] { "contact" }),
                new("documents", "Lägga ut dokument", "Blanketter, protokoll och inbjudningar att ladda ned.", "bi-file-earmark-text", new[] { "documents" }),
                new("pictures", "Visa bilder", "Ett galleri med miniatyrer.", "bi-images", new[] { "gallery" }),
            };
            if (region) list.Add(new("clubs", "Visa klubbarna i kretsen", "Kretsens klubbar med länk till deras sidor.", "bi-people", new[] { "region-clubs" }));
            return list;
        }
    }
}
