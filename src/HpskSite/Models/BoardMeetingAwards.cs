using System.Text.Json;

namespace HpskSite.Models
{
    /// <summary>
    /// Årsmötets utdelning av märken och medaljer, som den lagras på dagordningspunkten
    /// (<see cref="BoardMeetingAgendaItem.AwardsData"/>) och skrivs ut i protokollet.
    ///
    /// <para><b>⚠️ Detta är en SNAPSHOT, inte en läsning.</b> Utdelningslistan är härledd ur
    /// märkesliggaren av <c>MarkenOrderListService</c> — vilket är rätt för beställningskortet på
    /// Märken-fliken, som alltid ska visa nuläget. Ett protokoll är däremot en handling: ett märke
    /// kan makuleras, en valör rättas, en egenrapporterad medalj avvisas. En läsning hade alltså
    /// tyst ändrat vad årsmötet står som att ha delat ut, i efterhand, utan att något sa ifrån.
    /// Samma mönster och samma skäl som <c>MemberCertificateIssue.Snapshot</c>.</para>
    ///
    /// <para>Formen är JSON just för att den ska kunna utökas utan en ny migrering. Läsning är
    /// därför alltid defensiv: ett fält som saknas i äldre data ska degradera, inte kasta.</para>
    /// </summary>
    public class BoardMeetingAwards
    {
        /// <summary>Vilket verksamhetsår utdelningen gäller — nästan alltid mötesåret minus ett.</summary>
        public int Year { get; set; }

        /// <summary>När listan hämtades. Skiljer "hämtad men inte upplast" från "aldrig hämtad".</summary>
        public DateTime CapturedAt { get; set; } = DateTime.Now;

        public List<BoardMeetingAwardRow> Rows { get; set; } = new();

        /// <summary>
        /// Sådant som hörde till listan när den hämtades men inte är en rad att pricka av: en
        /// medaljplats som väntar på särskjutning, ett mästerskap vars resultatlista inte räknats
        /// om, en medaljindelning som ändrats i efterhand.
        ///
        /// <para><b>⚠️ Ligger i SNAPSHOTTEN, inte i svaret.</b> En läsning räknar inte om något, så
        /// en varning som bara fanns vid hämtningen hade försvunnit vid nästa sidladdning och
        /// lämnat en lista som ser komplett ut. Det som gjorde listan osäker måste överleva lika
        /// länge som listan.</para>
        /// </summary>
        public List<string> Notes { get; set; } = new();

        // ── Mottagningsstatus ────────────────────────────────────────────────
        //
        // ⚠️ TRE lägen plus ett fjärde som är FRÅNVARON av läge. null = "inte upplast än", vilket
        // INTE betyder att medlemmen uteblev. Exakt samma regel som evenemangsuppropet: ett upprop
        // som aldrig togs får aldrig läsas som frånvaro. Ett årsmöte där sekreteraren inte hann
        // pricka av ska inte protokollföras som att ingen fick sitt märke.
        public const string StatusReceived = "Mottaget";
        public const string StatusAbsent = "Franvarande";
        public const string StatusLater = "Senare";

        public static bool IsValidStatus(string? s) =>
            s == null || s == StatusReceived || s == StatusAbsent || s == StatusLater;

        public static string StatusLabel(string? s) => s switch
        {
            StatusReceived => "Mottaget",
            StatusAbsent => "Ej närvarande",
            StatusLater => "Delas ut senare",
            _ => "Inte upplast"
        };

        public int ReceivedCount => Rows.Count(r => r.Status == StatusReceived);
        public int AbsentCount => Rows.Count(r => r.Status == StatusAbsent);
        public int LaterCount => Rows.Count(r => r.Status == StatusLater);
        public int UncalledCount => Rows.Count(r => r.Status == null);

        /// <summary>Antal fysiska föremål i listan (en uppfylld guldfodring mellan två steg är inget).</summary>
        public int OrderableCount => Rows.Count(r => r.Orderable);

