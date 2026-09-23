using System.Security.Cryptography;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Var verifikationernas underlag ligger på disk.
    ///
    /// <para><b>⚠️⚠️ FILNAMNET ÄR INNEHÅLLETS HASH, inte ett Guid.</b> Det är skillnaden mot
    /// <see cref="HpskSite.Services.Staffing.PrepDocumentStorage"/>, och den är hela poängen: ett
    /// nytt innehåll ger ett nytt namn och därmed en ny rad. En bilaga <b>kan</b> alltså inte
    /// tyst bytas ut efter att verifikationen bokförts. Kravet "underlaget ska inte kunna ändras"
    /// blir en egenskap i stället för en regel någon måste minnas — samma tanke som
    /// verifikationsraden, fast den skyddas av en trigger.</para>
    ///
    /// <para><b>⚠️⚠️ SAMMA FIL KAN HÖRA TILL FLERA VERIFIKATIONER.</b> Ett kontoutdrag som är
    /// underlag till tolv poster laddas upp en gång och ligger en gång. Följden är att
    /// <b>ingen radering någonsin får ske här</b>: den som raderar "sin" bilaga hade tagit
    /// underlaget från någon annans verifikation. Det finns därför ingen Delete-metod i den här
    /// klassen, och det är avsiktligt — makuleringen bryter KOPPLINGEN, aldrig filen.</para>
    ///
    /// <para><b>⚠️ Filerna ligger under <c>App_Data</c>, inte i Umbraco media.</b> Publik media är
    /// security-by-obscurity, och ett kvitto bär ofta personuppgifter — namn, adress, ibland ett
    /// kortnummers sista siffror. De når bara ut genom en grindad controller.</para>
    ///
    /// <para><b>⚠️ Sandlådan delar katalog med den skarpa liggaren</b>, eftersom namnet är
    /// innehållet. Det är ofarligt: raderna ligger i skilda scheman och en kastad sandlåda lämnar
    /// på sin höjd en oanvänd fil. Städrutiner får däremot ALDRIG sopa katalogen på "föräldralösa"
    /// filer utan att läsa BÅDA schemana — en fil utan rad i dbo kan vara i bruk i sbx.</para>
    /// </summary>
    public class LedgerAttachmentStorage
    {
        private const string FolderName = "ledger-attachments";

        /// <summary>
        /// ⚠️ Medvetet SMALARE än prep-dokumenten. Ett verifikationsunderlag är ett kvitto, en
        /// faktura eller ett kontoutdrag — inte en presentation. Färre format betyder färre
        /// filtyper som ska gå att öppna om sju år.
        /// </summary>
        private static readonly string[] AllowedExtensions =
            { ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".heic", ".csv", ".txt" };

        /// <summary>15 MB — en mobilkamerabild eller en flersidig faktura, inte en video.</summary>
        private const long MaxBytes = 15L * 1024 * 1024;

        private readonly IWebHostEnvironment _environment;

        public LedgerAttachmentStorage(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        private string GetStorageDir()
        {
            var dir = Path.Combine(_environment.ContentRootPath, "App_Data", FolderName);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public (bool Ok, string? Error) Validate(string fileName, long size)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();

            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                return (false, "Underlaget ska vara en PDF, en bild eller en textfil (kvitto, faktura, kontoutdrag).");

            if (size <= 0) return (false, "Filen är tom.");

            if (size > MaxBytes)
                return (false, "Filen är för stor (max 15 MB). Fotografera kvittot i stället för att skanna i högsta upplösning.");

            return (true, null);
        }

        /// <summary>
        /// Sparar innehållet och svarar med namnet på disk.
        ///
        /// <para><b>⚠️ Finns filen redan skrivs den INTE om.</b> Samma innehåll är samma fil, och en
        /// omskrivning hade varit ett tillfälle för den att bli något annat. Det är också därför
        /// strömmen läses till minnet först: hashen måste vara känd innan något rör disken.</para>
        /// </summary>
        public async Task<(string StoredAs, long Size)> SaveAsync(Stream stream, string originalFileName)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var ext = Path.GetExtension(originalFileName)?.ToLowerInvariant() ?? "";
            var storedAs = hash + ext;

            var path = Path.Combine(GetStorageDir(), storedAs);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);

            return (storedAs, bytes.LongLength);
        }

        /// <summary>
        /// Absolut sökväg, eller null när filen saknas.
        /// <para>⚠️ Vägrar allt som inte är ett naket filnamn — traversering skyddas här, inte hos
        /// anroparen, eftersom namnet kommer ur databasen och därmed ser betrott ut.</para>
        /// </summary>
        public string? GetFilePath(string? storedAs)
        {
            if (string.IsNullOrWhiteSpace(storedAs)) return null;
            if (storedAs.Contains('/') || storedAs.Contains('\\') || storedAs.Contains("..")) return null;

            var path = Path.Combine(GetStorageDir(), storedAs);
            return File.Exists(path) ? path : null;
        }

        public static string ContentTypeFor(string storedAs) =>
            Path.GetExtension(storedAs)?.ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".heic" => "image/heic",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".csv" => "text/csv",
                ".txt" => "text/plain",
                _ => "application/octet-stream"
            };
    }
}
