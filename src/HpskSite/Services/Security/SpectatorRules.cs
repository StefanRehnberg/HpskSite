namespace HpskSite.Services.Security
{
    /// <summary>
    /// Regler för åskådarlistan i träningsmatchens hubb (säkerhetsplanen 2026-11-02, A7/H3).
    ///
    /// <para>Hubben tog förut åskådarens id, namn, bild och klubb som PARAMETRAR från klienten och skickade dem vidare
    /// till alla som följer matchen — där skrevs de in med <c>innerHTML</c>. Vem som helst kunde alltså lägga in
    /// <c>"&gt;&lt;img src=x onerror=…&gt;</c> som sitt namn och köra skript hos arrangören. Nu läses identiteten på
    /// servern ur inloggningen eller gästkakan; det som ändå går ut som text rensas här, och listorna har tak.</para>
    /// </summary>
    public static class SpectatorRules
    {
        public const int MaxSpectatorsPerMatch = 300;
        public const int MaxTrackedMatches = 2000;
        public const int MaxNameLength = 80;
        public const int MaxPictureUrlLength = 300;

        /// <summary>Namn som visas: trimmat, utan styrtecken, högst <see cref="MaxNameLength"/> tecken.</summary>
        public static string CleanName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Åskådare";
            var chars = name.Where(c => !char.IsControl(c)).ToArray();
            var cleaned = new string(chars).Trim();
            if (cleaned.Length == 0) return "Åskådare";
            return cleaned.Length > MaxNameLength ? cleaned.Substring(0, MaxNameLength) : cleaned;
        }

        /// <summary>
        /// Bildadress som får visas: bara en sajt-relativ sökväg (<c>/media/…</c>) eller en https-adress, utan tecken som kan
        /// bryta ut ur ett attribut. Allt annat blir tomt (klienten visar då en standardbild).
        /// </summary>
        public static string CleanPictureUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var u = url.Trim();
            if (u.Length > MaxPictureUrlLength) return "";
            if (u.IndexOfAny(new[] { '"', '\'', '<', '>', '`', ' ', '\\' }) >= 0) return "";
            if (u.Any(char.IsControl)) return "";
            if (u.StartsWith("//")) return "";                 // protokollrelativ = en annan värd
            if (u.StartsWith("/")) return u;
            return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : "";
        }

        /// <summary>Klubbnamn som visas: samma rensning som namn men utan standardtext.</summary>
        public static string CleanClub(string? club)
        {
            if (string.IsNullOrWhiteSpace(club)) return "";
            var cleaned = new string(club.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return cleaned.Length > MaxNameLength ? cleaned.Substring(0, MaxNameLength) : cleaned;
        }
    }
}
