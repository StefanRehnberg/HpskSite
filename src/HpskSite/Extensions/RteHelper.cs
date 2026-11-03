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
        /// <summary>ENDA vägen ut för RTE-HTML till en sida: plockar ut markup och saneras med vitlistan.</summary>
        public static Microsoft.AspNetCore.Html.IHtmlContent SafeHtml(string? value) =>
            new Microsoft.AspNetCore.Html.HtmlString(HpskHtmlSanitizer.Sanitize(ExtractMarkup(value)));

        /// <summary>Markup efter sanering, som text.</summary>
        public static string SafeMarkup(string? value) => HpskHtmlSanitizer.Sanitize(ExtractMarkup(value));

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
