namespace HpskSite.Shared.Constants;

/// <summary>
/// Shared handicap information text for display in web and mobile apps.
/// </summary>
public static class HandicapInfo
{
    public const string Title = "Så fungerar handicap";

    public const string Introduction =
        "Handicap gör det möjligt för skyttar på olika nivåer att tävla rättvist mot varandra.";

    public const string CalculationTitle = "Hur handicap beräknas:";

    public const string CalculationPoints =
        "• Baseras på ditt genomsnittliga serieresultat\n" +
        "• En skytt med snitt 48 har handicap 0\n" +
        "• Lägre snitt = positivt handicap (bonus)\n" +
        "• Högre snitt = negativt handicap (avdrag)";

    public const string StartingIndexTitle = "Startindex per skytteklass:";

    public const string StartingIndexPoints =
        "• Klass 1: 44 poäng (Nybörjare)\n" +
        "• Klass 2: 46 poäng (Guldmärkesskytt)\n" +
        "• Klass 3: 48 poäng (Riksmästare)";

    public const string ProvisionalTitle = "Provisoriskt handicap (P):";

    public const string ProvisionalPoints =
        "• Handicap baseras på snitt från senaste 5 matcher\n" +
        "• Nya skyttar: startindex ersätter saknade resultat\n" +
        "• Visas med (P) tills 5 riktiga resultat finns";

    public const string ProvisionalExampleTitle = "Exempel för Klass 1-skytt:";

    public const string ProvisionalExampleText =
        "• 0 matcher: (44+44+44+44+44)/5 = 44 → hcp +4\n" +
        "• 1 match (40p): (40+44+44+44+44)/5 = 43.2 → hcp +4.8\n" +
        "• 5 matcher: endast riktiga resultat används";

    public const string ResultCalculationTitle = "Så räknas slutresultatet:";

    public const string ResultCalculationPoints =
        "• Handicap läggs till på VARJE serie\n" +
        "• Max 50 poäng per serie (tak)\n" +
        "• Slutresultat = summan av alla justerade serier";

    public const string ExampleTitle = "Exempel (handicap +3):";

    public const string ExampleText =
        "Råpoäng: 47, 49, 46\n" +
        "• Serie 1: 47 + 3 = 50 ✓\n" +
        "• Serie 2: 49 + 3 = 52 → 50 (tak)\n" +
        "• Serie 3: 46 + 3 = 49 ✓\n" +
        "• Totalt: 149 poäng";

    public const string Note =
        "Notera: Höga råpoäng kan \"förlora\" handicap till 50-taket.";

    /// <summary>
    /// Full explanation text formatted for mobile DisplayAlert.
    /// </summary>
    public static string GetFullTextForMobile() =>
$@"{Introduction}

{CalculationTitle}
{CalculationPoints}

{StartingIndexTitle}
{StartingIndexPoints}

{ProvisionalTitle}
{ProvisionalPoints}

{ProvisionalExampleTitle}
{ProvisionalExampleText}

{ResultCalculationTitle}
{ResultCalculationPoints}

{ExampleTitle}
{ExampleText}

{Note}";
}
