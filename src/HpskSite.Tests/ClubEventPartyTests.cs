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
