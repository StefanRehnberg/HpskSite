using NPoco;

namespace HpskSite.Models.Firearms
{
    /// <summary>
    /// En ägares inpackade datanyckel. <b>Exakt en rad per (ScopeKind, ScopeId)</b>, upprätthållet av
    /// ett unikt index — inte bara av koden. Två DEK:ar för samma ägare vore inte ett dubblettproblem
    /// utan ett dataförlustproblem: hälften av vapenraderna hade blivit oläsbara.
    ///
    /// <para><b>Raden ÄR raderingen.</b> Ta bort den och ägarens vapenuppgifter är kryptografiskt
    /// förstörda i samma sekund, oavsett hur många vapenrader som ligger kvar.</para>
    /// </summary>
    [TableName("FirearmKeyVault")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FirearmVaultKey
    {
        public int Id { get; set; }

        /// <summary>"Member" eller "Club" — <see cref="FirearmOwnerKind"/> som sträng. Ingår i AAD:n.</summary>
        public string ScopeKind { get; set; } = string.Empty;

        public int ScopeId { get; set; }

        /// <summary>
        /// Vilken rotnyckelversion DEK:en är inpackad med. Bär rotationen: en ompackning byter
        /// version och <see cref="WrappedDek"/>, medan DEK:en själv är oförändrad — därför behöver
        /// ingen vapenrad krypteras om.
        /// </summary>
        public int KeyVersion { get; set; }

        /// <summary>DEK:en, inpackad med ägarens KEK. Aldrig klartextnyckeln.</summary>
        public byte[] WrappedDek { get; set; } = Array.Empty<byte>();

        public DateTime CreatedAt { get; set; }

        /// <summary>Senaste ompackningen. Null = aldrig roterad.</summary>
        public DateTime? RotatedAt { get; set; }

        /// <summary>
        /// ⚠️ Kastar på ett okänt <see cref="ScopeKind"/> i stället för att falla tillbaka på
        /// "Member". En tyst reserv här hade byggt en AAD för fel ägartyp, och felet skulle visa sig
        /// långt bort som "kunde inte avkryptera" — inte som den trasiga raden det är.
        /// </summary>
        [Ignore]
        public FirearmScope Scope => Enum.TryParse<FirearmOwnerKind>(ScopeKind, out var kind)
            ? new FirearmScope(kind, ScopeId)
            : throw new InvalidOperationException(
                $"FirearmKeyVault.Id={Id} har okänd ScopeKind '{ScopeKind}'. " +
                "Giltiga värden är namnen i FirearmOwnerKind.");
    }
}
