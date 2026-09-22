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
