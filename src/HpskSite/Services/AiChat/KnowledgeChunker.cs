using System.Security.Cryptography;
using System.Text;

namespace HpskSite.Services.AiChat
{
    /// <summary>
    /// Ett stycke av kunskapsbasen — det som söks fram och skickas med en fråga.
    /// </summary>
    /// <param name="FileName">Dokumentets filnamn.</param>
    /// <param name="DocTitle">Dokumentets titel ("# "-rubriken).</param>
    /// <param name="Heading">Rubrikvägen inom dokumentet, t.ex. "Betalning › Swish". Tom för inledningen.</param>
    /// <param name="Body">Styckets text, utan rubrikraden.</param>
    /// <param name="Roles">Dokumentets roller. Ett stycke ärver alltid sitt dokuments roller.</param>
    /// <param name="Order">Styckets plats i dokumentet, så urvalet kan skrivas ut i läsordning.</param>
    public sealed record KnowledgeChunk(
        string FileName, string DocTitle, string Heading, string Body,
        IReadOnlyList<string> Roles, int Order)
    {
        /// <summary>"Titel › Rubrik › Underrubrik" — vad som loggas och visas för modellen.</summary>
        public string Path => Heading.Length == 0 ? DocTitle : DocTitle + " › " + Heading;

        /// <summary>
        /// Texten som görs om till en vektor. Rubrikvägen ingår: ett stycke som bara säger "Klicka
        /// på knappen" betyder ingenting utan att man vet att det står under "Efteranmälan".
        /// </summary>
        public string EmbeddingText => Path + "\n\n" + Body;

        /// <summary>
        /// Nyckeln i vektorcachen: modellen + exakt den text som görs om till en vektor.
        ///
        /// <para><b>⚠️ Innehållet ÄR nyckeln, inte filnamn och position.</b> Ett ändrat stycke får
        /// en ny nyckel och räknas om; ett oförändrat stycke behåller sin vektor även om stycken
        /// före det lagts till eller tagits bort. Därför räknas bara det som faktiskt ändrats om,
        /// hur ofta kunskapsbasen än uppdateras.</para>
        /// </summary>
        public string Hash(string embeddingModel)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(embeddingModel + "\n" + EmbeddingText));
            return Convert.ToHexString(bytes);
        }
    }

    /// <summary>
    /// Delar ett kunskapsbasdokument i stycken efter dess rubriker.
    ///
    /// <para>Gränsen går vid "## " och "### ". Djupare rubriker stannar i sitt stycke — de är
    /// oftast punkter i samma förklaring, och ett stycke per "####" blev för smått för att bära
    /// sitt sammanhang. Rubriker inuti kodblock räknas inte.</para>
    /// </summary>
    public static class KnowledgeChunker
    {
        /// <summary>Ett stycke längre än så delas vid tomrader.</summary>
        public const int MaxChunkChars = 3000;

        public static List<KnowledgeChunk> Split(KnowledgeBaseDoc doc)
        {
            var chunks = new List<KnowledgeChunk>();
            var title = doc.Title;
            string? h2 = null, h3 = null;
            var body = new StringBuilder();
            var inFence = false;
            var order = 0;

            void Flush()
            {
                var text = body.ToString().Trim();
                body.Clear();
                if (text.Length == 0) return;

                var heading = h2 == null ? (h3 ?? "") : (h3 == null ? h2 : h2 + " › " + h3);
                foreach (var piece in SplitLong(text))
                    chunks.Add(new KnowledgeChunk(doc.FileName, title, heading, piece, doc.Roles, order++));
            }

            foreach (var raw in doc.Content.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    body.Append(line).Append('\n');
                    continue;
                }

                if (!inFence)
                {
                    if (line.StartsWith("# ", StringComparison.Ordinal))
                    {
                        // Titeln — den bärs redan av varje stycke via DocTitle.
                        Flush();
                        continue;
                    }
                    if (line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        Flush();
                        h2 = line.Substring(3).Trim();
                        h3 = null;
                        continue;
                    }
                    if (line.StartsWith("### ", StringComparison.Ordinal))
                    {
                        Flush();
                        h3 = line.Substring(4).Trim();
                        continue;
                    }
                }

                // ⚠️ Append('\n'), aldrig AppendLine: den ger "\r\n" på Windows, och då hittar
                // SplitLong inga tomrader att dela vid.
                body.Append(line).Append('\n');
            }

            Flush();
            return chunks;
        }

        /// <summary>
        /// Delar en för lång text vid tomrader, och en enskild för lång paragraf vid radbrytningar.
        /// Ingen text tappas: summan av bitarna är hela texten.
        /// </summary>
        internal static IEnumerable<string> SplitLong(string text)
        {
            if (text.Length <= MaxChunkChars)
            {
                yield return text;
                yield break;
            }

            var current = new StringBuilder();
            foreach (var paragraph in text.Split("\n\n"))
            {
                foreach (var part in HardSplit(paragraph))
                {
                    if (current.Length > 0 && current.Length + part.Length + 2 > MaxChunkChars)
                    {
                        yield return current.ToString().Trim();
                        current.Clear();
                    }
                    if (current.Length > 0) current.Append("\n\n");
                    current.Append(part);
                }
            }
            if (current.ToString().Trim().Length > 0)
                yield return current.ToString().Trim();
        }

        private static IEnumerable<string> HardSplit(string paragraph)
        {
            if (paragraph.Length <= MaxChunkChars)
            {
                yield return paragraph;
                yield break;
            }

            var current = new StringBuilder();
            foreach (var line in paragraph.Split('\n'))
            {
                if (current.Length > 0 && current.Length + line.Length + 1 > MaxChunkChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                if (current.Length > 0) current.Append('\n');
                current.Append(line);
            }
            if (current.Length > 0) yield return current.ToString();
        }
    }
}
