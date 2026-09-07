using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Mästerskapskategorin en skytt tävlar i — den indelning ett MÄSTERSKAP avgörs i.
    ///
    /// ⚠️ Skicklighetsklasserna 1–3 är INTE egna kategorier. C1, C2 och C3 är samma
    /// vapengrupp C öppen och har EN uppsättning finalister; A1+A2+A3 är "A"; B1+B2+B3 är
    /// "B". Vid SM och Landsdelsmästerskap delas vapengrupp C dessutom i sina fem
    /// kategorier: öppen, Dam, Vet Y, Vet Ä och Junior — var och en med egna finalister.
    /// (Stefan 2026-09-07: "Klasserna 1-3 är bara olika skicklighetsklasser inte egna
    /// vapenklasser.")
    ///
    /// ⚠️ DETTA ÄR INTE KLASSAMMANSLAGNING. Sammanslagningen i resultatlistan är regeln om
    /// FÄRRE ÄN FEM DELTAGARE (SHB FR-101) och bär den absoluta spärren "Klass 1 sammanslås
    /// inte med annan klass" (FR-102, ordagrant i D.2.3/F.2.3/H.2.3/I.2.3). Kategorin här
    /// gäller oavsett deltagarantal och rör inte den spärren. Blanda inte de två: att pool:a
    /// C1–C3 via sammanslagningsmodalen skulle upphäva FR-102 för alla tävlingar.
    ///
    /// Regeln är medvetet DENSAMMA som standardmedaljerna redan använder
    /// (<see cref="Precision.Services.StandardMedalCalculationService.ShouldSplitGroupC"/> +
    /// dess vapengruppsindelning) — medaljerna räknade redan per kategori medan finalerna
    /// räknade per underklass, och den skillnaden var buggen.
    /// </summary>
    public static class ChampionshipCategory
    {
        /// <summary>
        /// Delas vapengrupp C i sina fem kategorier? Bara vid SM och Landsdelsmästerskap —
        /// samma villkor som <c>StandardMedalCalculationService.ShouldSplitGroupC</c>.
        /// Vid krets- och klubbmästerskap är C en enda kategori.
        /// </summary>
        public static bool SplitsGroupC(string? competitionScope)
        {
            var scope = Normalize(competitionScope);
            return scope == CompetitionScopeHelper.SvensktMasterskap
                || scope == CompetitionScopeHelper.Landsdelsmasterskap;
        }

        /// <summary>Är omfattningen ett mästerskap alls?</summary>
        public static bool IsChampionship(string? competitionScope) =>
            CompetitionScopeHelper.IsChampionshipScope(Normalize(competitionScope));

        /// <summary>
        /// Kategorinamnet för en skytteklass. Tar både Id-formen ("C_Vet_Y") och
        /// Name-formen ("C Vet Y") — se [[shooting-class-id-vs-name-canonical]].
        /// En okänd klass returneras oförändrad i stället för att tyst hamna i fel grupp.
        /// </summary>
        public static string For(string? shootingClassIdOrName, bool splitGroupC)
        {
            if (string.IsNullOrWhiteSpace(shootingClassIdOrName)) return "";

            var cls = ShootingClasses.GetById(shootingClassIdOrName)
                   ?? ShootingClasses.GetByName(shootingClassIdOrName);
            if (cls == null) return shootingClassIdOrName.Trim();

            var weapon = WeaponLabel(cls.Weapon);

            // Bara vapengrupp C har underkategorier, och bara när omfattningen delar dem.
            // Att vidga det till L vore en ny regel, inte den befintliga — L Dam/Vet/Jun
            // poolas därför precis som medaljberäkningen gör i dag.
            if (splitGroupC && cls.Weapon == WeaponClass.C)
            {
                var sub = CSubCategory(cls.Id);
                if (sub != null) return $"C {sub}";
            }

            return weapon;
        }

        /// <summary>
        /// Uppslagstabell klass → kategori, i den form
        /// <c>PrecisionFinalsQualificationService.BuildFullClassRankings</c> väntar sig:
        /// nycklarna är klassernas VISNINGSNAMN.
        /// </summary>
        public static Dictionary<string, string> BuildLookup(
            IEnumerable<string> shootingClasses, bool splitGroupC)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in shootingClasses ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var key = ShootingClasses.ToCanonicalName(raw);
                if (key.Length == 0) continue;
                lookup[key] = For(raw, splitGroupC);
            }
            return lookup;
        }

        /// <summary>"Dam" / "Vet Y" / "Vet Ä" / "Jun", eller null för öppen klass.</summary>
        private static string? CSubCategory(string classId) => classId switch
        {
            "C1_Dam" or "C2_Dam" or "C3_Dam" => "Dam",
            "C_Vet_Y" => "Vet Y",
            "C_Vet_A" => "Vet Ä",
            "C_Jun" => "Jun",
            _ => null
        };

        private static string WeaponLabel(WeaponClass weapon) => weapon switch
        {
            WeaponClass.A => "A",
            WeaponClass.A_Opt => "A Opt",
            WeaponClass.A_M => "AM",
            WeaponClass.A_P => "AP",
            WeaponClass.A_G => "AG",
            WeaponClass.B => "B",
            WeaponClass.C => "C",
            WeaponClass.R => "R",
            WeaponClass.M => "M",
            WeaponClass.L => "L",
            _ => weapon.ToString()
        };

        /// <summary>
        /// competitionScope lagras normalt som en ren sträng, men en FlexibleDropdown kan ha
        /// lagt den som en JSON-array. Skala av i så fall — jämförelserna är Ordinal, så
        /// <c>["Landsdelsmästerskap"]</c> hade annars inte räknats som mästerskap alls.
        /// Samma försiktighet som CompetitionUrlProvider.ReadScopeValue.
        /// </summary>
        private static string Normalize(string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) return "";
            var s = scope.Trim();
            if (s.StartsWith("[") && s.EndsWith("]"))
                s = s.Trim('[', ']').Trim().Trim('"');
            return s;
        }
    }
}
