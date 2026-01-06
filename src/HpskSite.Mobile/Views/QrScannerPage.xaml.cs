using ZXing.Net.Maui;

namespace HpskSite.Mobile.Views;

public partial class QrScannerPage : ContentPage
{
    private bool _isProcessing;

    public QrScannerPage()
    {
        InitializeComponent();

        // Configure barcode reader options
        barcodeReader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormat.QrCode,
            AutoRotate = true,
            Multiple = false
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isProcessing = false;
        barcodeReader.IsDetecting = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        barcodeReader.IsDetecting = false;
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_isProcessing || e.Results == null || e.Results.Length == 0)
            return;

        _isProcessing = true;

        var result = e.Results[0];
        var scannedValue = result.Value;

        // Stop detecting while processing
        barcodeReader.IsDetecting = false;

        // Update UI on main thread
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            statusLabel.Text = $"Hittade: {scannedValue}";

            // Extract match code from QR content
            // QR code might contain just the code, or a URL with the code
            var matchCode = ExtractMatchCode(scannedValue);

            if (!string.IsNullOrEmpty(matchCode))
            {
                // Navigate back with the match code
                await Shell.Current.GoToAsync($"..?scannedCode={matchCode}");
            }
            else
            {
                // Invalid QR code
                await DisplayAlert("Ogiltig QR-kod",
                    "QR-koden innehaller ingen giltig matchkod.",
                    "OK");

                // Resume scanning
                _isProcessing = false;
                barcodeReader.IsDetecting = true;
                statusLabel.Text = "Skannar...";
            }
        });
    }

    private string? ExtractMatchCode(string scannedValue)
    {
        if (string.IsNullOrWhiteSpace(scannedValue))
            return null;

        // If it's a URL, try to extract the code from query parameters
        // QR codes contain URLs like: https://hpsktest.se/traningsmatch/?join=ABC123
        if (scannedValue.StartsWith("http://") || scannedValue.StartsWith("https://"))
        {
            try
            {
                var uri = new Uri(scannedValue);

                // Check query parameters - 'join' is the main parameter used
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var code = query["join"] ?? query["code"] ?? query["matchCode"] ?? query["c"];

                if (!string.IsNullOrEmpty(code))
                {
                    // Validate it looks like a match code (6 alphanumeric characters)
                    var cleaned = code.Trim().ToUpperInvariant();
                    if (cleaned.Length == 6 && cleaned.All(char.IsLetterOrDigit))
                    {
                        return cleaned;
                    }
                    return code.Trim().ToUpperInvariant();
                }

                // Check if the code is in the path (e.g., /join/ABC123)
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                {
                    var lastSegment = segments[^1].ToUpperInvariant();
                    // Match code is exactly 6 alphanumeric characters
                    if (lastSegment.Length == 6 && lastSegment.All(char.IsLetterOrDigit))
                    {
                        return lastSegment;
                    }
                }
            }
            catch
            {
                // Invalid URL, fall through
            }
        }

        // If it's just a plain code (6 alphanumeric characters), return it directly
        var trimmed = scannedValue.Trim().ToUpperInvariant();
        if (trimmed.Length == 6 && trimmed.All(char.IsLetterOrDigit))
        {
            return trimmed;
        }

        return null;
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
