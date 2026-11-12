using System.Text.RegularExpressions;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Personnummerspärren för klubbens och kretsens egna sidor (klubbsidorna steg 2, plan v3 D1). Körs vid VARJE sparning
    /// (autospar, utkast, versioner) över alla textfält inklusive titlar och menyetiketter, oavsett synlighet.
    ///
    /// <para>Mönstret är medvetet snävt så att telefon-, bank- och plusgironummer inte träffas: <c>ÅÅMMDD-NNNN</c> (eller med
    /// <c>+</c> för 100 år) och <c>ÅÅÅÅMMDD-NNNN</c>, samt tolv sammanhängande siffror <c>ÅÅÅÅMMDDNNNN</c>, med ett plausibelt
    /// datum (månad 01–12, dag 01–31). Tio siffror utan bindestreck räknas inte (det är lika ofta ett telefonnummer).
    /// Luhn prövas inte: spärren ska hellre varna en gång för mycket (admin kan välja "Det är inget personnummer") än släppa
    /// igenom ett felskrivet.</para>
    ///
    /// <para>Ren funktion.</para>
    /// </summary>
    public static class SitePersonalNumberGuard
    {
        // (ÅÅ eller ÅÅÅÅ)(MM)(DD) - (NNNN)  |  ÅÅÅÅMMDDNNNN   — inte omgivet av fler siffror.
        private static readonly Regex WithHyphen = new(
            @"(?<![\d])(?:19|20)?\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])[-+]\d{4}(?![\d])", RegexOptions.Compiled);
        private static readonly Regex TwelveDigits = new(
            @"(?<![\d])(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{4}(?![\d])", RegexOptions.Compiled);

        /// <summary>Det första som ser ut som ett personnummer i <paramref name="text"/>, eller null.</summary>
        public static string? Find(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = WithHyphen.Match(text);
            if (m.Success) return m.Value;
            m = TwelveDigits.Match(text);
            return m.Success ? m.Value : null;
        }

        /// <summary>HTML till ren text för sökningen (taggar bort, entiteter avkodade), så att ett nummer inte göms av markup.</summary>
        public static string PlainText(string? html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            // Blocktaggar skiljer ord ("<p>1</p><p>2</p>" är två stycken), inline-taggar gör det inte ("<strong>1948</strong>0312" är ett nummer).
            var blocks = Regex.Replace(html, @"</?(?:p|br|li|ul|ol|td|th|tr|table|thead|tbody|h[1-6]|div|blockquote|hr)\b[^>]*>", " ", RegexOptions.IgnoreCase);
            var noTags = Regex.Replace(blocks, "<[^>]*>", "");
            return System.Net.WebUtility.HtmlDecode(noTags);
        }

        /// <summary>Första träffen bland <paramref name="texts"/> (varje text kan vara HTML), eller null.</summary>
        public static string? FindInAny(IEnumerable<string?> texts)
        {
            foreach (var t in texts)
            {
                var hit = Find(PlainText(t));
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
