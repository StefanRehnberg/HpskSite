namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Vilka grenar som använder "Utanför mästerskapet". EN lista, en sajtinställning (<c>SiteSetting</c> <c>OutOfCompetition:Disciplines</c>), så att en gren
    /// kan släppas in — eller stängas av — utan deploy (Stefan 2026-11-06: precisionsfamiljen släpps in med en inställning, och en nödbroms ska gå att slå på
    /// en tävlingsdag). Förvalet är det som gällde före inställningen: <c>Faltskytte</c> och <c>MagnumFalt</c>.
    ///
    /// <para>Värdet är en kommaseparerad lista med grenarnas namn (<see cref="Known"/>). Tomt eller saknad rad = <see cref="Default"/>; <c>off</c> eller <c>none</c> =
    /// inga grenar alls (funktionen av överallt); okända namn ignoreras och loggas. Läses av <c>OutOfCompetitionDisciplinesHostedService</c> var 20:e sekund
    /// (inställningens egen cache är 60 s) och gäller i hela processen; ett läsfel lämnar den senast kända listan orörd.</para>
    ///
    /// <para>⚠️ <b>Släpp aldrig in en gren halvvägs.</b> Grenen får läggas i listan först när dess resultat-, medalj-, final-, lag- och ytor är klara och
    /// bevisade (golden-master + bild). Springskytte och skidskytte ingår inte i <see cref="Known"/> — de har egna resultatvägar som inte är gjorda.</para>
    /// </summary>
    public static class OutOfCompetitionDisciplines
    {
        public const string SettingKey = "OutOfCompetition:Disciplines";

        /// <summary>Förvalet: fältfamiljen, som var först.</summary>
        public static readonly IReadOnlyList<string> Default = new[] { "Faltskytte", "MagnumFalt" };

        /// <summary>Grenarna som KAN släppas in. Namnen är de kanoniska (<see cref="ChampionshipRule"/>).</summary>
        public static readonly IReadOnlyList<string> Known = new[]
        {
            "Faltskytte", "MagnumFalt",
            "Precision", "Milsnabb", "Duell", "NationellHelmatch", "MagnumPrecision", "Standardpistol", "Sportpistol"
        };

        private static volatile HashSet<string> _current = new(Default, StringComparer.OrdinalIgnoreCase);

        /// <summary>De grenar som gäller just nu.</summary>
        public static IReadOnlyCollection<string> Current => _current;

        /// <summary>Gäller beteendet den här grenen? (Kanoniskt grennamn, skiftlägesokänsligt.)</summary>
        public static bool Allows(string? discipline) => !string.IsNullOrEmpty(discipline) && _current.Contains(discipline);

        /// <summary>Tolkar inställningens värde. Rent — rör ingenting.</summary>
        public static (HashSet<string> Disciplines, List<string> Ignored) Parse(string? raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ignored = new List<string>();
            var text = (raw ?? "").Trim();
            if (text.Length == 0)
                return (new HashSet<string>(Default, StringComparer.OrdinalIgnoreCase), ignored);
            if (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
                return (set, ignored);

            foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var known = Known.FirstOrDefault(k => k.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (known != null) set.Add(known); else ignored.Add(part);
            }
            return (set, ignored);
        }

        /// <summary>Gör värdet gällande. Returnerar sant när listan ändrades.</summary>
        public static bool Apply(string? raw, out List<string> ignored)
        {
            var (parsed, ign) = Parse(raw);
            ignored = ign;
            var changed = !parsed.SetEquals(_current);
            if (changed) _current = parsed;
            return changed;
        }
    }
}
