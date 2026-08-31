using System.Collections.Generic;
using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Regeltabellen för länkgrinden i "Det här händer"-flödet.
    ///
    /// ⚠️ Grinden var först en ren INLOGGNINGS-grind ("är någon inloggad"), vilket gjorde en
    /// klubbintern tävling klickbar för varje medlem på sajten — även den som inte tillhör klubben
    /// (rapporterat av Stefan 2026-08-31). Rätt fråga är om besökaren har ÅTKOMST.
    ///
    /// E2E-sviten (`hpsk-verify/whatshappening-clickable-verify.mjs`) bevisar KOPPLINGEN mot verklig
    /// data och de två adminvägarna. Den kan däremot inte pröva klubbmedlemskapsvägen: dev har ingen
    /// medlem som tillhör just den klubb en maskerad rad ägs av. Det är vad de här testerna finns för.
    /// </summary>
    public class WhatsHappeningAccessTests
    {
        private const int OwningClub = 2607;
        private const int OtherClub = 2604;
        private const string OwningRegion = "Halland";
        private const string OtherRegion = "Dalarna";

        private static FeedItem Masked(int clubId = OwningClub, string region = OwningRegion) => new FeedItem
        {
            Source = FeedSource.Competition,
            Masked = true,
            ClubId = clubId,
            RegionCode = region,
            Title = "Klubbintern tävling",
            Url = "/competitions/2026/halland/falkenberg/standarden/"
        };

        private static FeedItem Open() => new FeedItem
        {
            Source = FeedSource.Competition,
            Masked = false,
            ClubId = OwningClub,
            RegionCode = OwningRegion,
            Title = "Öppen tävling",
            Url = "/competitions/2026/halland/falkenberg/oppen/"
        };

        // ── Utloggad ──────────────────────────────────────────────────────────
        [Fact]
        public void Anonymous_CanLinkOpen_ButNotMasked()
        {
            var a = WhatsHappeningAccess.Anonymous();
            Assert.True(a.CanLink(Open()));
            Assert.False(a.CanLink(Masked()));
        }

        // ── KLUBBMEDLEMSKAPSVÄGEN — det Stefan beskrev ────────────────────────
        [Fact]
        public void ClubMember_CanLinkOwnClubsMaskedItem()
        {
            var a = WhatsHappeningAccess.FromResolved(true, false, new[] { OwningClub }, null);
            Assert.True(a.CanLink(Masked()));
        }

        [Fact]
        public void MemberOfAnotherClub_CannotLinkMaskedItem()
        {
            var a = WhatsHappeningAccess.FromResolved(true, false, new[] { OtherClub }, null);
            Assert.False(a.CanLink(Masked()));
            // …men de öppna raderna ska fortfarande gå att klicka på. En grind som stänger
            // allt är lika fel som en som släpper in alla.
            Assert.True(a.CanLink(Open()));
        }

        [Fact]
        public void MemberOfSeveralClubs_CanLinkAnyOfThem()
        {
            // primaryClubId + memberClubIds — en medlem tillhör ofta flera klubbar.
            var a = WhatsHappeningAccess.FromResolved(true, false, new[] { OtherClub, OwningClub }, null);
            Assert.True(a.CanLink(Masked()));
        }

        [Fact]
        public void MemberWithNoClub_CannotLinkMaskedItem()
        {
            var a = WhatsHappeningAccess.FromResolved(true, false, new int[0], null);
            Assert.False(a.CanLink(Masked()));
        }

        // ── Kretsvägen ────────────────────────────────────────────────────────
        [Fact]
        public void RegionalAdminOfSameRegion_CanLinkMaskedItem()
        {
            var a = WhatsHappeningAccess.FromResolved(true, false, null, new[] { OwningRegion });
            Assert.True(a.CanLink(Masked()));
        }

        [Fact]
        public void RegionalAdminOfAnotherRegion_CannotLinkMaskedItem()
        {
            var a = WhatsHappeningAccess.FromResolved(true, false, null, new[] { OtherRegion });
            Assert.False(a.CanLink(Masked()));
        }

        [Fact]
        public void RegionCodeMatchIsCaseInsensitive()
        {
            // NormalizeRegionCode gemenar koden på en del kodvägar, så jämförelsen får
            // inte vara skiftlägeskänslig.
            var a = WhatsHappeningAccess.FromResolved(true, false, null, new[] { "halland" });
            Assert.True(a.CanLink(Masked(region: "Halland")));
        }

        [Fact]
        public void RegionalAdmin_DoesNotMatchItemWithoutRegion()
        {
            // ⚠️ En tom RegionCode får ALDRIG matcha — det skulle ge varje kretsadmin
            // åtkomst till varje rad som saknar krets.
            var a = WhatsHappeningAccess.FromResolved(true, false, null, new[] { OwningRegion });
            Assert.False(a.CanLink(Masked(clubId: 0, region: "")));
        }

        // ── Sajtvägen ─────────────────────────────────────────────────────────
        [Fact]
        public void SiteAdmin_CanLinkEverything()
        {
            var a = WhatsHappeningAccess.FromResolved(true, true, null, null);
            Assert.True(a.CanLink(Masked()));
            Assert.True(a.CanLink(Masked(clubId: 0, region: "")));
            Assert.True(a.CanLink(Open()));
        }

        // ── Url saknas ────────────────────────────────────────────────────────
        [Fact]
        public void ItemWithoutUrl_IsNeverLinkable()
        {
            // En död länk ser ut som ett fel på sidan, vilket är precis vad hela
            // ändringen skulle bli av med.
            var noUrl = Open();
            noUrl.Url = "";
            var a = WhatsHappeningAccess.FromResolved(true, true, null, null);
            Assert.False(a.CanLink(noUrl));
        }

        [Fact]
        public void NullItem_IsNeverLinkable()
        {
            var a = WhatsHappeningAccess.FromResolved(true, true, null, null);
            Assert.False(a.CanLink(null!));
        }

        // ── Klubbmedlemskap räknas inte för en UTLOGGAD besökare ───────────────
        [Fact]
        public void NotLoggedIn_ClubIdsAreIgnored()
        {
            // Skyddsnät: skulle någon bygga en åtkomstmängd med klubbar men isLoggedIn=false
            // får den inte ge åtkomst till maskerat innehåll.
            var a = WhatsHappeningAccess.FromResolved(false, false, new[] { OwningClub }, new[] { OwningRegion });
            Assert.False(a.CanLink(Masked()));
            Assert.True(a.CanLink(Open()));
        }
    }
}
