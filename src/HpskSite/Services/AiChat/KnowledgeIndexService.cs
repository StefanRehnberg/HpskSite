using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HpskSite.Services.AiChat
{
    /// <summary>
    /// Sökindexet över kunskapsbasen: varje stycke som en vektor, så en fråga kan få med sig de
    /// stycken som rör den i stället för hela kunskapsbasen.
    ///
    /// <para><b>Varför:</b> hela kunskapsbasen (~460 000 tecken, ~115 000 tokens) skickades med
    /// varje fråga. Det kostade ~€0,18 per fråga mot mistral-medium och tog 45+ sekunder.</para>
    ///
    /// <para><b>Omräkning är automatisk och bara för det som ändrats.</b> Vektorcachen nycklas på
    /// styckets innehåll (<see cref="KnowledgeChunk.Hash"/>). När kunskapsbasens cache löper ut
    /// (30 min) delas dokumenten om, och bara stycken med ny text skickas till embeddings-API:t.
    /// Vektorer för stycken som försvunnit rensas när cachen sparas.</para>
    ///
    /// <para><b>Cachen är en FIL, med flit.</b> Den är härledd och kostar ungefär ett öre att
    /// bygga om från noll — en tabell hade krävt ett migreringssteg, och ett okört migreringssteg
    /// har redan en gång gjort en funktion tyst död här. Försvinner filen räknas allt om.</para>
    ///
    /// <para><b>Ett fel får aldrig stoppa chatten.</b> <see cref="SearchAsync"/> returnerar null
    /// när sökningen inte kan genomföras, och anroparen skickar då hela kunskapsbasen som förut.</para>
    /// </summary>
    public class KnowledgeIndexService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AiChatOptions _options;
        private readonly KnowledgeBaseService _knowledgeBase;
        private readonly ILogger<KnowledgeIndexService> _logger;
        private readonly string _apiKey;
        private readonly string _cacheFile;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, float[]>? _vectors;            // hash → vektor (cachen)
        private IReadOnlyList<KnowledgeBaseDoc>? _indexedDocs;     // dokumentlistan indexet byggdes av
        private List<(KnowledgeChunk Chunk, float[] Vector)> _index = new();

        /// <summary>Embeddings-anropet delas i omgångar om högst så här många tecken och stycken.</summary>
        private const int BatchMaxChars = 24000;
        private const int BatchMaxItems = 32;

        public KnowledgeIndexService(
            IHttpClientFactory httpClientFactory,
            IOptions<AiChatOptions> options,
            KnowledgeBaseService knowledgeBase,
            IConfiguration configuration,
            IWebHostEnvironment env,
            ILogger<KnowledgeIndexService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _knowledgeBase = knowledgeBase;
            _logger = logger;
            _apiKey = configuration["AiChat:ApiKey"] ?? "";
            _cacheFile = Path.Combine(env.ContentRootPath, "App_Data", "AiChatIndex", "embeddings-v1.json");
        }

        public bool IsSupported => _options.Enabled && _apiKey.Length > 0 && _options.RetrievalSupported;

        public sealed record SearchResult(IReadOnlyList<KnowledgeChunk> Chunks, string KnowledgeText);

        /// <summary>
        /// De stycken som bäst matchar frågan, bland dem användarens roller får se.
        /// Null = sökningen kunde inte genomföras; skicka hela kunskapsbasen.
        /// </summary>
        public async Task<SearchResult?> SearchAsync(string query, List<string> userRoles, CancellationToken ct = default)
        {
            if (!IsSupported) return null;

            try
            {
                var index = await EnsureIndexAsync(ct);
                if (index.Count == 0) return null;

                var queryVector = (await EmbedAsync(new List<string> { query }, ct))[0];

                var scored = index
                    .Where(e => e.Chunk.Roles.Any(userRoles.Contains))
                    .Select(e => (e.Chunk, KnowledgeRetrieval.Cosine(queryVector, e.Vector)));

                var picked = KnowledgeRetrieval.SelectTop(scored, _options.RetrievalMaxChunks, _options.RetrievalMaxChars);
                if (picked.Count == 0) return null;

                return new SearchResult(picked, KnowledgeRetrieval.Format(picked));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AI-chattens sökning misslyckades ({Message}) — frågan får hela kunskapsbasen i stället.",
                    ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Bygger indexet om kunskapsbasen ändrats sedan förra gången. Anropas också av
        /// förvärmningen efter start, så att den första frågan inte får vänta på bygget.
        /// </summary>
        public async Task<IReadOnlyList<(KnowledgeChunk Chunk, float[] Vector)>> EnsureIndexAsync(CancellationToken ct = default)
        {
            var docs = _knowledgeBase.GetAllDocs();
            if (ReferenceEquals(docs, _indexedDocs)) return _index;

            await _lock.WaitAsync(ct);
            try
            {
                docs = _knowledgeBase.GetAllDocs();
                if (ReferenceEquals(docs, _indexedDocs)) return _index;

                var model = _options.ResolveEmbeddingModel();
                _vectors ??= LoadCache(model);

                var chunks = docs.SelectMany(KnowledgeChunker.Split).ToList();
                var hashed = chunks.Select(c => (Chunk: c, Hash: c.Hash(model))).ToList();
                var missing = hashed.Where(h => !_vectors.ContainsKey(h.Hash))
                                    .GroupBy(h => h.Hash).Select(g => g.First()).ToList();

                if (missing.Count > 0)
                {
                    var sw = Stopwatch.StartNew();
                    foreach (var batch in Batches(missing))
                    {
                        var vectors = await EmbedAsync(batch.Select(b => b.Chunk.EmbeddingText).ToList(), ct);
                        for (var i = 0; i < batch.Count; i++) _vectors[batch[i].Hash] = vectors[i];
                    }
                    _logger.LogInformation(
                        "AI-chattens sökindex: {Missing} av {Total} stycken räknades om ({Ms} ms).",
                        missing.Count, chunks.Count, sw.ElapsedMilliseconds);
                }

                // Bara vektorer för stycken som finns kvar sparas — utan det växer filen för alltid.
                var current = hashed.Select(h => h.Hash).ToHashSet();
                var removed = _vectors.Keys.Count(k => !current.Contains(k));
                if (removed > 0)
                    foreach (var k in _vectors.Keys.Where(k => !current.Contains(k)).ToList()) _vectors.Remove(k);
                if (missing.Count > 0 || removed > 0) SaveCache(model);

                _index = hashed.Select(h => (h.Chunk, _vectors[h.Hash])).ToList();
                _indexedDocs = docs;
                return _index;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static IEnumerable<List<(KnowledgeChunk Chunk, string Hash)>> Batches(List<(KnowledgeChunk Chunk, string Hash)> items)
        {
            var batch = new List<(KnowledgeChunk Chunk, string Hash)>();
            var chars = 0;
            foreach (var item in items)
            {
                var len = item.Chunk.EmbeddingText.Length;
                if (batch.Count > 0 && (batch.Count >= BatchMaxItems || chars + len > BatchMaxChars))
                {
                    yield return batch;
                    batch = new List<(KnowledgeChunk Chunk, string Hash)>();
                    chars = 0;
                }
                batch.Add(item);
                chars += len;
            }
            if (batch.Count > 0) yield return batch;
        }

        private async Task<List<float[]>> EmbedAsync(List<string> inputs, CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(new { model = _options.ResolveEmbeddingModel(), input = inputs });
            var client = _httpClientFactory.CreateClient("AiChat");

            for (var attempt = 1; ; attempt++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, _options.ResolveEmbeddingEndpoint());
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                // Hastighetsgränsen slår till när indexet byggs från noll (ett tjugotal anrop i rad).
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new Exception($"Embeddings API error: {(int)response.StatusCode} {Truncate(json, 300)}");

                using var doc = JsonDocument.Parse(json);
                var result = new float[inputs.Count][];
                foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    var i = item.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                    result[i] = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                }
                if (result.Any(r => r == null))
                    throw new Exception("Embeddings API returned fewer vectors than inputs.");
                return result.ToList();
            }
        }

        private Dictionary<string, float[]> LoadCache(string model)
        {
            try
            {
                if (!File.Exists(_cacheFile)) return new();
                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cacheFile));
                // En cache från en annan modell är oanvändbar: vektorerna ligger i olika rum.
                if (file == null || file.Model != model) return new();
                return file.Vectors.ToDictionary(kv => kv.Key, kv => FromBase64(kv.Value));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI-chattens vektorcache kunde inte läsas — den byggs om.");
                return new();
            }
        }

        private void SaveCache(string model)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                var file = new CacheFile
                {
                    Model = model,
                    Vectors = _vectors!.ToDictionary(kv => kv.Key, kv => ToBase64(kv.Value)),
                };
                // Skrivs till en temporär fil och flyttas på plats, så en avbruten skrivning aldrig
                // lämnar en halv cache efter sig.
                var tmp = _cacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file));
                File.Move(tmp, _cacheFile, overwrite: true);
            }
            catch (Exception ex)
            {
                // Inte allvarligt: indexet finns i minnet, och nästa start räknar om (~1 öre).
                _logger.LogWarning(ex, "AI-chattens vektorcache kunde inte sparas.");
            }
        }

        private static string ToBase64(float[] v)
        {
            var bytes = new byte[v.Length * sizeof(float)];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        private static float[] FromBase64(string s)
        {
            var bytes = Convert.FromBase64String(s);
            var v = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
            return v;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        private sealed class CacheFile
        {
            public string Model { get; set; } = "";
            public Dictionary<string, string> Vectors { get; set; } = new();
        }
    }
}
