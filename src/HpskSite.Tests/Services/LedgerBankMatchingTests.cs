using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ En automatisk matchning som kan vara fel är värre än ingen — den ser granskad ut.
    /// Reglerna prövas därför här, utan databas.
    /// </summary>
    public class LedgerBankMatchingTests
    {
        private static readonly DateTime Day = new(2026, 9, 15);

        /// <summary>
        /// ⚠️⚠️ Debet ökar en tillgång, alltså pengar IN. Vänds tecknet matchar varje insättning
        /// mot ett uttag på samma belopp — och det ser ut att stämma.
        /// </summary>
        [Theory]
        [InlineData(270, 0, 270)]      // debet 270 = +270 in
        [InlineData(0, 120, -120)]     // kredit 120 = -120 ut
        [InlineData(0, 0, 0)]
        public void Debet_ar_pengar_in(decimal debit, decimal credit, decimal expected)
            => LedgerBankMatching.SignedMovement(debit, credit).Should().Be(expected);

        [Fact]
        public void Samma_belopp_och_dag_kan_matcha()
            => LedgerBankMatching.CouldMatch(270m, Day, 270m, Day).Should().BeTrue();

        /// <summary>
        /// ⚠️ Liggaren bokför på ConfirmedUtc — dagen arrangören bekräftade — och det kan vara en
        /// vecka efter att pengarna landade. Ett snävt fönster gör automatiken meningslös.
        /// </summary>
        [Theory]
        [InlineData(7)]
        [InlineData(-7)]
        [InlineData(3)]
        public void Inom_fonstret_kan_matcha(int offset)
            => LedgerBankMatching.CouldMatch(270m, Day, 270m, Day.AddDays(offset)).Should().BeTrue();

        [Theory]
        [InlineData(8)]
        [InlineData(-8)]
        public void Utanfor_fonstret_matchar_inte(int offset)
            => LedgerBankMatching.CouldMatch(270m, Day, 270m, Day.AddDays(offset)).Should().BeFalse();

        /// <summary>Olika belopp är olika händelser, hur nära i tid de än ligger.</summary>
        [Fact]
        public void Olika_belopp_matchar_aldrig()
            => LedgerBankMatching.CouldMatch(270m, Day, 271m, Day).Should().BeFalse();

        /// <summary>
        /// ⚠️ En insättning får aldrig matcha ett uttag på samma belopp. Utan tecknet är
        /// −270 och +270 "samma belopp", och kontot ser avstämt ut med 540 kr fel.
        /// </summary>
        [Fact]
        public void Insattning_matchar_inte_uttag()
            => LedgerBankMatching.CouldMatch(270m, Day, -270m, Day).Should().BeFalse();

        // ── Entydighetskravet ───────────────────────────────────────────────────────────────

        [Fact]
        public void En_kandidat_valjs()
            => LedgerBankMatching.SingleCandidate(new[] { "a" }).Should().Be("a");

        /// <summary>
        /// ⚠️⚠️ KÄRNAN. Två betalningar på 270 kr samma vecka är vardag i en klubb, och att ta
        /// den första är ett myntkast som ser auktoritativt ut. Operatören ska se båda.
        /// </summary>
        [Fact]
        public void Flera_kandidater_ger_ingen_automatisk_matchning()
            => LedgerBankMatching.SingleCandidate(new[] { "a", "b" }).Should().BeNull();

        [Fact]
        public void Ingen_kandidat_ger_ingen_matchning()
            => LedgerBankMatching.SingleCandidate(System.Array.Empty<string>()).Should().BeNull();
    }
}
