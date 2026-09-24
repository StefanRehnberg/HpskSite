namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Vad ett AUTOMATISKT projekt handlar om, och hur det namnges. <b>Rena funktioner</b> — ingen
    /// databas, inget Umbraco — så att regeln som avgör vilken frusen rad som hamnar på vilket
    /// projekt går att pröva utan att skriva en enda verifikation.
    ///
    /// <para><b>⚠️⚠️ PRINCIPEN: MARKERA FINAST, GRUPPERA EFTERÅT.</b> <c>ProjectId</c> sitter på
    /// konteringsraden, och raderna är oföränderliga. Det som märks vid bokföringen kan alltså
    /// aldrig ändras — och avgifter bokförs långt innan någon kassör tänker på projekt. Märkningen
    /// måste därför vara något som inte KAN bli fel: <i>den här raden hör till den här
    /// tävlingen</i>. Det är ett faktum systemet redan känner genom <c>SourceType</c>/<c>SourceId</c>.
    /// Information går att aggregera, aldrig att återskapa: märker vi grovt förstör vi
    /// oåterkalleligt, märker vi fint bevarar vi varje framtida gruppering.</para>
    /// </summary>
    public static class LedgerProjectSource
    {
        /// <summary>Projektet är en tävling. Anmälnings- OCH lagavgifterna delar det.</summary>
        public const string Competition = "competition";

        /// <summary>Projektet är ett evenemang (<c>clubSimpleEvent</c>) — klubbens eller kretsens.</summary>
        public const string Event = "event";

        /// <summary>
        /// En projektGRUPP ur en tävlingsserie (<c>competitionSeries</c>). Aldrig ett projekt:
        /// serien är den gruppering som redan finns i datat, alltså "onsdagsserien"-fallet.
        /// </summary>
        public const string Series = "series";

        /// <summary>Längsta namn <c>LedgerProject.Name</c> tar.</summary>
        public const int MaxNameLength = 120;

        /// <summary>
        /// Vilket projekt en postning ur en källa hör till — eller <c>null</c> när källan inte är
        /// en tävling eller ett evenemang.
        ///
        /// <para><b>⚠️ Anmälnings- och lagavgiften pekar på SAMMA projekt.</b> Båda bär tävlingens
        /// nod-id i <c>SourceId</c> (se <see cref="LedgerSourceType"/>), och ett projekt per
        /// avgiftsslag hade delat tävlingens resultat i två — precis den uppdelning dimensionen
        /// finns för att undvika.</para>
        ///
        /// <para>⚠️ <c>SourceId</c> måste vara TÄVLINGENS id, aldrig anmälans. Nyckeln på projektet
        /// är (typ, id); ett anmälnings-id där hade gett ett projekt per skytt.</para>
        /// </summary>
        public static (string Kind, int Id)? For(string? sourceType, int? sourceId)
        {
            // Nod-id är alltid positiva. Ett noll- eller negativt id är ingen nod att projektera.
            if (sourceId is not > 0) return null;

            return sourceType switch
            {
                LedgerSourceType.CompetitionRegistration => (Competition, sourceId.Value),
                LedgerSourceType.TeamFee => (Competition, sourceId.Value),
                LedgerSourceType.CompetitionInvoice => (Competition, sourceId.Value),
                LedgerSourceType.Event => (Event, sourceId.Value),
                _ => null
            };
        }

        /// <summary>
        /// Namnen att pröva, i ordning, för ett automatiskt projekt.
        ///
        /// <para><b>⚠️⚠️ NAMNET ÄR UNIKT PER FÖRENING, OCH TVÅ OLIKA TÄVLINGAR KAN HETA LIKA.</b>
        /// Klubben kopierar "Nybörjarkväll" varje vecka, och "Klubbmästerskap precision" finns
        /// varje år. Skulle den andra källan TA det första projektets namn hamnar dess rader på det
        /// första projektet — frusna, för alltid. Därför en kedja av allt mer specifika namn:
        /// årtalet, sedan datumet, sist nodens id som alltid är unikt.</para>
        ///
        /// <para>Årtalet läggs bara till när namnet inte redan bär ETT årtal — "SSM 2025 2025" är
        /// ingen hjälp, och "Årsmöte 2027" som hålls hösten 2026 ska inte bli "Årsmöte 2027 2026".
        /// Årtalet i namnet är arrangörens eget, och det är det som betyder något.</para>
        /// </summary>
        public static List<string> NameCandidates(string? baseName, DateTime? date, string kind, int sourceId)
        {
            var name = CollapseSpaces(baseName);
            if (name.Length == 0)
                name = kind == Event ? "Evenemang" : "Tävling";

            var result = new List<string>();
            void Add(string candidate)
            {
                candidate = Truncate(candidate);
                if (!result.Contains(candidate, StringComparer.OrdinalIgnoreCase)) result.Add(candidate);
            }

            if (date is DateTime d && d.Year > 1900)
            {
                Add(HasYear(name) ? name : $"{name} {d.Year}");
                Add($"{name} {d:yyyy-MM-dd}");
            }
            else
            {
                Add(name);
            }

            // Sista utvägen: nodens id är unikt, så den här kandidaten kan aldrig krocka med ett
            // annat automatiskt projekt.
            Add($"{name} (#{sourceId})");
            return result;
        }

        /// <summary>Bär namnet redan ett årtal (1900–2099) som eget ord?</summary>
        private static bool HasYear(string name)
            => System.Text.RegularExpressions.Regex.IsMatch(name, @"(?<!\d)(19|20)\d{2}(?!\d)");

        private static string CollapseSpaces(string? s)
            => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>
        /// Kapar till <see cref="MaxNameLength"/>, och kapar i så fall BASEN — aldrig slutet, som är
        /// det som skiljer kandidaterna åt.
        /// </summary>
        private static string Truncate(string s)
        {
            if (s.Length <= MaxNameLength) return s;

            // Slutet (" 2026", " 2026-09-12", " (#9054)") är det särskiljande; behåll det.
            var cut = s.LastIndexOf(' ');
            if (cut <= 0) return s[..MaxNameLength];

            var tail = s[cut..];
            var room = MaxNameLength - tail.Length;
            return room <= 0 ? s[..MaxNameLength] : s[..room].TrimEnd() + tail;
        }
    }
}
