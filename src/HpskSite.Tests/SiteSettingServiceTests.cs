using HpskSite.Services.SiteSettings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// A2 i genomgången 2026-10-13: ett läsfel på en växel fick ALDRIG svara med standardvärdet
    /// (<c>node</c>) medan prod kör tabellerna. Senast kända värdet, annars vägran.
    /// </summary>
    public class SiteSettingServiceTests
    {
        private sealed class FakeSettings : SiteSettingService
        {
            public Func<string, string?> Read = _ => null;
            public int Reads;
            public FakeSettings(IMemoryCache cache) : base(null!, cache, NullLogger<SiteSettingService>.Instance) { }
            protected override string? ReadRaw(string key) { Reads++; return Read(key); }
        }

        private static (FakeSettings s, MemoryCache cache) Make()
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            return (new FakeSettings(cache), cache);
        }

        [Fact]
        public void Reads_the_stored_value_and_caches_it()
        {
            var (s, _) = Make();
            s.Read = _ => " table ";
            Assert.Equal("table", s.Get("Registrations:Store", "node"));
            Assert.Equal("table", s.Get("Registrations:Store", "node"));
            Assert.Equal(1, s.Reads);
        }

        [Fact]
        public void Missing_row_gives_the_default()
        {
            var (s, _) = Make();
            s.Read = _ => null;
            Assert.Equal("node", s.Get("Registrations:Store", "node"));
        }

        [Fact]
        public void Read_failure_after_a_good_read_keeps_the_last_known_value_not_the_default()
        {
            var (s, cache) = Make();
            s.Read = _ => "table";
            Assert.Equal("table", s.Get("Registrations:Store", "node"));

            cache.Compact(1.0); // cachen gått ut
            s.Read = _ => throw new TimeoutException("lås");
            Assert.Equal("table", s.Get("Registrations:Store", "node"));
        }

        [Fact]
        public void Read_failure_without_any_known_value_refuses()
        {
            var (s, _) = Make();
            s.Read = _ => throw new TimeoutException("lås");
            Assert.Throws<SiteSettingUnavailableException>(() => s.Get("Registrations:Store", "node"));
        }

        [Fact]
        public void A_refusal_is_not_cached_as_a_value()
        {
            var (s, _) = Make();
            s.Read = _ => throw new TimeoutException("lås");
            Assert.Throws<SiteSettingUnavailableException>(() => s.Get("Events:Store", "node"));
            s.Read = _ => "table";
            Assert.Equal("table", s.Get("Events:Store", "node"));
        }

        [Fact]
        public void GetFresh_bypasses_cache_and_last_known_value()
        {
            var (s, _) = Make();
            s.Read = _ => "node";
            Assert.Equal("node", s.Get("Events:Store", "node"));

            s.Read = _ => throw new TimeoutException("lås");
            // Get svarar med det kända värdet ur cachen; GetFresh vägrar.
            Assert.Equal("node", s.Get("Events:Store", "node"));
            Assert.Throws<SiteSettingUnavailableException>(() => s.GetFresh("Events:Store", "node"));

            s.Read = _ => "table";
            Assert.Equal("table", s.GetFresh("Events:Store", "node"));
        }
    }
}
