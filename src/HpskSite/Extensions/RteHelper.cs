using System.Text.Json;

namespace HpskSite.Extensions
{
    /// <summary>
    /// Helper for extracting HTML markup from Umbraco Rich Text Editor values.
    /// Umbraco v13+ stores RTE as JSON: {"markup":"<p>text</p>","blocks":{...}}
    /// This helper extracts the markup string, or returns the value as-is if it's plain HTML.
    /// </summary>
    public static class RteHelper
    {
        public static string ExtractMarkup(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (!value.TrimStart().StartsWith("{")) return value;

            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.TryGetProperty("markup", out var markup))
                    return markup.GetString() ?? "";
            }
            catch { }

            return value;
        }
    }
}
