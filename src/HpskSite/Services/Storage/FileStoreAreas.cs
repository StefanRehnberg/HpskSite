namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Områdena i <see cref="IFileStore"/>. På disk är ett område en katalog: de privata under
    /// <c>App_Data/{område}</c>, de publika under <c>wwwroot/images/{område}</c> (nås på
    /// <c>/images/{område}/…</c>). I blobblagringen ligger de under <c>app-data/</c> respektive
    /// <c>images/</c> i samma behållare, och de publika serveras av <see cref="PublicImageMiddleware"/>.
    ///
    /// <para>⚠️ Namnen är KATALOGNAMN på disk och i blobblagringen — byt aldrig ett namn, då försvinner
    /// filerna ur sajtens synfält.</para>
    /// </summary>
    public static class FileStoreAreas
    {
        // ── Privata (App_Data) — når ut bara genom en grindad controller ──
        public const string StandardMedalProofs = "standard-medal-proofs";
        public const string RangeDocuments = "range-documents";
        public const string PrepDocuments = "competition-prep-docs";
        public const string LedgerAttachments = "ledger-attachments";
        /// <summary>Lektionsmaterialet (läggs upp för hand, serveras genom /innehall).</summary>
        public const string Utbildning = "utbildning";
        /// <summary>AI-chattens vektorcache (härledd, går att bygga om).</summary>
        public const string AiChatIndex = "AiChatIndex";
        // Dokumentarkivets område är konfigurerbart (DocumentArchive:StoragePath, standard "documents").

        // ── Publika bilder (wwwroot/images) — adresserna /images/{område}/… är lagrade i databasen ──
        public const string FieldTargets = "field-targets";
        public const string FaltskytteImages = "faltskytte";
        public const string Faltkonfig = "faltkonfig";
        public const string Uploads = "uploads";

        public static readonly IReadOnlyList<string> PublicAreas =
            new[] { FieldTargets, FaltskytteImages, Faltkonfig, Uploads };

        public static bool IsPublic(string area) =>
            PublicAreas.Contains(area, StringComparer.OrdinalIgnoreCase);

        /// <summary>Adressen till en fil i ett publikt område.</summary>
        public static string PublicUrl(string area, string name) => $"/images/{area}/{name}";
    }

    /// <summary>Regeln för ett namn i lagringen: segment av bokstäver, siffror, punkt, bindestreck, understreck och inre mellanslag.</summary>
    public static class FileStoreNames
    {
        public static bool IsValidArea(string? area) =>
            !string.IsNullOrWhiteSpace(area) && IsValidSegment(area) && area.Length <= 64;

        /// <summary>
        /// ⚠️ Vägrar allt som kan peka ut ur området: tomma segment, '..', bakåtsnedstreck, en inledande
        /// punkt och andra tecken än bokstäver, siffror och <c>. _ -</c>.
        /// </summary>
        public static bool IsValidName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 400) return false;
            var segments = name.Split('/');
            if (segments.Length > 8) return false;
            return segments.All(IsValidSegment);
        }

        private static bool IsValidSegment(string segment)
        {
            // Mellanslag inne i ett namn finns i figurkatalogen ("1-3 bla.jpg") och är ofarligt.
            if (segment.Length == 0 || segment[0] == '.' || segment[0] == ' ' || segment[^1] == ' ') return false;
            foreach (var c in segment)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == ' ')) return false;
            return true;
        }

        /// <summary>
        /// Ett uppladdat filnamn som ett giltigt segment: otillåtna tecken blir bindestreck, tomt blir "fil".
        /// Ändelsen behålls.
        /// </summary>
        public static string SafeFileName(string? original)
        {
            var name = Path.GetFileName(original ?? "");
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray();
            var safe = new string(chars).Trim('.', '-');
            while (safe.Contains("--")) safe = safe.Replace("--", "-");
            return safe.Length == 0 ? "fil" : safe.Length > 120 ? safe.Substring(safe.Length - 120) : safe;
        }

        /// <summary>
        /// En sökväg i mediamappen som går att ta bort/läsa: inga '..'-segment, inga bakåtsnedstreck, inget
        /// kolon. Mildare än <see cref="IsValidName"/>, eftersom äldre mediefiler bär originalets filnamn.
        /// </summary>
        public static bool IsSafeRelativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 400) return false;
            if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/')) return false;
            return path.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
        }

        public static void Require(string area, string name)
        {
            if (!IsValidArea(area)) throw new ArgumentException($"Ogiltigt område: {area}");
            if (!IsValidName(name)) throw new ArgumentException($"Ogiltigt filnamn: {name}");
        }
    }
}
