using FluentAssertions;
using HpskSite.Middleware;
using HpskSite.Services.Ops;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>C3-planen F6: driftskollens regler — områdena, räknaren, strypningen, hälsoregeln, middlewaren.</summary>
    public class OpsTests
    {
        [Theory]
        [InlineData("/", "/")]
        [InlineData("", "/")]
        [InlineData("/competitions/2026/halland/x/", "/competitions")]
        [InlineData("/Competitions?x=1", "/competitions")]
        [InlineData("/umbraco/surface/Ops/GetErrorDay", "/umbraco/surface/ops")]
        [InlineData("/umbraco/api/Foo/Bar", "/umbraco/api/foo")]
        [InlineData("/umbraco/backoffice", "/umbraco")]
        [InlineData("/wp-login.php", "(fil .php)")]
        [InlineData("/x.png", "(fil .png)")]
        [InlineData("/a b<script>", "/a_b_script_")]
        public void Area(string path, string expected) => RequestErrorArea.For(path).Should().Be(expected);

        [Fact]
        public void Area_is_capped()
            => RequestErrorArea.For("/" + new string('a', 500)).Length.Should().Be(RequestErrorArea.MaxAreaLength);

        [Fact]
        public void Counter_groups_and_drains()
        {
            var c = new RequestErrorCounter();
            var now = new DateTime(2026, 10, 13, 12, 0, 0);
            c.Record(404, "/a/1", false, now);
            c.Record(404, "/a/2", false, now.AddMinutes(1));
            c.Record(404, "/a/3", true, now);
            c.Record(200, "/a/4", false, now);
            var rows = c.Drain();
            rows.Should().HaveCount(2);
            var human = rows.Single(r => !r.Key.IsBot);
            human.Count.Should().Be(2);
            human.LastPath.Should().Be("/a/2");
            c.Drain().Should().BeEmpty();
        }

        [Fact]
        public void Counter_overflows_into_one_key_and_keeps_the_total()
        {
            var c = new RequestErrorCounter();
            var now = DateTime.Today;
            for (var i = 0; i < RequestErrorCounter.MaxKeys + 50; i++) c.Record(404, "/p" + i, false, now);
            var rows = c.Drain();
            rows.Should().HaveCount(RequestErrorCounter.MaxKeys + 1);
            rows.Sum(r => r.Count).Should().Be(RequestErrorCounter.MaxKeys + 50);
            rows.Single(r => r.Key.Area == RequestErrorArea.Overflow).Count.Should().Be(50);
        }

        [Fact]
        public void Counter_restore_puts_back_what_could_not_be_written()
        {
            var c = new RequestErrorCounter();
            c.Record(500, "/x", false, DateTime.Today);
            var rows = c.Drain();
            c.Restore(rows);
            c.Record(500, "/x", false, DateTime.Today);
            c.Drain().Single().Count.Should().Be(2);
        }

        [Fact]
        public void Signals_are_throttled_per_key_but_counted()
        {
            OpsSignals.ResetForTests();
            OpsSignals.ShouldLog("k1").Should().BeTrue();
            OpsSignals.ShouldLog("k1").Should().BeFalse();
            OpsSignals.ShouldLog("k2").Should().BeTrue();
            var ex = new InvalidOperationException("x");
            OpsSignals.AuthGateFailed(NullLogger.Instance, ex, "G", 1).Should().BeFalse();
            OpsSignals.AuthGateFailed(NullLogger.Instance, ex, "G", 1).Should().BeFalse();
            OpsSignals.Count(OpsSignals.KindAuthError).Should().Be(2);
        }

        [Fact]
        public void Signals_cap_lines_per_window()
        {
            OpsSignals.ResetForTests();
            var logged = Enumerable.Range(0, OpsSignals.MaxLinesPerWindow + 20).Count(i => OpsSignals.ShouldLog("cap" + i));
            logged.Should().Be(OpsSignals.MaxLinesPerWindow);
        }

        [Fact]
        public void Store_miss_is_counted_in_both_modes()
        {
            OpsSignals.ResetForTests();
            OpsSignals.StoreMiss(NullLogger.Instance, "anmälan", 5, "node");
            OpsSignals.StoreMiss(NullLogger.Instance, "anmälan", 5, "table");
            OpsSignals.Count(OpsSignals.KindStoreMiss).Should().Be(2);
        }

        private static CompetitionHealthService.StoreState Store(string store = "table", bool exists = true,
            int born = 0, int next = 101, int highest = 100, bool frozen = false)
            => new() { Name = "X", Table = "dbo.X", Store = store, TableExists = exists, TableBorn = born, NextId = next, HighestReferenced = highest, WriteFrozen = frozen };

        private static CompetitionHealthService.Report Eval(params CompetitionHealthService.StoreState[] s)
        {
            var r = new CompetitionHealthService.Report { Stores = s.ToList() };
            CompetitionHealthService.Evaluate(r);
            return r;
        }

        [Fact] public void Health_ok() => Eval(Store()).Status.Should().Be("OK");
        [Fact] public void Health_fails_when_table_mode_has_no_table() => Eval(Store(exists: false)).Status.Should().Be("FAIL");
        [Fact] public void Health_fails_when_node_mode_hides_table_born_rows() => Eval(Store(store: "node", born: 3)).Status.Should().Be("FAIL");
        [Fact] public void Health_allows_table_born_rows_in_table_mode() => Eval(Store(born: 3)).Status.Should().Be("OK");
        [Fact] public void Health_fails_below_the_floor() => Eval(Store(next: 100, highest: 100)).Status.Should().Be("FAIL");
        [Fact] public void Health_ignores_floor_without_table() => Eval(Store(store: "node", exists: false, next: 0, highest: 50)).Status.Should().Be("OK");
        // Mätt i dev 2026-10-13: resultatnoder skapade efter senaste omsättningen — ofarligt så länge noderna gäller.
        [Fact] public void Health_ignores_floor_in_node_mode() => Eval(Store(store: "node", next: 11025, highest: 11051)).Status.Should().Be("OK");
        [Fact] public void Health_warns_when_frozen() => Eval(Store(frozen: true)).Status.Should().Be("WARN");

        [Fact]
        public void Health_warns_on_competitions_without_url()
        {
            var r = new CompetitionHealthService.Report { Stores = new() { Store() }, CompetitionsWithoutUrl = new() { "1 X" } };
            CompetitionHealthService.Evaluate(r);
            r.Status.Should().Be("WARN");
        }

        [Fact]
        public async Task Middleware_counts_404_and_unhandled_exception_as_500()
        {
            var c = new RequestErrorCounter();
            var notFound = new RequestErrorMiddleware(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; }, c);
            var ctx1 = new DefaultHttpContext(); ctx1.Request.Path = "/nope"; ctx1.Request.Headers.UserAgent = "Mozilla/5.0";
            await notFound.InvokeAsync(ctx1);
            var boom = new RequestErrorMiddleware(_ => throw new InvalidOperationException("x"), c);
            var ctx2 = new DefaultHttpContext(); ctx2.Request.Path = "/boom";
            var act = async () => await boom.InvokeAsync(ctx2);
            await act.Should().ThrowAsync<InvalidOperationException>();
            var rows = c.Drain();
            rows.Should().Contain(r => r.Key.Status == 404 && r.Key.Area == "/nope" && !r.Key.IsBot);
            rows.Should().Contain(r => r.Key.Status == 500 && r.Key.Area == "/boom" && r.Key.IsBot);
        }

        [Fact]
        public void Sink_renders_without_quotes_and_only_takes_fatal()
        {
            var q = new CriticalAlertQueue();
            var sink = new CriticalAlertSink(q);
            var parser = new Serilog.Parsing.MessageTemplateParser();
            Serilog.Events.LogEvent Ev(Serilog.Events.LogEventLevel l) => new(DateTimeOffset.Now, l, null,
                parser.Parse("Provlarm ({Name}, {N})"),
                new[] { new Serilog.Events.LogEventProperty("Name", new Serilog.Events.ScalarValue("Admin")),
                        new Serilog.Events.LogEventProperty("N", new Serilog.Events.ScalarValue(3)) });
            sink.Emit(Ev(Serilog.Events.LogEventLevel.Error));
            sink.Emit(Ev(Serilog.Events.LogEventLevel.Fatal));
            var items = q.DrainAll();
            items.Should().ContainSingle().Which.Message.Should().Be("Provlarm (Admin, 3)");
        }
    }
}
