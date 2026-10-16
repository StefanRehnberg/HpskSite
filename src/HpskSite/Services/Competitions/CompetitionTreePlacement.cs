using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Var en tävling eller serie hamnar i innehållsträdet</b> — tävlingsnavet och årsmappen under det
    /// (C3-planen S5c). Fanns i sex kopior i <c>CompetitionAdminController</c>. Gäller bara så länge
    /// tävlingarna är noder; tabellen har varken nav eller årsmappar.
    ///
    /// <para>⚠️ Navet letas upp på startsidan och dess barn (en fråga — där ligger det), sedan bland noderna
    /// av typen <c>competitionsHub</c>. Förut gicks HELA trädet med en fråga per nod: 14 sekunder per ny
    /// tävling på prodkopian (2026-10-16).</para>
    /// </summary>
    public class CompetitionTreePlacement
    {
        public const string HubAlias = "competitionsHub";
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;

        public CompetitionTreePlacement(IContentService contentService, IContentTypeService contentTypeService)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
        }

        /// <summary>Tävlingsnavet, eller null när det inte finns.</summary>
        public IContent? Hub()
        {
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root != null)
            {
                if (IsHub(root)) return root;
                var child = _contentService.GetPagedChildren(root.Id, 0, int.MaxValue, out _).FirstOrDefault(IsHub);
                if (child != null) return child;
            }
            var hubType = _contentTypeService.Get(HubAlias);
            return hubType == null
                ? null
                : _contentService.GetPagedOfType(hubType.Id, 0, 10, out _, null).FirstOrDefault(h => !h.Trashed);
        }

        /// <summary>
        /// Årsmappen under navet, skapad och publicerad om den saknas. Kastar
        /// <see cref="InvalidOperationException"/> med ett meddelande som kan visas när navet saknas eller
        /// mappen inte går att skapa.
        /// </summary>
        public int YearFolderId(int year)
        {
            var hub = Hub() ?? throw new InvalidOperationException("Tävlingsnavet (Competitions) finns inte.");
            var name = year.ToString();
            var folder = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _).FirstOrDefault(c => c.Name == name);
            if (folder != null) return folder.Id;

            folder = _contentService.Create(name, hub.Id, "contentPage");
            if (!_contentService.Save(folder).Success)
                throw new InvalidOperationException($"Årsmappen {name} kunde inte skapas.");
            _contentService.Publish(folder, new[] { "*" }, -1);
            return folder.Id;
        }

        private static bool IsHub(IContent c) =>
            c.ContentType.Alias == HubAlias || string.Equals(c.Name, "Competitions", StringComparison.OrdinalIgnoreCase);
    }
}