        /// <summary>
        /// Distinkta mottagare. Räknas på medlem, inte på rad — en medlem kan få flera saker och är
        /// ändå en person som kallas fram en gång.
        /// </summary>
        public int RecipientCount => Rows.Select(r => r.MemberId).Distinct().Count();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

        /// <summary>
        /// Läser snapshotten. Returnerar null för tom/ogiltig data — <b>aldrig</b> ett tomt objekt:
        /// anroparen måste kunna skilja "ingen lista hämtad" från "en hämtad lista utan rader", för
        /// det första ska visa en hämta-knapp och det andra ska säga att året var tomt.
        /// </summary>
        public static BoardMeetingAwards? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var a = JsonSerializer.Deserialize<BoardMeetingAwards>(json, JsonOpts);
                if (a == null) return null;
                a.Rows ??= new List<BoardMeetingAwardRow>();
                a.Notes ??= new List<string>();
                return a;
            }
            catch (JsonException)
            {
                // Trasig data ska inte ta ner protokollet. Behandlas som "ingen lista".
                return null;
            }
        }

        /// <summary>
        /// Bygger en färsk lista ur klubbens utdelningsunderlag. Statusarna börjar tomma — inget
        /// kryssas av oss; det är sekreteraren som antecknar vad som faktiskt hände på mötet.
        /// </summary>
        public static BoardMeetingAwards FromOrderList(MarkenOrderList list)
        {
            var a = new BoardMeetingAwards { Year = list.Year, CapturedAt = DateTime.Now };
            foreach (var m in list.Handout)
                foreach (var i in m.Items)
                    a.Rows.Add(new BoardMeetingAwardRow
                    {
                        MemberId = m.MemberId,
                        Name = m.Name,
                        Group = i.Group,
                        Item = i.Item,
                        Detail = i.Detail,
                        Orderable = i.Orderable,
                        Unverified = i.Unverified
                    });
            return a;
        }

        /// <summary>Gruppnamnet för en placeringsmedalj från ett klubb- eller kretsmästerskap.</summary>
        public const string GroupChampionshipMedal = "Mästerskapsmedalj";

        /// <summary>
        /// Lägger årets mästerskapsmedaljer till en lista.
        ///
        /// <para><b>⚠️ TÄVLINGEN INGÅR I ARTIKELNAMNET, och det är inte kosmetik.</b>
        /// Sammanslagningen nycklas på (medlem, grupp, artikel), och en skytt kan mycket väl ta
        /// guld i C Dam på två av årets mästerskap. Utan tävlingen i artikeln blir det en enda
        /// nyckel för två medaljer, och avprickningen av den ena hade följt med den andra.</para>
        ///
        /// <para>Medaljplatser utan mottagare blir <see cref="Notes"/>, aldrig rader: en rad är en
        /// person som kallas fram, och ett gissat namn i ett upprop är det värsta utfallet.</para>
        /// </summary>
        public static void AddMedals(BoardMeetingAwards awards, MedalHandoutList medals)
        {
            foreach (var r in medals.Handout)
                foreach (var i in r.Items)
                    awards.Rows.Add(new BoardMeetingAwardRow
                    {
                        MemberId = r.MemberId,
                        Name = r.Name,
                        Group = GroupChampionshipMedal,
                        Item = $"{i.Medal} · {i.Category} · {i.CompetitionName}",
                        Detail = string.IsNullOrWhiteSpace(r.Club) ? i.Detail : $"{r.Club} · {i.Detail}"
                    });

            foreach (var u in medals.Unresolved)
                awards.Notes.Add($"{u.CompetitionName} · {u.Category}: {u.Text}");

            foreach (var w in medals.Warnings)
                awards.Notes.Add(w);
        }

        /// <summary>
        /// Ordnar raderna så en persons alla rader står tillsammans, i bokstavsordning.
        ///
        /// <para>⚠️ Måste köras när listan har FLERA källor. Märkena kommer per medlem ur
        /// märkesliggaren och medaljerna per medlem ur tävlingarna; läggs de bara efter varandra
        /// står samma person på två ställen — och ytan grupperar på "ny medlem sedan förra raden",
        /// så hen hade kallats fram två gånger.</para>
        /// </summary>
        public static void SortByRecipient(BoardMeetingAwards awards)
        {
            var sv = StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false);
            awards.Rows = awards.Rows
                .OrderBy(r => r.Name, sv)
                .ThenBy(r => r.MemberId)
                .ThenBy(r => r.Group, sv)
                .ThenBy(r => r.Item, sv)
                .ToList();
        }

        /// <summary>
        /// Slår ihop en NY hämtning med de statusar som redan antecknats, matchat på
        /// (medlem, grupp, artikel).
        ///
        /// <para><b>⚠️ Finns för att en hämtning mitt i mötet inte ska radera avprickningen.</b>
        /// Listan är härledd, så den kan ändras medan mötet pågår — någon validerar en kvarglömd
        /// serie, eller sekreteraren hämtade fel år först. Utan sammanslagningen vore
        /// "Hämta listan" en knapp som tystnadslöst slänger arbetet.</para>
        ///
        /// <para>Rader som försvunnit ur underlaget faller bort — men <b>en rad som hunnit få en
        /// status behålls</b>, för den beskriver något som redan hänt i rummet.</para>
        /// </summary>
        public static BoardMeetingAwards Merge(BoardMeetingAwards fresh, BoardMeetingAwards? existing)
        {
            if (existing == null || existing.Rows.Count == 0) return fresh;

            static string Key(BoardMeetingAwardRow r) =>
                $"{r.MemberId}|{(r.Group ?? "").Trim()}|{(r.Item ?? "").Trim()}";

            var old = new Dictionary<string, BoardMeetingAwardRow>();
            foreach (var r in existing.Rows) old[Key(r)] = r;

            foreach (var r in fresh.Rows)
                if (old.TryGetValue(Key(r), out var prev))
                {
                    r.Status = prev.Status;
                    r.Note = prev.Note;
                }

            // Behåll avprickade rader som inte längre finns i underlaget, och säg varför de står kvar.
            var freshKeys = new HashSet<string>(fresh.Rows.Select(Key));
            foreach (var r in existing.Rows)
                if (r.Status != null && !freshKeys.Contains(Key(r)))
                {
                    r.NoLongerInLedger = true;
                    fresh.Rows.Add(r);
                }

            return fresh;
        }
    }

    /// <summary>En sak en medlem ska få på årsmötet, plus vad som hände när namnet lästes upp.</summary>
    public class BoardMeetingAwardRow
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";

        /// <summary>"Pistolskyttemärket", "Årtalsmärken", "Standardmedaljer", …</summary>
        public string Group { get; set; } = "";

        /// <summary>Artikeln — valör, steg eller medalj.</summary>
        public string Item { get; set; } = "";

        /// <summary>Sammanhang som läses upp: guldnummer, disciplin, tävling.</summary>
        public string Detail { get; set; } = "";

        /// <summary>
        /// False för det som inte bär något fysiskt märke (en uppfylld guldfodring mellan två
        /// årtalsmärkessteg). Står kvar på listan eftersom det LÄSES UPP på årsmötet.
        /// </summary>
        public bool Orderable { get; set; } = true;

        /// <summary>Rapporterat men inte granskat av en funktionär. Namnges, aldrig tyst uteslutet.</summary>
        public bool Unverified { get; set; }

        /// <summary>null = inte upplast än. Se konstanterna på <see cref="BoardMeetingAwards"/>.</summary>
        public string? Status { get; set; }

        /// <summary>Fri anteckning — vem som tog emot i medlemmens ställe, när det ska delas ut.</summary>
        public string? Note { get; set; }

        /// <summary>
        /// Sattes vid en omhämtning: raden är avprickad men finns inte längre i märkesliggaren
        /// (märket makulerades eller rättades efter mötet). Behålls och FLAGGAS, aldrig raderad —
        /// protokollet beskriver vad som hände i rummet, inte vad liggaren säger i efterhand.
        /// </summary>
        public bool NoLongerInLedger { get; set; }
    }
}
