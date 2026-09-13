using System.Text.RegularExpressions;
using HpskSite.CompetitionTypes.Springskytte.Models;

namespace HpskSite.CompetitionTypes.Springskytte.Services
{
    /// <summary>
    /// Klassammanslagning för SPRINGSKYTTE — en EGEN regelvärld, inte en gren i
    /// <see cref="HpskSite.Services.ClassMergingService"/>.
    ///
    /// ⚠️ DE TVÅ REGELVÄRLDARNA ANVÄNDER SAMMA ORD OM OLIKA SAKER, och att blanda ihop dem har
    /// redan kostat en utredning: precisionsfamiljen och fältskyttet slår samman
    /// KOMPETENSklasser (1/2/3) och har en absolut spärr för klass 1, medan springskyttet inte
    /// har några kompetensklasser alls (L.2.5–L.2.7: "Ej tillämpligt") och i stället slår
    /// samman ÅLDERSklasser. Skulle den ena motorn betjäna båda blir varje framtida rättelse
    /// i den ena ett tyst beteendebyte i den andra.
    ///
    /// Regeln, ordagrant ur SHB 2026 L.2.3.1 (Individuell tävling):
    ///   "Om deltagarantalet i någon klass understiger fem (5) äger tävlingsledningen rätt att
    ///    sammanslå åldersklasser med samma förutsättningar. Detta gäller ej D jun/H jun,
    ///    D 15/H 15 och D 18/H 18."
    ///
    /// Tre saker följer direkt ur den meningen och styr hela den här filen:
    ///
    /// 1. **"äger tävlingsledningen RÄTT att"** — beslutet är arrangörens, inte systemets.
    ///    Tjänsten föreslår därför alltid och tillämpar aldrig något av sig själv, och varje
    ///    förslag är ett admin-val (<see cref="SpringskytteMergeSuggestion.RequiresAdminChoice"/>
    ///    är alltid true) i stället för ett förvalt mål som ser ut som en rekommendation.
    ///
    /// 2. **"med samma förutsättningar"** — den enda förutsättning som skiljer springskyttets
    ///    åldersklasser åt i SHB är stödhanden: L.2.12.1 ger D 65/H 65 och D 70/H 70 rätt att
    ///    "använda stödhand på samtliga skjutstationer". Allt annat (sju varv, fem skott per
    ///    station, samma bana) är lika för alla. En 65+-klass får därför bara slås samman med
    ///    en annan 65+-klass, och en yngre bara med en yngre.
    ///
    /// 3. **Undantaget** är hårt och gäller åt BÅDA håll: de sex ungdoms- och juniorklasserna
    ///    är varken källa eller mål. ⚠️ Meningen går att läsa på två sätt — antingen att de
    ///    klasserna aldrig får slås samman alls, eller att D och H av just de åldrarna inte får
    ///    slås samman MED VARANDRA. Den här koden följer den STRÄNGARE läsningen, eftersom den
    ///    är säker under båda: den kan aldrig producera en sammanslagning som är förbjuden. Ett
    ///    förslag som visar sig otillåtet ändrar placeringar och medaljer i en publicerad lista;
    ///    ett uteblivet förslag kostar arrangören ett samtal till förbundet.
    /// </summary>
    public class SpringskytteClassMergingService
    {
        public const int MergeThreshold = 5;

        /// <summary>Ålder från och med vilken stödhand är tillåten på alla stationer (L.2.12.1).</summary>
        private const int SupportHandFromAge = 65;

        /// <summary>
        /// Ålderskoder som aldrig ingår i en sammanslagning (L.2.3.1:s undantag). Lagras som
        /// KODER ("jun", "15", "18") och inte som fulla klassnamn, så både "D jun" och "H jun"
        /// fångas av samma rad — och en tävling som stavar klassen med vapenprefix ("A-H 18")
        /// fångas av samma normalisering som allt annat här.
        /// </summary>
        private static readonly HashSet<string> ProtectedAgeCodes =
            new(StringComparer.OrdinalIgnoreCase) { "jun", "15", "18" };

