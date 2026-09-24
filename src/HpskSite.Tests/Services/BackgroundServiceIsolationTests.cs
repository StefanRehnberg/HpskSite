using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HpskSite.Services.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Spärren mot att en bakgrundstjänst ärver värdens ambienta Umbraco-scope. Se
    /// <see cref="IsolatedBackgroundService"/> för varför — kort: två tjänster på samma ambienta
    /// scope-stack delar databasanslutning, disponeringen kastar, och en övergiven transaktion
    /// låste varje inloggning i dev 2026-09-24.
    ///
    /// Två sorters test, och båda behövs:
    ///   1. ARKITEKTURREGELN — varje hostad tjänst i HpskSite ärver <see cref="IsolatedBackgroundService"/>.
    ///      Det är den som gör att en NY tjänst görs rätt: glömmer någon basklassen blir sviten röd.
    ///   2. MEKANISMEN — att basklassen verkligen släpper execution context. Umbracos ambienta
    ///      scope är en AsyncLocal, så ett AsyncLocal-värde som satts före start prövar exakt samma
    ///      sak utan att Umbraco behöver startas. Kontrollprovet med en vanlig BackgroundService
    ///      visar att provet KAN falla — utan det vore "värdet syns inte" grönt även på trasig kod.
    /// </summary>
    public class BackgroundServiceIsolationTests
    {
        private static readonly Assembly SiteAssembly = typeof(IsolatedBackgroundService).Assembly;

        [Fact]
        public void EveryHostedServiceInTheSite_DerivesFromIsolatedBackgroundService()
        {
            var offenders = SiteAssembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IHostedService).IsAssignableFrom(t))
                .Where(t => !typeof(IsolatedBackgroundService).IsAssignableFrom(t))
                .Select(t => t.FullName)
                .OrderBy(n => n)
                .ToList();

            Assert.True(offenders.Count == 0,
                "Dessa bakgrundstjänster ärver inte IsolatedBackgroundService och kan därför dela "
                + "ambient Umbraco-scope med andra tjänster (öppen transaktion, låst inloggning). "
                + "Ärv HpskSite.Services.Hosting.IsolatedBackgroundService och skriv arbetet i "
                + "ExecuteIsolatedAsync: " + string.Join(", ", offenders));
        }

        [Fact]
        public void TheSiteHasHostedServices_SoTheRuleAboveIsNotVacuous()
        {
            // Utan det här påståendet vore regeln ovan grön även om reflektionen slutade hitta
            // något alls (fel assembly, ändrad typ) — ett test som inte kan falla.
            var isolated = SiteAssembly.GetTypes()
                .Count(t => t.IsClass && !t.IsAbstract && typeof(IsolatedBackgroundService).IsAssignableFrom(t));
            Assert.True(isolated >= 8, $"Hittade bara {isolated} isolerade bakgrundstjänster.");
        }

        [Fact]
        public void ExecuteAsync_IsSealed_SoASubclassCannotBypassTheIsolation()
        {
            var method = typeof(IsolatedBackgroundService).GetMethod(
                "ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.True(method!.IsFinal, "IsolatedBackgroundService.ExecuteAsync måste vara sealed.");
        }

        private static readonly AsyncLocal<string?> Ambient = new();

        [Fact]
        public async Task IsolatedService_DoesNotSeeTheStartersAmbientContext()
        {
            Ambient.Value = "värdens scope";
            var probe = new IsolatedProbe();

            await probe.StartAsync(CancellationToken.None);
            var seen = await probe.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await probe.StopAsync(CancellationToken.None);

            Assert.Null(seen);
        }

        [Fact]
        public async Task ControlProbe_PlainBackgroundService_DoesSeeIt()
        {
            // Kontrollprovet: en vanlig BackgroundService ÄRVER värdet. Det är felet vi skyddar mot,
            // och det är beviset att provet ovan mäter något.
            Ambient.Value = "värdens scope";
            var probe = new PlainProbe();

            await probe.StartAsync(CancellationToken.None);
            var seen = await probe.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await probe.StopAsync(CancellationToken.None);

            Assert.Equal("värdens scope", seen);
        }

        private sealed class IsolatedProbe : IsolatedBackgroundService
        {
            public TaskCompletionSource<string?> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
            {
                await Task.Yield();
                Seen.TrySetResult(Ambient.Value);
            }
        }

        private sealed class PlainProbe : BackgroundService
        {
            public TaskCompletionSource<string?> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await Task.Yield();
                Seen.TrySetResult(Ambient.Value);
            }
        }
    }
}
