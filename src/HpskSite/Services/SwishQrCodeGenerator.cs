using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Globalization;

namespace HpskSite.Services;

public static class SwishQrCodeGenerator
{
    /// <summary>
    /// Generates a Swish URL that can be used for direct payment links or QR codes.
    /// </summary>
    /// <param name="phoneNumber">10 digits, no +46. Ex: 0702123456</param>
    /// <param name="amount">Two decimals with dot. Ex: 100.00</param>
    /// <param name="message">Max 50 chars</param>
    /// <returns>Swish payment URL</returns>
    public static string GetSwishUrl(string phoneNumber, string amount, string message)
    {
        // --- Validate & normalize ---
        phoneNumber = NormalizePhone(phoneNumber);
        if (phoneNumber.Length != 10 || !phoneNumber.All(char.IsDigit))
        {
            throw new ArgumentException("Phone must be 10 digits like 0701234567.");
        }

        if (!IsAmountOk(amount))
        {
            throw new ArgumentException("Amount must be like 100.00 (dot as decimal, two decimals).");
        }

        message = message?.Trim() ?? string.Empty;
        if (message.Length > 50)
        {
            message = message[..50]; // Swish raw QR limit
        }

        var payload = $"C{phoneNumber};{amount};{message};0";
        return payload;
    }

    /// <summary>
    /// Generates a Swish app deep link that opens the Swish app on a mobile device
    /// with a prefilled payment. The deep link uses a DIFFERENT payload format from
    /// the QR code: a JSON object URL-encoded into the `data` parameter, per the
    /// pattern documented in mast4461/swish-easy and the Swish developer docs.
    ///
    /// Previously this function reused the C-format QR payload — the Swish app
    /// rejects that inside `swish://payment?data=` with "Felaktig länk" because
    /// the deep-link parser expects JSON, not the QR text format.
    /// </summary>
    /// <param name="phoneNumber">10 digits. Either a Swish private/Företag mobile number
    /// starting with "07" (e.g. 0701234567) or a Swish Handel merchant number starting
    /// with "123" (e.g. 1230001234). Mobile numbers are normalised to "+46…" for the
    /// JSON payload; merchant numbers are sent as-is.</param>
    /// <param name="amount">Two decimals with dot. Ex: 100.00</param>
    /// <param name="message">Max 50 chars</param>
    /// <returns>Swish app URL (swish://payment?data=&lt;url-encoded JSON&gt;)</returns>
    public static string GetSwishAppUrl(string phoneNumber, string amount, string message)
    {
        // Validate + normalise via the shared helper.
        phoneNumber = NormalizePhone(phoneNumber);
        if (!IsValidSwishNumber(phoneNumber))
        {
            throw new ArgumentException("Swish number must be 10 digits starting with 07 (private/Företag) or 123 (Handel).");
        }
        if (!IsAmountOk(amount))
        {
            throw new ArgumentException("Amount must be like 100.00 (dot as decimal, two decimals).");
        }
        message = message?.Trim() ?? string.Empty;
        if (message.Length > 50)
        {
            message = message[..50];
        }

        // Mobile numbers (07…) → international form "+467…" with the leading 0 dropped.
        // Merchant numbers (123…) stay as-is — they're not phone numbers and don't take a
        // country code prefix.
        var payeeValue = phoneNumber.StartsWith("07")
            ? "+46" + phoneNumber.Substring(1)
            : phoneNumber;

        // Amount in JSON is a numeric SEK value — Swish accepts integer or fractional
        // depending on how öre are handled. We pass it through invariant-culture decimal
        // formatting so "100.00" arrives as 100, "150.50" as 150.5.
        var amountValue = decimal.Parse(amount, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture);
        var amountJson = amountValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Build the JSON payload by hand to keep escaping rules under our control —
        // System.Text.Json would add unnecessary spaces and re-encode unicode in ways
        // that vary by .NET version. The schema mirrors mast4461/swish-easy and the
        // Swish developer docs: version, payee, amount, message — each with an
        // editable flag locked to false so the shooter can't tamper with the prefill.
        var payloadJson = "{"
            + "\"version\":1,"
            + "\"payee\":{\"value\":\"" + JsonStringEscape(payeeValue) + "\",\"editable\":false},"
            + "\"amount\":{\"value\":" + amountJson + ",\"editable\":false},"
            + "\"message\":{\"value\":\"" + JsonStringEscape(message) + "\",\"editable\":false}"
            + "}";

        return $"swish://payment?data={Uri.EscapeDataString(payloadJson)}";
    }

