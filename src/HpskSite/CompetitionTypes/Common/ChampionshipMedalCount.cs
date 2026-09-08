namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Hur många mästerskapsmedaljer som delas ut i en mästerskapsklass, ordagrant enligt
    /// SHB 2026 C.3.4.1.
    ///
    /// ⚠️⚠️ MÄTS PÅ MÄSTERSKAPSKLASSEN, INTE PÅ SKICKLIGHETSKLASSEN. C.3.4.1 säger ordagrant:
    /// *"Antalet medaljer till de främsta i individuella mästerskap reduceras när antalet
    /// deltagare i EN VAPENGRUPP är lägre än fem"*. Klasserna 1, 2 och 3 är kompetensnivåer
    /// inom en vapengrupp och har inga egna medaljer — lagavsnittet räknar upp medaljgrupperna
    /// explicit: *"Förbundets medaljer inom var och en av vapengrupperna A, B, C, samt klasserna
    /// Damer C, Juniorer C, Veteraner C"* (och R i fält). Samma axel som finalgallringen och
    /// särskjutningen, se <see cref="ChampionshipCategory"/>.
    ///
    /// Att mäta per skicklighetsklass gav rakt felaktiga påståenden på skärmen: "B1, 3 deltagare
    /// → Enbart Guld" på en tävling där vapengrupp B har 31 deltagare och alltså full
    /// medaljomgivning. Rapporterat 2026-09-08.
    ///
    /// ⚠️ Regeln bor HÄR och inte hos en enskild konsument. Den behövs på tre ställen —
    /// resultatlistans medaljpanel, prisutdelningssidan och lagmedaljerna — och
    /// mästerskapskategori/skicklighetsklass har redan förväxlats tre gånger i den här
    /// kodbasen (finalgallringen, särskjutningen, medaljräkningen). En fjärde kopia av regeln
    /// är en fjärde chans att förväxla dem.
    /// </summary>
    public static class ChampionshipMedalCount
    {
        /// <summary>
        /// Antal medaljer och klartexten för dem.
        ///
        /// ⚠️ Juniorregeln är EGEN och går i MOTSATT riktning: *"Vid samtliga juniormästerskap
        /// skall medaljer delas ut till de 3 bästa. Är deltagarantalet färre än 3, delas
        /// medaljer ut till de som deltagit."* Junioren förlorar alltså aldrig sin medalj på få
        /// deltagare — antalet följer bara deltagarantalet.
        /// </summary>
        /// <param name="participants">
        /// Deltagare i mästerskapsklassen. För individuell tävling: distinkta skyttar i
        /// vapengruppen. För lagtävling: antalet STARTANDE LAG i gruppen — C.3.6.5.1 säger
        /// *"Om antalet startande lag i vapengrupp eller klass är mindre än fem reduceras
        /// antalet mästerskapstecken i samma ordning som i individuell tävling."*
        /// </param>
        /// <param name="isJuniorCategory">True för juniorklassen/juniorlaget.</param>
        /// <param name="unitPlural">
        /// Vad som räknas, i plural — "deltagande" (individuellt) eller "lag". Används bara av
        /// juniorregelns klartext, som är den enda som nämner enheten.
        ///
        /// ⚠️ Finns för att lagkortet inte ska säga "Medaljer till alla 1 deltagande" om ett
        /// LAG. Antalet är rätt i båda fallen; det var bara ordet som var fel, och ett
        /// felaktigt ord på en funktionärsskärm läses som ett felaktigt antal.
        /// </param>
        public static (int Medals, string Text) For(
            int participants,
            bool isJuniorCategory,
            string unitPlural = "deltagande")
        {
            if (isJuniorCategory)
            {
                var n = System.Math.Min(3, System.Math.Max(0, participants));
                return (n, participants >= 3
                    ? "Guld, Silver, Brons"
                    : $"Medaljer till alla {n} {unitPlural}");
            }
            return participants switch
            {
                >= 5 => (3, "Guld, Silver, Brons"),
                4 => (2, "Guld + Silver"),
                3 => (1, "Enbart Guld"),
                _ => (0, "Inga medaljer")
            };
        }

        /// <summary>Valören för en placering. "Guld" / "Silver" / "Brons".</summary>
        public static string MedalNameForPlace(int place) => place switch
        {
            1 => "Guld",
            2 => "Silver",
            3 => "Brons",
            _ => $"Plats {place}"
        };

        /// <summary>
        /// Hederspristaket enligt SHB C.3.4.2: *"Om så sker skall dessa tillfalla minst en
        /// fjärdedel av de i tävlingen deltagande."* Minst — arrangören får dela ut fler.
        /// </summary>
        public static int MinimumHonoraryAwards(int participants) =>
            participants <= 0 ? 0 : (int)System.Math.Ceiling(participants / 4.0);
    }
}
