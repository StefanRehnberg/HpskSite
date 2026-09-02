namespace HpskSite.Models.Configuration
{
    /// <summary>
    /// Rotnycklarna för vapenregistrets kryptering, ur <c>appsettings.Production.json</c> under
    /// <c>"Firearm"</c>:
    ///
    /// <code>
    /// "Firearm": {
    ///   "CurrentKeyVersion": 1,
    ///   "MasterKeys": { "1": "&lt;32 slumpbytes i base64&gt;" }
    /// }
    /// </code>
    ///
    /// <para><b>⚠️ Varför en konfigurerad hemlighet och inte DataProtection-nyckelringen.</b>
    /// <c>IDataProtection</c> är byggt för kortlivade, återkallningsbara nyttolaster och roterar var
    /// 90:e dag; nyckelringen är dessutom en KATALOG i <c>App_Data</c>. Den överlever en deploy, men
    /// inte att webbhotellet flyttar oss eller återställer en äldre diskavbild — och då är varje
    /// krypterad rad permanent oläsbar. Det här datat ska gå att läsa om 20 år, så det ska ligga i
    /// en enda hemlighet som backas upp tillsammans med anslutningsstängen. Tokens fortsätter
    /// använda DataProtection, som är det den är bra på.</para>
    ///
    /// <para><b>⚠️ HEMLIGHETEN ÄR KRONJUVELEN.</b> Förlorad nyckel = förlorad data, inte en trasig
    /// länk. Rutinen står i <c>Documentation/FIREARM_KEY_MANAGEMENT.md</c> och ska följas INNAN
    /// första krypterade raden skrivs.</para>
    ///
    /// <para><b>Rotation</b> sker genom att lägga till en NY version i <see cref="MasterKeys"/>,
    /// höja <see cref="CurrentKeyVersion"/> och packa om valven. Den gamla nyckeln måste ligga kvar
    /// tills varje valv är ompackat — annars går de opackade valven inte att läsa. Ingen vapenrad
    /// krypteras om, eftersom det bara är DEK:ens inpackning som byts.</para>
    /// </summary>
    public class FirearmCryptoOptions
    {
        /// <summary>Den version nya inpackningar ska använda.</summary>
        public int CurrentKeyVersion { get; set; } = 1;

        /// <summary>
        /// Version → 32 slumpbytes i base64. Alla versioner som fortfarande förekommer i
        /// <c>FirearmKeyVault.KeyVersion</c> måste finnas här, inte bara den aktuella.
        /// </summary>
        public Dictionary<string, string> MasterKeys { get; set; } = new();
    }
}
