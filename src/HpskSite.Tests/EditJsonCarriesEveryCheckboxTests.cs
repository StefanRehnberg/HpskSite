using System.Runtime.CompilerServices;
using HpskSite.CompetitionTypes.Common;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Bugg 2026-11-08: "Hederspriser utgår" sparades men var urbockad när tävlingen öppnades igen.
    /// Redigeringsmodalen fylls ur tävlingens JSON (hpskPopulateFromCatalog), och båda JSON-endpointsen
    /// utelämnade <c>isAwardingHonoraryAward</c> — så rutan ritades alltid tom, och nästa sparning
    /// skrev tillbaka false. Varje kryssruta i katalogen måste därför följa med i båda svaren.
    /// </summary>
    public class EditJsonCarriesEveryCheckboxTests
    {
        private static string Src(string file, [CallerFilePath] string here = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite", "Controllers", file)));

        /// <summary>Källtext utan radkommentarer, så att ett utkommenterat fält inte räknas.</summary>
        private static string Code(string src) =>
            string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        public static IEnumerable<object[]> Checkboxes() =>
            CompetitionFieldCatalog.AllFieldNames
                .Where(n => CompetitionFieldCatalog.Find(n)!.Control == FieldControl.Checkbox)
                .Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(Checkboxes))]
        public void Edit_endpoint_returns_the_checkbox(string name) =>
            Assert.Contains(name + " =", Code(Src("CompetitionEditController.cs")));

        [Theory]
        [MemberData(nameof(Checkboxes))]
        public void Admin_endpoint_returns_the_checkbox(string name) =>
            Assert.Contains(name + " =", Code(Src("CompetitionAdminController.cs")));
    }
}
