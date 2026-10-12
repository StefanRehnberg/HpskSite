using FluentAssertions;
using HpskSite.Models.CompetitionFees;
using HpskSite.Services.CompetitionFees;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Umbraco.Cms.Infrastructure.Persistence;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Stefans beslut 2026-10-12: går betalningsmodellen inte att läsa ska ingenting skapas.
    /// Förut svarade tjänsten "legacy", och då skapade PaymentService gamla fakturanoder för nya
    /// tävlingar så snart läsningen föll.
    /// </summary>
    public class CompetitionPaymentModelServiceTests
    {
        private static (CompetitionPaymentModelService Service, Mock<IUmbracoDatabaseFactory> Factory) Broken()
        {
            var factory = new Mock<IUmbracoDatabaseFactory>();
            factory.Setup(f => f.CreateDatabase()).Throws(new InvalidOperationException("Invalid object name 'dbo.CompetitionPaymentModel'."));
            return (new CompetitionPaymentModelService(factory.Object, NullLogger<CompetitionPaymentModelService>.Instance), factory);
        }

        [Fact]
        public void Olasbart_beslut_ar_varken_ny_eller_gammal_modell()
        {
            const int id = 990_001;
            CompetitionPaymentModelService.Forget(id);
            var (svc, _) = Broken();

            svc.Get(id).Should().Be(CompetitionPaymentModels.Unknown);
            svc.IsUnknown(id).Should().BeTrue();
            svc.IsLegacy(id).Should().BeFalse("den gamla modellen är vad som skapade fakturanoderna");
            svc.IsLedger(id).Should().BeFalse();
        }

        [Fact]
        public void Olasbart_beslut_cachas_inte()
        {
            const int id = 990_002;
            CompetitionPaymentModelService.Forget(id);
            var (svc, factory) = Broken();

            svc.Get(id);
            svc.Get(id);

            factory.Verify(f => f.CreateDatabase(), Times.Exactly(2), "nästa läsning ska försöka igen");
        }

        [Fact]
        public void Forsta_anmalan_skriver_aldrig_ned_ett_olasbart_beslut()
        {
            const int id = 990_003;
            CompetitionPaymentModelService.Forget(id);
            var (svc, factory) = Broken();

            svc.DecideAtFirstRegistration(id).Should().Be(CompetitionPaymentModels.Unknown);

            // Bara läsningen — ingen andra anslutning för att skriva "unknown" som tävlingens modell för alltid.
            factory.Verify(f => f.CreateDatabase(), Times.Once);
        }
    }
}
