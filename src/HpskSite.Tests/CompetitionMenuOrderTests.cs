using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Menyn visar bara pågående och kommande, närmast först (Stefan 2026-10-13).</summary>
    public class CompetitionMenuOrderTests
    {
        private static readonly DateTime Today = new(2026, 10, 13);
        private static readonly DateTime None = DateTime.MinValue;

        [Fact]
        public void Avslutad_visas_inte()
            => Assert.Null(MenuOrder.KeyFor(new(2026, 4, 1), new(2026, 9, 30), Today));

        [Fact]
        public void Pagaende_sorteras_som_idag()
            => Assert.Equal(Today, MenuOrder.KeyFor(new(2026, 4, 1), new(2026, 11, 30), Today));

        [Fact]
        public void Kommande_sorteras_pa_startdagen()
            => Assert.Equal(new DateTime(2026, 11, 1), MenuOrder.KeyFor(new(2026, 11, 1), new(2027, 3, 1), Today));

        [Fact]
        public void Utan_slutdatum_galler_startdagen()
        {
            Assert.Null(MenuOrder.KeyFor(new(2026, 10, 12), None, Today));
            Assert.Equal(Today, MenuOrder.KeyFor(Today, None, Today));
        }

        [Fact]
        public void Utan_datum_visas_inte()
            => Assert.Null(MenuOrder.KeyFor(None, None, Today));
    }
}
