namespace HpskSite.Models.Firearms
{
    /// <summary>
    /// Vem som äger vapnet, och därmed vilket valv som håller nyckeln.
    ///
    /// <b>⚠️ NAMNEN ÄR EN DEL AV KRYPTOT.</b> Enumets namn skrivs in i AAD:n för både
    /// nyckelinpackningen och vapnets uppgifter (se <see cref="Services.Firearms.FirearmCrypto"/>),
    /// så ett <i>byte av namn</i> på en medlem här gör varje redan lagrad rad oläsbar — permanent, och
    /// utan att något kompileringsfel varnar. Lägg gärna till en ny ägartyp; döp aldrig om en gammal.
    /// </summary>
    public enum FirearmOwnerKind
    {
        /// <summary>Medlemmens eget vapen. Läsbart för medlemmen och för klubbens föreningsintygsansvarige.</summary>
        Member = 0,

        /// <summary>Klubbens vapen. Tillhör en juridisk person, inte en fysisk — läsbart för klubbadmin.</summary>
        Club = 1,
    }

    /// <summary>
    /// Ägaren som ett värde: (typ, id). Ett valv per ägare, en DEK per valv.
    ///
    /// <b>Varför ägaren och inte vapnet bär nyckeln:</b> raderas valvsraden är hela ägarens
    /// vapendata kryptografiskt förstörd i en enda operation — vilket är GDPR-raderingen. En nyckel
    /// per vapen hade gjort samma radering till en loop som kan avbrytas halvvägs.
    /// </summary>
    public readonly record struct FirearmScope(FirearmOwnerKind Kind, int Id)
    {
        public static FirearmScope Member(int memberId) => new(FirearmOwnerKind.Member, memberId);
        public static FirearmScope Club(int clubId) => new(FirearmOwnerKind.Club, clubId);

        /// <summary>Formen som lagras i <c>FirearmKeyVault.ScopeKind</c> och som går in i AAD:n.</summary>
        public string KindName => Kind.ToString();

        public bool IsValid => Id > 0;

        public override string ToString() => $"{KindName}:{Id}";
    }
}
