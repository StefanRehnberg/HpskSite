using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Utgiftssidans regler.
    ///
    /// <para><b>⚠️⚠️ ATTESTSPÄRREN ÄR FÖRENINGENS ENDA INTERNKONTROLL ÖVER UTBETALNINGAR.</b> Den
    /// får inte vara den enda delen av bygget som bara går att pröva genom hela stacken — en regel
    /// som kräver en databas, en inloggning och en webbläsare för att mätas blir mätt en gång och
    /// sedan aldrig mer.</para>
    /// </summary>
    public class LedgerExpenseRulesTests
    {
        private static LedgerExpense Utlagg(
            int payee = 500, decimal amount = 450m, string date = "2026-09-01",
            string status = LedgerExpenseStatus.Registered, int registeredBy = 700) => new()
            {
                Kind = LedgerExpenseKind.Utlagg,
                PayeeMemberId = payee,
                PayeeName = "Anna Andersson",
                Description = "Kaffe och fika till klubbkvällen",
                Amount = amount,
                ExpenseDate = DateTime.Parse(date),
                AccountNumber = 6560,
                Status = status,
                RegisteredByMemberId = registeredBy
            };

        private static LedgerExpense Faktura(
            string? due = "2026-10-15", string date = "2026-09-15",
            string status = LedgerExpenseStatus.Registered) => new()
            {
                Kind = LedgerExpenseKind.Faktura,
                PayeeMemberId = 0,
                PayeeName = "Skjutbanegrus AB",
                Description = "Grus till kulfånget",
                Amount = 12500m,
                ExpenseDate = DateTime.Parse(date),
                DueDate = due is null ? null : DateTime.Parse(due),
                AccountNumber = 5170,
                Status = status,
                RegisteredByMemberId = 700
            };

        // ══ Validering ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Ett_giltigt_utlagg_slapps_igenom()
            => LedgerExpenseRules.Validate(Utlagg()).Should().BeNull();

        [Fact]
        public void En_giltig_faktura_slapps_igenom()
            => LedgerExpenseRules.Validate(Faktura()).Should().BeNull();

        [Fact]
        public void Okand_form_avvisas()
        {
            var e = Utlagg();
            e.Kind = "kvitto";

            LedgerExpenseRules.Validate(e).Should().Contain("utlägg eller en leverantörsfaktura");
        }

        [Fact]
        public void Noll_kronor_avvisas()
            => LedgerExpenseRules.Validate(Utlagg(amount: 0m)).Should().Contain("större än noll");

        [Fact]
        public void Negativt_belopp_avvisas()
            => LedgerExpenseRules.Validate(Utlagg(amount: -450m)).Should().Contain("större än noll");

        /// <summary>
        /// ⚠️ Taket finns för felskrivningen, inte för att sätta en gräns för vad en förening får
        /// köpa. En miljon är över varje rimlig föreningsutgift och under varje verklig
        /// fingerfelsrisk (45000 i stället för 450 fastnar inte, men 4500000000 gör det).
        /// </summary>
        [Fact]
        public void Orimligt_belopp_ifragasatts()
            => LedgerExpenseRules.Validate(Utlagg(amount: 5_000_000m))
                .Should().Contain("felskrivning");

        [Fact]
        public void Tom_beskrivning_avvisas()
        {
            var e = Utlagg();
            e.Description = "   ";

            LedgerExpenseRules.Validate(e).Should().Contain("vad utgiften avser");
        }

        /// <summary>Beskedet ska namnge VILKEN uppgift som fattas, och de två formerna frågar olika.</summary>
        [Fact]
        public void Saknad_mottagare_namnger_ratt_sak_per_form()
        {
            var u = Utlagg();
            u.PayeeName = "";
            LedgerExpenseRules.Validate(u).Should().Contain("ska ha pengarna tillbaka");

            var f = Faktura();
            f.PayeeName = "";
            LedgerExpenseRules.Validate(f).Should().Contain("leverantör");
        }

        [Fact]
        public void Kostnadskonto_kravs()
        {
            var e = Utlagg();
            e.AccountNumber = 0;

            LedgerExpenseRules.Validate(e).Should().Contain("vilket konto");
        }

        /// <summary>
        /// ⚠️ Ett utlägg har ingen extern förfallodag. Tillåts en blir listan "förfallna utgifter"
        /// obegriplig — den som lagt ut pengar har inte satt någon frist.
        /// </summary>
        [Fact]
        public void Utlagg_far_ingen_forfallodag()
        {
            var e = Utlagg();
            e.DueDate = DateTime.Parse("2026-10-01");

            LedgerExpenseRules.Validate(e).Should().Contain("ingen förfallodag");
        }

        [Fact]
        public void Forfallodag_fore_fakturadatum_avvisas()
            => LedgerExpenseRules.Validate(Faktura(due: "2026-09-01", date: "2026-09-15"))
                .Should().Contain("före fakturadatumet");

        /// <summary>
        /// ⚠️⚠️ Ett utlägg BEHÖVER en medlem, och det är inte formalia: det är det fältet
        /// attestspärren läser. Tillåts fritext kan någon registrera ett utlägg till sig själv och
        /// godkänna det, och kontrollen är borta.
        /// </summary>
        [Fact]
        public void Utlagg_kraver_en_medlem()
            => LedgerExpenseRules.Validate(Utlagg(payee: 0)).Should().Contain("vilken medlem");

        [Fact]
        public void Faktura_kraver_ingen_medlem()
            => LedgerExpenseRules.Validate(Faktura()).Should().BeNull();

        // ══ Attest ════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Nagon_annan_far_attestera()
            => LedgerExpenseRules.ApprovalRefusal(Utlagg(payee: 500), approverMemberId: 900)
                .Should().BeNull();

        /// <summary>
        /// ⚠️⚠️ HELA POÄNGEN. Mottagaren kan aldrig godkänna sin egen utbetalning — det är den enda
        /// regeln som alltid är rätt och alltid går att följa.
        /// </summary>
        [Fact]
        public void Mottagaren_far_ALDRIG_attestera_sin_egen_utbetalning()
            => LedgerExpenseRules.ApprovalRefusal(Utlagg(payee: 500), approverMemberId: 500)
                .Should().Contain("din egen utbetalning");

        /// <summary>
        /// ⚠️ Registreraren attesterar = varning, inte spärr. En liten förening har ofta en enda
        /// person som både öppnar posten och betalar räkningarna; en spärr där hade gjort
        /// funktionen oanvändbar för just dem.
        /// </summary>
        [Fact]
        public void Registreraren_far_attestera_men_det_noteras()
        {
            var e = Utlagg(payee: 500, registeredBy: 700);

            LedgerExpenseRules.ApprovalRefusal(e, approverMemberId: 700).Should().BeNull();
        }

        [Fact]
        public void Utloggad_far_inte_attestera()
            => LedgerExpenseRules.ApprovalRefusal(Utlagg(), approverMemberId: 0)
                .Should().Contain("inloggad");

        [Fact]
        public void Betald_utgift_kan_inte_attesteras_om()
            => LedgerExpenseRules.ApprovalRefusal(
                    Utlagg(status: LedgerExpenseStatus.Paid), approverMemberId: 900)
                .Should().Contain("redan betald");

        [Fact]
        public void Avvisad_utgift_kan_inte_attesteras()
            => LedgerExpenseRules.ApprovalRefusal(
                    Utlagg(status: LedgerExpenseStatus.Rejected), approverMemberId: 900)
                .Should().Contain("avvisad");

        // ══ Betalning ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⚠️⚠️ Attesten är ett VILLKOR för betalningen. Går det att betala en oattesterad utgift
        /// är attesten frivillig i praktiken, och då är internkontrollen en text i ett dokument.
        /// </summary>
        [Fact]
        public void Oattesterad_utgift_kan_inte_betalas()
            => LedgerExpenseRules.PaymentRefusal(Utlagg(status: LedgerExpenseStatus.Registered))
                .Should().Contain("attesteras innan");

        [Fact]
        public void Attesterad_utgift_kan_betalas()
            => LedgerExpenseRules.PaymentRefusal(Utlagg(status: LedgerExpenseStatus.Approved))
                .Should().BeNull();

        [Fact]
        public void Redan_betald_utgift_kan_inte_betalas_igen()
            => LedgerExpenseRules.PaymentRefusal(Utlagg(status: LedgerExpenseStatus.Paid))
                .Should().Contain("redan betald");

        [Fact]
        public void Avvisad_utgift_kan_inte_betalas()
            => LedgerExpenseRules.PaymentRefusal(Utlagg(status: LedgerExpenseStatus.Rejected))
                .Should().Contain("ska inte betalas");

        // ══ Ändring ═══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void En_betald_utgift_ar_last()
            => LedgerExpenseRules.IsEditable(Utlagg(status: LedgerExpenseStatus.Paid))
                .Should().BeFalse();

        [Fact]
        public void En_attesterad_utgift_gar_att_andra()
            => LedgerExpenseRules.IsEditable(Utlagg(status: LedgerExpenseStatus.Approved))
                .Should().BeTrue();

        /// <summary>
        /// ⚠️⚠️ Attesten gällde ett BESTÄMT belopp. Står den kvar efter att beloppet ändrats
        /// intygar den något ingen har sagt.
        /// </summary>
        [Fact]
        public void Andrat_belopp_river_attesten()
            => LedgerExpenseRules.ApprovalIsVoidedBy(Utlagg(amount: 450m), Utlagg(amount: 900m))
                .Should().BeTrue();

        [Fact]
        public void Andrat_konto_river_attesten()
        {
            var before = Utlagg();
            var after = Utlagg();
            after.AccountNumber = 5910;

            LedgerExpenseRules.ApprovalIsVoidedBy(before, after).Should().BeTrue();
        }

        [Fact]
        public void Andrad_mottagare_river_attesten()
            => LedgerExpenseRules.ApprovalIsVoidedBy(Utlagg(payee: 500), Utlagg(payee: 501))
                .Should().BeTrue();

        [Fact]
        public void Andrat_datum_river_attesten()
            => LedgerExpenseRules.ApprovalIsVoidedBy(
                    Utlagg(date: "2026-09-01"), Utlagg(date: "2026-09-02"))
                .Should().BeTrue();

        /// <summary>
        /// ⚠️ Kontrollprov åt andra hållet: rivs attesten av VARJE ändring blir en rättad
        /// stavning en ny attestrunda, och då slutar folk rätta stavfel.
        /// </summary>
        [Fact]
        public void Rattad_beskrivning_river_INTE_attesten()
        {
            var before = Utlagg();
            var after = Utlagg();
            after.Description = "Kaffe och fika till klubbkvällen (rättad)";

            LedgerExpenseRules.ApprovalIsVoidedBy(before, after).Should().BeFalse();
        }

        // ══ Förfallen ═════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Obetald_faktura_efter_forfallodagen_ar_forfallen()
            => LedgerExpenseRules.IsOverdue(Faktura(due: "2026-10-15"), DateTime.Parse("2026-10-16"))
                .Should().BeTrue();

        [Fact]
        public void Forfallodagen_sjalv_ar_inte_forsenad()
            => LedgerExpenseRules.IsOverdue(Faktura(due: "2026-10-15"), DateTime.Parse("2026-10-15"))
                .Should().BeFalse();

        [Fact]
        public void En_betald_faktura_ar_aldrig_forfallen()
            => LedgerExpenseRules.IsOverdue(
                    Faktura(due: "2026-10-15", status: LedgerExpenseStatus.Paid),
                    DateTime.Parse("2026-12-01"))
                .Should().BeFalse();

        [Fact]
        public void En_avvisad_faktura_ar_aldrig_forfallen()
            => LedgerExpenseRules.IsOverdue(
                    Faktura(due: "2026-10-15", status: LedgerExpenseStatus.Rejected),
                    DateTime.Parse("2026-12-01"))
                .Should().BeFalse();

        /// <summary>Ett utlägg har ingen förfallodag och kan därför aldrig vara försenat.</summary>
        [Fact]
        public void Ett_utlagg_ar_aldrig_forfallet()
            => LedgerExpenseRules.IsOverdue(Utlagg(), DateTime.Parse("2030-01-01"))
                .Should().BeFalse();
    }
}
