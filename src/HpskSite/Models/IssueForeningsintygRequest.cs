namespace HpskSite.Models
{
    /// <summary>
    /// Det som skickas in när ett föreningsintyg utfärdas.
    ///
    /// <b>⚠️ DTO:n bär MEDVETET bara INTYGSFÄLT.</b> Registerfälten — namn, personnummer, adress,
    /// föreningens organisationsnummer, ordförandens namn — finns inte här och kan alltså inte
    /// postas. De byggs om på servern vid utfärdandet, ur medlemsregistret, klubben, styrelsen och
    /// märkesliggaren.
    ///
    /// Det är inte en stilfråga. Ett föreningsintyg är en handling till Polismyndigheten,
    /// undertecknad i klubbens namn. Kunde klienten skicka in personnummer eller föreningsnamn hade
    /// den här sidan varit ett verktyg för att tillverka ett intyg med påhittade uppgifter. Lägg
    /// därför ALDRIG ett registerfält i den här klassen — behövs ett nytt sådant fält hör det i
    /// <see cref="ForeningsintygDocumentService"/>.
    ///
    /// <b>Kryssen är styrelsens.</b> §5/§6, behovsraderna och skjutskicklighetsrutorna kommer
    /// härifrån just för att de ÄR intygarens beslut — aktivitetssammanställningen visas som
    /// underlag intill dem men sätter dem aldrig.
    /// </summary>
    public class IssueForeningsintygRequest
    {
        // ── Vem och vilket år ────────────────────────────────────────

        public int MemberId { get; set; }

        /// <summary>Klubben som utfärdar. 0 = medlemmens primära klubb.</summary>
        public int ClubId { get; set; }

        /// <summary>Året aktivitetsunderlaget visades för. Påverkar inga fält på blanketten.</summary>
        public int ActivityYear { get; set; }

        /// <summary>
        /// Förfrågan som utfärdandet besvarar. 0 = klubben skriver ett intyg utan förfrågan.
        ///
        /// <para><b>⚠️ Det här är en ARBETSFLÖDESPEKARE, inte ett registerfält.</b> Den bär inga
        /// uppgifter till dokumentet — den säger bara vilket ärende som ska stängas. Servern
        /// kontrollerar att förfrågan tillhör samma medlem och samma klubb innan den rörs, så ett
        /// påhittat id kan inte stänga någon annans ärende.</para>
        ///
        /// <para><b>⚠️ Fältet finns för att de två halvorna INTE hängde ihop.</b> Att utfärda ett
        /// intyg lämnade förfrågan öppen, och att klicka "Utfärdad" i inkorgen skapade inget
        /// dokument. Tillståndet kunde alltså ljuga åt båda hållen. Ta inte bort kopplingen.</para>
        /// </summary>
        public int RequestId { get; set; }

        /// <summary>
        /// Ska medlemmen få ett mejl om att intyget är utfärdat? Utfärdaren väljer per gång.
        ///
        /// <para><b>⚠️⚠️ STRÄNG OCH INTE `bool?` — OCH DET ÄR TREDJE GÅNGEN DEN FÄLLAN SLÅR TILL I
        /// DEN HÄR KODBASEN.</b> ASP.NET Cores bool-bindning godtar bara "true"/"false"; <b>"0" och
        /// "1" binder INTE</b> utan faller tillbaka på default. Med en `bool?` blev ett urkryssat
        /// val alltså `null`, som tolkas som JA — så mejlet gick ut ändå och kvittot påstod att
        /// medlemmen fått besked. Mätt i webbläsaren 2026-09-09.</para>
        ///
        /// <para>Samma fälla kostade en runda på klubbvapnens <c>writeDetails</c> (där de krypterade
        /// uppgifterna aldrig skrevs medan sparningen rapporterade lyckat) och är dokumenterad i
        /// CLAUDE.md. <b>Skicka aldrig "1"/"0" till en bool-parameter.</b></para>
        ///
        /// <para><b>Utelämnat värde betyder JA</b>, alltså det beteende som gällde innan valet
        /// fanns: en omarkerad kryssruta skickas inte alls av FormData, och en äldre klient som inte
        /// känner till fältet ska inte tysta beskedet till medlemmen.</para>
        /// </summary>
        public string? NotifyMember { get; set; }

        /// <summary>Tolkningen av <see cref="NotifyMember"/>. Tomt/utelämnat = ja.</summary>
        public bool ShouldNotifyMember => IsTrueFlag(NotifyMember);

        /// <summary>Godtar "1", "true" och "on". Tomt eller null = true.</summary>
        internal static bool IsTrueFlag(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return true;
            var t = v.Trim();
            return t.Equals("1", StringComparison.Ordinal)
                || t.Equals("true", StringComparison.OrdinalIgnoreCase)
                || t.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Loggradens ändamål. Tomt blir "Vapenlicens".</summary>
        public string? Purpose { get; set; }

        /// <summary>Klubbintern anteckning på loggraden, står inte på intyget.</summary>
        public string? Notes { get; set; }

        // ── Aktivt deltagande ────────────────────────────────────────

        public bool AktivtDeltagitSexManader { get; set; }
        public bool AktivMedlemParagraf5 { get; set; }
        public bool AktivMedlemParagraf6 { get; set; }
        public bool VisasGenomLoggbok { get; set; }
        public bool VisasGenomSarskildaSkal { get; set; }

        // ── Förbund ──────────────────────────────────────────────────

        public string? Forbund { get; set; }
        public string? AnnatForbund { get; set; }

        // ── Vapnet ───────────────────────────────────────────────────

        public string? Vapentyp { get; set; }
        public string? AnnanVapentyp { get; set; }
        public string? Fabrikat { get; set; }
        public string? KaliberPatronbeteckning { get; set; }
        public string? Modell { get; set; }
        public string? Piplangd { get; set; }
        public bool ForeningenBedriverVerksamhet { get; set; }
        public string? VapengruppSkytteform { get; set; }

        // ── Behov av skjutvapen ──────────────────────────────────────

        public bool BehovInternaTavlingar { get; set; }
        public bool BehovExternaTavlingar { get; set; }
        public bool BehovLoggbok { get; set; }
        public bool BehovAnnat { get; set; }
        public string? BehovAnnatText { get; set; }

        // ── Behov av enhandsvapen ────────────────────────────────────

        public bool IntygetAvserYtterligareEnhandsvapen { get; set; }
        public bool IntygetAvserFornyelse { get; set; }
        public bool Tranat2GangerSexManader { get; set; }
        public bool Tranat4GangerPerArTvaAr { get; set; }
        public bool EnhandsvapenAnnatBilaga { get; set; }

        /// <summary>
        /// "Sedan tidigare ___ st skjutvapen för målskjutning i den verksamhet som bedrivs av det
        /// förbund som anges ovan." FÖRBUNDSSKOPAT — inte medlemmens totala vapeninnehav.
        /// </summary>
        public int? AntalVapenSedanTidigare { get; set; }

        // ── Skjutskicklighet ─────────────────────────────────────────

        public bool UppfyllerSkjutskicklighet { get; set; }

        /// <summary>
        /// Blanketten kräver ett datum. Märkesliggarens <c>AchievedDate</c> är en bokföringsstämpel
        /// (<c>AwardBadge</c> sätter dagens datum även för ett gammalt märke), så datumet skrivs in
        /// och förifylls aldrig.
        /// </summary>
        public string? SkjutprovDatum { get; set; }

        /// <summary>
        /// Guldmärkeskrysset. Förifylls som FÖRSLAG ur liggaren, men kommer härifrån vid
        /// utfärdandet — intygaren måste kunna kryssa av det, till exempel för en medlem vars märke
        /// finns på papper men inte i liggaren.
        /// </summary>
        public bool GuldmarkeSpsf { get; set; }

        public bool SilvermarkeSkyttesport { get; set; }
        public bool GuldmarkeAutomatvapenSkyttesport { get; set; }
        public bool SilvermarkeDynamiska { get; set; }
        public bool SkjutskicklighetAnnat { get; set; }
        public string? SkjutskicklighetAnnatText { get; set; }

        // ── Underskrift ──────────────────────────────────────────────

        /// <summary>Orten intyget skrivs under på. Förifylls med klubbens ort som förslag.</summary>
        public string? UnderskriftOrt { get; set; }

        /// <summary>
        /// Lägger intygsfälten på ett dokument vars registerfält redan är byggda på servern.
        /// Rör aldrig ett registerfält.
        /// </summary>
        public void ApplyTo(ForeningsintygDocument doc)
        {
            doc.ActivityYear = ActivityYear > 0 ? ActivityYear : doc.ActivityYear;

            doc.AktivtDeltagitSexManader = AktivtDeltagitSexManader;
            doc.AktivMedlemParagraf5 = AktivMedlemParagraf5;
            doc.AktivMedlemParagraf6 = AktivMedlemParagraf6;
            doc.VisasGenomLoggbok = VisasGenomLoggbok;
            doc.VisasGenomSarskildaSkal = VisasGenomSarskildaSkal;

            // Tomt förbund får inte tömma förvalet — SPSF är det som gäller för oss.
            if (!string.IsNullOrWhiteSpace(Forbund)) doc.Forbund = Forbund.Trim();
            doc.AnnatForbund = Trim(AnnatForbund);

            doc.Vapentyp = Trim(Vapentyp);
            doc.AnnanVapentyp = Trim(AnnanVapentyp);
            doc.Fabrikat = Trim(Fabrikat);
            doc.KaliberPatronbeteckning = Trim(KaliberPatronbeteckning);
            doc.Modell = Trim(Modell);
            doc.Piplangd = Trim(Piplangd);
            doc.ForeningenBedriverVerksamhet = ForeningenBedriverVerksamhet;
            doc.VapengruppSkytteform = Trim(VapengruppSkytteform);

            doc.BehovInternaTavlingar = BehovInternaTavlingar;
            doc.BehovExternaTavlingar = BehovExternaTavlingar;
            doc.BehovLoggbok = BehovLoggbok;
            doc.BehovAnnat = BehovAnnat;
            doc.BehovAnnatText = Trim(BehovAnnatText);

            doc.IntygetAvserYtterligareEnhandsvapen = IntygetAvserYtterligareEnhandsvapen;
            doc.IntygetAvserFornyelse = IntygetAvserFornyelse;
            doc.Tranat2GangerSexManader = Tranat2GangerSexManader;
            doc.Tranat4GangerPerArTvaAr = Tranat4GangerPerArTvaAr;
            doc.EnhandsvapenAnnatBilaga = EnhandsvapenAnnatBilaga;
            doc.AntalVapenSedanTidigare = AntalVapenSedanTidigare;

            doc.UppfyllerSkjutskicklighet = UppfyllerSkjutskicklighet;
            doc.SkjutprovDatum = Trim(SkjutprovDatum);
            doc.GuldmarkeSpsf = GuldmarkeSpsf;
            doc.SilvermarkeSkyttesport = SilvermarkeSkyttesport;
            doc.GuldmarkeAutomatvapenSkyttesport = GuldmarkeAutomatvapenSkyttesport;
            doc.SilvermarkeDynamiska = SilvermarkeDynamiska;
            doc.SkjutskicklighetAnnat = SkjutskicklighetAnnat;
            doc.SkjutskicklighetAnnatText = Trim(SkjutskicklighetAnnatText);

            // Ort får skrivas över med tomt: styrelsen kan medvetet lämna den blank.
            doc.UnderskriftOrt = Trim(UnderskriftOrt);
            doc.UnderskriftDatum = DateTime.Today.ToString("yyyy-MM-dd");
        }

        private static string Trim(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
    }
}
