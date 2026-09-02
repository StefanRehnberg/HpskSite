using Microsoft.Extensions.Options;
using HpskSite.Models.Configuration;
using HpskSite.Services.Firearms;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Läser och validerar rotnycklarna ur konfigurationen. Registrerad som <b>Singleton</b> —
    /// nycklarna ändras inte under en process, och base64-avkodningen ska inte göras per anrop.
    ///
    /// <para><b>⚠️ Konstruktorn kastar ALDRIG när nycklar saknas.</b> Funktionen är inte påslagen i
    /// alla miljöer, och en tjänst som spräcker appstarten på en oanvänd funktion är värre än
    /// funktionen själv. I stället är <see cref="IsConfigured"/> false, varje ANVÄNDNING kastar med
    /// ett meddelande som namnger konfigurationsnyckeln, och
    /// <see cref="FirearmKeyGuardHostedService"/> larmar högljutt vid start om det finns krypterad
    /// data men ingen nyckel.</para>
    ///
    /// <para>Ett <i>felformaterat</i> värde är däremot inget att vara tolerant mot — det betyder att
    /// någon har försökt konfigurera nyckeln och misslyckats, och att tiga om det är hur man får en
    /// halvkonfigurerad produktion. Sådana versioner registreras i <see cref="ConfigurationErrors"/>
    /// och namnges av guarden.</para>
    /// </summary>
    public class FirearmMasterKeyRing
    {
        public const string ConfigSection = "Firearm";
        public const string MasterKeysPath = "Firearm:MasterKeys";

        private readonly Dictionary<int, byte[]> _roots = new();
        private readonly List<string> _errors = new();

        public FirearmMasterKeyRing(IOptions<FirearmCryptoOptions> options)
        {
            var opts = options.Value ?? new FirearmCryptoOptions();
            CurrentKeyVersion = opts.CurrentKeyVersion;

            foreach (var (versionText, base64) in opts.MasterKeys ?? new Dictionary<string, string>())
            {
                if (!int.TryParse(versionText, out var version) || version <= 0)
                {
                    _errors.Add($"'{MasterKeysPath}:{versionText}' — versionen måste vara ett positivt heltal.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(base64))
                {
                    _errors.Add($"'{MasterKeysPath}:{versionText}' — värdet är tomt.");
                    continue;
                }

                byte[] raw;
                try
                {
                    raw = Convert.FromBase64String(base64.Trim());
                }
                catch (FormatException)
                {
                    _errors.Add($"'{MasterKeysPath}:{versionText}' — värdet är inte giltig base64.");
                    continue;
                }

                if (raw.Length < FirearmCrypto.KeySizeBytes)
                {
                    _errors.Add($"'{MasterKeysPath}:{versionText}' — nyckeln är {raw.Length} byte, " +
                                $"minst {FirearmCrypto.KeySizeBytes} krävs.");
                    continue;
                }

                _roots[version] = raw;
            }

            if (_roots.Count > 0 && !_roots.ContainsKey(CurrentKeyVersion))
            {
                // Detta är den farligaste felkonfigurationen: läsning fungerar för gamla valv medan
                // varje NY inpackning misslyckas. Namnge den, tig inte om den.
                _errors.Add($"'{ConfigSection}:CurrentKeyVersion' är {CurrentKeyVersion}, men ingen " +
                            $"nyckel med den versionen finns i '{MasterKeysPath}'. " +
                            $"Tillgängliga versioner: {string.Join(", ", _roots.Keys.OrderBy(v => v))}.");
            }
        }

        public int CurrentKeyVersion { get; }

        /// <summary>Minst en giltig rotnyckel finns.</summary>
        public bool IsConfigured => _roots.Count > 0;

        /// <summary>Nya inpackningar går att göra, alltså finns den AKTUELLA versionens nyckel.</summary>
        public bool CanWrapNewKeys => _roots.ContainsKey(CurrentKeyVersion);

        public IReadOnlyList<string> ConfigurationErrors => _errors;

        public IReadOnlyCollection<int> AvailableVersions => _roots.Keys.OrderBy(v => v).ToList();

        /// <summary>
        /// Rothemligheten för en given version. Kastar med en åtgärd i meddelandet — den som ser
        /// felet ska inte behöva läsa koden för att veta vad som ska göras.
        /// </summary>
        public byte[] RootSecretFor(int keyVersion)
        {
            if (_roots.TryGetValue(keyVersion, out var root)) return root;

            if (!IsConfigured)
                throw new FirearmCryptoException(
                    $"Vapenregistrets kryptering är inte konfigurerad: '{MasterKeysPath}' saknas. " +
                    "Lägg in rotnyckeln i appsettings.Production.json — se " +
                    "Documentation/FIREARM_KEY_MANAGEMENT.md.");

            throw new FirearmCryptoException(
                $"Rotnyckel version {keyVersion} saknas i '{MasterKeysPath}'. Den behövs för att läsa " +
                $"redan lagrad data och får inte tas bort förrän varje valv är ompackat. " +
                $"Konfigurerade versioner: {string.Join(", ", AvailableVersions)}.");
        }

        /// <summary>Rothemligheten som nya inpackningar ska använda.</summary>
        public byte[] CurrentRootSecret() => RootSecretFor(CurrentKeyVersion);
    }
}
