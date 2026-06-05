namespace HpskSite.Services.AiChat
{
    public class AiChatOptions
    {
        /// <summary>
        /// Enable or disable the AI chat feature
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// AI provider: "Mistral", "OpenAI", "Azure", "Claude", or "Gemini".
        /// "Mistral" = Mistral AI (EU-hosted, no third-country transfer) — OpenAI-compatible, set <see cref="Endpoint"/>.
        /// "Azure" = Azure OpenAI in an EU region (no third-country transfer); also requires <see cref="Endpoint"/>.
        /// Any provider other than azure/claude/gemini uses the OpenAI-compatible path (Bearer auth, model in body).
        /// </summary>
        public string Provider { get; set; } = "OpenAI";

        /// <summary>
        /// API key for the selected provider
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// Full chat-completions endpoint URL. Required for Mistral and Azure; optional for OpenAI (defaults to api.openai.com).
        /// Mistral (EU): https://api.mistral.ai/v1/chat/completions
        /// Azure (EU region): https://{resource}.openai.azure.com/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21
        /// </summary>
        public string Endpoint { get; set; } = "";

        /// <summary>
        /// Model identifier (e.g. "gpt-4.1-mini", "claude-haiku-4-5-20251001", "gemini-2.0-flash")
        /// </summary>
        public string Model { get; set; } = "gpt-4.1-mini";

        /// <summary>
        /// Max tokens in the AI response
        /// </summary>
        public int MaxTokens { get; set; } = 1024;

        /// <summary>
        /// Temperature (0-1). Lower = more deterministic
        /// </summary>
        public double Temperature { get; set; } = 0.3;
    }
}
