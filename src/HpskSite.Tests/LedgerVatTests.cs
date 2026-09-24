using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Momsen (2026-09-24, efter Michael Henriksson, Åmåls PK).
    ///
    /// <para><b>⚠️⚠️ REGISTRERINGEN ÄR EN GRIND I BOKFÖRINGEN.</b> En momssats på ett konto är ett
    /// förslag; bokförs gör den bara för en momsregistrerad förening. Utan grinden hade en sats
    /// satt av misstag gett momsrader hos en förening som inte får ta ut moms — och det felar
    /// inte, det balanserar.</para>
    /// </summary>
    public class LedgerVatTests
    {
        private static Dictionary<int, LedgerAccount> Accounts() => new()
        {
            [1930] = new LedgerAccount { Number = 1930, Name = "Föreningskonto" },
            [3040] = new LedgerAccount { Number = 3040, Name = "Kiosk", DefaultVatRate = 12m },
            [4020] = new LedgerAccount { Number = 4020, Name = "Tavlor", DefaultVatRate = 25m },
            [2610] = new LedgerAccount { Number = 2610, Name = "Utgående moms" },
            [2640] = new LedgerAccount { Number = 2640, Name = "Ingående moms" }
        };

        private static Dictionary<string, int> Roles() => new()
        {
            [LedgerAccountRoles.BankAccount] = 1930,
            [LedgerAccountRoles.VatOutgoing] = 2610,
            [LedgerAccountRoles.VatIncoming] = 2640
        };

        private static List<LedgerJournalEntryLine> Build(ManualEntryRequest r, bool vatRegistered)
        {
            var request = new LedgerPostingRequest
            {
                IssuerType = 0, IssuerId = 1, AccountingDate = new DateTime(2026, 9, 24),
                Lines = LedgerManualPostingService.BuildLines(r)
            };

            var lines = LedgerPostingService.BuildLines(
                request, Accounts(), Roles(), new Dictionary<int, LedgerProject>(), vatRegistered, out var error);

            error.Should().BeNull();
            return lines;
        }

        private static ManualEntryRequest Kiosk(bool received = true, decimal? rate = null) => new()
        {
            Amount = 112m, WeReceived = received, Date = new DateTime(2026, 9, 24),
            Description = "Kioskförsäljning", AccountNumber = received ? 3040 : 4020,
            PaymentAccountNumber = 1930, VatRate = rate
        };

        // ── Grinden ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Oregistrerad_forening_far_ALDRIG_moms_trots_kontots_sats()
        {
            var lines = Build(Kiosk(), vatRegistered: false);

            lines.Should().HaveCount(2);
            lines.Should().NotContain(l => l.AccountNumber == 2610 || l.AccountNumber == 2640);
            lines.Single(l => l.AccountNumber == 3040).Credit.Should().Be(112m);
        }

        [Fact]
        public void Oregistrerad_forening_far_ingen_moms_aven_med_uttrycklig_sats()
            => Build(Kiosk(rate: 25m), vatRegistered: false).Should().HaveCount(2);

        [Fact]
        public void Registrerad_forening_far_kontots_sats_och_utgaende_moms_pa_en_forsaljning()
        {
            // 112 kr inklusive 12 % ⇒ 100 netto + 12 moms.
            var lines = Build(Kiosk(), vatRegistered: true);

            lines.Single(l => l.AccountNumber == 1930).Debit.Should().Be(112m);
            lines.Single(l => l.AccountNumber == 3040).Credit.Should().Be(100m);
            lines.Single(l => l.AccountNumber == 2610).Credit.Should().Be(12m);
            lines.Sum(l => l.Debit).Should().Be(lines.Sum(l => l.Credit));
        }

        [Fact]
        public void Ett_inkop_ger_INGAENDE_moms()
        {
            // 125 kr inklusive 25 % ⇒ 100 netto + 25 ingående moms.
            var lines = Build(new ManualEntryRequest
            {
                Amount = 125m, WeReceived = false, Date = new DateTime(2026, 9, 24),
                Description = "Tavlor", AccountNumber = 4020, PaymentAccountNumber = 1930
            }, vatRegistered: true);

            lines.Single(l => l.AccountNumber == 4020).Debit.Should().Be(100m);
            lines.Single(l => l.AccountNumber == 2640).Debit.Should().Be(25m);
            lines.Should().NotContain(l => l.AccountNumber == 2610);
        }

        /// <summary>
        /// ⚠️ Riktningen följer knappen, inte kontoklassen. En återbetalning på ett intäktskonto
        /// ("Vi betalade" på 3040) är pengar UT — med kontoklassens gissning hade den fått
        /// utgående moms.
        /// </summary>
        [Fact]
        public void Aterbetalning_pa_intaktskonto_ger_ingaende_moms_efter_knappen()
        {
            var lines = Build(new ManualEntryRequest
            {
                Amount = 112m, WeReceived = false, Date = new DateTime(2026, 9, 24),
                Description = "Återbetald kioskvara", AccountNumber = 3040, PaymentAccountNumber = 1930
            }, vatRegistered: true);

            lines.Should().Contain(l => l.AccountNumber == 2640 && l.Debit == 12m);
            lines.Should().NotContain(l => l.AccountNumber == 2610);
        }

        [Fact]
        public void Kassorens_sats_vinner_over_kontots()
        {
            // 125 kr med 25 % i stället för kontots 12 % ⇒ 25 kr moms.
            var r = Kiosk(rate: 25m);
            r.Amount = 125m;
            Build(r, vatRegistered: true).Single(l => l.AccountNumber == 2610).Credit.Should().Be(25m);
        }

        [Fact]
        public void Noll_betyder_ingen_moms_pa_just_den_posten()
            => Build(Kiosk(rate: 0m), vatRegistered: true).Should().HaveCount(2);

        [Fact]
        public void Betalkontot_bar_aldrig_moms()
            => Build(Kiosk(), vatRegistered: true).Single(l => l.AccountNumber == 1930).VatAmount.Should().BeNull();

        // ── Satserna och numret ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null, true)]
        [InlineData("0", true)]
        [InlineData("25", true)]
        [InlineData("12", true)]
        [InlineData("6", true)]
        [InlineData("20", false)]
        [InlineData("-6", false)]
        public void Bara_svenska_satser_godtas(string? rate, bool ok)
            => LedgerVat.IsAllowedRate(rate is null ? null : decimal.Parse(rate)).Should().Be(ok);

        [Fact]
        public void En_ogiltig_sats_vagras_vid_bokforingen()
        {
            var r = Kiosk(rate: 20m);
            LedgerManualPostingService.Validate(r).Should().NotBeNull();
        }

        [Theory]
        [InlineData(3040, true)]
        [InlineData(4020, true)]
        [InlineData(7830, true)]
        [InlineData(1930, false)]   // balanskonto — en insättning har ingen moms
        [InlineData(2610, false)]
        [InlineData(8310, false)]   // finansiellt
        public void Bara_intakter_och_kostnader_kan_ha_moms(int account, bool ok)
            => LedgerVat.AccountCanCarryVat(account).Should().Be(ok);

        [Theory]
        [InlineData("SE802412345601", "SE802412345601")]
        [InlineData("se 802412-3456 01", "SE802412345601")]
        [InlineData("802412-3456", "SE802412345601")]     // organisationsnumret byggs om
        [InlineData("8024123456", "SE802412345601")]
        public void Momsnumret_normaliseras(string input, string expected)
        {
            var (n, err) = LedgerVat.NormalizeVatNumber(input);
            err.Should().BeNull();
            n.Should().Be(expected);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("SE8024123456")]      // saknar 01
        [InlineData("SE80241234560")]     // elva siffror
        [InlineData("NO802412345601")]
        public void Ogiltigt_momsnummer_vagras(string? input)
            => LedgerVat.NormalizeVatNumber(input).Error.Should().NotBeNull();
    }
}
