using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.CompetitionFees;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.CompetitionFees;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tävlingsavgifternas regler i den nya modellen (P3/P4).
    ///
    /// <para><b>⚠️ Två saker får aldrig gå fel:</b> delarna måste summera till exakt den avgift
    /// <see cref="RegistrationFeeCalculator"/> räknar fram (annars betalar någon för mycket eller
    /// för lite utan att något larmar), och planen får aldrig röra en rad som är pengar, ett
    /// påstående eller en fakturerad avgift.</para>
    /// </summary>
    public class CompetitionFeePlannerTests
    {
        private static readonly RegistrationFeeCalculator.FeeConfig Fees =
            new(BaseFee: 200m, JuniorFee: 100m, SubCompFee: 50m, SubCompFeeMode: "perClass");

        private static readonly RegistrationFeeCalculator.FeeConfig FeesPerReg =
            new(BaseFee: 200m, JuniorFee: 100m, SubCompFee: 50m, SubCompFeeMode: "perRegistration");

        private static IReadOnlySet<string> Types(params string[] t) => t.ToHashSet();

        // ── Delningen ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Utan_Klubben_betalar_ar_det_EN_del_som_skytten_betalar()
        {
            var parts = CompetitionFeePlanner.SplitRegistration(
                Fees, new[] { "C", "C_Jun" }, false, clubPays: false, Types(CompetitionFeeTypes.Junior));

            parts.Should().ContainSingle().Which.Should().Be(new FeePartAmount(CompetitionFeePart.All, 300m));
        }

        [Fact]
        public void Klubben_betalar_juniorklassen_skytten_seniorklassen()
        {
            var parts = CompetitionFeePlanner.SplitRegistration(
                Fees, new[] { "C", "C_Jun" }, false, clubPays: true, Types(CompetitionFeeTypes.Junior));

            parts.Should().BeEquivalentTo(new[]
            {
                new FeePartAmount(CompetitionFeePart.Self, 200m),
                new FeePartAmount(CompetitionFeePart.Club, 100m)
            }, o => o.WithStrictOrdering());
        }

        [Fact]
        public void Klubben_betalar_valt_men_ingen_klass_ar_tillaten_ger_hela_avgiften_till_skytten()
        {
            // Skytten kan inte binda klubben för en typ arrangören inte tillåtit.
            var parts = CompetitionFeePlanner.SplitRegistration(
                Fees, new[] { "C" }, false, clubPays: true, Types(CompetitionFeeTypes.Junior));

            parts.Should().ContainSingle().Which.Should().Be(new FeePartAmount(CompetitionFeePart.All, 200m));
        }

        [Fact]
        public void Alla_klasser_tillatna_ger_en_KLUBBDEL_och_ingen_skyttedel()
        {
            var parts = CompetitionFeePlanner.SplitRegistration(
                Fees, new[] { "C_Jun", "B_Jun" }, false, clubPays: true, Types(CompetitionFeeTypes.Junior));

            parts.Should().ContainSingle().Which.Should().Be(new FeePartAmount(CompetitionFeePart.Club, 200m));
        }

        [Fact]
        public void Deltavling_per_klass_foljer_sin_klass()
        {
            var parts = CompetitionFeePlanner.SplitRegistration(
                Fees, new[] { "C", "C_Jun" }, true, clubPays: true, Types(CompetitionFeeTypes.Junior));

            parts.Should().BeEquivalentTo(new[]
            {
                new FeePartAmount(CompetitionFeePart.Self, 250m),
                new FeePartAmount(CompetitionFeePart.Club, 150m)
            });
        }

        [Fact]
        public void Deltavling_per_anmalan_laggs_pa_SKYTTENS_del()
        {
            // Stefans beslut 2026-09-24.
            var parts = CompetitionFeePlanner.SplitRegistration(
                FeesPerReg, new[] { "C", "C_Jun" }, true, clubPays: true, Types(CompetitionFeeTypes.Junior));

            parts.Should().BeEquivalentTo(new[]
            {
                new FeePartAmount(CompetitionFeePart.Self, 250m),
                new FeePartAmount(CompetitionFeePart.Club, 100m)
            });
        }

        [Fact]
        public void Deltavling_per_anmalan_foljer_klubben_nar_det_inte_finns_nagon_skyttedel()
        {
            var parts = CompetitionFeePlanner.SplitRegistration(
                FeesPerReg, new[] { "C_Jun" }, true, clubPays: true, Types(CompetitionFeeTypes.Junior));

            parts.Should().ContainSingle().Which.Should().Be(new FeePartAmount(CompetitionFeePart.Club, 150m));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Delarna_summerar_ALLTID_till_kalkylatorns_avgift(bool clubPays, bool sub)
        {
            var classes = new[] { "C", "C_Jun", "B", "A-D jun" };
            foreach (var cfg in new[] { Fees, FeesPerReg })
            {
                var expected = classes.Sum(c => RegistrationFeeCalculator.FeeForClass(cfg, c, sub))
                               + RegistrationFeeCalculator.PerRegistrationSurcharge(cfg, sub);

                foreach (var types in new[]
                         {
                             Types(), Types(CompetitionFeeTypes.Junior), Types(CompetitionFeeTypes.Individual),
                             Types(CompetitionFeeTypes.Individual, CompetitionFeeTypes.Junior)
                         })
                {
                    var parts = CompetitionFeePlanner.SplitRegistration(cfg, classes, sub, clubPays, types);
                    parts.Sum(p => p.Amount).Should().Be(expected);
                }
            }
        }

        [Fact]
        public void Gratis_anmalan_ger_inga_delar()
        {
            var free = new RegistrationFeeCalculator.FeeConfig(0m, null, 0m, null);
            CompetitionFeePlanner.SplitRegistration(free, new[] { "C" }, false, true, Types(CompetitionFeeTypes.Individual))
                .Should().BeEmpty();
        }

        [Fact]
        public void Lag_ar_klubbdel_bara_nar_arrangoren_tillatit_lag()
        {
            CompetitionFeePlanner.SplitTeam(600m, false, Types(CompetitionFeeTypes.Team))
                .Single().Part.Should().Be(CompetitionFeePart.Club);
            CompetitionFeePlanner.SplitTeam(600m, false, Types(CompetitionFeeTypes.Junior))
                .Single().Part.Should().Be(CompetitionFeePart.All);
            // Stafett är en egen typ — lag räcker inte.
            CompetitionFeePlanner.SplitTeam(600m, true, Types(CompetitionFeeTypes.Team))
                .Single().Part.Should().Be(CompetitionFeePart.All);
        }

        // ── Planen ───────────────────────────────────────────────────────────────────────────

        private static int _id = 100;

        private static LedgerPayment Row(string part, decimal amount, bool claimed = false, bool confirmed = false,
            bool voided = false, int? coveredBy = null, decimal? actual = null)
            => new()
            {
                Id = ++_id,
                FeePart = part,
                Amount = amount,
                ActualAmount = actual,
                ClaimedUtc = claimed ? DateTime.UtcNow : null,
                ConfirmedUtc = confirmed ? DateTime.UtcNow : null,
                VoidedUtc = voided || coveredBy != null ? DateTime.UtcNow : null,
                CoveredByChargeId = coveredBy
            };

        private static readonly IReadOnlyDictionary<int, ChargeCoverage> NoCover = new Dictionary<int, ChargeCoverage>();

        private static List<FeePartAmount> Want(params (string part, decimal amount)[] p)
            => p.Select(x => new FeePartAmount(x.part, x.amount)).ToList();

        [Fact]
        public void Ny_anmalan_skapar_en_begaran()
        {
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 200m)), new List<LedgerPayment>(), NoCover);

            plan.Create.Should().ContainSingle().Which.Should().Be(new FeePartAmount(CompetitionFeePart.All, 200m));
            plan.VoidPaymentIds.Should().BeEmpty();
        }

        [Fact]
        public void En_oppen_rad_med_ratt_belopp_BEHALLS()
        {
            // Annars byter raden id vid varje omräkning och skyttens QR-kod blir ogiltig.
            var open = Row(CompetitionFeePart.All, 200m);
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 200m)), new[] { open }, NoCover);

            plan.IsNoop.Should().BeTrue();
        }

        [Fact]
        public void Hojd_avgift_makulerar_den_oppna_raden_och_begar_hela_nya_beloppet()
        {
            var open = Row(CompetitionFeePart.All, 200m);
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 300m)), new[] { open }, NoCover);

            plan.VoidPaymentIds.Should().Equal(open.Id);
            plan.Create.Should().ContainSingle().Which.Amount.Should().Be(300m);
        }

        [Fact]
        public void Hojd_avgift_efter_betalning_begar_BARA_mellanskillnaden()
        {
            var paid = Row(CompetitionFeePart.All, 200m, confirmed: true);
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 300m)), new[] { paid }, NoCover);

            plan.VoidPaymentIds.Should().BeEmpty();
            plan.Create.Should().ContainSingle().Which.Amount.Should().Be(100m);
        }

        [Fact]
        public void En_mottagen_rad_rors_ALDRIG_och_overskottet_rapporteras()
        {
            var paid = Row(CompetitionFeePart.All, 300m, confirmed: true);
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 200m)), new[] { paid }, NoCover);

            plan.IsNoop.Should().BeTrue();
            plan.Overpaid.Should().Be(100m);
        }

        [Fact]
        public void Ett_pastaende_rors_inte_och_raknas_som_tackning()
        {
            var claimed = Row(CompetitionFeePart.All, 200m, claimed: true);
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 200m)), new[] { claimed }, NoCover);

            plan.IsNoop.Should().BeTrue();
        }

        [Fact]
        public void Avanmald_makulerar_oppna_rader_men_inte_betalda()
        {
            var open = Row(CompetitionFeePart.All, 200m);
            var paidTopUp = Row(CompetitionFeePart.All, 50m, confirmed: true);
            var plan = CompetitionFeePlanner.Plan(new List<FeePartAmount>(), new[] { open, paidTopUp }, NoCover);

            plan.VoidPaymentIds.Should().Equal(open.Id);
            plan.Create.Should().BeEmpty();
            plan.Overpaid.Should().Be(50m);
        }

        [Fact]
        public void Redan_betalt_allt_och_sedan_Klubben_betalar_ger_INGEN_ny_begaran()
        {
            var paid = Row(CompetitionFeePart.All, 300m, confirmed: true);
            var plan = CompetitionFeePlanner.Plan(
                Want((CompetitionFeePart.Self, 200m), (CompetitionFeePart.Club, 100m)), new[] { paid }, NoCover);

            plan.Create.Should().BeEmpty();
            plan.Overpaid.Should().Be(0m);
        }

        [Fact]
        public void Fakturerad_avgift_raknas_som_tackning_sa_lange_fakturan_galler()
        {
            var covered = Row(CompetitionFeePart.Club, 100m, coveredBy: 7);
            var cover = new Dictionary<int, ChargeCoverage> { [covered.Id] = new(covered.Id, 7, 100m, false) };

            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.Club, 100m)), new[] { covered }, cover);
            plan.IsNoop.Should().BeTrue();
        }

        [Fact]
        public void Makulerad_eller_krediterad_faktura_ger_en_ny_begaran()
        {
            var covered = Row(CompetitionFeePart.Club, 100m, coveredBy: 7);
            var cover = new Dictionary<int, ChargeCoverage> { [covered.Id] = new(covered.Id, 7, 0m, false) };

            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.Club, 100m)), new[] { covered }, cover);
            plan.Create.Should().ContainSingle().Which.Should().Be(new FeePartAmount(CompetitionFeePart.Club, 100m));
        }

        [Fact]
        public void En_vanlig_makulerad_rad_ar_ingen_tackning()
        {
            var voided = Row(CompetitionFeePart.All, 200m, voided: true);
            var plan = CompetitionFeePlanner.Plan(Want((CompetitionFeePart.All, 200m)), new[] { voided }, NoCover);

            plan.Create.Should().ContainSingle().Which.Amount.Should().Be(200m);
        }

        [Fact]
        public void Delbyte_makulerar_rader_for_en_del_som_inte_langre_finns()
        {
            var openAll = Row(CompetitionFeePart.All, 300m);
            var plan = CompetitionFeePlanner.Plan(
                Want((CompetitionFeePart.Self, 200m), (CompetitionFeePart.Club, 100m)), new[] { openAll }, NoCover);

            plan.VoidPaymentIds.Should().Equal(openAll.Id);
            plan.Create.Should().BeEquivalentTo(Want((CompetitionFeePart.Self, 200m), (CompetitionFeePart.Club, 100m)));
        }

        // ── Läget ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Skyldig_utan_rader_ar_OBETALD_inte_betald()
        {
            // Innan omräkningen hunnit skapa raden — får aldrig läsas som betald.
            var s = FeeItemStatus.For(200m, new List<LedgerPayment>(), NoCover);
            s.Key.Should().Be(FeeStatusKeys.Unpaid);
        }

        [Fact]
        public void Lagen_i_ratt_ordning()
        {
            FeeItemStatus.For(0m, new List<LedgerPayment>(), NoCover).Key.Should().Be(FeeStatusKeys.NoFee);
            FeeItemStatus.For(200m, new[] { Row(CompetitionFeePart.All, 200m) }, NoCover).Key.Should().Be(FeeStatusKeys.Unpaid);
            FeeItemStatus.For(200m, new[] { Row(CompetitionFeePart.All, 200m, claimed: true) }, NoCover).Key.Should().Be(FeeStatusKeys.Claimed);
            FeeItemStatus.For(200m, new[] { Row(CompetitionFeePart.All, 200m, confirmed: true) }, NoCover).Key.Should().Be(FeeStatusKeys.Paid);

            var covered = Row(CompetitionFeePart.Club, 200m, coveredBy: 9);
            var unpaidCharge = new Dictionary<int, ChargeCoverage> { [covered.Id] = new(covered.Id, 9, 200m, false) };
            var paidCharge = new Dictionary<int, ChargeCoverage> { [covered.Id] = new(covered.Id, 9, 200m, true) };
            FeeItemStatus.For(200m, new[] { covered }, unpaidCharge).Key.Should().Be(FeeStatusKeys.Invoiced);
            FeeItemStatus.For(200m, new[] { covered }, paidCharge).Key.Should().Be(FeeStatusKeys.Paid);
        }

        [Fact]
        public void Egen_del_betald_och_klubbens_del_ofakturerad_ar_INTE_obetald()
        {
            // Första versionens fel: klubbens öppna del räknades som skyttens skuld.
            var rows = new[]
            {
                Row(CompetitionFeePart.Self, 200m, confirmed: true),
                Row(CompetitionFeePart.Club, 100m)
            };
            var s = FeeItemStatus.For(300m, rows, NoCover);
            s.Key.Should().Be(FeeStatusKeys.AwaitingInvoice);
            s.Open.Should().Be(0m);
            s.AwaitingInvoice.Should().Be(100m);

            var claimed = new[] { Row(CompetitionFeePart.Self, 200m, claimed: true), Row(CompetitionFeePart.Club, 100m) };
            FeeItemStatus.For(300m, claimed, NoCover).Key.Should().Be(FeeStatusKeys.Claimed);
        }

        // ── Fakturans saldo ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Fakturans_saldo_ar_summa_minus_kredit_minus_mottaget()
        {
            var inv = new LedgerCharge { Id = 5, Kind = LedgerChargeKind.Invoice, Amount = 1000m };
            var credit = new LedgerCharge { Id = 6, Kind = LedgerChargeKind.Credit, CreditsChargeId = 5, Amount = -200m };
            var pay = new LedgerPayment { ChargeId = 5, Amount = 500m, ConfirmedUtc = DateTime.UtcNow };
            var claimed = new LedgerPayment { ChargeId = 5, Amount = 300m, ClaimedUtc = DateTime.UtcNow };

            var b = LedgerChargeBalance.For(inv, new[] { credit }, new[] { pay, claimed });

            b.Net.Should().Be(800m);
            b.Outstanding.Should().Be(300m);
            b.Claimed.Should().Be(300m);
            b.IsSettled.Should().BeFalse();
        }

        [Fact]
        public void Makulerad_faktura_har_inget_saldo()
        {
            var inv = new LedgerCharge { Id = 5, Amount = 1000m, VoidedUtc = DateTime.UtcNow };
            LedgerChargeBalance.For(inv, Array.Empty<LedgerCharge>(), Array.Empty<LedgerPayment>())
                .Outstanding.Should().Be(0m);
        }

        [Fact]
        public void Typerna_normaliseras_och_okanda_faller_bort()
        {
            CompetitionFeeTypes.Format(new[] { "stafett", "junior", "hittepa", " lag " })
                .Should().Be("junior,lag,stafett");
            CompetitionFeeTypes.Parse("lag,,okand,junior").Should().BeEquivalentTo(new[] { "lag", "junior" });
        }
    }
}
