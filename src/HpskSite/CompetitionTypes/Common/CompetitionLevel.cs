using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using HpskSite.CompetitionTypes.Common.Utilities;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Tävlingens KATEGORI enligt SHB C.3.1 — vad tävlingen är GODKÄND SOM, alltså vem som har
    /// gett medgivande: ingen (föreningstävling), kretsstyrelsen (kretstävling) eller Förbundet
    /// (landsdels-, nationell och rikstävling).
    ///
    /// <para>⚠️⚠️ KATEGORIN ÄR INTE "VEM FÅR ANMÄLA SIG". SHB:s definitioner säger vilka som FÅR
    /// delta, en miniminivå, aldrig vilka som utesluts — en öppen tävling som klubben anordnar
    /// utan kretsens medgivande är en FÖRENINGSTÄVLING (C.3.1.5: "för föreningens egna medlemmar
    /// eller, förutom för dessa, även för skyttar tillhörande annan förening"). Första versionen
    /// (2026-10-01) blandade ihop de två och påstod att en öppen klubbtävling kräver kretsens
    /// tillstånd; det är fel. Vilka som får anmäla sig är bara <c>isClubOnly</c> (Stefans beslut
    /// 2026-10-01: alla får anmäla sig, arrangören agerar själv om någon inte får vara med).</para>
    ///
    /// <para>⚠️ STANDARDMEDALJ BARA VID KRETS OCH HÖGRE. SHB C.5.1.1 tillåter standardmedalj vid
    /// en föreningstävling per gren och år, men sådana medaljer är inte delkvalificerande —
    /// pistol.nu tar inte med dem alls (Stefans beslut 2026-10-01).</para>
    ///
    /// <para>⚠️ ENDA STÄLLET värdena, etiketterna, förklaringarna och reglerna bor.
    /// <c>_CompetitionLevelField.cshtml</c> serialiserar <see cref="All"/> och
    /// <see cref="ScopeSuggestions"/> till klienten i stället för att skriva av dem.</para>
    ///
    /// <para>⚠️ TOMT VÄRDE = EJ BEKRÄFTAT. Ingen backfill: kategorin FÖRESLÅS vid läsning
    /// (<see cref="Suggest"/>) och blir bekräftad när någon sparar tävlingen. Allt som har en
    /// officiell följd läser bara en bekräftad kategori.</para>
    ///
    /// <para>Lagras som Textstring, medvetet INTE som FlexibleDropdown: den typen kastar på
    /// ett rent strängvärde (se <see cref="CompetitionScopeHelper.ReadScope"/>).</para>
    /// </summary>
    public static class CompetitionLevel
    {
        public const string PropertyAlias = "competitionLevel";

        public const string Forening = "Forening";
        public const string Krets = "Krets";
        public const string Landsdel = "Landsdel";
        public const string Nationell = "Nationell";
        public const string Riks = "Riks";

        /// <summary>
        /// Värde från första versionen ("öppen klubbtävling, kräver kretsens tillstånd") som kan
        /// ligga sparat i prod. ⚠️ Läses som EJ BEKRÄFTAT, inte som föreningstävling: valet
        /// gjordes mot felaktiga texter och säger inget säkert om kategorin. Som föreningstävling
        /// hade det dessutom stängt av standardmedaljerna på en tävling som delar ut dem.
        /// Förslaget räknas om i stället.
        /// </summary>
        private const string LegacyForeningOppen = "ForeningOppen";

        /// <summary>
        /// Label = valet. ShortLabel = brickor och listor. Explanation = visas under valet.
        /// RequiresClub = kategorin förutsätter att en klubb är arrangör.
        /// </summary>
        public sealed record Option(string Value, string Label, string ShortLabel, string Explanation, bool RequiresClub);

        public static readonly IReadOnlyList<Option> All = new[]
        {
            new Option(Forening, "Föreningstävling", "Föreningstävling",
                "Klubbens egen tävling, utan kretsens eller Förbundets godkännande. Kräver ingen ansökan och inget pistolskyttekort. Ger inga standardmedaljer och räknas inte för märkesprov eller riksmästarklass.",
                RequiresClub: true),
            new Option(Krets, "Kretstävling", "Kretstävling",
                "Godkänd av kretsstyrelsen efter ansökan till kretsen. Kan ge standardmedaljer och räknas för märkesprov och riksmästarklass.",
                RequiresClub: false),
            new Option(Landsdel, "Landsdelstävling", "Landsdelstävling",
                "Godkänd av Förbundet efter ansökan via kretsen. Kan ge standardmedaljer och räknas för märkesprov och riksmästarklass.",
                RequiresClub: false),
            new Option(Nationell, "Nationell tävling", "Nationell",
                "Godkänd av Förbundet efter ansökan via kretsen och publicerad i Förbundets tävlingsprogram. Kan ge standardmedaljer och räknas för märkesprov och riksmästarklass.",
                RequiresClub: false),
            new Option(Riks, "Rikstävling", "Rikstävling",
                "Anordnas av Förbundet eller på Förbundets uppdrag, t.ex. ett SM.",
                RequiresClub: false),
        };

        /// <summary>
        /// Mästerskapet föreslår en kategori: klubbmästerskap → föreningstävling,
        /// kretsmästerskap → kretstävling, landsdelsmästerskap → landsdelstävling, SM → rikstävling.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> ScopeSuggestions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CompetitionScopeHelper.Klubbmasterskap] = Forening,
                [CompetitionScopeHelper.Kretsmasterskap] = Krets,
                [CompetitionScopeHelper.Landsdelsmasterskap] = Landsdel,
                [CompetitionScopeHelper.SvensktMasterskap] = Riks,
            };

        /// <summary>Det kanoniska värdet, eller tom sträng för tomt/okänt.</summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value.Trim();
            if (string.Equals(v, LegacyForeningOppen, StringComparison.OrdinalIgnoreCase)) return "";
            return All.FirstOrDefault(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        }

        /// <summary>Tomt (ej bekräftat) eller ett känt värde.</summary>
        public static bool IsValid(string? value)
            => string.IsNullOrWhiteSpace(value) || Normalize(value) != "";

        public static Option? Find(string? value)
        {
            var v = Normalize(value);
            return v == "" ? null : All.First(o => o.Value == v);
        }

        public static string ShortLabel(string? value) => Find(value)?.ShortLabel ?? "";

        /// <summary>
        /// Förslaget för en tävling som saknar bekräftad kategori. Ordningen är bärande:
        /// klubbintern vinner alltid (den ÄR ett faktum), sedan mästerskapet, sedan värden.
        ///
        /// ⚠️ En tävling som REDAN är standardmedaljsgrundande föreslås aldrig som
        /// föreningstävling — det förslaget hade stängt av medaljerna för en tävling som har
        /// delat ut dem, bara för att någon öppnade den. Medaljerna antyder att den är godkänd
        /// som kretstävling eller högre.
        /// </summary>
        public static string Suggest(string? scope, bool isClubOnly, bool hasClub, bool hasRegion, bool awardsStandardMedals = false)
        {
            if (isClubOnly && hasClub) return Forening;

            string result;
            var s = CompetitionScopeHelper.NormalizeScopeValue(scope);
            if (ScopeSuggestions.TryGetValue(s, out var fromScope))
                result = (fromScope == Forening && !hasClub) ? Krets : fromScope;
            else if (hasClub) result = Forening;
            else if (hasRegion) result = Krets;
            else result = Nationell;

            return result == Forening && awardsStandardMedals ? Krets : result;
        }

        /// <summary>
        /// Krets eller högre (krets, landsdel, nationell, riks). Läser bara en BEKRÄFTAD kategori;
        /// tomt svarar false.
        /// </summary>
        public static bool IsKretsOrAbove(string? level)
            => Normalize(level) is Krets or Landsdel or Nationell or Riks;

        /// <summary>Kategorin tillåter standardmedaljer. Tom (ej bekräftad) = som förut, ja.</summary>
        public static bool AllowsStandardMedals(string? level)
        {
            var v = Normalize(level);
            return v == "" || IsKretsOrAbove(v);
        }

        /// <summary>
        /// Kategorin får inte säga emot "endast klubbens egna medlemmar", kräva en klubb som
        /// saknas eller vara föreningstävling med standardmedaljer. Null = i ordning.
        /// Bara en BEKRÄFTAD (icke-tom) kategori prövas.
        /// </summary>
        public static string? ConsistencyError(string? level, bool isClubOnly, bool hasClub, bool awardsStandardMedals = false)
        {
            var v = Normalize(level);
            if (v == "") return null;
            if (isClubOnly && v != Forening)
                return "En tävling som bara är öppen för klubbens egna medlemmar är en föreningstävling. Välj \"Föreningstävling\" eller ta bort begränsningen.";
            if (Find(v)!.RequiresClub && !hasClub)
                return $"En {Find(v)!.Label.ToLowerInvariant()} kräver att en klubb är arrangör.";
            if (awardsStandardMedals && !IsKretsOrAbove(v))
                return "Standardmedaljer delas bara ut vid kretstävling eller högre (SHB C.5.1.1). Välj en annan tävlingsnivå eller ta bort Standardmedaljsgrundande.";
            return null;
        }

        /// <summary>Den lagrade (bekräftade) kategorin, normaliserad. Tom om ej satt eller saknad egenskap.</summary>
        public static string Read(IPublishedContent? competition)
        {
            if (competition == null || !competition.HasProperty(PropertyAlias)) return "";
            try { return Normalize(competition.Value<string>(PropertyAlias)); }
            catch { return ""; }
        }

        /// <inheritdoc cref="Read(IPublishedContent?)"/>
        public static string Read(IContent? competition)
        {
            if (competition == null || !competition.HasProperty(PropertyAlias)) return "";
            try { return Normalize(competition.GetValue<string>(PropertyAlias)); }
            catch { return ""; }
        }
    }
}
