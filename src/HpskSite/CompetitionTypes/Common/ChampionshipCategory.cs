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
    /// Vapengruppsindelningen är densamma som standardmedaljerna använder — medaljerna
    /// räknade redan per kategori medan finalerna räknade per underklass, och den skillnaden
    /// var buggen. Men ⚠️ NIVÅVILLKORET är INTE detsamma: se <see cref="SplitsGroupC"/>.
    /// Standardmedaljen delas per C-mästerskap bara vid SM/landsdel (C.5.1.1.4), medan
    /// mästerskapsklasserna finns på alla mästerskapsnivåer (C.3.6.4, C.3.4.1).
    /// </summary>
    public static class ChampionshipCategory
    {
        /// <summary>
        /// Delas vapengrupp C i sina fem mästerskapsklasser? **Ja vid ALLA mästerskap.**
        ///
        /// ⚠️⚠️ FÖRVÄXLA INTE MED <c>StandardMedalCalculationService.ShouldSplitGroupC</c>,
        /// som är sann bara för SM och Landsdelsmästerskap. Det är TVÅ OLIKA REGLER i SHB
        /// 2026, och de gäller olika saker:
        ///
        /// • <b>Standardmedaljberäkningen</b> delas per C-mästerskap bara vid SM och
        ///   landsdelsmästerskap. Ordagrant, C.5.1.1.4 (precision): *"Samtliga deltagare
        ///   sammanförs oavsett klasstillhörighet vapengruppsvis (A, A optisk, B och C)
        ///   UTOM vid SM och landsdelsmästerskap där separat standardmedaljberäkning skall
        ///   ske för varje mästerskap i vapengrupp C."* Samma i C.5.1.1.1 och C.5.1.1.2.
        ///
        /// • <b>Mästerskapsklasserna</b> — vem som tävlar om vilken medalj, och därmed vilka
        ///   finaler och särskjutningar som finns — har INGEN sådan begränsning:
        ///   – <b>C.3.6.4</b>: ett kretsmästerskap *"utförs enligt bestämmelserna för tävling
        ///     om svenskt mästerskap"*; undantagen rör bara femdeltagarkravet och vilka som
        ///     får delta.
        ///   – <b>C.3.4.1</b>: medaljreduceringen *"gäller vid landsdels-, krets- och
        ///     klubbmästerskap då respektive styrelse bestämt att deltagarantalet FÖR EGEN
        ///     KLASS får vara färre än 5"* — regeln förutsätter alltså att de egna klasserna
        ///     finns på alla tre nivåerna. Få deltagare hanteras med MEDALJREDUCERING, inte
        ///     med att klassen upphör.
        ///   – <b>C.3.7.1.1</b>: rekord noteras vid SM, landsdels- och kretsmästerskap, och
        ///     *"I vapengrupp C noteras rekord, förutom i öppen klass, även för damer, yngre
        ///     veteraner (VY), äldre veteraner (VÄ) och juniorer."*
        ///   – <b>F.2</b>: *"Skytt i junior-, dam- och veteranklass har möjlighet att vid ALLA
        ///     TÄVLINGAR: tävla inom den egna klassen."*
        ///
        /// Rättat 2026-09-07 efter Stefans invändning: den här metoden speglade först
        /// standardmedaljregeln, vilket poolade Dam/Vet/Junior in i öppen C på krets- och
        /// klubbmästerskap. Det gav fel finalklasser och kunde begära särskjutning mellan en
        /// dam och en öppen-klass-skytt.
        /// </summary>
        public static bool SplitsGroupC(string? competitionScope) => IsChampionship(competitionScope);

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
        /// **Vapengruppen** en skytteklass hör till — "C", "A", "A Opt", "B", "R" …
        ///
        /// ⚠️ Detta är en ANNAN axel än <see cref="For"/>. Vapengruppen är en
        /// SKJUTSESSION: alla C-klasser — öppen, Dam, Vet Y, Vet Ä och Junior — skjuter sin
        /// final tillsammans, på ett datum och en starttid, och hör därför i SAMMA
        /// finalstartlista. Kategorin avgör vem som tävlar om vilken MEDALJ inuti den listan.
        /// På SSM 2026 skjuts C med final på lördagen, A med final på söndag förmiddag och B
        /// på söndag eftermiddag — tre listor, sju kategorier.
        ///
        /// Implementerad som <c>For(cls, splitGroupC: false)</c> just för att de två axlarna
        /// inte ska kunna glida isär: vidgas kategorierna någon gång till vapengrupp L följer
        /// gruppetiketten med automatiskt.
        /// </summary>
        public static string WeaponGroupFor(string? shootingClassIdOrName) =>
            For(shootingClassIdOrName, splitGroupC: false);

        /// <summary>
        /// Vapengruppen för en MÄNGD klasser — en resultatlistegrupp, t.ex. de skyttar som
        /// står under rubriken "C2+Dam".
        ///
        /// ⚠️ Läser SKYTTARNAS klasser, aldrig gruppens rubrik. En grupp kan vara
        /// sammanslagen ("C2+Dam") eller omdöpt av en admin ("C2 Allmänt"), och rubriken
        /// resolvar då till ingenting. Samma regel som resultatlistans <c>crFlattenByWeaponGroup</c>.
        ///
        /// ⚠️ Spänner mängden över flera vapengrupper returneras <c>""</c> — en gissning
        /// skulle lägga skyttar i en final de inte skjuter. Det ska inte kunna hända (en
        /// resultatlistegrupp korsar aldrig vapengruppen), men om det gör det ska det sägas.
        /// </summary>
        public static string WeaponGroupForClasses(IEnumerable<string?>? shootingClasses)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in shootingClasses ?? Enumerable.Empty<string?>())
            {
                var g = WeaponGroupFor(raw);
                if (g.Length > 0) groups.Add(g);
            }
            return groups.Count == 1 ? groups.First() : "";
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
