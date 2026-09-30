namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Föreningens pengakonton — kassa, bank, Swish, placerings- och sparkonton (BAS 19xx).
    ///
    /// <para><b>Varför en egen regel:</b> en överföring mellan två egna konton (från sparkontot 1940
    /// till föreningskontot 1930) är ingen affär och påverkar inte resultatet. Bokför byggdes kring
    /// "vi betalade" och "vi fick in", och den posten gick därför inte att bokföra alls
    /// (rapporterat av en kassör 2026-09-29).</para>
    ///
    /// <para><b>⚠️ Begränsat till 19xx med flit</b> (Stefans beslut 2026-09-29). Andra balanskonton,
    /// t.ex. amortering av ett lån (2350 mot 1930), är också rena balansposter men läggs till först
    /// när någon behöver dem — en längre lista gör det lättare att välja fel konto.</para>
    /// </summary>
    public static class LedgerMoneyAccount
    {
        public const int First = 1900;
        public const int Last = 1999;

        public static bool Is(int accountNumber) => accountNumber >= First && accountNumber <= Last;
    }
}
