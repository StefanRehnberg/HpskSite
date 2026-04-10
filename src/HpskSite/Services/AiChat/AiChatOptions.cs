namespace HpskSite.Services.AiChat
{
    public class AiChatOptions
    {
        /// <summary>
        /// Enable or disable the AI chat feature
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// AI provider: "OpenAI", "Claude", or "Gemini"
        /// </summary>
        public string Provider { get; set; } = "OpenAI";

        /// <summary>
        /// API key for the selected provider
        /// </summary>
        public string ApiKey { get; set; } = "";

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
