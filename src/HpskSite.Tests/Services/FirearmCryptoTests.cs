using System;
using System.Linq;
using System.Text;
using HpskSite.Models.Configuration;
using HpskSite.Models.Firearms;
using HpskSite.Services.Firearms;
using Microsoft.Extensions.Options;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Kryptokärnan i vapenregistret. Testerna prövar de påståenden som annars bara går att TRO PÅ,
    /// och varje test är skrivet för att kunna FALLA:
    ///
    ///   1. Klartexten finns inte i nyttolasten. Nyttolasten ÄR vad som lagras i databaskolumnen, så
    ///      det här är rå-DB-påståendet på bytenivå. Stängs krypteringen av blir det rött.
    ///   2. Chiffret är bundet till sin rad (AAD). Flyttas en blob till en annan medlem eller ett
    ///      annat vapen MISSLYCKAS avkrypteringen — den ger aldrig fel ägares data. Tas AAD:n bort
    ///      blir det rött.
    ///   3. Fel eller saknad nyckel ger ett NAMNGIVET fel, aldrig skräpdata och aldrig tomt.
    ///   4. AAD- och HKDF-strängarna är pinnade. De är en del av chiffret; ändras de blir varje redan
    ///      lagrad rad oläsbar utan att något kompileringsfel varnar. Det här testet är den enda
    ///      spärren mot det.
    /// </summary>
    public class FirearmCryptoTests
    {
        private static readonly byte[] Root = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray();

        private static readonly FirearmScope MemberA = FirearmScope.Member(1078);
        private static readonly FirearmScope MemberB = FirearmScope.Member(5514);
        private static readonly FirearmScope ClubA = FirearmScope.Club(2604);

        // En realistisk nyttolast: exakt de fält utfärdaren av ett föreningsintyg behöver, med
        // svenska tecken — de ska round-trippa, och de får inte synas i chiffret.
        private const string Details =
            "{\"Fabrikat\":\"Pardini\",\"Modell\":\"SP-1\",\"Kaliber\":\".22 LR\"," +
            "\"Piplangd\":\"15,2 cm\",\"Licensnummer\":\"AB-12345\",\"Tillverkningsnummer\":\"P-99871\"," +
            "\"Anteckning\":\"Köpt i Växjö, förvaras hemma\"}";

        // ── 1. Klartexten finns inte i det som lagras ────────────────────────────────────────────

        [Fact]
        public void Payload_contains_none_of_the_plaintext()
        {
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, FirearmCrypto.DetailsAad(MemberA, 42));

            // Varje känsligt värde prövas för sig. Ett svep över hela JSON-strängen hade kunnat bli
            // grönt av fel skäl (t.ex. att strängen är längre än nyttolasten).
            foreach (var needle in new[]
                     { "Pardini", "SP-1", ".22 LR", "15,2 cm", "AB-12345", "P-99871", "Växjö", "Fabrikat" })
            {
                Assert.False(ContainsUtf8(payload, needle),
                    $"'{needle}' finns i klartext i nyttolasten — det som lagras i databasen.");
            }
        }

        [Fact]
        public void Payload_has_the_documented_shape()
        {
            var key = FirearmCrypto.NewDataKey();
            var plaintext = Encoding.UTF8.GetBytes(Details);
            var payload = FirearmCrypto.Seal(key, plaintext, "aad");

            Assert.Equal(FirearmCrypto.PayloadVersion, payload[0]);
            Assert.Equal(1 + 12 + plaintext.Length + 16, payload.Length);
        }

        [Fact]
        public void Same_plaintext_twice_gives_different_ciphertext()
        {
            // Ett återanvänt nonce i GCM är katastrofalt (det läcker klartext-XOR och autentiseringen
            // kan brytas). Två identiska nyttolaster måste alltså skilja sig.
            var key = FirearmCrypto.NewDataKey();
            var aad = FirearmCrypto.DetailsAad(MemberA, 42);

            var first = FirearmCrypto.SealString(key, Details, aad);
            var second = FirearmCrypto.SealString(key, Details, aad);

            Assert.NotEqual(Convert.ToBase64String(first), Convert.ToBase64String(second));
            Assert.Equal(Details, FirearmCrypto.OpenString(key, first, aad));
            Assert.Equal(Details, FirearmCrypto.OpenString(key, second, aad));
        }

        [Fact]
        public void Round_trip_preserves_swedish_characters()
        {
            var key = FirearmCrypto.NewDataKey();
            var aad = FirearmCrypto.DetailsAad(MemberA, 42);
            const string swedish = "Kaliber 6,5 × 55 · Fabrikat Ångström · förvaras i vapenskåp";

            var payload = FirearmCrypto.SealString(key, swedish, aad);
            Assert.Equal(swedish, FirearmCrypto.OpenString(key, payload, aad));
        }

        [Fact]
        public void Empty_plaintext_round_trips()
        {
            // Ett vapen kan ha alla skyddade fält tomma. Det ska ge en giltig nyttolast, inte ett kast.
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, "", "aad");

            Assert.Equal(FirearmCrypto.MinPayloadBytes, payload.Length);
            Assert.Equal("", FirearmCrypto.OpenString(key, payload, "aad"));
        }

        // ── 2. Chiffret är bundet till sin rad ───────────────────────────────────────────────────

        [Fact]
        public void Blob_moved_to_another_firearm_cannot_be_read()
        {
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, FirearmCrypto.DetailsAad(MemberA, 42));

            var ex = Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.OpenString(key, payload, FirearmCrypto.DetailsAad(MemberA, 43)));

            Assert.Contains("flyttats", ex.Message);
        }

        [Fact]
        public void Blob_moved_to_another_member_cannot_be_read()
        {
            // Den konkreta attacken: någon med databasåtkomst kopierar medlem A:s EncryptedDetails
            // till medlem B:s vapenrad. Utan AAD:n skulle B:s session visa A:s uppgifter.
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, FirearmCrypto.DetailsAad(MemberA, 42));

            Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.OpenString(key, payload, FirearmCrypto.DetailsAad(MemberB, 42)));
        }

        [Fact]
        public void Blob_moved_from_a_member_to_a_club_cannot_be_read()
        {
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, FirearmCrypto.DetailsAad(MemberA, 42));

            Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.OpenString(key, payload, FirearmCrypto.DetailsAad(ClubA, 42)));
        }

        [Fact]
        public void Wrapped_dek_bound_to_another_key_version_cannot_be_read()
        {
            // Under en rotation finns inpackningar av två versioner samtidigt. En inpackning från
            // version 1 får inte gå att öppna som om den vore version 2 — då skulle en halvfärdig
            // rotation kunna se lyckad ut.
            var kek = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberA, 1);
            var dek = FirearmCrypto.NewDataKey();
            var wrapped = FirearmCrypto.Seal(kek, dek, FirearmCrypto.VaultAad(MemberA, 1));

            Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.Open(kek, wrapped, FirearmCrypto.VaultAad(MemberA, 2)));
        }

        // ── 3. Fel nyckel och trasig data ger namngivna fel ─────────────────────────────────────

        [Fact]
        public void Wrong_key_fails_loudly()
        {
            var payload = FirearmCrypto.SealString(FirearmCrypto.NewDataKey(), Details, "aad");
            var ex = Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.OpenString(FirearmCrypto.NewDataKey(), payload, "aad"));

            // Meddelandet måste namnge ÅTGÄRDEN. "Kunde inte avkryptera" ensamt skickar operatören
            // på fel jakt — de tre orsakerna kräver helt olika svar.
            Assert.Contains("rotnyckel", ex.Message);
            Assert.Contains("Firearm:MasterKeys", ex.Message);
        }

        [Fact]
        public void Tampered_ciphertext_fails()
        {
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, "aad");
            payload[20] ^= 0xFF;

            Assert.Throws<FirearmCryptoException>(() => FirearmCrypto.OpenString(key, payload, "aad"));
        }

        [Fact]
        public void Tampered_tag_fails()
        {
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, "aad");
            payload[^1] ^= 0xFF;

            Assert.Throws<FirearmCryptoException>(() => FirearmCrypto.OpenString(key, payload, "aad"));
        }

        [Fact]
        public void Truncated_payload_is_named_as_truncated()
        {
            var key = FirearmCrypto.NewDataKey();
            var ex = Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.Open(key, new byte[FirearmCrypto.MinPayloadBytes - 1], "aad"));

            Assert.Contains("för kort", ex.Message);
        }

        [Fact]
        public void Unknown_payload_version_is_named()
        {
            var key = FirearmCrypto.NewDataKey();
            var payload = FirearmCrypto.SealString(key, Details, "aad");
            payload[0] = 99;

            var ex = Assert.Throws<FirearmCryptoException>(() => FirearmCrypto.OpenString(key, payload, "aad"));
            Assert.Contains("version", ex.Message);
            Assert.Contains("99", ex.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(16)]
        [InlineData(31)]
        [InlineData(33)]
        public void Wrong_key_size_is_refused(int size)
        {
            Assert.Throws<FirearmCryptoException>(() => FirearmCrypto.Seal(new byte[size], new byte[4], "aad"));
        }

        [Fact]
        public void Short_root_secret_is_refused()
        {
            var ex = Assert.Throws<FirearmCryptoException>(() =>
                FirearmCrypto.DeriveKeyEncryptionKey(new byte[16], MemberA, 1));
            Assert.Contains("för kort", ex.Message);
        }

        // ── 4. Nyckelhärledningen ───────────────────────────────────────────────────────────────

        [Fact]
        public void Kek_derivation_is_deterministic()
        {
            var first = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberA, 1);
            var second = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberA, 1);

            Assert.Equal(Convert.ToBase64String(first), Convert.ToBase64String(second));
            Assert.Equal(FirearmCrypto.KeySizeBytes, first.Length);
        }

        [Fact]
        public void Kek_differs_per_owner_and_per_version_and_per_root()
        {
            var baseline = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberA, 1);
            var otherMember = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberB, 1);
            var otherKind = FirearmCrypto.DeriveKeyEncryptionKey(Root, FirearmScope.Club(1078), 1);
            var otherVersion = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberA, 2);
            var otherRoot = FirearmCrypto.DeriveKeyEncryptionKey(Root.Select(b => (byte)(b ^ 1)).ToArray(), MemberA, 1);

            var all = new[] { baseline, otherMember, otherKind, otherVersion, otherRoot }
                .Select(Convert.ToBase64String).ToList();

            // Club(1078) mot Member(1078) är det som fångar en härledning som glömt ägarTYPEN och
            // bara tagit id:t — då hade en klubb och en medlem med samma nod-id delat nyckel.
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        // ── 5. De pinnade strängarna ────────────────────────────────────────────────────────────

        [Fact]
        public void Aad_strings_are_pinned()
        {
            // ⚠️ FALLER DET HÄR TESTET: ändra tillbaka strängen. AAD:n är en del av chiffret, så en
            // ändrad separator, ett ändrat prefix eller ett omdöpt värde i FirearmOwnerKind gör varje
            // redan lagrad rad permanent oläsbar — utan kompileringsfel och utan runtime-varning.
            // Behövs en ny form: höj FirearmCrypto.PayloadVersion och behåll den gamla vägen.
            Assert.Equal("firearm|42|Member|1078", FirearmCrypto.DetailsAad(MemberA, 42));
            Assert.Equal("firearm|42|Club|2604", FirearmCrypto.DetailsAad(ClubA, 42));
            Assert.Equal("vault|Member|1078|k1", FirearmCrypto.VaultAad(MemberA, 1));
            Assert.Equal("vault|Club|2604|k7", FirearmCrypto.VaultAad(ClubA, 7));
        }

        [Fact]
        public void Owner_kind_names_are_pinned()
        {
            // Samma skäl som ovan, men fångar det ETT steg tidigare: namnen i enumet.
            Assert.Equal("Member", FirearmOwnerKind.Member.ToString());
            Assert.Equal("Club", FirearmOwnerKind.Club.ToString());
            Assert.Equal(2, Enum.GetValues<FirearmOwnerKind>().Length);
        }

        [Fact]
        public void Kek_derivation_is_pinned_to_a_known_vector()
        {
            // Ett testvektor-påstående: samma rothemlighet, ägare och version måste ge samma KEK i
            // dag som i dag + 20 år. Ändras HKDF-info-strängen faller det här, och det är hela
            // poängen — utan vektorn kan ingen se att gammal data slutade gå att packa upp.
            //
            // Värdet är KORSVALIDERAT mot en oberoende RFC 5869-implementation (HMAC-SHA256 för hand
            // i Python), inte avskrivet ur .NET:s utdata. Det betyder två saker: härledningen följer
            // standarden, och ReadOnlySpan<byte>.Empty som salt beter sig som RFC:ns "utelämnat
            // salt" = HashLen nollbytes. Det senare var inte självklart — den array-tagande
            // överlagringen tar `null` för samma sak.
            //
            //   prk = HMAC-SHA256(key: 32 nollbytes, msg: rothemligheten)
            //   okm = HMAC-SHA256(key: prk, msg: "FirearmVault.v1|Member|1078|k1" + 0x01)[..32]
            var kek = FirearmCrypto.DeriveKeyEncryptionKey(Root, MemberA, 1);
            Assert.Equal("0yIvfz1lpb+8siDXZ9znaAkEtquh1dkmY4IP5QInOPw=", Convert.ToBase64String(kek));
        }

        // ── Ägarvärdet ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Scope_rejects_a_missing_id()
        {
            Assert.False(FirearmScope.Member(0).IsValid);
            Assert.False(FirearmScope.Club(-1).IsValid);
            Assert.True(FirearmScope.Member(1).IsValid);
        }

        // ── Nyckelringen ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Key_ring_without_configuration_does_not_throw_on_construction()
        {
            // En tjänst som spräcker appstarten på en oanvänd funktion är värre än funktionen själv.
            var ring = Ring(new FirearmCryptoOptions());

            Assert.False(ring.IsConfigured);
            Assert.False(ring.CanWrapNewKeys);
            Assert.Empty(ring.ConfigurationErrors);
        }

        [Fact]
        public void Key_ring_names_the_config_path_when_used_unconfigured()
        {
            var ring = Ring(new FirearmCryptoOptions());
            var ex = Assert.Throws<FirearmCryptoException>(() => ring.CurrentRootSecret());

            Assert.Contains("Firearm:MasterKeys", ex.Message);
            Assert.Contains("FIREARM_KEY_MANAGEMENT", ex.Message);
        }

        [Fact]
        public void Key_ring_reads_a_valid_key()
        {
            var ring = Ring(Options(1, (1, Root)));

            Assert.True(ring.IsConfigured);
            Assert.True(ring.CanWrapNewKeys);
            Assert.Empty(ring.ConfigurationErrors);
            Assert.Equal(Convert.ToBase64String(Root), Convert.ToBase64String(ring.CurrentRootSecret()));
        }

        [Fact]
        public void Key_ring_keeps_old_versions_readable()
        {
            // Under en rotation MÅSTE den gamla nyckeln ligga kvar tills varje valv är ompackat.
            var oldRoot = Root.Select(b => (byte)(b ^ 0x5A)).ToArray();
            var ring = Ring(Options(2, (1, oldRoot), (2, Root)));

            Assert.Equal(new[] { 1, 2 }, ring.AvailableVersions);
            Assert.Equal(Convert.ToBase64String(oldRoot), Convert.ToBase64String(ring.RootSecretFor(1)));
            Assert.Equal(Convert.ToBase64String(Root), Convert.ToBase64String(ring.CurrentRootSecret()));
        }

        [Fact]
        public void Key_ring_refusing_a_removed_version_says_it_is_still_needed()
        {
            var ring = Ring(Options(2, (2, Root)));
            var ex = Assert.Throws<FirearmCryptoException>(() => ring.RootSecretFor(1));

            Assert.Contains("version 1", ex.Message);
            Assert.Contains("ompackat", ex.Message);
        }

        [Fact]
        public void Key_ring_reports_a_current_version_with_no_key()
        {
            // Den farligaste felkonfigurationen: gamla valv går att LÄSA medan varje ny inpackning
            // misslyckas. Ett halvfungerande register är värre än ett som säger ifrån.
            var ring = Ring(Options(3, (1, Root)));

            Assert.True(ring.IsConfigured);
            Assert.False(ring.CanWrapNewKeys);
            Assert.Contains(ring.ConfigurationErrors, e => e.Contains("CurrentKeyVersion"));
        }

        [Theory]
        [InlineData("inte base64!!", "base64")]
        [InlineData("", "tomt")]
        public void Key_ring_reports_a_malformed_value_instead_of_ignoring_it(string value, string expected)
        {
            // Ett felformaterat värde betyder att någon FÖRSÖKT konfigurera nyckeln och misslyckats.
            // Att tiga om det är hur man får en halvkonfigurerad produktion.
            var ring = Ring(new FirearmCryptoOptions
            {
                CurrentKeyVersion = 1,
                MasterKeys = new() { ["1"] = value },
            });

            Assert.False(ring.IsConfigured);
            Assert.Contains(ring.ConfigurationErrors, e => e.Contains(expected));
        }

        [Fact]
        public void Key_ring_refuses_a_too_short_key()
        {
            var ring = Ring(new FirearmCryptoOptions
            {
                CurrentKeyVersion = 1,
                MasterKeys = new() { ["1"] = Convert.ToBase64String(new byte[16]) },
            });

            Assert.False(ring.IsConfigured);
            Assert.Contains(ring.ConfigurationErrors, e => e.Contains("16 byte"));
        }

        [Fact]
        public void Key_ring_reports_a_non_numeric_version()
        {
            var ring = Ring(new FirearmCryptoOptions
            {
                CurrentKeyVersion = 1,
                MasterKeys = new() { ["current"] = Convert.ToBase64String(Root) },
            });

            Assert.False(ring.IsConfigured);
            Assert.Contains(ring.ConfigurationErrors, e => e.Contains("positivt heltal"));
        }

        // ── Hjälpare ────────────────────────────────────────────────────────────────────────────

        private static FirearmMasterKeyRing Ring(FirearmCryptoOptions options)
            => new(Microsoft.Extensions.Options.Options.Create(options));

        private static FirearmCryptoOptions Options(int current, params (int Version, byte[] Key)[] keys)
            => new()
            {
                CurrentKeyVersion = current,
                MasterKeys = keys.ToDictionary(
                    k => k.Version.ToString(), k => Convert.ToBase64String(k.Key)),
            };

        private static bool ContainsUtf8(byte[] haystack, string needle)
        {
            var bytes = Encoding.UTF8.GetBytes(needle);
            if (bytes.Length == 0 || bytes.Length > haystack.Length) return false;

            for (var i = 0; i <= haystack.Length - bytes.Length; i++)
            {
                var match = true;
                for (var j = 0; j < bytes.Length; j++)
                {
                    if (haystack[i + j] != bytes[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}
