namespace HpskSite.Models
{
    /// <summary>
    /// Medlemstypens fasta NYCKLAR. De sju standardnycklarna är exakt den text som redan ligger i
    /// <c>ClubMembership.MembershipType</c>, så ingen medlem behöver migreras. En klubb kan senare ge en
    /// nyckel ett eget NAMN (steg 2); nyckeln ändras aldrig.
    ///
    /// <para>⚠️ Jämför alltid med <see cref="Same"/>/<see cref="IsKey"/> (skiftlägesokänsligt) och skriv aldrig
    /// en typ som strängliteral — gå genom konstanterna här. Familj är enda typen med särregel
    /// (hushållet betalar via huvudmedlemmen) och hänger på nyckeln.</para>
    /// </summary>
    public static class MembershipTypeKeys
    {
        public const string Senior = "Senior";
        public const string Pensionar = "Pensionär";
        public const string Junior = "Junior";
        public const string Familj = "Familj";
        public const string Hedersmedlem = "Hedersmedlem";
        public const string Standig = "Ständig";
        public const string Stodjande = "Stödjande";

        /// <summary>Prefix för klubbens egna extra typer (nyckeln genereras, härleds aldrig ur namnet).</summary>
        public const string CustomPrefix = "x-";

        /// <summary>De sju standardnycklarna i fast visningsordning.</summary>
        public static readonly IReadOnlyList<string> Standard = new[]
        {
            Senior, Pensionar, Junior, Familj, Hedersmedlem, Standig, Stodjande
        };

        private static string? Trim(string? s) => s?.Trim();

        /// <summary>Samma typ? Skiftlägesokänsligt, tomma ytterkanter ignoreras. Två tomma är INTE samma.</summary>
        public static bool Same(string? a, string? b)
        {
            a = Trim(a); b = Trim(b);
            return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsKey(string? type, string key) => Same(type, key);

        public static bool IsFamilj(string? type) => Same(type, Familj);

        /// <summary>Standardnyckeln i sin kanoniska stavning, annars null (egen typ eller okänd text).</summary>
        public static string? StandardKey(string? type)
        {
            foreach (var k in Standard)
                if (Same(type, k)) return k;
            return null;
        }

        public static bool IsStandard(string? type) => StandardKey(type) != null;

        public static bool IsCustom(string? type)
        {
            var t = Trim(type);
            return !string.IsNullOrEmpty(t) && t.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Får medlemmen själv välja typen på betalsidan som förval? AV för Familj (klubben kopplar ihop
        /// hushållet), Hedersmedlem och Ständig (avgiftsfria typer ska inte gå att välja sig till).
        /// Allt annat — även egna typer och okänd text — är PÅ. Avgörs på nyckeln, inte på namnprefix.
        /// </summary>
        public static bool DefaultMemberSelectable(string? type)
        {
            var key = StandardKey(type);
            return key is not (Familj or Hedersmedlem or Standig);
        }
    }
}
