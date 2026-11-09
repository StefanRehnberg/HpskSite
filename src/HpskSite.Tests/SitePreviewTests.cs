using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Förhandsläget i Hemsidan: vad en biljett får öppna, och att biljetten inte går att förfalska, låna eller återanvända efter utgång.</summary>
    public class SitePreviewTests
    {
        private const string Owner = "/halland/ankeborg-pistolklubb/";
        private static SitePreviewTicket Anon() => new("Club", 9003, "anon", Owner, null, "5513", 0);
        private static SitePreviewTicket Member() => new("Club", 9003, "member", Owner, Owner + "ankskytte/", "5513", 0);

        [Theory]
        [InlineData("/halland/ankeborg-pistolklubb/")]
        [InlineData("/halland/ankeborg-pistolklubb")]
        [InlineData("/halland/ankeborg-pistolklubb/kalender/")]
        [InlineData("/Halland/Ankeborg-Pistolklubb/Banor/")]
        [InlineData("/umbraco/surface/Club/GetUpcomingEvents")]
        [InlineData("/umbraco/surface/ShootingRange/GetClubRanges")]
        [InlineData("/umbraco/surface/CompetitionRecords/ListForClub")]
        [InlineData("/umbraco/surface/SiteFile/Image")]
        public void Utloggad_förhandsvisning_får_öppna_föreningens_sidor_och_de_läsande_dataanropen(string path) =>
            Assert.True(SitePreviewPolicy.PathAllowed(Anon(), path));

        [Theory]
        // Ekonomi, Styrelsearbete och Hemsidan: aldrig, var de än ligger
        [InlineData("/ekonomi")]
        [InlineData("/ekonomi/")]
        [InlineData("/ekonomi?type=0&id=9003")]
        [InlineData("/styrelse")]
        [InlineData("/styrelse/arenden")]
        [InlineData("/halland/ankeborg-pistolklubb/hemsidan/")]
        [InlineData("/halland/ankeborg-pistolklubb/hemsidan")]
        [InlineData("/halland/ankeborg-pistolklubb/ekonomi/")]
        [InlineData("/halland/ankeborg-pistolklubb/styrelse/")]
        [InlineData("/Ekonomi")]
        [InlineData("/EKONOMI/")]
        // adminsidor, backoffice och skrivande anrop
        [InlineData("/admin-page")]
        [InlineData("/admin")]
        [InlineData("/tavlingsplanering/12")]
        [InlineData("/umbraco")]
        [InlineData("/umbraco/surface/SiteEdit/SavePage")]
        [InlineData("/umbraco/surface/SiteEdit/StartPreview")]
        [InlineData("/umbraco/surface/Ledger/Post")]
        [InlineData("/umbraco/api/anything")]
        [InlineData("/login-register/")]
        [InlineData("/user-profile-page/")]
        // en annan förening
        [InlineData("/halland/varbergs-pk/")]
        [InlineData("/halland/")]
        [InlineData("/")]
        [InlineData("/halland/ankeborg-pistolklubbet/")]   // delar prefix men är en annan adress
        // försök att ta sig ut ur föreningens adress
        [InlineData("/halland/ankeborg-pistolklubb/../ekonomi")]
        [InlineData("/halland/ankeborg-pistolklubb/%2e%2e/ekonomi")]
        [InlineData("/halland/ankeborg-pistolklubb/%2E%2E%2Fekonomi")]
        [InlineData("/halland/ankeborg-pistolklubb/..%2fekonomi")]
        [InlineData("/halland/ankeborg-pistolklubb/\\..\\ekonomi")]
        [InlineData("/halland/ankeborg-pistolklubb//ekonomi")]
        [InlineData("//evil.example/halland/ankeborg-pistolklubb/")]
        [InlineData("")]
        [InlineData(null)]
        public void Förhandsläget_öppnar_aldrig_Ekonomi_Styrelse_Hemsidan_adminsidor_eller_andra_föreningar(string? path)
        {
            Assert.False(SitePreviewPolicy.PathAllowed(Anon(), path));
            Assert.False(SitePreviewPolicy.PathAllowed(Member(), path));
        }

        [Fact]
        public void En_annan_besökare_än_utloggad_får_bara_öppna_just_den_sidan_och_inte_klubbens_andra_sidor()
        {
            Assert.True(SitePreviewPolicy.PathAllowed(Member(), "/halland/ankeborg-pistolklubb/ankskytte/"));
            Assert.True(SitePreviewPolicy.PathAllowed(Member(), "/halland/ankeborg-pistolklubb/ankskytte"));
            Assert.False(SitePreviewPolicy.PathAllowed(Member(), "/halland/ankeborg-pistolklubb/"));
            Assert.False(SitePreviewPolicy.PathAllowed(Member(), "/halland/ankeborg-pistolklubb/kalender/"));
            Assert.False(SitePreviewPolicy.PathAllowed(Member(), "/halland/ankeborg-pistolklubb/ankskytte/under/"));
            // en annan besökare utan angiven sida får ingenting
            Assert.False(SitePreviewPolicy.PathAllowed(Member() with { PagePath = null }, "/halland/ankeborg-pistolklubb/ankskytte/"));
        }

        [Theory]
        [InlineData("GET", true)]
        [InlineData("get", true)]
        [InlineData("HEAD", true)]
        [InlineData("POST", false)]
        [InlineData("PUT", false)]
        [InlineData("PATCH", false)]
        [InlineData("DELETE", false)]
        [InlineData("OPTIONS", false)]
        public void Förhandsläget_är_bara_läsning(string method, bool allowed) => Assert.Equal(allowed, SitePreviewPolicy.MethodAllowed(method));

        private static SitePreviewTickets Tickets(string app = "test") => new(DataProtectionProvider.Create(app));

        [Fact]
        public void En_biljett_går_att_läsa_tillbaka_för_den_den_utfärdades_åt()
        {
            var tickets = Tickets(); var now = DateTimeOffset.UtcNow;
            var token = tickets.Issue("Club", 9003, "member", Owner, Owner + "ankskytte/", "5513", now);
            var t = tickets.Read(token, "5513", now.AddMinutes(1));
            Assert.NotNull(t);
            Assert.Equal(("Club", 9003, "member", Owner + "ankskytte/"), (t!.OwnerType, t.OwnerId, t.Persona, t.PagePath));
        }

        [Fact]
        public void En_lånad_biljett_fungerar_inte_för_någon_annan_inloggad_eller_utan_inloggning()
        {
            var tickets = Tickets(); var now = DateTimeOffset.UtcNow;
            var token = tickets.Issue("Club", 9003, "anon", Owner, null, "5513", now);
            Assert.Null(tickets.Read(token, "5514", now));   // en annan medlem
            Assert.Null(tickets.Read(token, null, now));      // ingen inloggad
            Assert.Null(tickets.Read(token, "", now));
        }

        [Fact]
        public void En_biljett_slutar_gälla_efter_en_halvtimme()
        {
            var tickets = Tickets(); var now = DateTimeOffset.UtcNow;
            var token = tickets.Issue("Club", 9003, "anon", Owner, null, "5513", now);
            Assert.NotNull(tickets.Read(token, "5513", now.AddMinutes(29)));
            Assert.Null(tickets.Read(token, "5513", now.AddMinutes(31)));
        }

        [Fact]
        public void En_ändrad_eller_förfalskad_biljett_läses_aldrig()
        {
            var tickets = Tickets(); var now = DateTimeOffset.UtcNow;
            var token = tickets.Issue("Club", 9003, "anon", Owner, null, "5513", now);
            Assert.Null(tickets.Read(token + "A", "5513", now));
            Assert.Null(tickets.Read(token[..^3], "5513", now));
            Assert.Null(tickets.Read("inte-en-biljett", "5513", now));
            Assert.Null(tickets.Read("", "5513", now));
            Assert.Null(tickets.Read(null, "5513", now));
            Assert.Null(tickets.Read(new string('A', 5000), "5513", now));
            // ren JSON utan signatur duger inte
            Assert.Null(tickets.Read("{\"OwnerType\":\"Club\",\"OwnerId\":9003,\"Persona\":\"board\",\"OwnerPath\":\"/\",\"PagePath\":null,\"Member\":\"5513\",\"ExpiresUnix\":99999999999}", "5513", now));
        }

        [Fact]
        public void En_biljett_från_en_annan_nyckelring_eller_en_annan_sort_av_skyddad_data_läses_inte()
        {
            var now = DateTimeOffset.UtcNow;
            var token = Tickets("annan-app").Issue("Club", 9003, "anon", Owner, null, "5513", now);
            Assert.Null(Tickets("test").Read(token, "5513", now));
            // samma nyckelring men fel syfte (till exempel en länk till något helt annat)
            var provider = DataProtectionProvider.Create("test");
            var other = provider.CreateProtector("Planning.Mail.v1").Protect("{\"OwnerType\":\"Club\",\"OwnerId\":9003,\"Persona\":\"anon\",\"OwnerPath\":\"/\",\"PagePath\":null,\"Member\":\"5513\",\"ExpiresUnix\":99999999999}");
            Assert.Null(new SitePreviewTickets(provider).Read(other, "5513", now));
        }
    }
}
