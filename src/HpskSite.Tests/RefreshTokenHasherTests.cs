using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (H6): refresh-token lagras bara som hash.</summary>
    public class RefreshTokenHasherTests
    {
        [Fact]
        public void Kand_testvektor_sha256_av_abc()
        {
            // NIST-testvektorn. Migreringens SQL (HASHBYTES över ASCII) ger samma värde.
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", RefreshTokenHasher.Hash("abc"));
        }

        [Fact]
        public void Hashen_ar_64_gemena_hextecken_och_ar_stabil()
        {
            var h = RefreshTokenHasher.Hash("QWxsYXRva2VuYmFzZTY0PT0+/+==");
            Assert.Equal(64, h.Length);
            Assert.Equal(h, h.ToLowerInvariant());
            Assert.Matches("^[0-9a-f]{64}$", h);
            Assert.Equal(h, RefreshTokenHasher.Hash("QWxsYXRva2VuYmFzZTY0PT0+/+=="));
        }

        [Fact]
        public void Olika_tokens_ger_olika_hashar()
        {
            Assert.NotEqual(RefreshTokenHasher.Hash("a"), RefreshTokenHasher.Hash("b"));
        }
    }
}
