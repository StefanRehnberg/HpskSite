using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ EN skrivväg för en klubbavgifts belopp (medlemsavgift steg 2.4). Avgiften bär rader och
    /// <c>Amount</c> = radernas summa; sätter något annat beloppet kan raderna och summan säga olika saker.
    /// Allt går genom <c>MembershipFeeService.ApplyCalculation</c>. Kretsavgiften har sin egen väg
    /// (<c>RegionFeeCalculator.Total</c>) och räknas inte hit.
    /// </summary>
    public class ClubFeeSingleWritePathTests
    {
        private static string SourceRoot([CallerFilePath] string here = "")
            => Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite");

        private static IEnumerable<(string File, string Text)> ServiceAndControllerSources()
        {
            var root = SourceRoot();
            foreach (var dir in new[] { "Services", "Controllers", "Models" })
                foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                    yield return (f, File.ReadAllText(f));
        }

        [Fact]
        public void Ingen_tilldelar_Amount_pa_en_avgift_utom_ApplyCalculation_och_kretsavgiften()
        {
            var assign = new Regex(@"\b(?:charge|row)\.Amount\s*=(?!=)\s*(?<rhs>[^;]*);");
            var offenders = new List<string>();
            // Bara medlemsavgiftens filer: tävlingsavgiften (CompetitionFeeService) är en annan sort och har sin egen skrivväg.
            foreach (var (file, text) in ServiceAndControllerSources().Where(s => Path.GetFileName(s.File).StartsWith("MembershipFee")))
                foreach (Match m in assign.Matches(text))
                {
                    var rhs = m.Groups["rhs"].Value;
                    if (rhs.Contains("calc.Total") || rhs.Contains("RegionFeeCalculator.Total")) continue;
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
                }
            Assert.True(offenders.Count == 0, "Beloppet på en klubbavgift får bara sättas av ApplyCalculation:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void En_ny_klubbavgift_skapas_aldrig_med_ett_eget_belopp()
        {
            // new MembershipFeeCharge { … ClubId = … } (utan IssuerType/PayerClubId = kretsens) får inte sätta Amount.
            var init = new Regex(@"new MembershipFeeCharge\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
            var offenders = new List<string>();
            foreach (var (file, text) in ServiceAndControllerSources())
                foreach (Match m in init.Matches(text))
                {
                    var body = m.Groups["body"].Value;
                    var isClubFee = Regex.IsMatch(body, @"\bClubId\s*=") && !body.Contains("IssuerType") && !body.Contains("PayerClubId");
                    if (isClubFee && Regex.IsMatch(body, @"\bAmount\s*=")) offenders.Add(Path.GetFileName(file));
                }
            Assert.True(offenders.Count == 0, "En klubbavgift skapas utan Amount och skrivs av ApplyCalculation: " + string.Join(", ", offenders));
        }
    }
}
