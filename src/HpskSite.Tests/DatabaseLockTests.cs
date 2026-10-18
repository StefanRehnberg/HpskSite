using FluentAssertions;
using HpskSite.Services.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Körs mot en riktig SQL Server när <c>HPSK_TEST_SQL</c> är satt (en anslutningssträng, t.ex. dev-databasen),
    /// annars hoppas de över MED NAMN — aldrig grönt utan att ha prövat något. Lås och räknare i databasen
    /// (skalplanen D1) kan bara bevisas av att en ANNAN anslutning ser dem; det är vad två instanser är.
    /// </summary>
    public sealed class SqlFactAttribute : FactAttribute
    {
        public SqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HPSK_TEST_SQL")))
                Skip = "HPSK_TEST_SQL är inte satt — kör mot en SQL Server för att pröva låsen i databasen.";
        }
    }

    public class DatabaseLockTests
    {
        private static string Cs => Environment.GetEnvironmentVariable("HPSK_TEST_SQL")!;

        private static IConfiguration Config(string? cs) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:umbracoDbDSN"] = cs })
            .Build();

        private static DatabaseLock Lock(string? cs) => new(Config(cs), NullLogger<DatabaseLock>.Instance);

        private static string Name() => "test:" + Guid.NewGuid().ToString("N");

        /// <summary>Kan en ANNAN session få låset just nu? (APPLOCK_TEST: 1 = ja, 0 = nej.)</summary>
        private static int OtherSessionCouldTake(string resource)
        {
            using var conn = new SqlConnection(Cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT APPLOCK_TEST('public', @r, 'Exclusive', 'Session')";
            cmd.Parameters.AddWithValue("@r", resource);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        [SqlFact]
        public void Ett_taget_las_syns_for_en_annan_anslutning_och_slapps_vid_Dispose()
        {
            var name = Name();
            using (var lease = Lock(Cs).TryAcquire(name, TimeSpan.Zero))
            {
                lease.Should().NotBeNull();
                lease!.IsDistributed.Should().BeTrue("låset ska ligga i databasen, inte i processen");
                OtherSessionCouldTake(name).Should().Be(0, "en annan instans får inte kunna ta låset");
            }
            OtherSessionCouldTake(name).Should().Be(1, "låset ska vara släppt efter Dispose");
        }

        /// <summary>
        /// ⚠️ Kan INTE skilja databaslåset från låset i processen (det senare är statiskt och delas också
        /// mellan två DatabaseLock i samma process — A/B 2026-10-18: grönt med båda). Det är
        /// <see cref="Ett_taget_las_syns_for_en_annan_anslutning_och_slapps_vid_Dispose"/> som bevisar databasen.
        /// </summary>
        [SqlFact]
        public void Tva_instanser_far_inte_samma_las_samtidigt()
        {
            var name = Name();
            var a = Lock(Cs);
            var b = Lock(Cs); // en egen instans = egen anslutning, som en annan server
            using var first = a.TryAcquire(name, TimeSpan.Zero);
            first.Should().NotBeNull();
            using var second = b.TryAcquire(name, TimeSpan.FromMilliseconds(200));
            second.Should().BeNull();
        }

        [SqlFact]
        public async Task Ett_andra_anrop_vantar_och_far_laset_nar_det_slapps()
        {
            var name = Name();
            var l = Lock(Cs);
            var first = await l.TryAcquireAsync(name, TimeSpan.Zero);
            first.Should().NotBeNull();
            var waiting = l.TryAcquireAsync(name, TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            waiting.IsCompleted.Should().BeFalse();
            first!.Dispose();
            using var second = await waiting;
            second.Should().NotBeNull();
        }

        [SqlFact]
        public void Olika_namn_hindrar_inte_varandra()
        {
            var l = Lock(Cs);
            using var a = l.TryAcquire(Name(), TimeSpan.Zero);
            using var b = l.TryAcquire(Name(), TimeSpan.Zero);
            a.Should().NotBeNull();
            b.Should().NotBeNull();
        }

        [Fact]
        public void Utan_SQL_Server_galler_laset_i_processen()
        {
            var name = Name();
            var l = Lock(null);
            using var first = l.TryAcquire(name, TimeSpan.Zero);
            first.Should().NotBeNull();
            first!.IsDistributed.Should().BeFalse();
            l.TryAcquire(name, TimeSpan.FromMilliseconds(50)).Should().BeNull();
            first.Dispose();
            using var again = l.TryAcquire(name, TimeSpan.Zero);
            again.Should().NotBeNull();
        }

        [Fact]
        public void Dispose_tva_ganger_slapper_bara_en_gang()
        {
            var name = Name();
            var l = Lock(null);
            var lease = l.TryAcquire(name, TimeSpan.Zero)!;
            lease.Dispose();
            lease.Dispose();
            using var a = l.TryAcquire(name, TimeSpan.Zero);
            a.Should().NotBeNull();
            l.TryAcquire(name, TimeSpan.Zero).Should().BeNull("en dubbel Release hade släppt in två");
        }

        // ── Räknarna ─────────────────────────────────────────────────────────────

        private static RateCounterService Counter(string? cs) =>
            new(Config(cs), new MemoryCache(new MemoryCacheOptions()), NullLogger<RateCounterService>.Instance);

        [SqlFact]
        public void Raknaren_ger_1_till_N_aven_nar_anropen_kommer_samtidigt()
        {
            var key = Name();
            var results = new System.Collections.Concurrent.ConcurrentBag<int>();
            Parallel.For(0, 20, _ => results.Add(Counter(Cs).Increment(key, TimeSpan.FromMinutes(5))));
            results.OrderBy(x => x).Should().Equal(Enumerable.Range(1, 20));
        }

        [SqlFact]
        public void Raknaren_delas_mellan_instanser()
        {
            var key = Name();
            Counter(Cs).Increment(key, TimeSpan.FromMinutes(5)).Should().Be(1);
            Counter(Cs).Increment(key, TimeSpan.FromMinutes(5)).Should().Be(2, "en ny instans ska se samma räknare");
        }

        [SqlFact]
        public void Ett_utgatt_fonster_borjar_om_pa_1()
        {
            // Fönstret måste vara längre än en kall anslutning under full testlast (~0,5 s) — med 0,3 s
            // hann fönstret gå ut mellan de två första anropen (2026-10-18).
            var key = Name();
            var c = Counter(Cs);
            var window = TimeSpan.FromSeconds(2);
            c.Increment(key, window).Should().Be(1);
            c.Increment(key, window).Should().Be(2);
            Thread.Sleep(2500);
            c.Increment(key, window).Should().Be(1);
        }

        [SqlFact]
        public void TryClaimOnce_ger_ja_en_gang_per_fonster_aven_mellan_instanser()
        {
            var key = Name();
            Counter(Cs).TryClaimOnce(key, TimeSpan.FromMinutes(5)).Should().BeTrue();
            Counter(Cs).TryClaimOnce(key, TimeSpan.FromMinutes(5)).Should().BeFalse();
        }

        [Fact]
        public void Utan_SQL_Server_raknas_i_minnet()
        {
            var key = Name();
            var c = Counter(null);
            c.Increment(key, TimeSpan.FromMinutes(5)).Should().Be(1);
            c.Increment(key, TimeSpan.FromMinutes(5)).Should().Be(2);
            c.TryClaimOnce(Name(), TimeSpan.FromMinutes(5)).Should().BeTrue();
        }
    }
}
