namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Filerna sajten själv skriver och läser, utanför Umbracos mediabibliotek (skalplanen D3, 2026-10-19).
    ///
    /// <para><b>Varför:</b> på lokal disk dör filerna med servern och går inte att dela mellan två instanser.
    /// Bakom gränssnittet ligger antingen disken (standard, exakt som förut) eller en blobblagring
    /// (<c>FileStorage:Provider = AzureBlob</c>). Ingen anropare vet vilket.</para>
    ///
    /// <para><b>Ett namn är relativt sitt område</b> (<see cref="FileStoreAreas"/>) och får bära
    /// undermappar (<c>123/st1_A_tg1.jpg</c>). Namnet prövas här (<see cref="FileStoreNames"/>), inte hos
    /// anroparen — det kommer ofta ur databasen och ser därför betrott ut.</para>
    /// </summary>
    public interface IFileStore
    {
        /// <summary>Sparar (skriver över).</summary>
        Task SaveAsync(string area, string name, Stream content, CancellationToken ct = default);

        /// <summary>Sparar bara om filen saknas. Svarar false när den redan fanns (innehållet rörs inte).</summary>
        Task<bool> CreateIfAbsentAsync(string area, string name, Stream content, CancellationToken ct = default);

        /// <summary>En läsbar ström, eller null när filen saknas eller namnet inte är giltigt.</summary>
        Task<Stream?> OpenReadAsync(string area, string name, CancellationToken ct = default);

        Task<bool> ExistsAsync(string area, string name, CancellationToken ct = default);

        /// <summary>Tar bort. Svarar false när filen inte fanns.</summary>
        Task<bool> DeleteAsync(string area, string name, CancellationToken ct = default);

        /// <summary>Namnen i området (med undermappar, '/' som avskiljare), valfritt filtrerade på början.</summary>
        IAsyncEnumerable<StoredFileInfo> ListAsync(string area, string? prefix = null, CancellationToken ct = default);

        /// <summary>"Disk" eller "AzureBlob" — för hälsosidan och flytten.</summary>
        string ProviderName { get; }
    }

    public sealed record StoredFileInfo(string Name, long Size);
}
