using System.Text;
using System.Text.Json;

namespace HpskSite.Services
{
    public class BrevoEmailService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<BrevoEmailService> _logger;
        private const string BrevoApiUrl = "https://api.brevo.com/v3";

        public BrevoEmailService(IHttpClientFactory httpClientFactory, ILogger<BrevoEmailService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Validate a Brevo API key by calling the account endpoint.
        /// </summary>
        public async Task<(bool IsValid, string? AccountName)> ValidateApiKeyAsync(string apiKey)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("Brevo");
                var request = new HttpRequestMessage(HttpMethod.Get, $"{BrevoApiUrl}/account");
                request.Headers.Add("api-key", apiKey);

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return (false, null);

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var name = doc.RootElement.TryGetProperty("companyName", out var prop)
                    ? prop.GetString()
                    : "OK";

                return (true, name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to validate Brevo API key");
                return (false, null);
            }
        }

        /// <summary>
        /// Send a single email via Brevo API.
        /// </summary>
        public async Task<bool> SendEmailAsync(string apiKey, string fromEmail, string fromName,
            string toEmail, string toName, string subject, string htmlBody)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("Brevo");
                var payload = new
                {
                    sender = new { name = fromName, email = fromEmail },
                    to = new[] { new { email = toEmail, name = toName } },
                    subject = subject,
                    htmlContent = htmlBody
                };

                var request = new HttpRequestMessage(HttpMethod.Post, $"{BrevoApiUrl}/smtp/email");
                request.Headers.Add("api-key", apiKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Brevo email sent to {Email}", toEmail);
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Brevo API error {StatusCode}: {Body}", response.StatusCode, errorBody);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Brevo email to {Email}", toEmail);
                return false;
            }
        }

        /// <summary>
        /// Send emails to multiple recipients via Brevo API (one per recipient).
        /// Returns (sentCount, failedCount).
        /// </summary>
        public async Task<(int Sent, int Failed)> SendBulkEmailAsync(string apiKey, string fromEmail,
            string fromName, List<(string Email, string Name)> recipients, string subject, string htmlBody)
        {
            int sent = 0, failed = 0;

            foreach (var (email, name) in recipients)
            {
                var success = await SendEmailAsync(apiKey, fromEmail, fromName, email, name, subject, htmlBody);
                if (success)
                    sent++;
                else
                    failed++;

                // Small delay to respect rate limits
                if (sent + failed < recipients.Count)
                    await Task.Delay(100);
            }

            _logger.LogInformation("Brevo bulk send: {Sent} sent, {Failed} failed out of {Total}",
                sent, failed, recipients.Count);

            return (sent, failed);
        }
    }
}
