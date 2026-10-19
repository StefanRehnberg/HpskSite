using Microsoft.AspNetCore.StaticFiles;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Serverar <c>/images/{publikt område}/…</c> ur <see cref="IFileStore"/> när lagringen är en
    /// blobblagring. Registreras bara då (<see cref="FileStorageComposer"/>) — på disk serverar de
    /// statiska filerna samma adresser som förut.
    ///
    /// <para>Adresserna står i databasen (figurkatalogen, stationskonfigurationerna, texter med
    /// uppladdade bilder), så de får inte ändras när filerna flyttar. Saknas filen i lagringen går
    /// förfrågan vidare till de statiska filerna — en bild som ännu inte flyttats syns ändå.</para>
    /// </summary>
    public class PublicImageMiddleware
    {
        private static readonly FileExtensionContentTypeProvider ContentTypes = new();
        private readonly RequestDelegate _next;

        public PublicImageMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, IFileStore store)
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                var path = context.Request.Path.Value ?? "";
                if (TryParse(path, out var area, out var name))
                {
                    var stream = await store.OpenReadAsync(area, name, context.RequestAborted);
                    if (stream != null)
                    {
                        await using (stream)
                        {
                            context.Response.ContentType = ContentTypes.TryGetContentType(name, out var ct)
                                ? ct : "application/octet-stream";
                            // Kort cachetid: en figurbild kan bytas ut under samma namn.
                            context.Response.Headers.CacheControl = "public, max-age=300";
                            if (stream.CanSeek) context.Response.ContentLength = stream.Length;
                            if (!HttpMethods.IsHead(context.Request.Method))
                                await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
                        }
                        return;
                    }
                }
            }
            await _next(context);
        }

        /// <summary><c>/images/{område}/{namn}</c> → (område, namn), bara för de publika områdena.</summary>
        public static bool TryParse(string path, out string area, out string name)
        {
            area = name = "";
            const string root = "/images/";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var rest = Uri.UnescapeDataString(path.Substring(root.Length));
            var slash = rest.IndexOf('/');
            if (slash <= 0) return false;
            var candidate = rest.Substring(0, slash);
            var match = FileStoreAreas.PublicAreas.FirstOrDefault(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            area = match;
            name = rest.Substring(slash + 1);
            return FileStoreNames.IsValidName(name);
        }
    }
}
