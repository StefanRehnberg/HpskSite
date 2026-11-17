namespace HpskSite.Services.SitePages
{
    /// <summary>En ikon i sidans ikonbibliotek: Bootstrap Icons-namnet, svensk etikett (sökbar) och grupp.</summary>
    public sealed record SitePageIcon(string Key, string Label, string Group);

    /// <summary>
    /// Ikonbiblioteket för egna sidor (ENDA källan). Sidans ikon visas i menyn. Servern tar bara emot en nyckel ur den här listan
    /// (aldrig fri text, eftersom nyckeln hamnar i en CSS-klass); en okänd eller tom nyckel ger standardikonen.
    /// Namnen finns i Bootstrap Icons 1.11 (den version sajten laddar).
    /// </summary>
    public static class SitePageIcons
    {
        public const string Default = "bi-file-earmark-text";

        public static readonly IReadOnlyList<SitePageIcon> All = Build();

        private static readonly HashSet<string> Keys = new(All.Select(i => i.Key), StringComparer.Ordinal);

        public static bool IsValid(string? key) => key != null && Keys.Contains(key);

        /// <summary>Nyckeln som ska visas: den valda om den finns i biblioteket, annars standardikonen.</summary>
        public static string Resolve(string? key) => IsValid(key) ? key! : Default;

        /// <summary>Rensar ett värde som kommer utifrån: null (standardikonen) för tomt, standardikonen eller okänt; annars nyckeln.</summary>
        public static string? Normalize(string? key) =>
            string.IsNullOrWhiteSpace(key) || key == Default || !IsValid(key.Trim()) ? null : key.Trim();

        private static IReadOnlyList<SitePageIcon> Build()
        {
            var list = new List<SitePageIcon>();
            void G(string group, params (string key, string label)[] items)
            {
                foreach (var (key, label) in items) list.Add(new SitePageIcon("bi-" + key, label, group));
            }

            G("Sidor och text",
                ("file-earmark-text", "Textsida dokument"), ("file-text", "Dokument text"), ("journal-text", "Anteckningsbok journal"), ("book", "Bok"), ("bookmark", "Bokmärke"),
                ("card-text", "Kort text"), ("clipboard-check", "Checklista klar"), ("list-check", "Lista att göra"), ("list-ul", "Punktlista"), ("info-circle", "Information om"),
                ("question-circle", "Fråga frågor och svar"), ("exclamation-triangle", "Varning viktigt"), ("megaphone", "Megafon nyhet meddelande"), ("newspaper", "Tidning nyheter"), ("quote", "Citat"));
            G("Skytte och tävling",
                ("bullseye", "Måltavla skytte"), ("trophy", "Pokal tävling"), ("award", "Medalj utmärkelse"), ("flag", "Flagga"), ("stopwatch", "Tidtagning stoppur"),
                ("speedometer2", "Fart mätare"), ("graph-up", "Resultat statistik"), ("bar-chart", "Stapeldiagram"), ("star", "Stjärna"), ("lightning-charge", "Blixt snabb"),
                ("shield-check", "Skydd säkerhet"), ("shield-lock", "Säkerhet regler"), ("crosshair", "Sikte"), ("fire", "Eld"), ("hourglass-split", "Tid timglas"));
            G("Människor",
                ("people", "Människor grupp"), ("people-fill", "Medlemmar"), ("person", "Person"), ("person-badge", "Funktionär badge"), ("person-lines-fill", "Kontakt personer"),
                ("person-plus", "Ny medlem bli medlem"), ("person-check", "Godkänd person"), ("mortarboard", "Utbildning kurs"), ("heart", "Hjärta"), ("hand-thumbs-up", "Tummen upp"),
                ("emoji-smile", "Glad"), ("chat-dots", "Prat samtal"), ("chat-left-text", "Meddelande"), ("hand-index", "Hand"), ("balloon", "Ballong fest"));
            G("Plats och tid",
                ("house", "Hem"), ("house-door", "Hus"), ("geo-alt", "Plats karta"), ("map", "Karta"), ("compass", "Kompass"),
                ("calendar-event", "Kalender händelse"), ("calendar3", "Kalender"), ("calendar-check", "Kalender klar"), ("clock", "Klocka tid"), ("alarm", "Alarm påminnelse"),
                ("signpost", "Vägvisare"), ("building", "Byggnad lokal"), ("tree", "Träd natur"), ("sun", "Sol"), ("cloud-sun", "Väder"));
            G("Dokument och filer",
                ("archive", "Arkiv"), ("folder", "Mapp"), ("folder2-open", "Öppen mapp"), ("files", "Filer"), ("file-earmark-pdf", "PDF"),
                ("file-earmark-arrow-down", "Ladda ner"), ("download", "Ladda ner"), ("clipboard", "Urklipp"), ("pencil-square", "Skriva anmälan formulär"), ("receipt", "Kvitto"),
                ("paperclip", "Bilaga"), ("link-45deg", "Länk"), ("box-arrow-up-right", "Extern länk"), ("file-earmark-ruled", "Protokoll"), ("journal-bookmark", "Regelbok"));
            G("Bilder och media",
                ("image", "Bild"), ("images", "Bilder galleri"), ("camera", "Kamera foto"), ("camera-video", "Film video"), ("play-circle", "Spela upp"),
                ("music-note", "Musik"), ("palette", "Färg design"), ("brush", "Pensel"), ("film", "Film"), ("mic", "Mikrofon"),
                ("globe", "Webb värld"), ("broadcast", "Sändning"), ("qr-code", "QR-kod"), ("share", "Dela"), ("rss", "Flöde"));
            G("Pengar och föreningen",
                ("cash-coin", "Pengar avgift"), ("wallet2", "Plånbok"), ("credit-card", "Kort betala"), ("piggy-bank", "Sparbössa"), ("bank", "Bank"),
                ("calculator", "Räkna kalkyl"), ("percent", "Procent rabatt"), ("tag", "Pris etikett"), ("cart", "Varukorg butik"), ("bag", "Shop"),
                ("briefcase", "Portfölj styrelse"), ("gear", "Inställningar"), ("tools", "Verktyg"), ("wrench", "Skiftnyckel underhåll"), ("key", "Nyckel"));
            G("Övrigt",
                ("gift", "Present"), ("cup-hot", "Fika kaffe"), ("egg-fried", "Mat"), ("basket", "Korg"), ("lightbulb", "Idé"),
                ("puzzle", "Pussel"), ("rocket-takeoff", "Raket"), ("patch-check", "Godkänd"), ("pin-map", "Nål"), ("telephone", "Telefon"),
                ("envelope", "Brev e-post"), ("envelope-paper", "Kuvert"), ("binoculars", "Kikare"), ("lock", "Lås"), ("circle", "Cirkel"));
            return list;
        }
    }
}
