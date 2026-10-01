using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using HpskSite.CompetitionTypes.Common.Utilities;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Tävlingens NIVÅ — vilka som får delta (SHB C.3.1) — skild från MÄSTERSKAPET
    /// (<c>competitionScope</c>, om en mästartitel avgörs). Ett mästerskap kan ingå i en
    /// tävling på vilken nivå som helst, så de två är olika frågor och lagras var för sig.
    ///
    /// <para>⚠️ ENDA STÄLLET värdena, etiketterna, förklaringarna och förvalsregeln bor.
    /// Guiden, redigeringsmodalen, Springskyttemodalen, listan och servern läser härifrån —
    /// <c>_CompetitionLevelField.cshtml</c> serialiserar <see cref="All"/> och
    /// <see cref="ScopeSuggestions"/> till klienten i stället för att skriva av dem.</para>
    ///
    /// <para>⚠️ TOMT VÄRDE = EJ BEKRÄFTAT. Befintliga tävlingar får ingen backfill: nivån
    /// FÖRESLÅS vid läsning (<see cref="Suggest"/>) och blir bekräftad först när någon
    /// sparar tävlingen. Allt som har en officiell följd (märkena) läser därför bara en
    /// bekräftad nivå — en gissning får aldrig ge eller ta en skytts märke.</para>
    ///
    /// <para>Lagras som Textstring, medvetet INTE som FlexibleDropdown: den typen kastar på
    /// ett rent strängvärde (se <see cref="CompetitionScopeHelper.ReadScope"/>).</para>
    /// </summary>
    public static class CompetitionLevel
    {
        public const string PropertyAlias = "competitionLevel";

        /// <summary>Bara klubbens egna medlemmar. Alltid tillsammans med <c>isClubOnly</c>.</summary>
        public const string Forening = "Forening";
        /// <summary>Klubbens medlemmar och inbjudna föreningar — kräver tillstånd hos kretsen.</summary>
        public const string ForeningOppen = "ForeningOppen";
        public const string Krets = "Krets";
        public const string Landsdel = "Landsdel";
        public const string Nationell = "Nationell";
        public const string Riks = "Riks";

        /// <summary>
        /// Label = svaret på frågan "Vem får anmäla sig?" (det användaren väljer).
        /// ShortLabel = kort namn för brickor och listor. Explanation = visas under valet.
        /// RequiresClub = nivån förutsätter att en klubb är arrangör.
        /// </summary>
        public sealed record Option(string Value, string Label, string ShortLabel, string Explanation, bool RequiresClub);

        public static readonly IReadOnlyList<Option> All = new[]
        {
            new Option(Forening, "Bara klubbens egna medlemmar", "Endast klubb",
                "Klubbtävling. Syns bara för klubbens medlemmar och ger inga standardmedaljer.",
                RequiresClub: true),
            new Option(ForeningOppen, "Klubbens medlemmar och inbjudna föreningar", "Öppen klubbtävling",
                "Öppen föreningstävling. Vem som helst får anmäla sig, även skyttar utanför kretsen. Kräver tillstånd från kretsen.",
                RequiresClub: true),
            new Option(Krets, "Alla föreningar i kretsen", "Kretstävling",
                "Kretstävling. Kräver kretsstyrelsens medgivande.",
                RequiresClub: false),
            new Option(Landsdel, "Alla kretsar i landsdelen", "Landsdelstävling",
                "Landsdelstävling. Ansöks via kretsen och kräver Förbundets godkännande.",
                RequiresClub: false),
            new Option(Nationell, "Alla föreningar i Förbundet", "Nationell tävling",
                "Nationell tävling. Ansöks via kretsen och kräver Förbundets medgivande.",
                RequiresClub: false),
            new Option(Riks, "Rikstävling på Förbundets uppdrag", "Rikstävling",
                "Anordnas av eller på uppdrag av Förbundet, t.ex. ett SM.",
                RequiresClub: false),
        };

        /// <summary>
        /// Mästerskapet föreslår en nivå. Klubbmästerskap → bara egna medlemmar,
        /// kretsmästerskap → krets, landsdelsmästerskap → landsdel, SM → riks.
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
        /// Förslaget för en tävling som saknar bekräftad nivå. Ordningen är bärande:
        /// klubbintern vinner alltid (den ÄR ett faktum), sedan mästerskapet, sedan värden.
        /// </summary>
        public static string Suggest(string? scope, bool isClubOnly, bool hasClub, bool hasRegion)
        {
            if (isClubOnly && hasClub) return Forening;

            var s = CompetitionScopeHelper.NormalizeScopeValue(scope);
            if (ScopeSuggestions.TryGetValue(s, out var fromScope))
            {
                // Ett klubbmästerskap utan klubb kan inte vara klubbinternt.
                if (fromScope == Forening && !hasClub) return Krets;
                return fromScope;
            }

            if (hasClub) return ForeningOppen;
            if (hasRegion) return Krets;
            return Nationell;
        }

        /// <summary>
        /// Krets eller högre i SHB:s mening (krets, landsdel, nationell, riks). Läser bara en
        /// BEKRÄFTAD nivå; tomt svarar false.
        ///
        /// ⚠️ <see cref="ForeningOppen"/> räknas INTE, trots att en sådan tävling kan vara
        /// större än en kretstävling — om den ska räknas som "krets eller högre" för märkena är
        /// en öppen fråga i designen, och märkestilldelning är enkelriktad.
        /// </summary>
        public static bool IsKretsOrAbove(string? level)
            => Normalize(level) is Krets or Landsdel or Nationell or Riks;

        /// <summary>
        /// Nivån och <c>isClubOnly</c> beskriver samma fakta för den klubbinterna tävlingen och
        /// får inte säga emot varandra. Null = i ordning.
        /// </summary>
        public static string? ConsistencyError(string? level, bool isClubOnly, bool hasClub)
        {
            var v = Normalize(level);
            if (v == "") return null;
            if (isClubOnly && v != Forening)
                return "Tävlingen är markerad som endast för klubbens medlemmar men nivån är en annan. Välj \"Bara klubbens egna medlemmar\".";
            if (v == Forening && !isClubOnly)
                return "Nivån \"Bara klubbens egna medlemmar\" kräver att tävlingen är endast för klubben.";
            if (Find(v)!.RequiresClub && !hasClub)
                return $"Nivån \"{Find(v)!.Label}\" kräver att en klubb är arrangör.";
            return null;
        }

        /// <summary>Den lagrade (bekräftade) nivån, normaliserad. Tom om ej satt eller saknad egenskap.</summary>
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
