using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Alla prov som skriver i Site*-tabellerna mot en riktig SQL Server (<c>HPSK_TEST_SQL</c>) körs ETT I TAGET. De delar tabeller och nyckelintervall:
    /// <c>SitePageStore.LockOwner</c> tar <c>UPDLOCK, HOLDLOCK</c> på en <c>SiteOwner</c>-rad, vilket låser intervallet mot grannraderna, så två prov som
    /// parallellt skapar ägare (990001, 990002, 990003) kan vänta på varandra eller hamna i ett dödläge. Det gav ett enstaka fall i hela sviten
    /// (samtidighetsprovet) som gick grönt ensamt. Samtidighetsprovet SJÄLVT använder två anslutningar med flit; det är provklasserna mellan varandra som
    /// inte ska kollidera.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class SiteSqlCollection
    {
        public const string Name = "SiteSql";
    }
}
