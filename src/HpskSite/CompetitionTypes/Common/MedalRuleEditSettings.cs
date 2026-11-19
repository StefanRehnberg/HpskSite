namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Vad som får sparas som arrangörens medaljregler (<c>medalGroupingMode</c>, <c>medalPlaces</c>, <c>medalTieRule</c>) och när en ändring
    /// kräver en bekräftelse. Rena regler utan beroenden, så att sparvägen (<c>SetMedalGrouping</c>) och testerna prövar SAMMA kod.
    ///
    /// <para>Fälten skrivs bara genom <c>SetMedalGrouping</c>, aldrig genom fältpåsen (<see cref="Aliases"/> hoppas över där): den
    /// endpointen prövar behörigheten, spärren, bekräftelsen och räknar om resultatlistan i samma handling.</para>
    /// </summary>
    public static class MedalRuleEditSettings
    {
        /// <summary>
        /// Har grenen ytan för klubbens medaljregler? Springskytte och skidskytte har egna regler och får den inte; precisionsfamiljen och
        /// fältet har den. (Sajtinställningen Medals:ClubRules, mörk lansering, togs bort 2026-11-19: funktionen släpps direkt vid deploy,
        /// som allt annat.)
        /// </summary>
        public static bool SurfaceAppliesTo(string? competitionType)
        {
            var type = competitionType ?? "";
            return !type.Contains("spring", StringComparison.OrdinalIgnoreCase) && !type.Contains("skid", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Fältens alias. Fältpåsen (guiden, redigeringen, annonsdialogerna) skriver dem aldrig.</summary>
        public static readonly IReadOnlySet<string> Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "medalGroupingMode", "medalPlaces", "medalTieRule"
        };

        /// <summary>
        /// Prövar en ändring. Null = i ordning, annars meddelandet till arrangören. <paramref name="mode"/>, <paramref name="places"/> och
        /// <paramref name="tieRule"/> är null när de inte skickas (rörs inte); en tom sträng för läget/oavgjort-regeln betyder "som förbundet".
        /// </summary>
        /// <param name="scope">Tävlingens omfattning — nivåspärren: bara klubb- och kretsmästerskap får välja.</param>
        public static string? Refusal(string? scope, string? mode, int? places, string? tieRule)
        {
            if (mode == null && places == null && tieRule == null) return null;

            if (!string.IsNullOrWhiteSpace(mode) && MedalGroupingModes.Normalize(mode) == null)
                return $"Okänd medaljindelning: \"{mode}\".";
            if (places is < 0 or > 3)
                return "Antal medaljplatser måste vara 0–3 (0 = som förbundet).";
            if (!string.IsNullOrWhiteSpace(tieRule) && MedalTieRules.Normalize(tieRule) == null)
                return $"Okänd regel för lika resultat: \"{tieRule}\".";

            if (!ChampionshipRule.ForScope(scope).MayChooseMedalRules)
                return "Medaljreglerna går inte att välja vid landsdels- och svenskt mästerskap: där är de fem C-mästerskapen "
                     + "(öppen, Dam, Vet Y, Vet Ä, Junior) vad förbundet delar ut.";

            return null;
        }

        /// <summary>
        /// Kräver ändringen en bekräftelse? Ja när något redan är utdelat eller bygger på den nuvarande indelningen: en publicerad lista
        /// (medaljörerna ändras för alla som ser den), skjutna särskjutningar (raderna är nycklade på skyttens klass, inte på gruppen, och
        /// återanvänds tyst i nya grupper) eller en egen hedersprisfördelning. Null = ingen bekräftelse behövs.
        /// </summary>
        public static string? ConfirmationNeeded(bool listIsOfficial, int shootOffEntries, bool hasCustomHonoraryConfig)
        {
            var reasons = new List<string>();
            if (listIsOfficial)
                reasons.Add("Resultatlistan är publicerad: medaljörerna ändras för alla som ser den.");
            if (shootOffEntries > 0)
                reasons.Add($"Det finns {shootOffEntries} noterade särskjutningsrundor. De hör till skyttens klass och räknas om i de nya grupperna, "
                          + "så en strid kan få nya motståndare eller falla bort.");
            if (hasCustomHonoraryConfig)
                reasons.Add("Hederspriserna har en egen fördelning som är kopplad till grupperna.");

            return reasons.Count == 0 ? null : string.Join(" ", reasons);
        }
    }
}
