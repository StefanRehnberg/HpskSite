using System.Runtime.CompilerServices;
using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ BASLINJEN FÖR ENDPOINTS (säkerhetsplanen 2026-11-02, release 1c). Varje action i sajtens egna controllers ska ha
    /// ett av: <c>[Authorize]</c>, <c>[AllowAnonymous]</c>, <c>[PublicEndpoint("skäl")]</c> eller <c>[InCodeAuth("var")]</c>.
    /// Förut kontrollerade nästan alla sin behörighet i sin egen kod, utan att något kunde se vilka — så
    /// <c>Club/GetClubMembers</c> låg öppen för anonyma. De som ännu saknar markering står i
    /// <c>endpoint-auth-baseline.txt</c> (en rad per action). Testet fäller bygget för
    /// <list type="bullet">
    /// <item>en NY omärkt action (märk den — det är ett beslut, inte en formalitet), och</item>
    /// <item>en rad i baslinjen som inte längre är omärkt (ta bort raden: ratchet, listan får bara krympa).</item>
    /// </list>
    /// Körtid: <see cref="UnclassifiedEndpointFilter"/> loggar anonyma anrop till de omärkta, och nekar dem i läget Enforce.
    /// Skapa om listan: <c>HPSK_REGEN_BASELINE=1 dotnet test -c Release --filter EndpointAuthBaselineTests</c>.
    /// </summary>
    public class EndpointAuthBaselineTests
    {
        private static string BaselinePath([CallerFilePath] string thisFile = "") =>
            Path.Combine(Path.GetDirectoryName(thisFile)!, "endpoint-auth-baseline.txt");

        private static SortedSet<string> Unclassified() =>
            new(EndpointAuthClassifier.OwnActions(typeof(UnclassifiedEndpointFilter).Assembly)
                .Where(m => EndpointAuthClassifier.Classify(m) == EndpointAuthKind.Unclassified)
                .Select(EndpointAuthClassifier.Key), StringComparer.Ordinal);

        [Fact]
        public void Omarkta_endpoints_ar_exakt_baslinjen()
        {
            var actual = Unclassified();
            if (Environment.GetEnvironmentVariable("HPSK_REGEN_BASELINE") == "1")
            {
                File.WriteAllLines(BaselinePath(), actual);
                return;
            }
            var baseline = new SortedSet<string>(
                File.ReadAllLines(BaselinePath()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()), StringComparer.Ordinal);

            var added = actual.Except(baseline).ToList();
            var gone = baseline.Except(actual).ToList();
            Assert.True(added.Count == 0,
                "Nya endpoints utan markering. Lägg [Authorize], [AllowAnonymous], [PublicEndpoint(\"skäl\")] eller [InCodeAuth(\"var\")]:\n" +
                string.Join("\n", added));
            Assert.True(gone.Count == 0,
                "Dessa är markerade (eller borta) — ta bort dem ur endpoint-auth-baseline.txt så att listan krymper:\n" +
                string.Join("\n", gone));
        }

        [Fact]
        public void Klassningen_kanner_igen_varje_markering()
        {
            var t = typeof(Fixture);
            Assert.Equal(EndpointAuthKind.Authorize, EndpointAuthClassifier.Classify(t.GetMethod(nameof(Fixture.A))!));
            Assert.Equal(EndpointAuthKind.AllowAnonymous, EndpointAuthClassifier.Classify(t.GetMethod(nameof(Fixture.B))!));
            Assert.Equal(EndpointAuthKind.PublicEndpoint, EndpointAuthClassifier.Classify(t.GetMethod(nameof(Fixture.C))!));
            Assert.Equal(EndpointAuthKind.InCodeAuth, EndpointAuthClassifier.Classify(t.GetMethod(nameof(Fixture.D))!));
            Assert.Equal(EndpointAuthKind.Unclassified, EndpointAuthClassifier.Classify(t.GetMethod(nameof(Fixture.E))!));
        }

        [Fact]
        public void Lagen_tolkas()
        {
            Assert.Equal("log", UnclassifiedEndpointFilter.ModeOf(null));
            Assert.Equal("log", UnclassifiedEndpointFilter.ModeOf("nonsens"));
            Assert.Equal("enforce", UnclassifiedEndpointFilter.ModeOf(" Enforce "));
            Assert.Equal("off", UnclassifiedEndpointFilter.ModeOf("OFF"));
        }

        public class Fixture : Microsoft.AspNetCore.Mvc.ControllerBase
        {
            [Microsoft.AspNetCore.Authorization.Authorize] public void A() { }
            [Microsoft.AspNetCore.Authorization.AllowAnonymous] public void B() { }
            [PublicEndpoint("test")] public void C() { }
            [InCodeAuth("test")] public void D() { }
            public void E() { }
        }
    }
}
