using FluentAssertions;
using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Deltagarens val av prisrad vid anmälan.
    ///
    /// <para><b>⚠️ DET HÄR AVGÖR VAD EN MEDLEM DEBITERAS.</b> Går det fel står någon som anmäld
    /// till ett belopp hen aldrig pekat på, och felet upptäcks först vid betalningen — av
    /// medlemmen, inte av oss.</para>
    ///
    /// <para><b>Den bärande regeln: plocka ALDRIG "första raden".</b> Har evenemanget flera priser
    /// och inget giltigt val kom in ska anmälan vägras, inte gissas.</para>
    /// </summary>
    public class EventPriceChoiceTests
    {
        private static ClubEventContext Ctx(params EventPrice[] rows) => new()
        {
            Prices = EventPrices.Parse(EventPrices.Serialize(rows))
        };

        private static ClubEventContext Trasig() => new()
        {
            Prices = EventPrices.Parse("{ inte json")
        };

        private static EventPrice P(string id, string label, decimal amount) => new(id, label, amount);

        // ── Ingen avgift ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Utan_priser_finns_inget_att_valja_och_anmalan_slapps_igenom()
        {
            var ctx = Ctx();
            ClubEventParticipationService.ResolvePriceChoice(ctx, null).Should().BeNull();
            ClubEventParticipationService.PriceChoiceError(ctx, null).Should().BeNull();
        }

        // ── Ett pris väljer sig självt ──────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nagot-annat")]
        public void Ett_enda_pris_valjs_oavsett_vad_som_skickades(string? sent)
        {
            var ctx = Ctx(P("avgift", "Avgift", 300m));
            var vald = ClubEventParticipationService.ResolvePriceChoice(ctx, sent);
            vald!.Amount.Should().Be(300m);
            ClubEventParticipationService.PriceChoiceError(ctx, sent).Should().BeNull();
        }

        // ── ⚠️ FLERA PRISER: kärnan ─────────────────────────────────────────────────────────

        [Fact]
        public void Flera_priser_och_ett_giltigt_val_ger_den_raden()
        {
            var ctx = Ctx(P("vuxen", "Vuxen", 180m), P("barn", "Barn 7-15", 90m));
            var vald = ClubEventParticipationService.ResolvePriceChoice(ctx, "barn");
            vald!.Label.Should().Be("Barn 7-15");
            vald.Amount.Should().Be(90m);
            ClubEventParticipationService.PriceChoiceError(ctx, "barn").Should().BeNull();
        }

        /// <summary>
        /// ⚠️ Utan val: ingen rad plockas, och anmälan MÅSTE vägras. Att ta den första hade satt
        /// 180 kr på ett barn vars förälder aldrig valde det.
        /// </summary>
        [Fact]
        public void Flera_priser_utan_val_plockar_aldrig_forsta_raden()
        {
            var ctx = Ctx(P("vuxen", "Vuxen", 180m), P("barn", "Barn 7-15", 90m));
            ClubEventParticipationService.ResolvePriceChoice(ctx, null).Should().BeNull();

            var fel = ClubEventParticipationService.PriceChoiceError(ctx, null);
            fel.Should().NotBeNull();
            // Felet ska RÄKNA UPP alternativen — "välj ett pris" utan att säga vilka är en gåta.
            fel!.Should().Contain("Vuxen").And.Contain("Barn 7-15");
        }

        /// <summary>
        /// Ett id som inte finns är något annat än inget val alls: raden kan ha tagits bort medan
        /// formuläret stod öppet. Meddelandet ska säga det, så medlemmen laddar om i stället för
        /// att leta efter en knapp hen redan tryckt på.
        /// </summary>
        [Fact]
        public void Okant_id_sager_att_raden_ar_borta_inte_att_val_saknas()
        {
            var ctx = Ctx(P("vuxen", "Vuxen", 180m), P("barn", "Barn 7-15", 90m));
            var fel = ClubEventParticipationService.PriceChoiceError(ctx, "finns-inte");
            fel.Should().NotBeNull();
            fel!.ToLowerInvariant().Should().Contain("finns inte");
        }

        // ── ⚠️ Oläsbart är inte gratis ──────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Både "ingen avgift" och "oläsbart" ger null från ResolvePriceChoice, men de betyder
        /// motsatta saker. Utan PriceChoiceError hade en trasig prislista tyst släppt igenom en
        /// gratis anmälan till ett evenemang som kostar pengar.
        /// </summary>
        [Fact]
        public void Olasbara_priser_vagrar_anmalan_i_stallet_for_att_bli_gratis()
        {
            var ctx = Trasig();
            ctx.Prices.Unreadable.Should().BeTrue();
            ClubEventParticipationService.ResolvePriceChoice(ctx, null).Should().BeNull();

            var fel = ClubEventParticipationService.PriceChoiceError(ctx, null);
            fel.Should().NotBeNull("en trasig prislista far aldrig ge en gratis anmalan");
            fel!.ToLowerInvariant().Should().Contain("ingen anmalan gjordes");
        }

        // ── Noll kronor är ett pris ─────────────────────────────────────────────────────────

        [Fact]
        public void Gratisraden_gar_att_valja_och_ger_beloppet_noll()
        {
            var ctx = Ctx(P("vuxen", "Vuxen", 180m), P("liten", "Under 7 ar", 0m));
            var vald = ClubEventParticipationService.ResolvePriceChoice(ctx, "liten");
            vald!.Amount.Should().Be(0m);
            vald.Label.Should().Be("Under 7 ar");
            ClubEventParticipationService.PriceChoiceError(ctx, "liten").Should().BeNull();
        }

        // ── Prods Sommarfest, hela vägen ────────────────────────────────────────────────────

        [Fact]
        public void Prods_sommarfest_tre_priser_och_varje_val_ger_ratt_belopp()
        {
            var ctx = Ctx(P("vuxen", "Vuxen", 180m), P("barn-7-15", "Barn 7-15", 90m), P("under-7-ar", "Under 7 ar", 0m));

            ClubEventParticipationService.ResolvePriceChoice(ctx, "vuxen")!.Amount.Should().Be(180m);
            ClubEventParticipationService.ResolvePriceChoice(ctx, "barn-7-15")!.Amount.Should().Be(90m);
            ClubEventParticipationService.ResolvePriceChoice(ctx, "under-7-ar")!.Amount.Should().Be(0m);
            ClubEventParticipationService.PriceChoiceError(ctx, null).Should().NotBeNull();
        }
    }
}