        /// <summary>
        /// Analyserar en tävlings klasser. Nyckeln i <paramref name="counts"/> är
        /// (vapengrupp, åldersklass) — springskyttet publicerar per vapengrupp och en
        /// sammanslagning får aldrig korsa den gränsen.
        /// </summary>
        public SpringskytteMergeAnalysis Analyze(IEnumerable<SpringskytteClassCount> counts)
        {
            var analysis = new SpringskytteMergeAnalysis();
            var list = counts?.ToList() ?? new List<SpringskytteClassCount>();

            foreach (var c in list.OrderBy(c => c.WeaponClass, StringComparer.CurrentCulture)
                                  .ThenBy(c => SortOrder(c.AgeGenderClass)))
            {
                analysis.Classes.Add(new SpringskytteClassInfo
                {
                    WeaponClass = c.WeaponClass ?? "",
                    AgeGenderClass = c.AgeGenderClass ?? "",
                    Key = MakeKey(c.WeaponClass, c.AgeGenderClass),
                    ParticipantCount = c.ParticipantCount,
                    BelowThreshold = c.ParticipantCount < MergeThreshold
                });
            }

            foreach (var cls in analysis.Classes.Where(c => c.BelowThreshold))
            {
                var targets = CandidateTargets(cls, analysis.Classes);
                if (targets.Count > 0)
                {
                    analysis.Suggestions.Add(new SpringskytteMergeSuggestion
                    {
                        SourceKey = cls.Key,
                        SourceClass = cls.AgeGenderClass,
                        WeaponClass = cls.WeaponClass,
                        SourceCount = cls.ParticipantCount,
                        PossibleTargets = targets,
                        // Alltid arrangörens val: SHB ger rätten till tävlingsledningen, och ett
                        // förvalt mål hade läst som förbundets rekommendation.
                        RequiresAdminChoice = true,
                        Reason = SupportHandAllowed(cls.AgeGenderClass)
                            ? "Åldersklass med stödhandsrätt (65 år och äldre) — får slås samman med en annan klass som har samma förutsättningar (SHB L.2.3.1)."
                            : "Åldersklass under 65 år — får slås samman med en annan klass som har samma förutsättningar (SHB L.2.3.1)."
                    });
                }
                else
                {
                    cls.MergeBlockReason = BlockReason(cls, analysis.Classes);
                }
            }

            return analysis;
        }

        /// <summary>
        /// Målklasser som regeln tillåter OCH som finns i tävlingen: samma vapengrupp, samma
        /// stödhandsförutsättning, ingen skyddad ungdoms-/juniorklass, inte sig själv.
        /// </summary>
        private static List<string> CandidateTargets(SpringskytteClassInfo cls, List<SpringskytteClassInfo> all)
        {
            if (IsProtected(cls.AgeGenderClass) || AgeOf(cls.AgeGenderClass) == null)
                return new List<string>();

            return all
                .Where(o => o.Key != cls.Key)
                .Where(o => string.Equals(o.WeaponClass, cls.WeaponClass, StringComparison.OrdinalIgnoreCase))
                .Where(o => !IsProtected(o.AgeGenderClass) && AgeOf(o.AgeGenderClass) != null)
                .Where(o => SupportHandAllowed(o.AgeGenderClass) == SupportHandAllowed(cls.AgeGenderClass))
                .OrderBy(o => SortOrder(o.AgeGenderClass))
                .Select(o => o.Key)
                .ToList();
        }

        /// <summary>
        /// Varför klassen står kvar trots färre än fem. Formuleras för arrangören: den nämner
        /// regeln och — när det är den verkliga orsaken — vilken förutsättning som skiljer.
        /// </summary>
        private static string BlockReason(SpringskytteClassInfo cls, List<SpringskytteClassInfo> all)
        {
            if (IsProtected(cls.AgeGenderClass))
                return "Ungdoms- och juniorklasserna (D/H jun, D/H 15, D/H 18) slås aldrig samman (SHB L.2.3.1).";

            if (AgeOf(cls.AgeGenderClass) == null)
                return "Klassen känns inte igen som en åldersklass, så ingen sammanslagning kan föreslås.";

            var sameGroupExists = all.Any(o => o.Key != cls.Key
                && string.Equals(o.WeaponClass, cls.WeaponClass, StringComparison.OrdinalIgnoreCase)
                && !IsProtected(o.AgeGenderClass) && AgeOf(o.AgeGenderClass) != null);

            if (!sameGroupExists)
                return $"Det finns ingen annan åldersklass i vapengrupp {cls.WeaponClass} att slå samman med i den här tävlingen.";

            // Det finns andra åldersklasser — men på fel sida av stödhandsgränsen.
            return SupportHandAllowed(cls.AgeGenderClass)
                ? $"Övriga åldersklasser i vapengrupp {cls.WeaponClass} saknar stödhandsrätt, så de har inte samma förutsättningar (SHB L.2.12.1)."
                : $"Övriga åldersklasser i vapengrupp {cls.WeaponClass} har stödhandsrätt (65 år och äldre), så de har inte samma förutsättningar (SHB L.2.12.1).";
        }

        // ── Namn och nycklar ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gruppnyckeln är "vapengrupp|åldersklass" — exakt formen bägge resultatvägarna redan
        /// grupperar på, så sammanslagningen kan appliceras som ett uppslag i stället för som en
        /// ny gruppering fri att glida från den riktiga.
        /// </summary>
        public static string MakeKey(string? weaponClass, string? ageGenderClass)
            => $"{(weaponClass ?? "").Trim()}|{(ageGenderClass ?? "").Trim()}";

