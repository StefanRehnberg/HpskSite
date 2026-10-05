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

        /// <summary>
        /// Skicka bara de stycken av kunskapsbasen som rör frågan (semantisk sökning) i stället
        /// för hela kunskapsbasen. Gäller OpenAI-kompatibla leverantörer (Mistral, OpenAI); övriga
        /// får hela kunskapsbasen som förut. Faller sökningen skickas hela kunskapsbasen.
        /// </summary>
        public bool Retrieval { get; set; } = true;

        /// <summary>
        /// Embeddings-endpoint. Tom = härleds ur <see cref="Endpoint"/> ("/chat/completions" →
        /// "/embeddings"), eller OpenAI:s om Endpoint också är tom.
        /// </summary>
        public string EmbeddingEndpoint { get; set; } = "";

        /// <summary>Embeddings-modell. Tom = "mistral-embed" mot Mistral, annars "text-embedding-3-small".</summary>
        public string EmbeddingModel { get; set; } = "";

        /// <summary>Högst så många stycken per fråga.</summary>
        public int RetrievalMaxChunks { get; set; } = 10;

        /// <summary>Högst så många tecken kunskapsbas per fråga (~4 tecken per token).</summary>
        public int RetrievalMaxChars { get; set; } = 16000;

        public string ResolveEmbeddingEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(EmbeddingEndpoint)) return EmbeddingEndpoint;
            if (string.IsNullOrWhiteSpace(Endpoint)) return "https://api.openai.com/v1/embeddings";
            var i = Endpoint.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? Endpoint.Substring(0, i) + "/embeddings" : "";
        }

        public string ResolveEmbeddingModel()
        {
            if (!string.IsNullOrWhiteSpace(EmbeddingModel)) return EmbeddingModel;
            return ResolveEmbeddingEndpoint().Contains("mistral", StringComparison.OrdinalIgnoreCase)
                ? "mistral-embed"
                : "text-embedding-3-small";
        }

        /// <summary>Sökningen kräver ett OpenAI-kompatibelt embeddings-API.</summary>
        public bool RetrievalSupported
        {
            get
            {
                var p = Provider.ToLowerInvariant();
                return Retrieval && p != "claude" && p != "gemini" && p != "azure"
                       && ResolveEmbeddingEndpoint().Length > 0;
            }
        }
    }
}
