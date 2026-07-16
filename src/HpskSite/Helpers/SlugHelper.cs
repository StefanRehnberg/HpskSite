using System.Text;
using System.Text.RegularExpressions;

namespace HpskSite.Helpers
{
    /// <summary>
    /// Produces stable, URL-safe slugs from human labels. Swedish-aware (å/ä→a, ö→o).
    /// Used to build shareable Springskytte start-list URLs (/startlista/{compId}/{slug})
    /// from a list's name, replacing the fragile name-derived Umbraco child-node URL.
    /// </summary>
    public static class SlugHelper
    {
        public static string Slugify(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var sb = new StringBuilder();
            foreach (var raw in input.Trim().ToLowerInvariant())
            {
                switch (raw)
                {
                    case 'å': case 'ä': sb.Append('a'); break;
                    case 'ö': sb.Append('o'); break;
                    case 'é': case 'è': case 'ê': sb.Append('e'); break;
                    case 'ü': sb.Append('u'); break;
                    case ' ': case '_': case '-': case '/': sb.Append('-'); break;
                    default:
                        // Keep ASCII letters/digits only; drop anything else (incl. other non-ASCII).
                        if ((raw >= 'a' && raw <= 'z') || (raw >= '0' && raw <= '9')) sb.Append(raw);
                        break;
                }
            }
            return Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        }
    }
}
