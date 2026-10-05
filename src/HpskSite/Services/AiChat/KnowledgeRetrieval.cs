using System.Text;

namespace HpskSite.Services.AiChat
{
    /// <summary>
    /// Väljer vilka stycken en fråga får med sig, och skriver ut dem för modellen.
    /// Rena funktioner — ingen databas, inget nätverk — så urvalet går att testa.
    /// </summary>
    public static class KnowledgeRetrieval
    {
        /// <summary>Cosinuslikhet. Noll om någon av vektorerna saknar längd.</summary>
        public static double Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0;
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        /// <summary>
        /// De bäst matchande styckena, inom både ett tak för antal och ett tak för tecken.
        ///
        /// <para>Det bästa stycket tas alltid med, även om det ensamt är längre än teckentaket —
        /// ett svar utan underlag är sämre än ett något dyrare svar. Ett stycke som inte ryms
        /// hoppas över, men sökningen fortsätter: ett kortare stycke längre ned kan rymmas.</para>
        /// </summary>
        public static List<KnowledgeChunk> SelectTop(
            IEnumerable<(KnowledgeChunk Chunk, double Score)> scored, int maxChunks, int maxChars)
        {
            var picked = new List<KnowledgeChunk>();
            var chars = 0;
            foreach (var (chunk, _) in scored.OrderByDescending(s => s.Score))
            {
                if (picked.Count >= maxChunks) break;
                var len = chunk.EmbeddingText.Length;
                if (picked.Count > 0 && chars + len > maxChars) continue;
                picked.Add(chunk);
                chars += len;
            }
            return picked;
        }

        /// <summary>
        /// Skriver ut urvalet för modellen: grupperat per dokument, och inom dokumentet i
        /// läsordning — inte i träffordning. Ett steg 3 före steg 1 gör en instruktion obegriplig.
        /// Dokumentet med bäst träff kommer först.
        /// </summary>
        public static string Format(IReadOnlyList<KnowledgeChunk> picked)
        {
            var sb = new StringBuilder();
            var docOrder = picked.Select(c => c.FileName).Distinct().ToList();
            foreach (var file in docOrder)
            {
                var inDoc = picked.Where(c => c.FileName == file).OrderBy(c => c.Order).ToList();
                if (sb.Length > 0) sb.Append("\n\n---\n\n");
                sb.Append("# ").Append(inDoc[0].DocTitle).Append('\n');
                foreach (var c in inDoc)
                {
                    sb.Append('\n');
                    if (c.Heading.Length > 0) sb.Append("## ").Append(c.Heading).Append("\n\n");
                    sb.Append(c.Body).Append('\n');
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Sökfrågan: den aktuella frågan plus användarens föregående fråga. En följdfråga som
        /// "och hur gör jag det på mobilen?" säger ingenting om ämnet på egen hand.
        /// </summary>
        public static string BuildQuery(string userMessage, IEnumerable<ChatMessage> history)
        {
            var previous = history.LastOrDefault(m => m.Role == "user")?.Content;
            return string.IsNullOrWhiteSpace(previous) ? userMessage : previous.Trim() + "\n" + userMessage;
        }
    }
}
