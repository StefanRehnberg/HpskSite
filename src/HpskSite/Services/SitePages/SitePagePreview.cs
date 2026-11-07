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
        /// <summary>Personerna för ägartypen. Kretsens medlemssynlighet finns inte i steg 2 (kommer med 2d), så kretsen har Utloggad, Inloggad och Kretsstyrelse.</summary>
        public static IReadOnlyList<PreviewPersona> Personas(string ownerType) => ownerType == SiteTemplates.Region
            ? new[]
            {
                new PreviewPersona("anon", "Utloggad besökare", SiteViewer.Anonymous),
                new PreviewPersona("logged", "Inloggad", new SiteViewer(true, false, false, false)),
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
        public static PreviewResult Build(string ownerType, string title, SiteVisibility pageVisibility, SiteContentResult content, PreviewPersona persona, ISiteBlockData data, string ownerUrl)
        {
            var template = SiteTemplates.For(ownerType);
            var def = new SitePageDef(SitePageRequest.FreePageKey, "x", title, 0, new[] { new SiteBlockDef("page", pageVisibility) });
            var decision = SitePageAccessRules.Decide(template, def, persona.Viewer);
            switch (decision)
            {
                case SitePageDecision.LoginRedirect:
                    return new PreviewResult(decision, $"{persona.Label} skickas till inloggningen och kan sedan läsa sidan om hen får.", "");
                case SitePageDecision.NotFound:
                    return new PreviewResult(decision, $"{persona.Label} får beskedet att sidan inte finns. Ingen kan se att den finns.", "");
            }
            var ctx = new SiteRenderContext(persona.Viewer, ownerUrl, template, data);
            var body = SiteBlockRenderer.RenderAll(content.Blocks, pageVisibility, ctx);
            if (body.Length == 0)
                return new PreviewResult(decision, $"{persona.Label} kan öppna sidan men ser inget av innehållet: alla block är för någon annan.", Document(title, ""));
            return new PreviewResult(decision, $"Så ser {persona.Label.ToLowerInvariant()} sidan.", Document(title, body));
        }

        /// <summary>
        /// Ett komplett dokument för en <c>&lt;iframe sandbox srcdoc&gt;</c>. Titeln kodas; brödtexten är redan renderarens (kodad och sanerad). Bara bootstrap-stilen laddas utifrån;
        /// inga skript. Iframen har <c>sandbox=""</c>, så inget skript kan köras i den ändå.
        /// </summary>
        public static string Document(string title, string bodyHtml) =>
            "<!doctype html><html lang=\"sv\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.8/dist/css/bootstrap.min.css\" rel=\"stylesheet\"></head><body class=\"p-3\">" +
            $"<h1 class=\"h2 mb-4\">{Encoder.Encode(title)}</h1>{bodyHtml}</body></html>";
    }
}