    /// <summary>
    /// True when the input is a syntactically valid Swish number — 10 digits,
    /// either starting with "07" (private mobile / Swish för Företag) or "123"
    /// (Swish Handel merchant alias). Does NOT verify that the number is
    /// actually registered with Swish; that requires merchant API access we
    /// don't have.
    /// </summary>
    public static bool IsValidSwishNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) return false;
        var normalized = NormalizePhone(phoneNumber);
        if (normalized.Length != 10 || !normalized.All(char.IsDigit)) return false;
        return normalized.StartsWith("07") || normalized.StartsWith("123");
    }

    private static string JsonStringEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Generates a Swish QR (PNG bytes) with a centered logo. Cross-platform (ImageSharp).
    /// </summary>
    /// <param name="phoneNumber">10 digits, no +46. Ex: 0702123456</param>
    /// <param name="amount">Two decimals with dot. Ex: 100.00</param>
    /// <param name="message">Max 50 chars</param>
    /// <param name="logoPath">Path to PNG logo (transparent is fine)</param>
    public static byte[] GeneratePng(string phoneNumber, string amount, string message,
        int pixelsPerModule = 20,
        int iconSizePercent = 25,
        int iconBorderWidth = 6)
    {
        // Use the new GetSwishUrl method for consistency
        var payload = GetSwishUrl(phoneNumber, amount, message);

        return GeneratePngFromUrl(payload, pixelsPerModule, iconSizePercent, iconBorderWidth);
    }

    /// <summary>
    /// Generates a Swish QR (PNG bytes) from a Swish URL with a centered logo.
    /// </summary>
    /// <param name="swishUrl">Swish URL generated by GetSwishUrl method</param>
    /// <param name="pixelsPerModule">QR code pixel size</param>
    /// <param name="iconSizePercent">Logo size percentage</param>
    /// <param name="iconBorderWidth">Logo border width</param>
    /// <returns>PNG bytes</returns>
    public static byte[] GeneratePngFromUrl(string swishUrl,
        int pixelsPerModule = 20,
        int iconSizePercent = 25,
        int iconBorderWidth = 6)
    {
        var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(swishUrl, QRCodeGenerator.ECCLevel.Q);

        // Load the logo (any format supported by ImageSharp)
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Swish Logo.png");
        if (!File.Exists(logoPath))
        {
            throw new FileNotFoundException("Swish logo not found.", logoPath);
        }

        using var icon = Image.Load<Rgba32>(logoPath);

        var qr = new QRCoder.QRCode(data);

        using var img = qr.GetGraphic(
            pixelsPerModule: pixelsPerModule,
            darkColor: Color.Black,
            lightColor: Color.White,
            icon: icon,
            iconSizePercent: iconSizePercent,
            iconBorderWidth: iconBorderWidth,
            drawQuietZones: true
        );

        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder()); 
        return ms.ToArray();
    }

    internal static string NormalizePhone(string input)
    {
        // Remove spaces and country code if someone passes +46
        var s = new string((input ?? string.Empty).Where(char.IsDigit).ToArray());
        // If it starts with 46 and then 7..., convert to 0 + rest
        if (s.StartsWith("46") && s.Length >= 11 && s[2] == '7')
        {
            s = "0" + s[2..];
        }

        return s;
    }

    internal static bool IsAmountOk(string amount)
    {
        // Must be dot as decimal and two decimals
        if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            return false;
        }

        // Preserve exact two decimals formatting
        var formatted = d.ToString("0.00", CultureInfo.InvariantCulture);
        return string.Equals(formatted, amount, StringComparison.Ordinal);
    }
}
