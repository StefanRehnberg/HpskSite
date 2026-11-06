using System;
using HpskSite.CompetitionTypes.Common;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Spärren mot ändringar av vilka som står utanför mästerskapet (expertgruppen 2026-11-06, K3/K6). Fältets beteende måste vara OFÖRÄNDRAT
    /// (bara <c>FaltskytteResultsOfficial</c> avgör), och precisionsfamiljen får samma skydd plus finalens: en byggd finalstartlista eller en
    /// låst kvalgrupp får inte tyst ändras av en ny gästflagga.
    /// </summary>
    public class OutOfCompetitionLockTests
    {
        private const OutOfCompetitionLockReason None = OutOfCompetitionLockReason.None;

        // ── fält/MagnumFält: oförändrat ───────────────────────────────────────────
        [Fact]
        public void Falt_publicerad_ar_last() =>
            Assert.Equal(OutOfCompetitionLockReason.ResultsPublished, OutOfCompetitionLock.Evaluate(true, false, false, false, false));

        [Fact]
        public void Falt_opublicerad_ar_olast() =>
            Assert.Equal(None, OutOfCompetitionLock.Evaluate(false, false, false, false, false));

        [Fact]
        public void Falt_bryr_sig_inte_om_precisionens_finalsignaler()
        {
            // Ett fältresultat har varken finalstartlista eller kvalgrupper; även om signalerna skulle vara sanna får de inte låsa fältet.
            Assert.Equal(None, OutOfCompetitionLock.Evaluate(false, false, true, true, true));
        }

        // ── precisionsfamiljen ────────────────────────────────────────────────────
        [Fact]
        public void Precision_utan_nagot_ar_olast() =>
            Assert.Equal(None, OutOfCompetitionLock.Evaluate(false, true, false, false, false));

        [Fact]
        public void Precision_publicerad_lista_ar_last() =>
            Assert.Equal(OutOfCompetitionLockReason.ResultsPublished, OutOfCompetitionLock.Evaluate(false, true, true, false, false));

        [Fact]
        public void Precision_byggd_finalstartlista_ar_last() =>
            Assert.Equal(OutOfCompetitionLockReason.FinalsBuilt, OutOfCompetitionLock.Evaluate(false, true, false, true, false));

        [Fact]
        public void Precision_last_kvalgrupp_ar_last() =>
            Assert.Equal(OutOfCompetitionLockReason.QualifyingFrozen, OutOfCompetitionLock.Evaluate(false, true, false, false, true));

        [Fact]
        public void Precision_publicerad_gar_fore_final_och_kvalgrupp() =>
            Assert.Equal(OutOfCompetitionLockReason.ResultsPublished, OutOfCompetitionLock.Evaluate(false, true, true, true, true));

        [Fact]
        public void Precision_final_gar_fore_kvalgrupp() =>
            Assert.Equal(OutOfCompetitionLockReason.FinalsBuilt, OutOfCompetitionLock.Evaluate(false, true, false, true, true));

        // ── vilka typer är precisionsfamiljen ─────────────────────────────────────
        [Theory]
        [InlineData("Precision", true)]
        [InlineData("precision", true)]
        [InlineData("Milsnabb", true)]
        [InlineData("Duell", true)]
        [InlineData("NationellHelmatch", true)]
        [InlineData("MagnumPrecision", true)]
        [InlineData("Standardpistol", true)]
        [InlineData("Sportpistol", true)]
        [InlineData(null, true)]            // tom/okänd typ = Precision (äldre noder), som CompetitionFinals.Supports
        [InlineData("", true)]
        [InlineData("Faltskytte", false)]
        [InlineData("MagnumFalt", false)]
        [InlineData("Springskytte", false)]
        public void Precisionsfamiljen_kanns_igen(string? typeId, bool expected) =>
            Assert.Equal(expected, OutOfCompetitionLock.IsPrecisionFamily(typeId));

        // ── frysningen vid publicering/omräkning (textspärr: kontrollerar att anropen står kvar) ──
        [Fact]
        public void Precisionens_publicering_och_omrakning_fryser_besluten_fore_rakningen()
        {
            var src = ReadController();
            var toggle = src.IndexOf("public async Task<IActionResult> ToggleResultsOfficial(", StringComparison.Ordinal);
            Assert.True(toggle > 0, "ToggleResultsOfficial hittades inte");
            var freezeAtPublish = CodeIndexOf(src, "FreezeOutOfCompetitionDecisions(competition, \"publicering\")", toggle);
            var calcAtPublish = CodeIndexOf(src, "CalculateFinalResults(dbResults", toggle);
            Assert.True(freezeAtPublish > toggle, "Publiceringen fryser inte besluten");
            Assert.True(freezeAtPublish < calcAtPublish, "Besluten måste frysas FÖRE räkningen, annars räknas listan på lösa förslag");

            var refresh = src.IndexOf("private async Task<bool> RefreshResultArtifactAsync(", StringComparison.Ordinal);
            Assert.True(refresh > 0, "RefreshResultArtifactAsync hittades inte");
            var freezeAtRefresh = CodeIndexOf(src, "if (resultPage.IsOfficial) FreezeOutOfCompetitionDecisions(competition, reason)", refresh);
            var calcAtRefresh = CodeIndexOf(src, "CalculateFinalResults(results", refresh);
            Assert.True(freezeAtRefresh > refresh, "Omräkningen av en officiell lista fryser inte besluten");
            Assert.True(freezeAtRefresh < calcAtRefresh, "Besluten måste frysas FÖRE räkningen");
        }

        [Fact]
        public void Precisionens_publicering_stoppas_av_en_gast_i_ett_lag()
        {
            // "Inga gäster i lag" (Stefan 2026-11-06). Som fältets PublishResults: kontrollen sker FÖRE frysningen, så en gäst inte stämplas in i ett lag.
            var src = ReadController();
            var toggle = src.IndexOf("public async Task<IActionResult> ToggleResultsOfficial(", StringComparison.Ordinal);
            var guard = CodeIndexOf(src, "_teamService.OutsideMemberInTeamErrorAsync(request.CompetitionId)", toggle);
            var freeze = CodeIndexOf(src, "FreezeOutOfCompetitionDecisions(competition, \"publicering\")", toggle);
            Assert.True(guard > toggle, "Publiceringen kontrollerar inte om en gäst ligger i ett lag");
            Assert.True(guard < freeze, "Lagkontrollen måste ske FÖRE frysningen");
        }

        [Fact]
        public void Klassbyte_flyttar_gastflaggan_med_skytten()
        {
            // Lagringen raderar beslutet när klassraden byts ut ("class-removed"). Utan att flaggan läses FÖRE ändringen och stämplas på den nya
            // klassen blir en gäst tyst mästerskapsdeltagare vid ett klassbyte (expertgruppen K7).
            var src = ReadController();
            var change = src.IndexOf("public async Task<IActionResult> ChangeShooterClass(", StringComparison.Ordinal);
            Assert.True(change > 0, "ChangeShooterClass hittades inte");
            var read = CodeIndexOf(src, "carryOutside = _outOfCompetition.Load(competition, outsideRule).Contains(request.MemberId, request.OldShootingClass)", change);
            var update = CodeIndexOf(src, "UpdateRegistrationShooterClass(request.CompetitionId", change);
            var stamp = CodeIndexOf(src, "_registrations.StampOutOfCompetition(request.CompetitionId, movedRegistration.Id, newClass.Name, \"class-changed\")", change);
            Assert.True(read > change, "Klassbytet läser inte om skytten står utanför");
            Assert.True(read < update, "Flaggan måste läsas FÖRE registreringen ändras (beslutet raderas av ändringen)");
            Assert.True(stamp > update, "Flaggan måste stämplas på den nya klassen EFTER registreringen ändrats");
        }

        /// <summary>Första träffen på en rad som INTE är en kommentar. En bortkommenterad kod får inte räknas som kvar (A/B 2026-11-06: den räknades).</summary>
        private static int CodeIndexOf(string src, string needle, int from)
        {
            var i = from;
            while ((i = src.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                var lineStart = src.LastIndexOf('\n', i) + 1;
                var before = src.Substring(lineStart, i - lineStart);
                if (!before.Contains("//", StringComparison.Ordinal)) return i;
                i += needle.Length;
            }
            return -1;
        }

        private static string ReadController([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            var src = System.IO.Path.GetDirectoryName(thisFile)!;
            var path = System.IO.Path.Combine(src, "..", "HpskSite", "Controllers", "CompetitionResultsController.cs");
            return System.IO.File.ReadAllText(path);
        }

        // ── kvalgruppens ögonblicksbild ───────────────────────────────────────────
        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("{}", false)]
        [InlineData("{\"CompetitionId\":5,\"ClassSnapshots\":{}}", false)]
        [InlineData("{\"CompetitionId\":5,\"ClassSnapshots\":{\"C\":{\"ChampionshipClass\":\"C\"}}}", true)]
        [InlineData("inte json", false)]    // trasig JSON räknas som ingen låst grupp, samma som PrecisionQualifyingResultsService.GetSnapshot
        public void Last_grupp_i_ogonblicksbilden(string? json, bool expected) =>
            Assert.Equal(expected, OutOfCompetitionLockReader.HasFrozenGroup(json));
    }
}
