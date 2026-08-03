using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// paymentStatus is not always a bare string in older data — some rows are stored JSON-wrapped as
    /// ["Paid"]. Three separate CleanStatus/CleanPaymentStatus helpers already exist to defend against
    /// it, which is the evidence such rows are real. The samlingsfaktura flow compares statuses in a
    /// dozen places, and getting it wrong is not cosmetic:
    ///
    ///   - a legacy ["Pending"] invoice would be reported as un-consolidatable, so a club could not pay
    ///     for that shooter at all;
    ///   - the paid-cascade would fail to skip a child already ["Paid"], re-sending its
    ///     betalningsbekräftelse;
    ///   - a ["Cancelled"] parent would still be treated as open, locking its children permanently.
    ///
    /// These cases cannot be manufactured through the app (SetInvoicePropertySafely validates
    /// paymentStatus against a whitelist), so the normaliser is verified directly.
    /// </summary>
    public class ConsolidatedInvoiceStatusTests
    {
        [Theory]
        [InlineData("Pending", "Pending")]
        [InlineData("Paid", "Paid")]
        [InlineData("Cancelled", "Cancelled")]
        [InlineData("Refunded", "Refunded")]
        public void PlainValuesPassThrough(string raw, string expected)
        {
            Assert.Equal(expected, ConsolidatedInvoiceService.NormalizeStatus(raw));
        }

        [Theory]
        [InlineData("[\"Paid\"]", "Paid")]
        [InlineData("[\"Pending\"]", "Pending")]
        [InlineData("[\"Cancelled\"]", "Cancelled")]
        [InlineData("['Paid']", "Paid")]
        [InlineData("[Paid]", "Paid")]
        public void JsonWrappedLegacyValuesAreUnwrapped(string raw, string expected)
        {
            Assert.Equal(expected, ConsolidatedInvoiceService.NormalizeStatus(raw));
        }

        [Theory]
        [InlineData("  Paid  ", "Paid")]
        [InlineData("\"Paid\"", "Paid")]
        [InlineData(" [\"Paid\"] ", "Paid")]
        public void WhitespaceAndQuotesAreStripped(string raw, string expected)
        {
            Assert.Equal(expected, ConsolidatedInvoiceService.NormalizeStatus(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void UnsetMeansPending(string? raw)
        {
            // Unset must read as unpaid, not as "unknown" — the same convention the rest of the invoice
            // code uses. Reading it as anything else would hide payable invoices from consolidation.
            Assert.Equal("Pending", ConsolidatedInvoiceService.NormalizeStatus(raw));
        }

        [Fact]
        public void AnUnparseableValueIsNotSilentlyTurnedIntoPaid()
        {
            // Garbage must never normalise to Paid — that would mark money as received.
            foreach (var junk in new[] { "???", "[", "[]", "{\"status\":\"Paid\"}" })
            {
                var result = ConsolidatedInvoiceService.NormalizeStatus(junk);
                Assert.NotEqual("Paid", result);
            }
        }

        [Fact]
        public void MultiValueArrayTakesTheFirstEntry()
        {
            Assert.Equal("Paid", ConsolidatedInvoiceService.NormalizeStatus("[\"Paid\",\"Pending\"]"));
        }
    }
}
