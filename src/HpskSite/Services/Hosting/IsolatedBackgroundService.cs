namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Basklass för VARJE egen bakgrundstjänst i pistol.nu. Ärv den här, aldrig
    /// <see cref="BackgroundService"/> direkt — ett arkitekturtest
    /// (<c>BackgroundServiceIsolationTests</c>) faller annars.
    ///
    /// <para><b>⚠️⚠️ Varför den finns.</b> Värden startar alla bakgrundstjänster från samma
    /// uppstartskontext, och Umbracos <i>ambienta</i> scope är en <c>AsyncLocal</c> som flyter med
    /// execution context. En vanlig <see cref="BackgroundService"/> ärver därför SAMMA ambienta
    /// scope-stack som alla andra. Två tjänster som öppnar ett <c>IScopeProvider</c>-scope samtidigt
    /// blir då förälder och barn och delar databasanslutning
    /// (<i>"ExecuteScalar requires an open and available Connection. The connection's current state
    /// is connecting"</i>), disponeringen kastar <i>"The Scope … being disposed is not the Ambient
    /// Scope"</i>, och rotscopet blir kvar med en ÖPPEN TRANSAKTION. Varje senare tjänst på stacken
    /// skriver sedan in i den transaktionen och låsen släpps aldrig.</para>
    ///
    /// <para>Mätt i dev 2026-09-24: fyra startkontroller med samma fördröjning kapplöpte, rankingen
    /// lade 70 X-lås på <c>RankingSnapshot</c> och S-lås på <c>umbracoLock</c> i den övergivna
    /// transaktionen, och varje inloggning hängde eftersom <c>MemberService.Save</c> inte fick sitt
    /// skrivlås. Samma familj som prodlåsningen 2026-08-29 (Task.Run utan SuppressFlow).</para>
    ///
    /// <para><b>Hur den löser det.</b> <see cref="ExecuteAsync"/> är FÖRSEGLAD och kör
    /// <see cref="ExecuteIsolatedAsync"/> i en <c>Task.Run</c> med flödet undertryckt. Arbetet
    /// börjar alltså i en tom execution context och får en egen scope-stack, oavsett vad som
    /// råkade vara ambient när värden startade tjänsten.</para>
    ///
    /// <para><b>⚠️ Isoleringen gäller MELLAN tjänster, inte inuti en.</b> Startar en tjänst själv
    /// parallellt arbete (<c>Task.WhenAll</c>, fire-and-forget) delar de delarna fortfarande
    /// tjänstens kontext. Samma regel gäller då som överallt annars:
    /// <c>using (ExecutionContext.SuppressFlow()) { _ = Task.Run(...); }</c>.</para>
    /// </summary>
    public abstract class IsolatedBackgroundService : BackgroundService
    {
        /// <summary>
        /// Förseglad med flit: en ärvande tjänst som åsidosatte den här kunde köra sitt arbete i
        /// uppstartskontexten igen och därmed återinföra felet. Skriv arbetet i
        /// <see cref="ExecuteIsolatedAsync"/>.
        /// </summary>
        protected sealed override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (ExecutionContext.SuppressFlow())
            {
                // CancellationToken.None: delegaten ska alltid köras och hantera avbrottet själv.
                // En token här kunde låta uppgiften bli Canceled innan arbetet ens börjat, och då
                // når aldrig tjänstens egen avslutningsloggning fram.
                return Task.Run(() => ExecuteIsolatedAsync(stoppingToken), CancellationToken.None);
            }
        }

        /// <summary>
        /// Tjänstens arbete. Körs i en egen, tom execution context — ingen ambient Umbraco-scope
        /// från uppstarten eller från någon annan bakgrundstjänst följer med hit.
        /// </summary>
        protected abstract Task ExecuteIsolatedAsync(CancellationToken stoppingToken);
    }
}
