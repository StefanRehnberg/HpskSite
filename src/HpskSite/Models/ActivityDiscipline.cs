namespace HpskSite.Models
{
    /// <summary>
    /// Grenen (disciplinen) som en aktivitetspost hör till, normaliserad till EN kanonisk form.
    ///
    /// <para><b>Varför den behövs.</b> Ett föreningsintyg gäller ett vapen för en bestämd
    /// verksamhet, så utfärdaren måste kunna se medlemmens aktivitet <i>i just den grenen</i> — söker
    /// någon licens för ett fältvapen är det fältaktiviteten som ska beläggas. Före det här fanns
    /// grenen bara som fritext inuti postens rubrik och gick inte att filtrera på.</para>
    ///
    /// <para><b>Den kanoniska mängden är <see cref="CompetitionTypes.All"/></b> — ingen ny lista.
    /// Kodbasen bär redan tre olika disciplinlistor som är oense (<c>MemberDataPresenceService</c>
    /// har Faltskytte, <c>RankingSnapshotService</c> saknar den, <c>MarkenFamilies</c> stavar den
    /// <c>Falt</c>), och en fjärde hade bara gjort saken värre.</para>
    ///
    /// <para><b>Mätt i prod 2026-09-02</b> (så normaliseringen är byggd på vad som FINNS, inte på vad
    /// konstanterna påstår):</para>
    /// <list type="bullet">
    /// <item><c>TrainingScores.Discipline</c>: Precision 1273 · Milsnabb 84 · Faltskytte 71 ·
    /// Duell 64 · MagnumPrecision 16 · NationellHelmatch 4 — alla redan kanoniska id:n.</item>
    /// <item><c>competition.competitionType</c>: Precision 228 · Faltskytte 98 · Milsnabb 34 ·
    /// Springskytte 27 · Duell 13 · NationellHelmatch 11 · <b>"Magnum Fält" 2</b> · Sportpistol 2.</item>
    /// </list>
    ///
    /// <para><b>⚠️ "Magnum Fält" är visningsNAMNET, inte id:t <c>MagnumFalt</c></b> — med mellanslag
    /// och ä. Exakt den drift CLAUDE.md varnar för, och den finns i skarp data. Två tävlingar faller
    /// utanför varje filter som jämför literalt. Därför normaliseras diakriter och blanksteg bort
    /// innan jämförelsen.</para>
    /// </summary>
    public static class ActivityDiscipline
    {
        /// <summary>De kanoniska id:na, i katalogens ordning.</summary>
        public static IReadOnlyList<string> All => CompetitionTypes.All.Select(t => t.Id).ToList();

        /// <summary>
        /// Normaliserar ett råvärde till ett kanoniskt disciplin-id, eller <c>""</c> när det inte går
        /// att avgöra.
        ///
        /// <para><b>⚠️ Använder INTE <see cref="CompetitionTypes.GetFuzzy"/>, med flit.</b> GetFuzzy
        /// faller tillbaka på PREFIXmatchning, så <c>"Magnum"</c> resolvar där till
        /// <c>MagnumPrecision</c> — den kommer först i katalogen — och en trunkerad eller halvskriven
        /// gren skulle alltså tyst attribueras till en annan gren. På ett filter som ska belägga
        /// aktivitet inför en licensansökan är tyst felattribution sämre än inget svar. Här matchas
        /// bara EXAKT, på id eller namn, med diakriter och blanksteg normaliserade.</para>
        /// </summary>
        public static string Canonical(string? raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) return "";

            // 1. Exakt id — den överväldigande majoriteten, och den billigaste jämförelsen.
            var byId = CompetitionTypes.All
                .FirstOrDefault(t => t.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId.Id;

            // 2. Exakt visningsnamn.
            var byName = CompetitionTypes.All
                .FirstOrDefault(t => t.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName.Id;

            // 3. Normaliserad exakt jämförelse — det som fångar "Magnum Fält" → "MagnumFalt".
            var norm = Normalize(value);
            var byNorm = CompetitionTypes.All
                .FirstOrDefault(t => Normalize(t.Id) == norm || Normalize(t.Name) == norm);
            return byNorm?.Id ?? "";
        }

        /// <summary>Visningsnamnet för ett kanoniskt id. Okänt id renderas som sig självt, aldrig tomt.</summary>
        public static string Label(string? canonicalId)
        {
            var value = (canonicalId ?? "").Trim();
            if (value.Length == 0) return "";
            return CompetitionTypes.GetById(value)?.Name ?? value;
        }

        /// <summary>
        /// Sorteringsnyckel för en disciplin — katalogens ordning, inte alfabetisk.
        /// Precision är den ojämförligt största grenen och ska stå först i en chip-rad; en
        /// bokstavsordning hade lagt Duell där.
        /// </summary>
        public static int SortKey(string? canonicalId)
        {
            var idx = CompetitionTypes.All.FindIndex(t =>
                t.Id.Equals((canonicalId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            return idx < 0 ? int.MaxValue : idx;
        }

        /// <summary>
        /// Samma normalisering som <c>CompetitionTypes</c> använder: blanksteg bort, svenska tecken
        /// veckade, gemener. Medvetet en KOPIA av regeln och inte ett anrop — <c>Normalize</c> där är
        /// privat, och att göra den publik skulle inbjuda till att prefixmatchningen används igen.
        /// </summary>
        private static string Normalize(string s) =>
            s.Replace(" ", "")
             .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
             .Replace("Å", "A").Replace("Ä", "A").Replace("Ö", "O")
             .Replace("é", "e").Replace("É", "E")
             .ToLowerInvariant();
    }
}
