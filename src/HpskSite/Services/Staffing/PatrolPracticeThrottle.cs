using System.Collections.Concurrent;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Skydd för övningsläget (öppet för inloggade medlemmar): motorn är CPU på request-tråden, så det får köras högst <see cref="MaxConcurrent"/> beräkningar samtidigt
    /// i hela appen och högst <see cref="PerMemberPerMinute"/> per medlem och minut. Appen har ingen generell rate limiter; den här gäller bara övningen.
    /// Inget om scenariot sparas här — bara tidpunkter per medlem-id.
    /// </summary>
    public sealed class PatrolPracticeThrottle
    {
        public const int MaxConcurrent = 3, PerMemberPerMinute = 60;
        private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
        private readonly ConcurrentDictionary<int, Queue<DateTime>> _calls = new();

        /// <summary>Ger en plats (lämnas tillbaka med Dispose) eller null + skälet. Tiden skickas in så att det går att testa.</summary>
        public IDisposable? TryEnter(int memberId, DateTime nowUtc, out string? reason)
        {
            var q = _calls.GetOrAdd(memberId, _ => new Queue<DateTime>());
            lock (q)
            {
                while (q.Count > 0 && nowUtc - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
                if (q.Count >= PerMemberPerMinute) { reason = "Du har provat många gånger på kort tid. Vänta en minut och försök igen."; return null; }
                if (!_slots.Wait(0)) { reason = "Många övar just nu. Försök igen om en stund."; return null; }
                q.Enqueue(nowUtc);
            }
            reason = null;
            return new Lease(_slots);
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _s;
            public Lease(SemaphoreSlim s) { _s = s; }
            public void Dispose() { Interlocked.Exchange(ref _s, null)?.Release(); }
        }
    }
}
