namespace HpskSite.Models
{
    /// <summary>
    /// De två formatreglerna Swish ställer på en betalning, samlade där de går att mäta.
    ///
    /// <para><b>⚠️ BÅDA HAR REDAN KRASCHAT EN GÅNG.</b> Första utsågan skickade beloppet som
    /// <c>"450"</c> och referensen okapad. <c>SwishQrCodeGenerator</c> kastar
    /// <see cref="ArgumentException"/> på båda — alltså ett undantag vid FÖRSTA klicket på
    /// "Betala med Swish", inte ett felmeddelande. Reglerna ligger därför här och inte som privata
    /// metoder i en controller: en regel som bara går att pröva genom hela stacken blir inte
    /// prövad.</para>
    /// </summary>
    public static class EventPaymentFormat
    {
        /// <summary>Swish kapar meddelandet vid 50 tecken (rå QR-gräns).</summary>
        public const int MaxReferenceLength = 50;

        /// <summary>
        /// Beloppet som Swish vill ha det.
        ///
        /// <para><b>⚠️ EXAKT två decimaler med PUNKT.</b> <c>SwishQrCodeGenerator.IsAmountOk</c>
        /// jämför STRÄNGEN mot <c>d.ToString("0.00")</c>, inte talet — så "450" och "450,00"
        /// avvisas båda. Kulturen måste vara invariant: svensk kultur ger komma, och då kastar
        /// generatorn.</para>
        /// </summary>
        public static string Amount(decimal amount)
            => amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Meddelandet betalaren skickar med, och det arrangören känner igen i Swish-appen.
        ///
        /// <para><b>⚠️ BETALNINGSNUMRET FÅR ALDRIG KAPAS.</b> Evenemangets namn ensamt räcker inte
        /// när tre personer betalar samma kväll — numret är det som gör raden unik, så det är
        /// NAMNET som förkortas när det inte får plats. Motsatt ordning hade gett femtio tecken
        /// evenemangsnamn och ingen möjlighet att para ihop betalningen med anmälan.</para>
        /// </summary>
        public static string Reference(string? eventName, int paymentId)
        {
            var tail = $" {paymentId}";
            var name = (eventName ?? "").Trim();

            // ⚠️ Numret ensamt kan i teorin fylla hela utrymmet. Då är namnet borta, och det är
            // rätt prioritering — utan numret går betalningen inte att härleda till någon.
            if (tail.Length >= MaxReferenceLength) return tail.Trim();

            var room = MaxReferenceLength - tail.Length;
            if (name.Length > room) name = name[..room];
            return (name + tail).Trim();
        }
    }
}
