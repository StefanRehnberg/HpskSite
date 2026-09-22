namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ekonomiöversikten — de tre panelerna ur underlaget till Varbergs PK
    /// (<c>/ekonomifragor/wf</c>): <b>att få in</b>, <b>kommit in</b> och <b>per tävling</b>.
    ///
    /// <para><b>⚠️⚠️ DEN TREDJE PANELEN ÄR HELA POÄNGEN.</b> "48 anmälda, 44 betalt, 4 saknas" är
    /// siffran Fredrik beskrev som okänd, och den <b>kan inte finnas i bokföringen</b>: bokföringen
    /// ser vad som kom in, aldrig vad som borde ha kommit in. Det är den enda nyttan vi tillför en
    /// klubb som redan har ett fungerande bokföringsprogram — bygg aldrig bort den för att
    /// förenkla.</para>
    ///
    /// <para><b>⚠️ Översikten visas för ALLA föreningsformer.</b> En som bokför hos oss behöver
    /// samma tre svar som en som inte gör det; skillnaden är vad som ligger <i>under</i> den.</para>
    /// </summary>
    public class LedgerOverview
    {
        /// <summary>Summan av allt som begärts men inte kommit in.</summary>
        public decimal OutstandingTotal { get; set; }

        /// <summary>Panel 1, uppdelad per avgiftsslag.</summary>
        public List<OutstandingGroup> Outstanding { get; } = new();

        /// <summary>Panel 2 — de senast mottagna betalningarna, nyast först.</summary>
        public List<ReceivedRow> Received { get; } = new();

        /// <summary>Panel 3 — en rad per tävling eller händelse som har avgifter.</summary>
        public List<SourceCompleteness> PerSource { get; } = new();

        /// <summary>
        /// Panel 4 för den förening som <b>bokför här</b> — de senast bokförda verifikationerna.
        ///
        /// <para><b>⚠️⚠️ PANELEN LÄSTE BARA BETALNINGSRADER, OCH DÅ VAR DEN TOM.</b> En klubb som
        /// bokfört ett helt år med manuella verifikationer fick svaret "Inga betalningar ännu" på
        /// frågan "vad hände senast?". Panelens egen text sa varför: <i>"de flesta posterna skriver
        /// ingen människa — de faller ut ur anmälningar och avgifter"</i>. Det är sant för en klubb
        /// vars enda ekonomi är tävlingsavgifter, och <b>falskt för den som för hela sin bokföring
        /// hos oss</b> — alltså precis den vi bygger liggaren för.</para>
        ///
        /// <para><b>⚠️ Tom lista betyder "föreningen bokför inte här", inte "inget har hänt"</b> —
        /// då står panelen kvar på betalningsraderna. Ingen form att läsa av, ingen flagga att
        /// hålla i takt: finns det verifikationer bokför de här, och då är verifikationerna det
        /// sannare svaret. En bekräftad betalning bär alltid en verifikation hos en sådan förening,
        /// så listorna dubblerar inte varandra.</para>
        /// </summary>
        public List<JournalRow> Journal { get; } = new();
    }

    /// <summary>En bokförd verifikation, så som panel 4 visar den.</summary>
    public class JournalRow
    {
        public int EntryId { get; set; }

        /// <summary>Verifikationsnumret, formaterat (<c>A-14</c>).</summary>
        public string Number { get; set; } = "";

        /// <summary>Bokföringsdatumet — det som avgör period, inte systemtiden.</summary>
        public DateTime Date { get; set; }

        public string Description { get; set; } = "";

        /// <summary>Motpartens namn ur verifikationens snapshot. Tom när posten saknar motpart.</summary>
        public string Counterparty { get; set; } = "";

        /// <summary>
        /// Verifikationens omslutning — summan av debetsidan.
        ///
        /// <para><b>⚠️ Ett ENDA tal, och det är summan av EN sida.</b> Debet och kredit är lika
        /// stora i en balanserad verifikation, så att summera båda hade visat dubbelt belopp. Ett
        /// "netto" finns inte att visa: en verifikation har inget tecken, det har kontona.</para>
        /// </summary>
        public decimal Amount { get; set; }
    }

    /// <summary>Ett avgiftsslag som ännu inte kommit in.</summary>
    public class OutstandingGroup
    {
        /// <summary>"Anmälningsavgifter", "Medlemsavgifter", … Klartext, inte nyckeln.</summary>
        public string Label { get; set; } = "";

        public int Count { get; set; }

        public decimal Amount { get; set; }
    }

    /// <summary>En mottagen betalning, så som panel 2 visar den.</summary>
    public class ReceivedRow
    {
        public int PaymentId { get; set; }

        public DateTime Date { get; set; }

        public string PayerName { get; set; } = "";

        /// <summary>Vad den avsåg — tävlingens namn när det finns, annars avgiftsslaget.</summary>
        public string What { get; set; } = "";

        public decimal Amount { get; set; }

        /// <summary>
        /// Kvittonumret, formaterat (<c>K-212</c>). Null när kvitto saknas.
        /// <para>⚠️ <b>Kvittonummer, inte verifikationsnummer.</b> Två serier, två syften — en
        /// hopblandning här hade visat bokföringens interna numrering för en betalare.</para>
        /// </summary>
        public string? ReceiptNumber { get; set; }

        public int? ReceiptId { get; set; }
    }

    /// <summary>
    /// En tävlings eller händelses avgiftsläge: hur många som förväntas, hur många som betalat,
    /// och vad som fattas.
    /// </summary>
    public class SourceCompleteness
    {
        public string SourceType { get; set; } = "";

        public int SourceId { get; set; }

        /// <summary>Tävlingens namn ur noden. Faller tillbaka på avgiftsslaget om noden är borta.</summary>
        public string Name { get; set; } = "";

        /// <summary>Antal betalningsrader — alltså hur många avgifter som förväntas.</summary>
        public int Expected { get; set; }

        /// <summary>Hur många som faktiskt kommit in.</summary>
        public int Settled { get; set; }

        public int MissingCount => Expected - Settled;

        public decimal MissingAmount { get; set; }
    }
}
