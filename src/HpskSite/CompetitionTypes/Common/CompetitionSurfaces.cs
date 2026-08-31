namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Vilka YTOR en tävlingstyp har i tävlingshanteringen — flikraden och de vyer som hänger på
    /// den. THE one place these answers live, av exakt samma skäl som
    /// <see cref="CompetitionResultTables"/> och <see cref="PrecisionFamily"/> finns.
    ///
    /// ⚠️ Innan 2026-08-31 låg de här svaren som handskrivna grenlistor **inline i två vyer**:
    /// <c>Views/CompetitionManagement.cshtml</c> (<c>hasFunktionarerTab</c> + Stationer-fliken) och
    /// <c>Views/Partials/CompetitionResultsManagement.cshtml</c> (<c>supportsSkjutledareView</c>).
    /// Följden blev precis den lukt de andra registren finns för att stoppa: när Standardpistol och
    /// Sportpistol lades till 2026-08-26 ärvde de tyst luckan, och **fem grenar** — Milsnabb, Duell,
    /// NationellHelmatch, Standardpistol, Sportpistol — hade ingen Funktionärer-flik trots att de
    /// delar seriemodell, startlistor, resultatendpoints OCH inmatningsskärm med Precision.
    /// Rapporterat från en tävlingshelg: "knapparna för att öppna resultatinmatning ligger
    /// fortfarande kvar på Resultat-fliken".
    ///
    /// ⚠️ Och de två frågorna var SAMMA flagga, vilket är varför de måste vara skilda här:
    /// <c>supportsSkjutledareView</c> styrde både om Skjutledare-vyn fanns OCH om
    /// startknapparna låg kvar på Resultat-fliken. Att ge en gren hubben utan vyn (eller vyn utan
    /// hubben) var alltså omöjligt utan att knapparna hamnade på två ställen samtidigt.
    /// </summary>
    public static class CompetitionSurfaces
    {
        /// <summary>
        /// Normaliserar en lagrad tävlingstyp till registrets Id.
        ///
        /// ⚠️ Egenskapen är fritext i praktiken: dev-databasen innehåller en nod med
        /// <c>competitionType = "Magnum Precision"</c> — MED MELLANSLAG — bredvid noder med
        /// <c>"MagnumPrecision"</c>. En exakt strängjämförelse missar den, och effekten är tyst:
        /// tävlingen tappar sin flik i stället för att något går sönder synligt. Samma skäl som
        /// <see cref="Models.CompetitionTypes.GetFuzzy"/> finns för.
        ///
        /// Exakt först (Id, sedan Namn), fuzzy sist — så en korrekt lagrad typ aldrig kan
        /// fuzzy-matchas till en annan gren.
        /// </summary>
        private static string Canonical(string? typeId)
        {
            var t = (typeId ?? "").Trim();
            if (t.Length == 0) return "";
            var known = Models.CompetitionTypes.GetById(t)
                     ?? Models.CompetitionTypes.GetByName(t)
                     ?? Models.CompetitionTypes.GetFuzzy(t);
            return known?.Id ?? t;
        }

        /// <summary>
        /// Har grenen en **Funktionärer**-flik — dagens-läge-navet med skjutlagstidslinje, live
        /// funktionärsbelastning, startknappar in i inmatningen och tävlingsledningens
        /// meddelandekonsol?
        ///
        /// Hela precisionsfamiljen, plus Springskytte som har sin egen variant av navet
        /// (<c>SpringskytteFunktionarerManagement</c>). Familjen räknas via
        /// <see cref="PrecisionFamily.IsMember"/> så en ny gren i registret får fliken
        /// automatiskt — det var det som inte hände för Standardpistol och Sportpistol.
        ///
        /// Fältskytte/MagnumFält har **Stationer** i stället, se <see cref="HasStationerTab"/>.
        /// </summary>
        public static bool HasFunktionarerHub(string? typeId)
        {
            var t = Canonical(typeId);
            if (t.Length == 0) return true; // tomt = legacy Precision-nod, samma fallback som överallt annars
            return PrecisionFamily.IsMember(t)
                || t.Equals("Springskytte", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Har grenen en **Stationer**-flik? Fältskytte och MagnumFält, som skjuts i patruller över
        /// stationer i stället för i skjutlag över serier.
        /// </summary>
        public static bool HasStationerTab(string? typeId)
        {
            var t = Canonical(typeId);
            return t.Equals("Faltskytte", StringComparison.OrdinalIgnoreCase)
                || t.Equals("MagnumFalt", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Har grenen en **Skjutledare-vy** (<c>/skjutledare?c=…&amp;l=…</c>)?
        ///
        /// ⚠️ MEDVETET BARA Precision och MagnumPrecision (Stefan 2026-08-31). Vyn är byggd kring
        /// precisionens kommandoord och seriegång, och **snabbskyttegrenarna har andra kommandon** —
        /// Milsnabb, Duell, Standardpistol och Sportpistol skjuts med annan tidssättning och andra
        /// kommandon, så att slå på vyn för dem skulle ge skjutledaren fel uppläsning. Det är en
        /// egen post i backloggen (P2), inte en lucka att fylla på vägen.
        ///
        /// Håll den här skild från <see cref="HasFunktionarerHub"/>: en gren ska kunna få navet utan
        /// vyn. Det var precis vad den tidigare gemensamma flaggan gjorde omöjligt.
        /// </summary>
        public static bool HasSkjutledareView(string? typeId)
        {
            var t = Canonical(typeId);
            if (t.Length == 0) return true; // tomt = legacy Precision-nod
            return t.Equals("Precision", StringComparison.OrdinalIgnoreCase)
                || t.Equals("MagnumPrecision", StringComparison.OrdinalIgnoreCase);
        }
    }
}
