using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HpskSite.Services.AiChat
{
    public class AiChatService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AiChatOptions _options;
        private readonly KnowledgeBaseService _knowledgeBase;
        private readonly string _apiKey;

        // Only send the last N messages as conversation context to keep token usage low
        private const int MaxHistoryMessages = 10;

        public AiChatService(IHttpClientFactory httpClientFactory, IOptions<AiChatOptions> options, KnowledgeBaseService knowledgeBase, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _knowledgeBase = knowledgeBase;
            _apiKey = configuration["AiChat:ApiKey"] ?? "";
        }

        public bool IsEnabled => _options.Enabled && !string.IsNullOrEmpty(_apiKey);

        public async Task<string> GetResponseAsync(string userMessage, List<ChatMessage> conversationHistory, List<string> userRoles)
        {
            var systemPrompt = _knowledgeBase.GetSystemPrompt(userRoles);
            var knowledgeBase = _knowledgeBase.GetFilteredKnowledgeBase(userRoles);
            var fullSystemPrompt = $"{systemPrompt}\n\n## Kunskapsbas\n\n{knowledgeBase}";

            // Trim history to keep costs down
            var trimmedHistory = conversationHistory.Count > MaxHistoryMessages
                ? conversationHistory.Skip(conversationHistory.Count - MaxHistoryMessages).ToList()
                : conversationHistory;

            return _options.Provider.ToLowerInvariant() switch
            {
                "claude" => await CallClaudeAsync(fullSystemPrompt, userMessage, trimmedHistory),
                "gemini" => await CallGeminiAsync(fullSystemPrompt, userMessage, trimmedHistory),
                "azure" => await CallAzureOpenAiAsync(fullSystemPrompt, userMessage, trimmedHistory),
                // Mistral (EU) is OpenAI-compatible (Bearer auth, model in body) — same path,
                // just a different Endpoint. Any unknown provider also falls through to here.
                _ => await CallOpenAiAsync(fullSystemPrompt, userMessage, trimmedHistory),
            };
        }

        /// <summary>
        /// Azure OpenAI (EU region). Same request/response shape as OpenAI's chat-completions API,
        /// but the deployment lives in the URL (<see cref="AiChatOptions.Endpoint"/>) and auth uses
        /// the "api-key" header instead of a bearer token. Keeps all data within EU/EES.
        /// </summary>
        private async Task<string> CallAzureOpenAiAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
        {
            if (string.IsNullOrWhiteSpace(_options.Endpoint))
                throw new Exception("Azure OpenAI: AiChat:Endpoint is not configured.");

            var messages = new List<object> { new { role = "system", content = systemPrompt } };
            foreach (var msg in history)
                messages.Add(new { role = msg.Role, content = msg.Content });
            messages.Add(new { role = "user", content = userMessage });

            // Azure takes the deployment from the URL, so no "model" field is needed in the body.
            var payload = new
            {
                messages,
                max_tokens = _options.MaxTokens,
                temperature = _options.Temperature
            };

            var client = _httpClientFactory.CreateClient("AiChat");
            var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            request.Headers.Add("api-key", _apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Azure OpenAI API error: {response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }

        private async Task<string> CallOpenAiAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
        {
            var messages = new List<object> { new { role = "system", content = systemPrompt } };
            foreach (var msg in history)
                messages.Add(new { role = msg.Role, content = msg.Content });
            messages.Add(new { role = "user", content = userMessage });

            var payload = new
            {
                model = _options.Model,
                messages,
                max_tokens = _options.MaxTokens,
                temperature = _options.Temperature
            };

            var client = _httpClientFactory.CreateClient("AiChat");
            // Endpoint override lets this same path serve OpenAI-compatible providers (e.g. Mistral EU)
            // without a separate method. Defaults to OpenAI when Endpoint is not configured.
            var url = string.IsNullOrWhiteSpace(_options.Endpoint)
                ? "https://api.openai.com/v1/chat/completions"
                : _options.Endpoint;
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"OpenAI API error: {response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }

        private async Task<string> CallClaudeAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
        {
            var messages = new List<object>();
            foreach (var msg in history)
                messages.Add(new { role = msg.Role, content = msg.Content });
            messages.Add(new { role = "user", content = userMessage });

            var payload = new
            {
                model = _options.Model,
                system = systemPrompt,
                messages,
                max_tokens = _options.MaxTokens,
                temperature = _options.Temperature
            };

            var client = _httpClientFactory.CreateClient("AiChat");
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[AiChat] Claude API error {response.StatusCode}: {json}");
                throw new Exception($"Claude API error: {response.StatusCode} - {json}");
            }

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? "";
        }

        private async Task<string> CallGeminiAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
        {
            var contents = new List<object>();

            foreach (var msg in history)
            {
                contents.Add(new
                {
                    role = msg.Role == "assistant" ? "model" : "user",
                    parts = new[] { new { text = msg.Content } }
                });
            }
            contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

            var payload = new
            {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents,
                generationConfig = new
                {
                    maxOutputTokens = _options.MaxTokens,
                    temperature = _options.Temperature
                }
            };

            var client = _httpClientFactory.CreateClient("AiChat");
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_options.Model}:generateContent?key={_apiKey}";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Gemini API error: {response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";
        }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
