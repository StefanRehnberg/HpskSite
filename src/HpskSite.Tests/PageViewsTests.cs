using System.Runtime.CompilerServices;
using HpskSite.Routing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// C3-planen §5.5 punkt 6: att ta bort en mall i Umbraco raderar vyfilen med mallens namn. Sidornas vyer
    /// (<see cref="PageViews"/>) får därför aldrig heta som en mall, och filerna med de gamla namnen ska förbli
    /// mellanled — skrivs sidans innehåll tillbaka i dem försvinner det med mallen.
    /// </summary>
    public class PageViewsTests
    {
        [Fact]
        public void Varje_sidvy_finns()
        {
            foreach (var view in PageViews.All)
                Assert.True(File.Exists(Path.Combine(ViewsDir(), view + ".cshtml")), $"Vyn {view}.cshtml saknas.");
        }

        [Fact]
        public void Ingen_sidvy_heter_som_en_mall()
        {
            foreach (var view in PageViews.All)
                Assert.DoesNotContain(PageViews.LegacyTemplates, t => string.Equals(t, view, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void De_gamla_namnen_ar_mellanled()
        {
            foreach (var view in PageViews.All)
            {
                var legacy = view[..^"Page".Length];
                var path = Path.Combine(ViewsDir(), legacy + ".cshtml");
                Assert.True(File.Exists(path), $"Mellanledet {legacy}.cshtml saknas — Umbracos mall i nodläget hittar ingen vy.");
                var text = File.ReadAllText(path);
                Assert.Contains($"PartialAsync(\"{view}\"", text);
                Assert.True(text.Split('\n').Length < 15, $"{legacy}.cshtml är inte längre ett mellanled — sidans innehåll hör i {view}.cshtml.");
            }
        }

        [Theory]
        [InlineData("Competition", PageViews.Competition)]
        [InlineData("precisionFinalsStartList", PageViews.FinalsStartList)]
        [InlineData("RegistrationInvoice", "RegistrationInvoice")]
        public void Mallen_leder_till_sidans_vy(string template, string expected)
            => Assert.Equal(expected, PageViews.ForTemplate(template));

        private static string ViewsDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "HpskSite", "Views");
    }
}
