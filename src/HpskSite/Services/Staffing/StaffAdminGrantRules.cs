namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Steg 5, Expertgruppen steg 4–6 (K2 och K8): <b>vem får dela ut "Ge behörighet"</b> på ett uppdrag. Ren funktion över fakta som
    /// kontrollern hämtar; svaret är texten till den som frågar, eller null = får.
    /// <list type="bullet">
    /// <item>En tävlings egen bemanning (ingen planering): som förut.</item>
    /// <item><b>Det gemensamma</b> (huvudplanering utan egen tävling, med delar) gäller alla veckans tävlingar — bara huvudplaneringens
    /// Sammankallande ger det. En "Kan ändra" som bara skriver in folk kunde annars ge sig själv tävlingshantering överallt.</item>
    /// <item>En planering utan tävling och utan delar: det finns inget att ge behörighet till (spärras av tjänsten).</item>
    /// <item>En planering med tävling som den <b>själv</b> är kopplad till: som förut.</item>
    /// <item>En planering vars tävling kommer från "Bemannar för" eller arv (inte egen koppling): bara den som har organisatörsrätt i
    /// tävlingen — annars kunde en delplaneringsledare ge appbehörighet i en annan klubbs tävling.</item>
    /// </list>
    /// </summary>
    public static class StaffAdminGrantRules
    {
        public const string NeedsConvener = "Det gemensammas behörighet ges av huvudplaneringens sammankallande.";
        public const string NeedsOrganiser = "Behörighet i den här tävlingen ges av arrangören. Planeringen är inte själv kopplad till den.";

        public static string? Refusal(bool hasPlanning, bool hasCompetition, bool carriesFamilyGrant, bool isRootConvener, bool competitionIsOwnLink, bool hasOrganiserAccess)
        {
            if (!hasPlanning) return null;
            if (!hasCompetition) return carriesFamilyGrant && !isRootConvener ? NeedsConvener : null;
            return !competitionIsOwnLink && !hasOrganiserAccess ? NeedsOrganiser : null;
        }
    }
}
