namespace HpskSite.Models
{
    /// <summary>
    /// Vem som får anmäla sig till en klubb- eller kretshändelse.
    ///
    /// <para>Fram till 2026-09-18 var regeln hårdkodad: en klubbhändelse för klubbens medlemmar,
    /// en kretshändelse för medlemmar i kretsens klubbar. Det stämmer för en städdag och för ett
    /// medlemsmöte, men inte för en gåsaskjutning som bjuder in grannklubbarna, och inte för en
    /// nybörjarkurs vars hela syfte är att nå dem som ännu <i>inte</i> är medlemmar.</para>
    ///
    /// <para><b>⚠️ ETT OKÄNT VÄRDE FALLER TILL DEN SMALASTE NIVÅN.</b> Ett fältvärde som inte går
    /// att läsa — en tom egenskap, en felstavning, en halv migrering — får aldrig ÖPPNA en
    /// anmälan. Det är skillnaden mellan att en arrangör undrar varför grannklubben inte kommer in
    /// och att en händelse tyst står öppen för hela landet. <see cref="Normalise"/> är därför
    /// enkelriktad mot <see cref="Club"/>.</para>
    /// </summary>
    public static class EventAudience
    {
        /// <summary>Doctype-egenskapen på <c>clubSimpleEvent</c> (operatörstillagd).
        /// <b>⚠️ Saknas den är <c>SetValue</c> en TYST no-op</b> — skrivvägen måste vägra och
        /// namnge egenskapen, inte rapportera en sparning som inte hände.</summary>
        public const string Property = "eventAudience";

        /// <summary>Den ägande klubbens medlemmar. <b>Standard</b>, och därmed det en deploy ger
        /// varje befintlig händelse — ingen händelse öppnas av att koden rullas ut.</summary>
        public const string Club = "Club";

        /// <summary>Medlemmar i någon av kretsens klubbar. På en kretshändelse är det redan vad
        /// <see cref="Club"/> betyder, så där är de två samma sak.</summary>
        public const string Region = "Region";

        /// <summary>Alla inloggade medlemmar på pistol.nu, oavsett klubb.</summary>
        public const string AllMembers = "AllMembers";

        /// <summary>
        /// Vem som helst, utan konto.
        ///
        /// <para><b>⚠️⚠️ DEN HÄR NIVÅN ÄR INTE "ALLMEMBERS FAST BREDARE".</b> Den byter ut hela
        /// förutsättningen: deltagaren har ingen inloggning, alltså ingen identitet vi kan lita på,
        /// ingen väg att avboka via Min sida, och inga kontaktuppgifter vi redan har. Följden är
        /// att klubben lagrar personuppgifter om en icke-medlem, att avbokningen måste gå via en
        /// länk i ett mejl, och att formuläret står öppet på en publik sida.</para>
        ///
        /// <para>Den som saknar konto men kommer i sällskap med en medlem ska INTE gå den här
        /// vägen — gästfunktionen bär hen på medlemmens ansvar, och då räcker ett namn.</para>
        /// </summary>
        public const string Open = "Open";

        /// <summary>Från smalast till bredast. <b>Ordningen är bärande</b> — den styr både
        /// rullgardinens ordning och <see cref="IsAtLeastAsWideAs"/>.</summary>
        public static readonly string[] All = { Club, Region, AllMembers, Open };

        /// <summary>
        /// Tolkar ett lagrat värde. <b>Allt som inte är exakt känt blir <see cref="Club"/></b> —
        /// se klassens huvud för varför riktningen aldrig får vändas.
        /// </summary>
        public static string Normalise(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Club;
            var v = raw.Trim();
            foreach (var known in All)
                if (string.Equals(known, v, StringComparison.OrdinalIgnoreCase)) return known;
            return Club;
        }

        public static string Display(string? value) => Normalise(value) switch
        {
            Region => "Alla i kretsen",
            AllMembers => "Alla medlemmar på pistol.nu",
            Open => "Vem som helst, även utan konto",
            _ => "Klubbens medlemmar"
        };

        /// <summary>Kort form till besked och brickor, i samma mening som "Anmälan är öppen för …".</summary>
        public static string Phrase(string? value, string ownerName, bool isRegionOwned) => Normalise(value) switch
        {
            Region => "medlemmar i kretsens klubbar",
            AllMembers => "alla medlemmar på pistol.nu",
            Open => "alla, även utan konto",
            _ => isRegionOwned ? "medlemmar i kretsens klubbar" : $"medlemmar i {ownerName}"
        };

        /// <summary>Kräver anmälan en inloggning? Falskt bara för <see cref="Open"/>.</summary>
        public static bool RequiresLogin(string? value) => Normalise(value) != Open;

        /// <summary>Får en besökare utan konto anmäla sig?</summary>
        public static bool AllowsAnonymous(string? value) => Normalise(value) == Open;

        /// <summary>
        /// Är <paramref name="value"/> minst lika bred som <paramref name="other"/>? Jämför på
        /// <see cref="All"/>-ordningen, aldrig på strängen.
        /// </summary>
        public static bool IsAtLeastAsWideAs(string? value, string other)
            => Array.IndexOf(All, Normalise(value)) >= Array.IndexOf(All, Normalise(other));
    }
}
