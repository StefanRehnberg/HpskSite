using System.Text;
using System.Text.RegularExpressions;
using HpskSite.Services.Addresses;

namespace HpskSite.Services.SitePages
{
    /// <summary>Utfallet av <see cref="SitePageSlugRules.Check"/>.</summary>
    public sealed record SlugCheck(bool Ok, string? Reason)
    {
        public static readonly SlugCheck Free = new(true, null);
        public static SlugCheck Taken(string reason) => new(false, reason);
    }

    /// <summary>
    /// EN valideringsfunktion för adressen (sluggen) till en egen sida hos en klubb eller krets (klubbsidorna steg 2, plan v2 §2).
    /// En egen sidas adress är <c>/{ägare}/{slug}/</c> och delar namnrymd med ägarens systemsidor, sortsegmenten, årtal,
    /// händelsernas gamla trädadresser, ägarens barnnoder och — hos en krets — kretsens klubbar (<c>/{krets}/{klubb}/</c>).
    /// Körs vid skapande, namnbyte av sida och (åt andra hållet) vid skapande/namnbyte av klubb.
    ///
    /// <para>Ren funktion: databasens svar (redan tagna adresser, barnnoder, klubbar) skickas in av tjänsten.</para>
    /// </summary>
    public static class SitePageSlugRules
    {
        public const int MaxLength = 60;

        private static readonly Regex Shape = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

        /// <summary>Segment som aldrig är en egen sida, oavsett ägare (redigeringsytan och sajtens egna adresser).</summary>
        private static readonly string[] AlwaysReserved = { "hemsidan", "admin", "api", "umbraco", "media", "css", "js", "images", "login-register" };

        /// <summary>
        /// Gör en titel till ett slugförslag: gemener, å/ä/ö blir a/a/o, allt utom a–z, 0–9 blir bindestreck, kortas till
        /// <see cref="MaxLength"/>. Tom titel ger tom sträng (anroparen frågar efter ett annat namn).
        /// </summary>
        public static string SlugFrom(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var sb = new StringBuilder(title.Length);
            foreach (var ch in title.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC))
            {
                sb.Append(ch switch
                {
                    'å' or 'ä' or 'à' or 'á' or 'â' or 'ã' => 'a',
                    'ö' or 'ò' or 'ó' or 'ô' or 'õ' or 'ø' => 'o',
                    'é' or 'è' or 'ê' or 'ë' => 'e',
                    'ü' or 'ú' or 'ù' or 'û' => 'u',
                    'í' or 'ì' or 'î' or 'ï' => 'i',
                    'ç' => 'c',
                    'ñ' => 'n',
                    >= 'a' and <= 'z' => ch,
                    >= '0' and <= '9' => ch,
                    _ => '-'
                });
            }
            var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('-');
            return slug;
        }

        /// <summary>
        /// Får <paramref name="slug"/> användas för en egen sida hos en ägare av typen <paramref name="ownerType"/>?
        /// </summary>
        /// <param name="takenAddressSlugs">Ägarens adresser utan år, av ALLA sorter (händelsernas trädadresser m.fl.) och andra egna sidor.</param>
        /// <param name="childNodeSegments">Url-segmenten för ägarens barnnoder i trädet.</param>
        /// <param name="regionClubSegments">Hos en krets: url-segmenten för kretsens klubbar. Ignoreras för en klubb.</param>
        public static SlugCheck Check(string? slug, string ownerType,
            IEnumerable<string> takenAddressSlugs, IEnumerable<string> childNodeSegments, IEnumerable<string>? regionClubSegments = null)
        {
            if (string.IsNullOrEmpty(slug)) return SlugCheck.Taken("Adressen får inte vara tom.");
            if (slug.Length > MaxLength) return SlugCheck.Taken($"Adressen får vara högst {MaxLength} tecken.");
            if (!Shape.IsMatch(slug)) return SlugCheck.Taken("Adressen får bara innehålla små bokstäver a–z, siffror och bindestreck.");

            if (AlwaysReserved.Contains(slug, StringComparer.Ordinal)) return SlugCheck.Taken("Det ordet används av sajten.");
            if (PageAddressRules.IsReservedOwnerChildSegment(slug)) return SlugCheck.Taken("Det ordet används i adresserna till tävlingar, händelser och serier.");

            var template = SiteTemplates.For(ownerType);
            if (template.PageBySlug(slug) != null || SiteTemplates.Slugs(ownerType).Contains(slug, StringComparer.OrdinalIgnoreCase))
                return SlugCheck.Taken("Det namnet används redan av en av sidorna i menyn.");

            if (takenAddressSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase)) return SlugCheck.Taken("Det finns redan en sida med den adressen.");
            if (childNodeSegments.Contains(slug, StringComparer.OrdinalIgnoreCase)) return SlugCheck.Taken("Det finns redan en sida med den adressen.");
            if (ownerType == SiteTemplates.Region && regionClubSegments != null
                && regionClubSegments.Contains(slug, StringComparer.OrdinalIgnoreCase))
                return SlugCheck.Taken("En av kretsens klubbar har den adressen.");

            return SlugCheck.Free;
        }

        /// <summary>
        /// Första lediga av <paramref name="baseSlug"/>, <c>-2</c>, <c>-3</c> … för ett förslag som krockar. Ger null om bassluggen
        /// själv är otillåten (t.ex. ett reserverat ord eller fel form) — då ska admin välja ett annat namn, inte få ett nummer.
        /// </summary>
        public static string? Suggest(string baseSlug, string ownerType,
            IEnumerable<string> takenAddressSlugs, IEnumerable<string> childNodeSegments, IEnumerable<string>? regionClubSegments = null)
        {
            var taken = takenAddressSlugs.ToList();
            var children = childNodeSegments.ToList();
            var clubs = regionClubSegments?.ToList();
            if (string.IsNullOrEmpty(baseSlug)) return null;

            var first = Check(baseSlug, ownerType, taken, children, clubs);
            if (first.Ok) return baseSlug;
            // Bara en rent upptagen adress får ett nummer; ett reserverat ord eller en felaktig form gör det inte.
            if (!IsOnlyTaken(baseSlug, ownerType)) return null;
            for (var n = 2; n < 100; n++)
            {
                var candidate = baseSlug.Length + 1 + n.ToString().Length > MaxLength
                    ? baseSlug[..(MaxLength - 1 - n.ToString().Length)].TrimEnd('-') + "-" + n
                    : baseSlug + "-" + n;
                if (Check(candidate, ownerType, taken, children, clubs).Ok) return candidate;
            }
            return null;
        }

        /// <summary>Är sluggen giltig i sig (form, reserverade ord, menyn) så att en krock bara beror på att den är upptagen?</summary>
        private static bool IsOnlyTaken(string slug, string ownerType) =>
            Check(slug, ownerType, Array.Empty<string>(), Array.Empty<string>(), null).Ok;
    }
}
