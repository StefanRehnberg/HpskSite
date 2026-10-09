namespace HpskSite.Routing
{
    /// <summary>
    /// Hur en händelsesida får sin händelse (sajtstrukturen fas 2). På årsadressen
    /// <c>/{klubb eller krets}/{år}/{slug}/</c> är sidans modell ÄGARNODEN och händelsen ligger i
    /// <c>HttpContext.Items</c> under <see cref="ItemKey"/>; på den gamla trädadressen är modellen
    /// händelsenoden själv. Vyn <c>ClubSimpleEvent.cshtml</c> klarar båda.
    /// </summary>
    public static class ClubEventRoute
    {
        public const string ItemKey = "hpsk.clubEvent";
    }
}
