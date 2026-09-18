using FluentAssertions;
using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Sällskapet: en medlem och de hen tagit med sig.
    ///
    /// <para><b>⚠️ DEN BÄRANDE REGELN: EN RAD ÄR EN PERSON.</b> Hugo som anmäler sig själv, sin fru
    /// och sin son är tre rader. Vore de en rad med "+2" skulle platserna räknas fel, avgiften bli
    /// en tredjedel av den rätta, och uppropet ha en bock att sätta på tre personer — tre fel av
    /// ett modellval, och alla tre tysta.</para>
    ///
    /// <para>Testerna här mäter <see cref="ClubEventParticipationService.BuildParty"/>, som är det
    /// enda stället summan räknas. Skrivvägarna (gäst in, gäst ut, kaskaden) sitter mot databasen
    /// och mäts av <c>hpsk-verify/club-event-guests-verify.sql</c>.</para>
    /// </summary>
    public class ClubEventPartyTests
    {
        private const int Hugo = 4711;
        private const int Annan = 9001;

        private static ClubEventRoster Roster(EventPriceList prices, params ClubEventRosterRow[] rows)
        {
            var r = new ClubEventRoster { Context = new ClubEventContext { Prices = prices } };
            r.Rows.AddRange(rows);
            return r;
        }

        private static EventPriceList Priser(params (string Id, string Label, decimal Amount)[] rows)
            => EventPrices.Parse(EventPrices.Serialize(
                rows.Select(x => new EventPrice(x.Id, x.Label, x.Amount))));

        private static EventPriceList Gratis() => new();

        private static ClubEventRosterRow Medlem(int id, decimal? fee, string label = "Vuxen") => new()
        {
            Id = 100 + id,
            MemberId = id,
            IsGuest = false,
            Name = $"Medlem {id}",
            SignedUpAt = DateTime.Now,
            FeeAmount = fee,
            FeeLabel = fee == null ? null : label
        };

        private static ClubEventRosterRow Gast(int rowId, int hostId, string name, decimal? fee, string? label) => new()
        {
            Id = rowId,
            MemberId = ClubEvents.GuestMemberId,
            IsGuest = true,
            GuestOfMemberId = hostId,
            Name = name,
            SignedUpAt = DateTime.Now,
            FeeAmount = fee,
            FeeLabel = label
        };

        // ── Kärnan: familjen på tre ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Prods Sommarfest, hela vägen: Hugo 180, frun 180, sonen 90. <b>450 kr och tre platser.</b>
        /// Det här är hela skälet gästerna är egna rader.
        /// </summary>
        [Fact]
        public void Hugo_med_fru_och_son_ar_tre_personer_och_450_kronor()
        {
            var roster = Roster(
                Priser(("vuxen", "Vuxen", 180m), ("barn", "Barn 7-15", 90m)),
                Medlem(Hugo, 180m),
                Gast(201, Hugo, "Karin", 180m, "Vuxen"),
                Gast(202, Hugo, "Emil", 90m, "Barn 7-15"));

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);

            party.People.Should().Be(3, "sallskapet tar TRE platser, inte en");
            party.Total.Should().Be(450m);
            party.Guests.Should().HaveCount(2);
            party.MissingPrice.Should().BeFalse();
            party.HasGuests.Should().BeTrue();
        }

        /// <summary>
        /// ⚠️ Gratisraden är ett pris. Är lillasyster under sju år kostar hon 0, och hon tar ändå
        /// en plats. Att inte räkna henne för att hon är gratis är exakt det fel som gör att
        /// arrangören ställer fram för få stolar.
        /// </summary>
        [Fact]
        public void Gratis_gast_kostar_noll_men_tar_anda_en_plats()
        {
            var roster = Roster(
                Priser(("vuxen", "Vuxen", 180m), ("liten", "Under 7 ar", 0m)),
                Medlem(Hugo, 180m),
                Gast(201, Hugo, "Alva", 0m, "Under 7 ar"));

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);

            party.People.Should().Be(2);
            party.Total.Should().Be(180m);
            party.MissingPrice.Should().BeFalse();
        }

        // ── ⚠️ Någon annans gäster är inte mina ─────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Utan <c>GuestOfMemberId</c>-filtret hade varje gäst på evenemanget hamnat i varje
        /// medlems sällskap — och alltså på varje medlems faktura.
        /// </summary>
        [Fact]
        public void En_annan_medlems_gast_raknas_aldrig_in_i_mitt_sallskap()
        {
            var roster = Roster(
                Priser(("vuxen", "Vuxen", 180m)),
                Medlem(Hugo, 180m),
                Gast(201, Hugo, "Karin", 180m, "Vuxen"),
                Medlem(Annan, 180m),
                Gast(202, Annan, "Bosse", 180m, "Vuxen"));

            var hugos = ClubEventParticipationService.BuildParty(roster, Hugo);
            hugos.People.Should().Be(2);
            hugos.Total.Should().Be(360m);
            hugos.Guests.Should().ContainSingle().Which.Name.Should().Be("Karin");

            var andras = ClubEventParticipationService.BuildParty(roster, Annan);
            andras.Guests.Should().ContainSingle().Which.Name.Should().Be("Bosse");
        }

        /// <summary>
        /// ⚠️⚠️ En gästrad bär <c>MemberId = 0</c>. Fångar sällskapet på siffran i stället för på
        /// <c>IsGuest</c> blir varje gäst på evenemanget "min egen anmälan" för en utloggad
        /// besökare, vars medlems-id också är 0. Det är en läcka, inte ett räknefel.
        /// </summary>
        [Fact]
        public void MemberId_noll_hamtar_aldrig_en_gast_som_sin_egen_rad()
        {
            var roster = Roster(
                Priser(("vuxen", "Vuxen", 180m)),
                Gast(201, Hugo, "Karin", 180m, "Vuxen"),
                Gast(202, Hugo, "Emil", 90m, "Barn 7-15"));

            var party = ClubEventParticipationService.BuildParty(roster, ClubEvents.GuestMemberId);

            party.Self.Should().BeNull("en gastrad ar aldrig nagons egna anmalan");
            party.Guests.Should().BeEmpty("ingen ar gast hos medlem 0");
            party.People.Should().Be(0);
            party.Total.Should().Be(0m);
        }

        // ── Avbokade räknas inte ────────────────────────────────────────────────────────────────

        [Fact]
        public void Avbokad_gast_tar_varken_plats_eller_pengar()
        {
            var avbokad = Gast(202, Hugo, "Emil", 90m, "Barn 7-15");
            avbokad.Cancelled = true;

            var roster = Roster(
                Priser(("vuxen", "Vuxen", 180m), ("barn", "Barn 7-15", 90m)),
                Medlem(Hugo, 180m),
                Gast(201, Hugo, "Karin", 180m, "Vuxen"),
                avbokad);

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);
            party.People.Should().Be(2);
            party.Total.Should().Be(360m);
        }

        // ── ⚠️ Saknat belopp betyder två olika saker ────────────────────────────────────────────

        /// <summary>
        /// ⚠️⚠️ På ett GRATIS evenemang är belopp null helt korrekt, och sällskapet är fullständigt.
        /// Utan frågan till evenemangets prisrader hade varje gratis sällskap flaggats som
        /// ofullständigt — och en varning som alltid lyser slutar folk att läsa, precis i tid till
        /// den gång den betyder något.
        /// </summary>
        [Fact]
        public void Gratis_evenemang_flaggas_inte_som_saknat_pris()
        {
            var roster = Roster(
                Gratis(),
                Medlem(Hugo, null),
                Gast(201, Hugo, "Karin", null, null));

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);

            party.People.Should().Be(2);
            party.Total.Should().Be(0m);
            party.MissingPrice.Should().BeFalse("gratis evenemang SKA sakna belopp");
        }

        /// <summary>
        /// Och tvärtom: tar evenemanget avgift men en rad saknar belopp är summan inte hela
        /// sanningen, och kortet måste säga det i stället för att visa en total som ser färdig ut.
        /// </summary>
        [Fact]
        public void Saknat_belopp_pa_avgiftsbelagt_evenemang_flaggas()
        {
            var roster = Roster(
                Priser(("vuxen", "Vuxen", 180m), ("barn", "Barn 7-15", 90m)),
                Medlem(Hugo, 180m),
                Gast(201, Hugo, "Karin", null, null));

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);

            party.MissingPrice.Should().BeTrue();
            party.Total.Should().Be(180m, "summan racknar bara det som FINNS, och ar darfor inte hela sanningen");
        }

        /// <summary>
        /// ⚠️ Oläsbara prisrader är inte gratis. Ett sällskap på ett evenemang vars priser inte går
        /// att läsa måste flaggas, annars läses tystnaden som noll kronor.
        /// </summary>
        [Fact]
        public void Olasbara_priser_flaggar_sallskapet_i_stallet_for_att_bli_gratis()
        {
            var roster = Roster(
                EventPrices.Parse("{ inte json"),
                Medlem(Hugo, null),
                Gast(201, Hugo, "Karin", null, null));

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);

            party.MissingPrice.Should().BeTrue("olasbart far aldrig lasas som ingen avgift");
        }

        // ── Ensam medlem ────────────────────────────────────────────────────────────────────────

        // ── ⚠️ Betalningen: det som avgör om anmälan är giltig ──────────────────────────────────

        private static HpskSite.Models.Ledger.LedgerPayment Betalning(
            int payerId, decimal amount, bool claimed = false, bool confirmed = false,
            bool voided = false, decimal? actual = null) => new()
            {
                Id = 1,
                SourceType = HpskSite.Models.Ledger.LedgerSourceType.Event,
                SourceId = 99,
                PayerMemberId = payerId,
                Amount = amount,
                ActualAmount = actual,
                ClaimedUtc = claimed || confirmed ? DateTime.UtcNow : null,
                ConfirmedUtc = confirmed ? DateTime.UtcNow : null,
                VoidedUtc = voided ? DateTime.UtcNow : null,
            };

        private static ClubEventRoster Sallskap450() => Roster(
            Priser(("vuxen", "Vuxen", 180m), ("barn", "Barn 7-15", 90m)),
            Medlem(Hugo, 180m),
            Gast(201, Hugo, "Karin", 180m, "Vuxen"),
            Gast(202, Hugo, "Emil", 90m, "Barn 7-15"));

        /// <summary>
        /// ⚠️⚠️ SPÄRREN ÄR ATT SWISH-KODEN VISATS, inte att någon bekräftat pengarna.
        ///
        /// <para>Stefans regel 2026-09-18: <i>"betalningen behöver inte bekräftas, men swish-koden
        /// måste ha visats eller mailats, annars kan vi inte förutsätta att det har betalats."</i>
        /// Utan visad kod har medlemmen aldrig fått en chans att betala, och då är det inte rimligt
        /// att hålla hen till betalningen.</para>
        /// </summary>
        [Fact]
        public void Utan_visad_kod_ar_anmalan_inte_giltig()
        {
            var p = ClubEventParticipationService.BuildParty(Sallskap450(), Hugo);
            p.Total.Should().Be(450m);
            p.AmountPresented.Should().Be(0m);
            p.IsPaymentPresented.Should().BeFalse("koden har aldrig visats");
            p.Outstanding.Should().Be(450m, "och arrangoren vantar fortfarande pa pengarna");
        }

        /// <summary>
        /// ⚠️⚠️ KÄRNAN: en VISAD kod räcker för spärren, utan påstående och utan bekräftelse.
        /// Betalningsraden ÄR beviset att koden visats — <c>StartPayment</c> skapar raden och
        /// returnerar QR-uppgifterna i samma anrop.
        /// </summary>
        [Fact]
        public void En_visad_kod_racker_for_sparren()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 450m) });

            p.AmountPresented.Should().Be(450m);
            p.IsPaymentPresented.Should().BeTrue("koden har visats for hela summan");
            // ⚠️ Men det är INTE pengar, och arrangören väntar fortfarande.
            p.ConfirmedPaid.Should().Be(0m);
            p.ClaimedPaid.Should().Be(0m);
            p.Outstanding.Should().Be(450m);
            p.IsSettled.Should().BeFalse();
        }

        /// <summary>
        /// ⚠️ En kod visad för DELAR av summan räcker inte. Lägger Hugo till sonen efter att ha
        /// hämtat koden för 360 är de sista 90 kronorna något han aldrig ombetts betala.
        /// </summary>
        [Fact]
        public void En_kod_for_bara_delar_av_summan_racker_inte()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 360m) });

            p.AmountPresented.Should().Be(360m);
            p.IsPaymentPresented.Should().BeFalse();
            p.RemainingToRequest.Should().Be(450m, "inget ar betalt eller pastatt an");
        }

        /// <summary>
        /// ⚠️ EN betalning för HELA sällskapet. Hugo swishar 450 en gång — en betalning per rad
        /// hade gett tre QR-koder för en överföring.
        /// </summary>
        [Fact]
        public void En_bekraftad_betalning_tacker_hela_sallskapet()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 450m, confirmed: true) });

            p.ConfirmedPaid.Should().Be(450m);
            p.Outstanding.Should().Be(0m);
            p.IsSettled.Should().BeTrue();
            p.IsPaymentPresented.Should().BeTrue("en bekraftad betalning har passerat kodsteget");
            p.AwaitingConfirmation.Should().BeFalse();
        }

        /// <summary>
        /// ⚠️⚠️ ETT PÅSTÅENDE ÄR INTE PENGAR. Det håller anmälan giltig och tar bort beloppet ur
        /// nästa begäran, men <c>Outstanding</c> — det arrangören stämmer av mot bankkontot — rörs
        /// INTE. Slås de ihop kan avprickningslistan inte skilja den som betalat från den som
        /// sagt det.
        /// </summary>
        [Fact]
        public void Ett_pastaende_ar_inte_pengar()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 450m, claimed: true) });

            p.ClaimedPaid.Should().Be(450m);
            p.ConfirmedPaid.Should().Be(0m, "ett pastaende ar inte pengar");
            p.IsPaymentPresented.Should().BeTrue();
            p.Outstanding.Should().Be(450m, "arrangoren vantar fortfarande");
            p.IsSettled.Should().BeFalse();
            p.AwaitingConfirmation.Should().BeTrue();
            p.RemainingToRequest.Should().Be(0m, "hen har redan sagt att hen betalat allt");
        }

        /// <summary>
        /// ⚠️ Nästa begäran gäller BARA det som återstår. Har Hugo sagt att han betalat 270 och
        /// sedan lägger till en gäst för 180 ska QR:en gälla 180 — en begäran på hela summan hade
        /// bett honom betala det han redan sagt sig ha betalat.
        /// </summary>
        [Fact]
        public void Nasta_begaran_galler_bara_det_som_aterstar()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 270m, claimed: true) });

            p.RemainingToRequest.Should().Be(180m);
            p.IsPaymentPresented.Should().BeFalse("bara 270 av 450 har visats");
        }

        /// <summary>
        /// ⚠️ En MAKULERAD betalning är inte pengar och får inte göra anmälan giltig — annars står
        /// någon som anmäld på en betalning arrangören uttryckligen strukit.
        ///
        /// <para><b>⚠️ MÄTT 2026-09-18: det här testet faller INTE när <c>BuildParty</c>s eget
        /// makuleringsfilter tas bort.</b> Skyddet ligger en nivå ned — <c>IsMoney</c> och
        /// <c>IsClaimedOnly</c> kontrollerar båda <c>VoidedUtc</c>. Filtret i <c>BuildParty</c> är
        /// alltså dubbel säkring, inte det testet bevisar. Skriv inte om modellens predikat i tron
        /// att det här testet vaktar dem.</para>
        /// </summary>
        [Fact]
        public void Makulerad_betalning_raknas_inte()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 450m, confirmed: true, voided: true) });

            p.ConfirmedPaid.Should().Be(0m);
            p.Outstanding.Should().Be(450m);
            p.IsSettled.Should().BeFalse();
        }

        /// <summary>⚠️ NÅGON ANNANS betalning får aldrig täcka mitt sällskap.</summary>
        [Fact]
        public void En_annans_betalning_tacker_inte_mitt_sallskap()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Annan, 450m, confirmed: true) });

            p.ConfirmedPaid.Should().Be(0m);
            p.IsSettled.Should().BeFalse();
        }

        /// <summary>
        /// Arrangören tog emot ett annat belopp än det begärda — då är det MOTTAGET som gäller.
        /// Betalade Hugo 400 av 450 är anmälan inte betald.
        /// </summary>
        [Fact]
        public void Mottaget_belopp_gar_fore_begart()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 450m, confirmed: true, actual: 400m) });

            p.ConfirmedPaid.Should().Be(400m);
            p.Outstanding.Should().Be(50m);
            p.IsSettled.Should().BeFalse();
        }

        /// <summary>
        /// Delbetalningar summerar — och de tre talen svarar på tre olika frågor samtidigt.
        /// </summary>
        [Fact]
        public void Flera_betalningar_summerar()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo,
                new[] { Betalning(Hugo, 180m, confirmed: true), Betalning(Hugo, 270m, claimed: true) });

            p.ConfirmedPaid.Should().Be(180m);
            p.ClaimedPaid.Should().Be(270m);
            p.AmountPresented.Should().Be(450m);

            p.IsPaymentPresented.Should().BeTrue("hela summan har visats");
            p.RemainingToRequest.Should().Be(0m, "inget kvar att be om");
            // ⚠️ Men bara 180 är PENGAR. Arrangören väntar fortfarande på 270, och det är den
            // siffran som ska stämmas av mot bankkontot.
            p.Outstanding.Should().Be(270m);
            p.IsSettled.Should().BeFalse();
            p.AwaitingConfirmation.Should().BeTrue("180 av 450 ar bekraftat");
        }

        /// <summary>
        /// ⚠️ Ett GRATIS sällskap är alltid giltigt — noll att betala är betalt. Utan den här
        /// regeln hade spärren låst varje avgiftsfritt evenemang.
        /// </summary>
        [Fact]
        public void Gratis_sallskap_ar_alltid_betalt()
        {
            var roster = Roster(Gratis(), Medlem(Hugo, null), Gast(201, Hugo, "Karin", null, null));
            var p = ClubEventParticipationService.BuildParty(roster, Hugo);

            p.Total.Should().Be(0m);
            p.Outstanding.Should().Be(0m);
            p.IsSettled.Should().BeTrue();
            p.AwaitingConfirmation.Should().BeFalse();
        }

        /// <summary>⚠️ Överbetalning ger inte en negativ skuld — det hade läst som ett tillgodo
        /// sällskapet inte har.</summary>
        [Fact]
        public void Overbetalning_ger_noll_i_skuld_aldrig_negativt()
        {
            var p = ClubEventParticipationService.BuildParty(
                Sallskap450(), Hugo, new[] { Betalning(Hugo, 500m, confirmed: true) });

            p.Outstanding.Should().Be(0m);
            p.IsSettled.Should().BeTrue();
        }

        [Fact]
        public void Medlem_utan_gaster_ar_ett_sallskap_pa_en()
        {
            var roster = Roster(Priser(("vuxen", "Vuxen", 180m)), Medlem(Hugo, 180m));

            var party = ClubEventParticipationService.BuildParty(roster, Hugo);
            party.People.Should().Be(1);
            party.Total.Should().Be(180m);
            party.HasGuests.Should().BeFalse();
        }
    }
}
