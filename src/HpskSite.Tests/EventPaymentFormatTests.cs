using FluentAssertions;
using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Swishens två formatkrav.
    ///
    /// <para><b>⚠️ DE HÄR TESTEN FINNS FÖR ATT BÅDA REGLERNA REDAN HAR KRASCHAT.</b> Första
    /// utsågan skickade beloppet som <c>"450"</c> och referensen okapad, och
    /// <c>SwishQrCodeGenerator</c> kastar <see cref="System.ArgumentException"/> på båda — alltså
    /// ett undantag vid FÖRSTA klicket på "Betala med Swish", inte ett felmeddelande.</para>
    ///
    /// <para>⚠️ Varje beloppstest prövas mot <c>SwishQrCodeGenerator</c> SJÄLV och inte mot en
    /// avskrift av dess regel. En egen tolkning av "två decimaler" hade kunnat vara grön mot sig
    /// själv och röd mot verkligheten.</para>
    /// </summary>
    public class EventPaymentFormatTests
    {
        // ── Beloppet ────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(450)]
        [InlineData(180)]
        [InlineData(0.5)]
        [InlineData(1234.56)]
        [InlineData(90.1)]
        public void Beloppet_godtas_av_swishgeneratorn(decimal amount)
        {
            var s = EventPaymentFormat.Amount(amount);
            // ⚠️ Mätt mot generatorns EGEN kontroll, inte mot en avskrift av den.
            SwishQrCodeGenerator.IsAmountOk(s).Should().BeTrue($"\"{s}\" maste godtas");
        }

        /// <summary>
        /// ⚠️ Kontrollprov: de former som INTE godtas. Utan det kunde testet ovan vara grönt även
        /// om <c>IsAmountOk</c> svarade sant på allt.
        /// </summary>
        [Theory]
        [InlineData("450")]
        [InlineData("450,00")]
        [InlineData("450.0")]
        [InlineData("450.000")]
        public void Fel_former_avvisas_av_generatorn(string raw)
            => SwishQrCodeGenerator.IsAmountOk(raw).Should().BeFalse();

        /// <summary>⚠️ Svensk kultur ger komma. Formateringen MÅSTE vara invariant, annars kastar
        /// generatorn på en maskin med sv-SE — alltså på varje maskin vi kör på.</summary>
        [Fact]
        public void Kulturen_far_inte_avgora_decimaltecknet()
        {
            var before = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("sv-SE");
                EventPaymentFormat.Amount(450m).Should().Be("450.00");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = before; }
        }

        // ── Referensen ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Referensen_bar_bade_namnet_och_numret()
            => EventPaymentFormat.Reference("Sommarfest", 42).Should().Be("Sommarfest 42");

        /// <summary>
        /// ⚠️⚠️ NUMRET FÅR ALDRIG KAPAS. Det är det som gör betalningen härledbar till en anmälan —
        /// evenemangets namn ensamt räcker inte när tre personer betalar samma kväll. Kapas numret
        /// i stället för namnet står arrangören med en Swish-rad hen inte kan para ihop med någon.
        /// </summary>
        [Fact]
        public void Ett_langt_namn_kapas_men_aldrig_numret()
        {
            var name = new string('A', 80);
            var r = EventPaymentFormat.Reference(name, 12345);

            r.Length.Should().BeLessThanOrEqualTo(EventPaymentFormat.MaxReferenceLength);
            r.Should().EndWith(" 12345");
        }

        [Theory]
        [InlineData("Sommarfest", 1)]
        [InlineData("Nybörjarkurs steg 1 med extra långt namn som fortsätter", 999999)]
        [InlineData("", 7)]
        [InlineData(null, 7)]
        public void Referensen_haller_gransen_och_bar_numret(string? name, int id)
        {
            var r = EventPaymentFormat.Reference(name, id);
            r.Length.Should().BeLessThanOrEqualTo(EventPaymentFormat.MaxReferenceLength);
            r.Should().Contain(id.ToString());
        }

        /// <summary>Referensen måste också passera generatorn — den kapar själv vid 50, men då har
        /// den redan kapat NUMRET, vilket är precis det vi inte vill.</summary>
        [Fact]
        public void Referensen_kapas_av_oss_och_inte_av_generatorn()
        {
            var name = new string('B', 200);
            var r = EventPaymentFormat.Reference(name, 4711);

            // Generatorn kapar vid 50 utan att bry sig om vad som försvinner.
            var payload = SwishQrCodeGenerator.GetSwishUrl("0701234567", "100.00", r);
            payload.Should().Contain("4711", "numret maste overleva hela vagen ut i QR-koden");
        }
    }
}
