using System;
using FluentAssertions;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tests for the Swish QR code + deep-link helpers.
    ///
    /// The two payload formats are deliberately different:
    ///   - QR code text:  C{phone};{amount};{message};{lock}  (parsed by the Swish app's scanner)
    ///   - Deep link URL: swish://payment?data={url-encoded JSON}
    ///
    /// Reusing the QR text inside the deep link is the bug that caused "Felaktig länk"
    /// in production (commit 3848836 reverted to that, fixed here).
    /// </summary>
    public class SwishQrCodeGeneratorTests
    {
        // ── IsValidSwishNumber ─────────────────────────────────────────────────

        [Theory]
        [InlineData("0701234567", true)]   // private mobile (Swish Privat / Företag)
        [InlineData("0731234567", true)]   // any 07X prefix
        [InlineData("1234567890", true)]   // Swish Handel merchant alias
        [InlineData("1239876543", true)]   // any 123 prefix
        [InlineData("070 123 45 67", true)] // formatting tolerated
        [InlineData("070-123-4567", true)]  // formatting tolerated
        public void IsValidSwishNumber_AcceptsValidFormats(string input, bool expected)
        {
            SwishQrCodeGenerator.IsValidSwishNumber(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("0501234567")]   // 05 prefix — not Swish
        [InlineData("0801234567")]   // 08 (landline) — not Swish
        [InlineData("12345")]        // too short
        [InlineData("12345678901")]  // 11 digits — too long
        [InlineData("070ABCDEFG")]   // contains letters
        [InlineData("456789012")]    // wrong prefix
        public void IsValidSwishNumber_RejectsInvalidFormats(string? input)
        {
            SwishQrCodeGenerator.IsValidSwishNumber(input).Should().BeFalse();
        }

        // ── GetSwishUrl (QR-text format) ──────────────────────────────────────

        [Fact]
        public void GetSwishUrl_BuildsCFormat()
        {
            var payload = SwishQrCodeGenerator.GetSwishUrl("0701234567", "100.00", "Hello");
            payload.Should().Be("C0701234567;100.00;Hello;0");
        }

        [Fact]
        public void GetSwishUrl_AcceptsMerchantNumber()
        {
            var payload = SwishQrCodeGenerator.GetSwishUrl("1234567890", "150.50", "Reg #42");
            payload.Should().Be("C1234567890;150.50;Reg #42;0");
        }

        // ── GetSwishAppUrl (deep link) ────────────────────────────────────────

        [Fact]
        public void GetSwishAppUrl_PrivateNumberUsesPlus46Prefix()
        {
            // Mobile numbers are normalised to international form (+46…) per the Swish
            // app's deep-link JSON schema. The leading 0 is dropped.
            var url = SwishQrCodeGenerator.GetSwishAppUrl("0701234567", "100.00", "Hello");

            url.Should().StartWith("swish://payment?data=");
            // Decode the data parameter so we can assert against the JSON.
            var encoded = url.Substring("swish://payment?data=".Length);
            var json = Uri.UnescapeDataString(encoded);
            json.Should().Contain("\"payee\":{\"value\":\"+46701234567\"");
            json.Should().Contain("\"amount\":{\"value\":100");
            json.Should().Contain("\"message\":{\"value\":\"Hello\"");
            json.Should().Contain("\"version\":1");
        }

        [Fact]
        public void GetSwishAppUrl_MerchantNumberStaysAsIs()
        {
            // Swish Handel aliases (123…) aren't phone numbers — no country-code prefix.
            var url = SwishQrCodeGenerator.GetSwishAppUrl("1234567890", "250.00", "Order");

            var encoded = url.Substring("swish://payment?data=".Length);
            var json = Uri.UnescapeDataString(encoded);
            json.Should().Contain("\"payee\":{\"value\":\"1234567890\"");
        }

        [Fact]
        public void GetSwishAppUrl_AmountHandlesDecimals()
        {
            // 100.00 → 100 (trailing zeros trimmed), 150.50 → 150.5
            var url = SwishQrCodeGenerator.GetSwishAppUrl("0701234567", "150.50", "x");
            var json = Uri.UnescapeDataString(url.Substring("swish://payment?data=".Length));
            json.Should().Contain("\"amount\":{\"value\":150.5");

            var url2 = SwishQrCodeGenerator.GetSwishAppUrl("0701234567", "100.00", "x");
            var json2 = Uri.UnescapeDataString(url2.Substring("swish://payment?data=".Length));
            json2.Should().Contain("\"amount\":{\"value\":100");
        }

        [Theory]
        [InlineData("0501234567")] // wrong prefix
        [InlineData("12345")]      // too short
        [InlineData("")]
        public void GetSwishAppUrl_RejectsInvalidNumbers(string badNumber)
        {
            Action act = () => SwishQrCodeGenerator.GetSwishAppUrl(badNumber, "100.00", "x");
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void GetSwishAppUrl_DoesNotUseCFormat()
        {
            // Regression guard: the deep link must NOT contain the QR's C-format,
            // which is what triggered "Felaktig länk" in production.
            var url = SwishQrCodeGenerator.GetSwishAppUrl("0701234567", "100.00", "Hello");
            var decoded = Uri.UnescapeDataString(url.Substring("swish://payment?data=".Length));
            decoded.Should().NotStartWith("C0701234567");
            decoded.Should().StartWith("{"); // must be JSON
        }
    }
}
