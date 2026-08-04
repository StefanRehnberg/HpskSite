using System.Globalization;
using System.Text.Json;
using QRCoder;

namespace HpskSite.Services;

/// <summary>
/// QR code for paying an invoice by bankgiro, in the Swedish invoice-QR format that bank apps read
/// ("Skanna faktura" / "Skanna QR-kod" in Swedbank, SEB, Handelsbanken, Nordea m.fl.).
///
/// This is a DIFFERENT thing from the Swish QR:
///   * Swish QR encodes a Swish payment and is opened by the Swish app.
///   * This encodes an INVOICE (payee account, amount, reference, dates) and is opened by the payer's
///     own bank app, which prefills a bankgiro payment that the payer then confirms.
///
/// It needs **no agreement with Bankgirot or the banks** — the issuer just prints the code, and all the
/// work happens in the payer's bank app. That is why it can be offered to every club and krets that has
/// filled in a bankgiro.
///
/// Payload is the compact JSON object of the Swedish invoice-QR specification, version 1:
///   {"uqr":1,"tp":1,"nme":"Payee","cid":"8021234567","iref":"5326-38-1",
///    "idt":"20260804","ddt":"20260818","due":200.00,"pt":"BG","acc":"50747534","cur":"SEK"}
/// Fields: uqr = spec version, tp = 1 (invoice), nme = payee name, cid = payee org.nr,
/// iref = invoice reference/OCR, idt = invoice date, ddt = due date, due = amount, pt = account type
/// (BG bankgiro / PG plusgiro), acc = account number, cur = currency.
///
/// NOTE ON THE REFERENCE: `iref` carries our invoice number as a plain reference. A club that has
/// ordered OCR-kontroll on its bankgiro requires a numeric OCR reference with a check digit, which an
/// invoice number like "5326-team-38-1" is not — those clubs will see the payment arrive with the
/// reference as a message instead. Nothing is lost (the organiser still matches it), but it is the one
/// detail to check with a club that uses OCR.
/// </summary>
public static class BankgiroQrCodeGenerator
{
    /// <summary>Builds the invoice-QR JSON payload. Empty fields are omitted rather than sent blank.</summary>
    public static string BuildPayload(
        string payeeName,
        string bankgiroNumber,
        decimal amount,
        string reference,
        string? payeeOrgNumber = null,
        DateTime? invoiceDate = null,
        DateTime? dueDate = null)
    {
        // The account goes in WITH its hyphen ("5074-7534"). Verified against a real bank app
        // 2026-08-04: a digits-only account is rejected when scanning, so don't "normalise" it away.
        var account = FormatAccount(bankgiroNumber);
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Bankgiro number is required.", nameof(bankgiroNumber));
        if (amount <= 0m)
            throw new ArgumentException("Amount must be positive.", nameof(amount));

        // Ordered dictionary keeps the field order of the spec's examples — some older bank apps have
        // been sensitive to it, and it costs nothing to be conservative with money.
        var payload = new Dictionary<string, object>
        {
            ["uqr"] = 1,
            ["tp"] = 1,
            ["nme"] = Truncate(payeeName?.Trim() ?? "", 35)
        };

        var org = new string((payeeOrgNumber ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (org.Length > 0) payload["cid"] = org;

        var iref = Truncate((reference ?? "").Trim(), 25);
        if (iref.Length > 0) payload["iref"] = iref;

        if (invoiceDate.HasValue) payload["idt"] = invoiceDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (dueDate.HasValue) payload["ddt"] = dueDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // Amount as a JSON number with two decimals and a dot, never the sv-SE comma.
        payload["due"] = decimal.Parse(amount.ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        payload["pt"] = "BG";
        payload["acc"] = account;
        payload["cur"] = "SEK";

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            // Swedish names must stay readable in the payload, so don't escape non-ASCII.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>PNG bytes for the invoice QR. No logo — bank apps expect a plain code.</summary>
    public static byte[] GeneratePng(
        string payeeName,
        string bankgiroNumber,
        decimal amount,
        string reference,
        string? payeeOrgNumber = null,
        DateTime? invoiceDate = null,
        DateTime? dueDate = null,
        int pixelsPerModule = 10)
    {
        var payload = BuildPayload(payeeName, bankgiroNumber, amount, reference, payeeOrgNumber, invoiceDate, dueDate);
        var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// A bankgiro in the format bank apps accept when scanning: digits WITH the hyphen before the last
    /// four ("5074-7534"). Adds the hyphen if the caller stored the number without one, so the QR is
    /// well-formed regardless of how the club typed it into settings.
    /// </summary>
    public static string FormatAccount(string? bankgiro)
    {
        var digits = DigitsOnly(bankgiro);
        if (digits.Length is not (7 or 8)) return digits.Length == 0 ? "" : digits;
        return digits[..^4] + "-" + digits[^4..];
    }

    /// <summary>Just the digits — for validation, never for the QR payload (see FormatAccount).</summary>
    public static string DigitsOnly(string? bankgiro)
        => new string((bankgiro ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Valid bankgiro numbers are 7 or 8 digits.</summary>
    public static bool IsValidBankgiro(string? bankgiro)
    {
        var digits = DigitsOnly(bankgiro);
        return digits.Length is 7 or 8;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
