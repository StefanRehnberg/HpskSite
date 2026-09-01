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
