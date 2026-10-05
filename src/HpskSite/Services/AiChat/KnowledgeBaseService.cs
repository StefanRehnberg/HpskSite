using Microsoft.AspNetCore.Hosting;

namespace HpskSite.Services.AiChat
{
    public class KnowledgeBaseService
    {
        private readonly string _docsPath;
        private readonly string _systemPromptPath;
        private List<KnowledgeBaseDoc>? _cachedDocs;
        private string? _cachedSystemPrompt;
        private DateTime _cacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

        public KnowledgeBaseService(IWebHostEnvironment env)
        {
            var basePath = Path.Combine(env.ContentRootPath, "KnowledgeBase");
            _docsPath = Path.Combine(basePath, "docs");
            _systemPromptPath = Path.Combine(basePath, "system-prompt.md");
        }

        public string GetSystemPrompt(List<string> userRoles)
        {
            var prompt = LoadSystemPrompt();
            return prompt.Replace("{{USER_ROLES}}", string.Join(", ", userRoles));
        }

        public string GetFilteredKnowledgeBase(List<string> userRoles)
        {
            var filtered = LoadDocs().Where(d => IsVisibleTo(d, userRoles)).ToList();
            return string.Join("\n\n---\n\n", filtered.Select(d => d.Content));
        }

        /// <summary>
        /// Alla dokument, oberoende av roll. Samma lista (samma objekt) returneras tills cachen
        /// löper ut, så sökindexet kan avgöra om något ändrats med en referensjämförelse.
        /// </summary>
        public IReadOnlyList<KnowledgeBaseDoc> GetAllDocs() => LoadDocs();

        /// <summary>Rollfiltret — ETT ställe, så sökningen och hela-kunskapsbasen-vägen inte kan glida isär.</summary>
        public static bool IsVisibleTo(KnowledgeBaseDoc doc, IEnumerable<string> userRoles)
            => doc.Roles.Any(r => userRoles.Contains(r));

        private string LoadSystemPrompt()
        {
            if (_cachedSystemPrompt != null && DateTime.UtcNow - _cacheTime < CacheDuration)
                return _cachedSystemPrompt;

            _cachedSystemPrompt = File.Exists(_systemPromptPath)
                ? File.ReadAllText(_systemPromptPath)
                : "Du är en hjälpsam assistent för pistol.nu.";

            _cacheTime = DateTime.UtcNow;
            return _cachedSystemPrompt;
        }

        private List<KnowledgeBaseDoc> LoadDocs()
        {
            var cached = _cachedDocs;
            if (cached != null && DateTime.UtcNow - _cacheTime < CacheDuration)
                return cached;

            // Byggs i en lokal lista och publiceras först när den är klar — sökindexet läser
            // listan från andra trådar och får aldrig se en halvfylld.
            var docs = new List<KnowledgeBaseDoc>();

            if (Directory.Exists(_docsPath))
            {
                foreach (var file in Directory.GetFiles(_docsPath, "*.md").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var text = File.ReadAllText(file);
                    var doc = ParseDoc(text, Path.GetFileName(file));
                    if (doc != null)
                        docs.Add(doc);
                }
            }

            _cachedDocs = docs;
            _cacheTime = DateTime.UtcNow;
            return docs;
        }

        private static KnowledgeBaseDoc? ParseDoc(string text, string fileName)
        {
            if (!text.StartsWith("---"))
                return new KnowledgeBaseDoc { FileName = fileName, Roles = new List<string> { "public" }, Content = text };

            var endIndex = text.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIndex < 0)
                return new KnowledgeBaseDoc { FileName = fileName, Roles = new List<string> { "public" }, Content = text };

            var frontmatter = text.Substring(3, endIndex - 3);
            var content = text.Substring(endIndex + 3).Trim();
            var roles = new List<string>();

            foreach (var line in frontmatter.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("roles:"))
                {
                    var rolesStr = trimmed.Substring(6).Trim().Trim('[', ']');
                    roles = rolesStr.Split(',').Select(r => r.Trim()).Where(r => !string.IsNullOrEmpty(r)).ToList();
                }
            }

            if (roles.Count == 0)
                roles.Add("public");

            return new KnowledgeBaseDoc { FileName = fileName, Roles = roles, Content = content };
        }
    }

    public class KnowledgeBaseDoc
    {
        public string FileName { get; set; } = "";
        public List<string> Roles { get; set; } = new();
        public string Content { get; set; } = "";

        /// <summary>Dokumentets första "# "-rubrik, annars filnamnet utan ändelse.</summary>
        public string Title
        {
            get
            {
                foreach (var line in Content.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("# ", StringComparison.Ordinal)) return t.Substring(2).Trim();
                }
                return Path.GetFileNameWithoutExtension(FileName);
            }
        }
    }
}
