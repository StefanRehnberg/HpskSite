using System.Text.Json;
using HpskSite.Models.Firearms;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Den enda ytan resten av kodbasen ska använda för att skydda och läsa ett vapens känsliga
    /// uppgifter. Löser ägarens nyckel ur valvet och binder chiffret till raden.
    ///
    /// <para><b>⚠️ Den här klassen avgör INGEN behörighet.</b> Att kunna avkryptera är inte samma sak
    /// som att få läsa. Grinden — medlemmen själv, eller klubbens föreningsintygsansvarige med ett
    /// aktivt styrelseuppdrag — hör i behörighetslagret (steg 2), och varje läsning ska dessutom
    /// skriva en rad i <c>FirearmAccessLog</c>. Anropa aldrig <see cref="Unprotect"/> direkt från en
    /// controller.</para>
    ///
    /// <para><b>⚠️ SKRIVNINGEN ÄR TVÅSTEGS, och det är inte valfritt.</b> AAD:n binder chiffret till
    /// vapnets id, så id:t måste finnas innan uppgifterna kan krypteras: <i>infoga raden med
    /// klartextkolumnerna → läs id:t → kryptera → uppdatera raden</i>. Krypteras det med id 0
    /// misslyckas varje senare läsning, eftersom AAD:n då aldrig kan matcha. Därför vägrar
    /// <see cref="Protect"/> ett id som inte är satt i stället för att producera en rad ingen kan
    /// läsa.</para>
    /// </summary>
    public class FirearmProtector
    {
        private readonly FirearmVaultService _vault;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // Ingen indentering: nyttolasten är binär i databasen, och blanktecken är bara utrymme
            // som ändå krypteras. Encoder-standarden duger — inget av detta renderas som HTML.
            WriteIndented = false,
        };

        public FirearmProtector(FirearmVaultService vault)
        {
            _vault = vault;
        }

        /// <summary>Krypterar ett vapens uppgifter. Skapar ägarens valv om det inte finns.</summary>
        public byte[] Protect(FirearmScope scope, int firearmId, string plaintextJson)
        {
            RequireFirearmId(firearmId);

            var dek = _vault.GetOrCreateDataKey(scope);
            try
            {
                return FirearmCrypto.SealString(
                    dek, plaintextJson ?? string.Empty, FirearmCrypto.DetailsAad(scope, firearmId));
            }
            finally
            {
                Array.Clear(dek, 0, dek.Length);
            }
        }

        /// <summary>
        /// Läser ett vapens uppgifter.
        ///
        /// <para>Kastar <see cref="FirearmCryptoException"/> när valvet är borta — det betyder
        /// kryptografiskt raderat, och att svara med en tom sträng där hade fått raderade uppgifter
        /// att se ut som ouppfyllda fält.</para>
        /// </summary>
        public string Unprotect(FirearmScope scope, int firearmId, byte[]? payload)
        {
            RequireFirearmId(firearmId);

            if (payload is null || payload.Length == 0)
                return string.Empty; // Inget har någonsin skrivits på raden. Det är ett tomt fält.

            var dek = _vault.TryGetDataKey(scope)
                ?? throw new FirearmCryptoException(
                    $"Det finns inget valv för {scope}, men vapen {firearmId} bär krypterade " +
                    "uppgifter. Nyckeln är raderad (kryptografisk radering) — uppgifterna går inte " +
                    "att återskapa.");
            try
            {
                return FirearmCrypto.OpenString(
                    dek, payload, FirearmCrypto.DetailsAad(scope, firearmId));
            }
            finally
            {
                Array.Clear(dek, 0, dek.Length);
            }
        }

        /// <summary>Serialiserar och krypterar i ett steg.</summary>
        public byte[] ProtectObject<T>(FirearmScope scope, int firearmId, T value)
            => Protect(scope, firearmId, JsonSerializer.Serialize(value, JsonOptions));

        /// <summary>
        /// Läser och deserialiserar. Svarar <c>null</c> bara när raden aldrig haft något innehåll —
        /// ett fel i avkrypteringen kastar, det tystas inte till null.
        /// </summary>
        public T? UnprotectObject<T>(FirearmScope scope, int firearmId, byte[]? payload) where T : class
        {
            var json = Unprotect(scope, firearmId, payload);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        private static void RequireFirearmId(int firearmId)
        {
            if (firearmId <= 0)
                throw new ArgumentException(
                    "Vapnets id måste vara satt innan uppgifterna krypteras — AAD:n binder chiffret " +
                    "till raden. Infoga raden först, läs id:t, kryptera sedan och uppdatera.",
                    nameof(firearmId));
        }
    }
}