        /// <summary>
        /// Bygger uppslaget källnyckel/målnyckel → gemensamt gruppnamn. Union-find, så flera
        /// klasser som pekar mot samma mål (eller en kedja) hamnar i EN grupp — samma lärdom som
        /// precisionsfamiljens <c>BuildMergeGroupLookup</c>, där parvis hantering gav en grupp
        /// per merge och sista skrivningen vann på målklassen.
        /// </summary>
        public static Dictionary<string, string> BuildMergeGroupLookup(IEnumerable<SpringskytteClassMergeAction>? merges)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = merges?.Where(m => !string.IsNullOrWhiteSpace(m.SourceKey)
                                       && !string.IsNullOrWhiteSpace(m.TargetKey)).ToList()
                       ?? new List<SpringskytteClassMergeAction>();
            if (list.Count == 0) return lookup;

            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Find(string c)
            {
                if (!parent.ContainsKey(c)) parent[c] = c;
                while (parent[c] != c)
                {
                    parent[c] = parent[parent[c]];
                    c = parent[c];
                }
                return c;
            }

            foreach (var m in list) { Find(m.SourceKey); Find(m.TargetKey); }
            foreach (var m in list)
            {
                var s = Find(m.SourceKey);
                var t = Find(m.TargetKey);
                if (s != t) parent[s] = t;   // målet vinner
            }

            foreach (var group in parent.Keys.GroupBy(Find))
            {
                var members = group.ToList();
                if (members.Count <= 1) continue;
                var name = CombinedName(members);
                foreach (var m in members) lookup[m] = name;
            }
            return lookup;
        }

        /// <summary>
        /// Namnet på en sammanslagen grupp: vapengruppen en gång, sedan åldersklasserna i
        /// åldersordning — "A|H 50" + "A|H 60" → "A|H 50+H 60". Nyckelformen behålls, eftersom
        /// resultatvägarna bygger sina rubriker ur den.
        ///
        /// ⚠️ Vapengruppen tas från den FÖRSTA medlemmen och inte per medlem: en sammanslagning
        /// får aldrig korsa vapengrupp (springskyttet publicerar per vapengrupp), och skulle en
        /// sådan konfiguration ändå ligga lagrad ska namnet inte dölja det.
        /// </summary>
        private static string CombinedName(List<string> keys)
        {
            var weapon = keys[0].Split('|')[0];
            var ages = keys
                .Select(k => k.Split('|').Length > 1 ? k.Split('|')[1] : k)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(SortOrder)
                .ToList();
            return $"{weapon}|{string.Join("+", ages)}";
        }

        // ── Klassavkodning ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Åldersklassens kod: "H 50" → "50", "D jun" → "jun", "A-H 65" → "65". Null när
        /// strängen inte är en åldersklass vi känner igen (dev-data bär bl.a. tomma värden och
        /// "vuxen-tvahand"), och en okänd klass får ALDRIG ett förslag — vi kan inte påstå att
        /// den har samma förutsättningar som någon annan.
        /// </summary>
        public static string? AgeCodeOf(string? ageGenderClass)
        {
            if (string.IsNullOrWhiteSpace(ageGenderClass)) return null;
            var s = ageGenderClass.Trim();
            if (Regex.IsMatch(s, @"\bjun\b", RegexOptions.IgnoreCase)) return "jun";
            var m = Regex.Match(s, @"(\d{2})\s*$");
            if (!m.Success) return null;
            var code = m.Groups[1].Value;
            return code is "15" or "18" or "21" or "35" or "50" or "60" or "65" or "70" ? code : null;
        }

        /// <summary>Numerisk ålder för stödhandsjämförelsen; juniorklasserna har ingen (de är skyddade).</summary>
        public static int? AgeOf(string? ageGenderClass)
        {
            var code = AgeCodeOf(ageGenderClass);
            if (code == null) return null;
            if (code == "jun") return 15;
            return int.Parse(code);
        }

        public static bool IsProtected(string? ageGenderClass)
        {
            var code = AgeCodeOf(ageGenderClass);
            return code != null && ProtectedAgeCodes.Contains(code);
        }

        /// <summary>
        /// Stödhandsrätt enligt L.2.12.1 — den enda "förutsättning" som skiljer springskyttets
        /// åldersklasser åt, och därmed den enda gräns en sammanslagning inte får korsa.
        /// </summary>
        public static bool SupportHandAllowed(string? ageGenderClass)
        {
            var age = AgeOf(ageGenderClass);
            return age != null && age >= SupportHandFromAge;
        }

        private static int SortOrder(string? ageGenderClass)
        {
            var age = AgeOf(ageGenderClass) ?? 999;
            // Dam före herr inom samma ålder, så listan läser som klasstabellen i SHB.
            var genderBump = (ageGenderClass ?? "").TrimStart().StartsWith("D", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            return age * 10 + genderBump;
        }
    }
}
