using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Evenemangets prisrader.
    ///
    /// <para><b>⚠️ Det farligaste felet här är TYST:</b> en prislista som inte går att läsa och som
    /// tolkas som "ingen avgift" gör evenemanget gratis på skärmen. Ingen upptäcker det förrän
    /// någon står vid kassan. Därför prövas "oläsbar" och "ingen avgift" som två skilda utfall,
    /// överallt.</para>
    /// </summary>
    public class EventPricesTests
    {
        private static EventPrice P(string id, string label, decimal amount) => new(id, label, amount);

        // ── Tomt och trasigt ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Tom_egenskap_ar_ingen_avgift_inte_olasbar(string? raw)
        {
            var r = EventPrices.Parse(raw);
            r.IsFree.Should().BeTrue();
            r.Unreadable.Should().BeFalse();
        }

        /// <summary>
        /// ⚠️ Kärnpåståendet. Oläsbart får ALDRIG bli "gratis" — det är skillnaden mellan att säga
        /// "vi vet inte" och att hitta på ett pris.
        /// </summary>
        [Theory]
        [InlineData("{ inte json")]
        [InlineData("[{\"label\":\"Vuxen\"}]")]              // id saknas
        [InlineData("[{\"id\":\"vuxen\"}]")]                  // etikett saknas
        [InlineData("[{\"id\":\"v\",\"label\":\"V\",\"amount\":-5}]")]
        [InlineData("[{\"id\":\"v\",\"label\":\"A\",\"amount\":1},{\"id\":\"v\",\"label\":\"B\",\"amount\":2}]")]
        public void Olasbart_sager_olasbart_och_aldrig_gratis(string raw)
        {
            var r = EventPrices.Parse(raw);
            r.Unreadable.Should().BeTrue();
            r.IsFree.Should().BeFalse("en trasig lista far aldrig lasas som gratis");
            r.Rows.Should().BeEmpty();
        }

        // ── Den vanliga formen ──────────────────────────────────────────────────────────────

        [Fact]
        public void Ett_pris_ar_den_enkla_formen()
        {
            var json = EventPrices.Serialize(new[] { P("avgift", "Avgift", 300m) });
            var r = EventPrices.Parse(json);
            r.IsSingle.Should().BeTrue();
            r.IsFree.Should().BeFalse();
            r.Rows[0].Amount.Should().Be(300m);
        }

        [Fact]
        public void Round_trip_bevarar_id_etikett_och_belopp()
        {
            var rows = new[] { P("vuxen", "Vuxen", 180m), P("barn", "Barn 7-15", 90m), P("liten", "Under 7 ar", 0m) };
            var back = EventPrices.Parse(EventPrices.Serialize(rows)).Rows;
            back.Should().HaveCount(3);
            back.Select(x => x.Id).Should().ContainInOrder("vuxen", "barn", "liten");
            back[2].Amount.Should().Be(0m);
        }

        /// <summary>
        /// ⚠️ 0 är ett PRIS, inte en tom rad. "Under 7 år: 0 kr" är en uppgift arrangören lämnat,
        /// och den ska visas. Samma regel som tävlingarnas juniorAvgift.
        /// </summary>
        [Fact]
        public void Noll_ar_ett_pris_inte_en_tom_rad()
        {
            var r = EventPrices.Parse(EventPrices.Serialize(new[] { P("liten", "Under 7 ar", 0m) }));
            r.IsFree.Should().BeFalse("listan har en rad - evenemanget ar inte utan avgift");
            r.Rows[0].Amount.Should().Be(0m);
        }

        // ── ⚠️ ID-STABILITETEN, som hela snapshotmodellen hanger pa ──────────────────────────

        /// <summary>
        /// Id genereras EN gång, ur etiketten. Ändras etiketten senare står id:t still — annars
        /// tappar varje redan anmäld deltagare kopplingen till raden de valde.
        /// </summary>
        [Fact]
        public void Id_genereras_ur_etiketten_men_ar_inte_bundet_till_den()
        {
            var id = EventPrices.NewId("Barn 7-15", Array.Empty<string>());
            id.Should().Be("barn-7-15");

            // Etiketten ändras, id:t följer med oförändrat in i den sparade raden.
            var rows = new[] { P(id, "Barn 7-14", 90m) };
            EventPrices.Parse(EventPrices.Serialize(rows)).Rows[0].Id.Should().Be("barn-7-15");
        }

        [Fact]
        public void Kolliderande_id_far_lopnummer()
        {
            EventPrices.NewId("Vuxen", new[] { "vuxen" }).Should().Be("vuxen-2");
            EventPrices.NewId("Vuxen", new[] { "vuxen", "vuxen-2" }).Should().Be("vuxen-3");
        }

        /// <summary>
        /// ⚠️ Diakriter fars INTE bort. "Över 65" och "Over 65" ar olika ord, och att folda dem
        /// hade slagit ihop tva rader till ett id. Samma regel som medlemsdedupens namnnyckel.
        /// </summary>
        [Fact]
        public void Diakriter_foldas_inte_bort()
        {
            EventPrices.NewId("Över 65", Array.Empty<string>()).Should().Be("över-65");
        }

        [Fact]
        public void Tom_etikett_ger_anda_ett_anvandbart_id()
            => EventPrices.NewId("", Array.Empty<string>()).Should().Be("pris");

        // ── Valideringen ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Tom_lista_ar_giltig_det_ar_det_vanliga_fallet()
            => EventPrices.Validate(Array.Empty<EventPrice>()).Should().BeNull();

        [Fact]
        public void Giltig_lista_slapps_igenom()
            => EventPrices.Validate(new[] { P("vuxen", "Vuxen", 180m), P("barn", "Barn", 90m) })
                .Should().BeNull();

        [Theory]
        [InlineData("", 100, "namn")]
        [InlineData("Vuxen", -1, "negativt")]
        [InlineData("Vuxen", 99999, "högt")]
        public void Felaktig_rad_namnger_vad_som_ar_fel(string label, decimal amount, string expect)
        {
            var msg = EventPrices.Validate(new[] { P("x", label, amount) });
            msg.Should().NotBeNull();
            msg!.ToLowerInvariant().Should().Contain(expect.ToLowerInvariant());
        }

        /// <summary>
        /// Två rader med samma NAMN är inte ett datafel men en omöjlig valsituation: medlemmen ser
        /// två likadana alternativ och kan inte veta vilket som är vilket.
        /// </summary>
        [Fact]
        public void Tva_rader_med_samma_namn_avvisas()
        {
            var msg = EventPrices.Validate(new[] { P("a", "Vuxen", 180m), P("b", "vuxen", 200m) });
            msg.Should().NotBeNull();
            msg!.Should().Contain("Vuxen");
        }

        [Fact]
        public void Fler_rader_an_taket_avvisas()
        {
            var many = Enumerable.Range(1, EventPrices.MaxRows + 1)
                                 .Select(i => P($"p{i}", $"Pris {i}", i)).ToArray();
            EventPrices.Validate(many).Should().NotBeNull();
        }

        // ── Uppslaget som anmälan använder ──────────────────────────────────────────────────

        [Fact]
        public void ById_hittar_raden_och_tal_skrap()
        {
            var list = EventPrices.Parse(EventPrices.Serialize(new[] { P("vuxen", "Vuxen", 180m) }));
            list.ById("vuxen")!.Amount.Should().Be(180m);
            list.ById("finns-inte").Should().BeNull();
            list.ById(null).Should().BeNull();
            list.ById("").Should().BeNull();
        }

        // ── ⚠️ PROD:S EGET FALL ─────────────────────────────────────────────────────────────

        /// <summary>
        /// "180 spänn per vuxen, 90 för barn och småttingar gratis." — fritexten som inget enskilt
        /// tal kunde uttrycka, och hela skälet prisraderna finns. Så här ser den ut som data.
        /// </summary>
        [Fact]
        public void Prods_sommarfest_gar_att_uttrycka_som_rader()
        {
            var rows = new[]
            {
                P(EventPrices.NewId("Vuxen", Array.Empty<string>()), "Vuxen", 180m),
                P(EventPrices.NewId("Barn 7-15", new[] { "vuxen" }), "Barn 7-15", 90m),
                P(EventPrices.NewId("Under 7 ar", new[] { "vuxen", "barn-7-15" }), "Under 7 ar", 0m),
            };
            EventPrices.Validate(rows).Should().BeNull();

            var back = EventPrices.Parse(EventPrices.Serialize(rows));
            back.Rows.Should().HaveCount(3);
            back.IsSingle.Should().BeFalse();
            back.Rows.Sum(r => r.Amount).Should().Be(270m);
        }
    }
}
