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
            var docs = LoadDocs();
            var filtered = docs.Where(d => d.Roles.Any(r => userRoles.Contains(r))).ToList();
            return string.Join("\n\n---\n\n", filtered.Select(d => d.Content));
        }

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
            if (_cachedDocs != null && DateTime.UtcNow - _cacheTime < CacheDuration)
                return _cachedDocs;

            _cachedDocs = new List<KnowledgeBaseDoc>();

            if (!Directory.Exists(_docsPath))
                return _cachedDocs;

            foreach (var file in Directory.GetFiles(_docsPath, "*.md"))
            {
                var text = File.ReadAllText(file);
                var doc = ParseDoc(text, Path.GetFileName(file));
                if (doc != null)
                    _cachedDocs.Add(doc);
            }

            _cacheTime = DateTime.UtcNow;
            return _cachedDocs;
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
    }
}
