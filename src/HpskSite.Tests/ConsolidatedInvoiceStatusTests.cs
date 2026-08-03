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

    /// <summary>
    /// Amounts decide what a club is asked to pay, so a misread is the most expensive failure in this
    /// feature. The property is a Decimal, but the value that comes back is whatever was stored — a
    /// boxed decimal or double normally, a string on older or hand-edited rows, and a Swedish-formatted
    /// string carries a decimal comma and possibly a (non-breaking) space as thousands separator.
    /// Parsing that with invariant rules returns 0, which would silently under-bill.
    /// </summary>
    public class ConsolidatedInvoiceAmountTests
    {
        [Fact]
        public void BoxedNumericTypesPassThrough()
        {
            Assert.Equal(150m, ConsolidatedInvoiceService.ParseAmount(150m));
            Assert.Equal(150.5m, ConsolidatedInvoiceService.ParseAmount(150.5d));
        }

        [Theory]
        [InlineData("150", 150)]
        [InlineData("150.00", 150)]
        [InlineData("150,00", 150)]
        [InlineData(" 150,50 ", 150.5)]
        [InlineData("1050", 1050)]
        [InlineData("1 050,00", 1050)]      // space as thousands separator
        public void SwedishAndPlainStringsParse(string raw, double expected)
        {
            Assert.Equal((decimal)expected, ConsolidatedInvoiceService.ParseAmount(raw));
        }


        [Fact]
        public void NonBreakingSpaceThousandsSeparatorParses()
        {
            // What a copy-paste out of a formatted report or Excel actually contains: U+00A0.
            Assert.Equal(1050m, ConsolidatedInvoiceService.ParseAmount("1 050,00"));
        }
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("gratis")]
        public void UnreadableValuesAreZeroNotAGuess(string? raw)
        {
            // Zero is the honest answer for "no amount stored"; such an invoice is then reported as
            // having nothing to pay rather than being billed an invented figure.
            Assert.Equal(0m, ConsolidatedInvoiceService.ParseAmount(raw));
        }
    }
}
