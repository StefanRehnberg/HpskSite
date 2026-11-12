using System.Globalization;
using System.Text;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Vad de tre nya sidhuvudena (alla utom Standard) ritar: namn, text, logo och bannerbild. <see cref="TaglineHtml"/> är REDAN
    /// säkrad (RteHelper.SafeMarkup) och används av kretsens välkomsttext; <see cref="Tagline"/> är ren text och kodas av vyn.
    /// </summary>
    public sealed record SiteHeaderModel(string Style, string Palette, string Name, string? Tagline, string? TaglineHtml, string? LogoUrl, string? BannerUrl);

    /// <summary>
    /// Klubbens/kretsens utseende (klubbsidorna, Hem-bollningen 2026-11-11): vilken grunddesign sidhuvudet har och vilken
    /// grundfärg sidorna har. Ren regelklass utan databas: vitlistorna, färgerna, kontrasträkningen och CSS:en.
    ///
    /// <para><b>Ren presentation.</b> Inget här påverkar vem som ser vad. Ett okänt värde (gammal rad, handskriven SQL,
    /// återställd deploy) blir alltid standard, aldrig ett fel och aldrig fri text i HTML/CSS.</para>
    ///
    /// <para><b>Färgen är en kuraterad palett, inte en fri väljare:</b> varje färg är kontrastprovad (SiteAppearanceTests):
    /// vit text på färgen och färgen som länk på vitt ≥ 4,5:1, och den ljusade länkfärgen i mörkt läge ≥ 4,5:1 mot
    /// mörk bakgrund. Allt annat (hover, dämpad bakgrund, mörkt läge) räknas ur den EN färgen här, så en ny färg i
    /// listan kan inte ge en oläslig kombination utan att provet fäller bygget.</para>
    /// </summary>
    public static class SiteAppearance
    {
        public const string DefaultHeader = "standard";
        public const string DefaultPalette = "bla";

        /// <summary>Grunddesignerna för sidhuvudet. Nyckeln lagras; ordningen är väljarens ordning.</summary>
        public sealed record HeaderStyle(string Key, string Label, string Help);

        public static readonly IReadOnlyList<HeaderStyle> Headers = new[]
        {
            new HeaderStyle("standard", "Standard", "Bannerbild överst, logon till vänster och namnet över bilden. Så ser det ut idag."),
            new HeaderStyle("utan-banner-vanster", "Utan bild, logon till vänster", "Ren rad med logon till vänster och namnet bredvid. Ingen bannerbild."),
            new HeaderStyle("utan-banner-mitten", "Utan bild, logon i mitten", "Logon och namnet centrerade i mitten. Ingen bannerbild."),
            new HeaderStyle("banner-utan-logo", "Bannerbild, logon under", "Bannerbilden i full bredd, med logon och namnet på raden under bilden."),
            new HeaderStyle("farg-vanster", "Färgat band, logon till vänster", "Ett band i vald färg med vit text och logon till vänster. Ingen bannerbild."),
            new HeaderStyle("farg-mitten", "Färgat band, logon i mitten", "Ett band i vald färg med vit text, logon och namnet centrerade. Ingen bannerbild."),
            new HeaderStyle("kompakt", "Kompakt", "En smal rad med liten logo och namnet. Lämnar mest plats åt innehållet."),
            new HeaderStyle("accentkant", "Accentkant", "Vit ruta med en kraftig kant i vald färg åt vänster. Lugn och ren."),
            new HeaderStyle("logo-hoger", "Logon till höger", "Namnet och texten till vänster, logon till höger. Ingen bannerbild."),
            new HeaderStyle("farg-hoger", "Färgat band, logon till höger", "Ett band i vald färg med vit text, namnet till vänster och logon till höger. Ingen bannerbild."),
            new HeaderStyle("banner-hoger", "Bannerbild, logon till höger", "Bannerbilden i full bredd, med namnet på raden under och logon till höger."),
            new HeaderStyle("accentkant-hoger", "Accentkant, logon till höger", "Vit ruta med en kraftig kant i vald färg åt vänster, namnet till vänster och logon till höger."),
        };

        /// <summary>En färg i paletten. <see cref="Hex"/> är knappfärgen (vit text) och länkfärgen i ljust läge.</summary>
        public sealed record PaletteColor(string Key, string Label, string Hex);

        public static readonly IReadOnlyList<PaletteColor> Palette = new[]
        {
            new PaletteColor("bla", "Blå (standard)", "#0d6efd"),
            new PaletteColor("marin", "Marinblå", "#1e3a8a"),
            new PaletteColor("turkos", "Turkos", "#0f766e"),
            new PaletteColor("skog", "Skogsgrön", "#166534"),
            new PaletteColor("lila", "Lila", "#6d28d9"),
            new PaletteColor("cerise", "Cerise", "#be185d"),
            new PaletteColor("vinrod", "Vinröd", "#9f1239"),
            new PaletteColor("brandgul", "Brandgul", "#b45309"),
            new PaletteColor("brun", "Brun", "#7c4a21"),
            new PaletteColor("skiffer", "Skifferfärgad", "#475569"),
            new PaletteColor("indigo", "Indigo", "#4338ca"),
            new PaletteColor("cyan", "Havsblå", "#0e7490"),
            new PaletteColor("smaragd", "Smaragdgrön", "#047857"),
            new PaletteColor("oliv", "Olivgrön", "#4d7c0f"),
            new PaletteColor("militar", "Militärgrön", "#3f6212"),
            new PaletteColor("guld", "Guld", "#a16207"),
            new PaletteColor("orange", "Orange", "#c2410c"),
            new PaletteColor("rod", "Röd", "#b91c1c"),
            new PaletteColor("fuchsia", "Fuchsia", "#a21caf"),
            new PaletteColor("grafit", "Grafit", "#1f2937"),
        };

        public static string NormalizeHeader(string? key) =>
            key != null && Headers.Any(h => h.Key == key) ? key : DefaultHeader;

        public static string NormalizePalette(string? key) =>
            key != null && Palette.Any(p => p.Key == key) ? key : DefaultPalette;

        public static bool IsValidHeader(string? key) => key != null && Headers.Any(h => h.Key == key);
        public static bool IsValidPalette(string? key) => key != null && Palette.Any(p => p.Key == key);

        public static PaletteColor ColorOf(string? key) =>
            Palette.First(p => p.Key == NormalizePalette(key));

        /// <summary>Ägarens utseende, redan normaliserat.</summary>
        public sealed record Look(string Header, string Palette)
        {
            public static readonly Look Default = new(DefaultHeader, DefaultPalette);
            /// <summary>Behöver sidan en egen färg-CSS? Standardblått ritas av Bootstrap självt.</summary>
            public bool HasCustomColor => Palette != DefaultPalette;
        }

        public static Look LookOf(string? header, string? palette) => new(NormalizeHeader(header), NormalizePalette(palette));

        // ── Färgräkning ────────────────────────────────────────────────────────

        public const string DarkBodyHex = "#212529"; // Bootstraps mörka bakgrund (--bs-body-bg i data-bs-theme=dark)
        public const string WhiteHex = "#ffffff";

        public static (int R, int G, int B) Rgb(string hex)
        {
            var h = hex.TrimStart('#');
            return (int.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        public static string Hex((int R, int G, int B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        /// <summary>WCAG relativ luminans.</summary>
        public static double Luminance(string hex)
        {
            var (r, g, b) = Rgb(hex);
            static double Ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
        }

        /// <summary>WCAG kontrastförhållande, 1..21.</summary>
        public static double Contrast(string a, string b)
        {
            var la = Luminance(a); var lb = Luminance(b);
            var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        /// <summary>Blandar <paramref name="a"/> med <paramref name="b"/>: andelen b (0..1).</summary>
        public static string Mix(string a, string b, double weightOfB)
        {
            var (ar, ag, ab) = Rgb(a); var (br, bg, bb) = Rgb(b);
            int M(int x, int y) => (int)Math.Round(x + (y - x) * weightOfB);
            return Hex((M(ar, br), M(ag, bg), M(ab, bb)));
        }

        /// <summary>Länkfärgen i mörkt läge: färgen ljusas (blandas med vitt) tills den når 4,5:1 mot mörk bakgrund.</summary>
        public static string LightenForDark(string hex)
        {
            for (var t = 0.0; t <= 1.0001; t += 0.02)
            {
                var c = Mix(hex, WhiteHex, t);
                // Läsbar både mot själva bakgrunden och mot den dämpade primärbakgrunden (alert/badge) i mörkt läge.
                if (Contrast(c, DarkBodyHex) >= 4.5 && Contrast(c, Mix(hex, DarkBodyHex, 0.8)) >= 4.5) return c;
            }
            return WhiteHex;
        }

        /// <summary>De härledda färgerna för en palettfärg. Allt räknas ur EN färg, aldrig handskrivet.</summary>
        public sealed record Scheme(
            string Primary, string Hover, string Active,
            string LightSubtle, string LightBorderSubtle, string LightEmphasis,
            string DarkLink, string DarkHover, string DarkSubtle, string DarkBorderSubtle)
        {
            public static Scheme For(string hex)
            {
                var link = LightenForDark(hex);
                return new Scheme(
                    Primary: hex,
                    Hover: Mix(hex, "#000000", 0.15),
                    Active: Mix(hex, "#000000", 0.25),
                    LightSubtle: Mix(hex, WhiteHex, 0.9),
                    LightBorderSubtle: Mix(hex, WhiteHex, 0.7),
                    LightEmphasis: Mix(hex, "#000000", 0.4),
                    DarkLink: link,
                    DarkHover: Mix(link, WhiteHex, 0.2),
                    DarkSubtle: Mix(hex, DarkBodyHex, 0.8),
                    DarkBorderSubtle: Mix(hex, DarkBodyHex, 0.5));
            }
        }

        private static string RgbList(string hex) { var (r, g, b) = Rgb(hex); return $"{r},{g},{b}"; }

        /// <summary>Klassen på &lt;body&gt; som CSS:en nedan hänger på.</summary>
        public const string BodyClass = "owner-themed";

        /// <summary>
        /// CSS:en för en palettfärg, bara för sidor med klassen <see cref="BodyClass"/> (klubbens/kretsens egna sidor).
        /// Standardfärgen ger ingen CSS. Innehållet kommer ENBART ur vitlistan ovan (hex ur koden), aldrig ur indata.
        /// </summary>
        public static string Css(string? paletteKey)
        {
            var key = NormalizePalette(paletteKey);
            if (key == DefaultPalette) return "";
            var s = Scheme.For(ColorOf(key).Hex);
            var b = "body." + BodyClass;
            var d = "[data-bs-theme=dark] " + b;
            var sb = new StringBuilder();
            // Variabler (länkar, text-primary, bg-primary m.fl. läser dessa).
            sb.Append(b).Append("{--bs-primary:").Append(s.Primary).Append(";--bs-primary-rgb:").Append(RgbList(s.Primary))
              .Append(";--bs-link-color:").Append(s.Primary).Append(";--bs-link-color-rgb:").Append(RgbList(s.Primary))
              .Append(";--bs-link-hover-color:").Append(s.Hover).Append(";--bs-link-hover-color-rgb:").Append(RgbList(s.Hover))
              .Append(";--bs-primary-bg-subtle:").Append(s.LightSubtle).Append(";--bs-primary-border-subtle:").Append(s.LightBorderSubtle)
              .Append(";--bs-primary-text-emphasis:").Append(s.LightEmphasis).Append("}\n");
            sb.Append(d).Append("{--bs-primary:").Append(s.DarkLink).Append(";--bs-primary-rgb:").Append(RgbList(s.DarkLink))
              .Append(";--bs-link-color:").Append(s.DarkLink).Append(";--bs-link-color-rgb:").Append(RgbList(s.DarkLink))
              .Append(";--bs-link-hover-color:").Append(s.DarkHover).Append(";--bs-link-hover-color-rgb:").Append(RgbList(s.DarkHover))
              .Append(";--bs-primary-bg-subtle:").Append(s.DarkSubtle).Append(";--bs-primary-border-subtle:").Append(s.DarkBorderSubtle)
              .Append(";--bs-primary-text-emphasis:").Append(s.DarkLink).Append("}\n");
            // Bootstraps knappar har bokstavliga färger (inte variabler): skrivs över per komponent.
            sb.Append(b).Append(" .btn-primary{--bs-btn-bg:").Append(s.Primary).Append(";--bs-btn-border-color:").Append(s.Primary)
              .Append(";--bs-btn-hover-bg:").Append(s.Hover).Append(";--bs-btn-hover-border-color:").Append(s.Hover)
              .Append(";--bs-btn-active-bg:").Append(s.Active).Append(";--bs-btn-active-border-color:").Append(s.Active)
              .Append(";--bs-btn-disabled-bg:").Append(s.Primary).Append(";--bs-btn-disabled-border-color:").Append(s.Primary)
              .Append(";--bs-btn-focus-shadow-rgb:").Append(RgbList(s.Primary)).Append("}\n");
            sb.Append(b).Append(" .btn-outline-primary{--bs-btn-color:").Append(s.Primary).Append(";--bs-btn-border-color:").Append(s.Primary)
              .Append(";--bs-btn-hover-bg:").Append(s.Primary).Append(";--bs-btn-hover-border-color:").Append(s.Primary)
              .Append(";--bs-btn-active-bg:").Append(s.Primary).Append(";--bs-btn-active-border-color:").Append(s.Primary)
              .Append(";--bs-btn-disabled-color:").Append(s.Primary).Append(";--bs-btn-disabled-border-color:").Append(s.Primary)
              .Append(";--bs-btn-focus-shadow-rgb:").Append(RgbList(s.Primary)).Append("}\n");
            sb.Append(d).Append(" .btn-outline-primary{--bs-btn-color:").Append(s.DarkLink).Append(";--bs-btn-border-color:").Append(s.DarkLink)
              .Append(";--bs-btn-hover-color:#fff;--bs-btn-hover-bg:").Append(s.Primary).Append(";--bs-btn-hover-border-color:").Append(s.Primary)
              .Append(";--bs-btn-active-bg:").Append(s.Primary).Append(";--bs-btn-active-border-color:").Append(s.Primary)
              .Append(";--bs-btn-disabled-color:").Append(s.DarkLink).Append(";--bs-btn-disabled-border-color:").Append(s.DarkLink).Append("}\n");
            sb.Append(b).Append(" .form-check-input:checked,").Append(b).Append(" .form-switch .form-check-input:checked")
              .Append("{background-color:").Append(s.Primary).Append(";border-color:").Append(s.Primary).Append("}\n");
            sb.Append(b).Append(" .form-control:focus,").Append(b).Append(" .form-select:focus")
              .Append("{border-color:").Append(Mix(s.Primary, WhiteHex, 0.4)).Append(";box-shadow:0 0 0 .25rem rgba(").Append(RgbList(s.Primary)).Append(",.25)}\n");
            sb.Append(b).Append(" .nav-pills{--bs-nav-pills-link-active-bg:").Append(s.Primary).Append("}\n");
            sb.Append(b).Append(" .page-link{--bs-pagination-color:").Append(s.Primary).Append(";--bs-pagination-hover-color:").Append(s.Hover)
              .Append("}\n").Append(b).Append(" .page-item.active .page-link{--bs-pagination-active-bg:").Append(s.Primary)
              .Append(";--bs-pagination-active-border-color:").Append(s.Primary).Append("}\n");
            sb.Append(b).Append(" .progress{--bs-progress-bar-bg:").Append(s.Primary).Append("}\n");
            return sb.ToString();
        }

        /// <summary>Bakgrunden på de färgade banden (vit text): alltid vald palettfärg, även standardblå (aldrig den lila bannerfyllnaden). Båda ändar klarar vit text.</summary>
        public static string HeaderBand(string? paletteKey)
        {
            var hex = ColorOf(paletteKey).Hex;
            return $"linear-gradient(135deg, {hex} 0%, {Mix(hex, "#000000", 0.3)} 100%)";
        }

        /// <summary>Färgen sidhuvudet fyller med när bannerbild saknas (eller utan bild): standarden behåller dagens lila.</summary>
        public static string HeaderFill(string? paletteKey)
        {
            var key = NormalizePalette(paletteKey);
            if (key == DefaultPalette) return "linear-gradient(135deg, #667eea 0%, #764ba2 100%)";
            var hex = ColorOf(key).Hex;
            return $"linear-gradient(135deg, {hex} 0%, {Mix(hex, "#000000", 0.35)} 100%)";
        }
    }
}
