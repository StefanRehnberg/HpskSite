using System.Text;
using Azure.Storage.Blobs;
using FluentAssertions;
using HpskSite.Services.AiChat;
using HpskSite.Services.Storage;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Körs mot en blobblagring när <c>HPSK_TEST_BLOB</c> är satt (en anslutningssträng, t.ex. Azurite:
    /// <c>UseDevelopmentStorage=true</c>), annars hoppas de över MED NAMN.
    /// </summary>
    public sealed class BlobFactAttribute : FactAttribute
    {
        public BlobFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HPSK_TEST_BLOB")))
                Skip = "HPSK_TEST_BLOB är inte satt — kör mot Azurite eller en blobblagring för att pröva blobbläget.";
        }
    }

    /// <summary>Skalplanen D3: fillagringen. Samma kontrakt prövas mot disken och mot blobblagringen.</summary>
    public class FileStoreTests
    {
        // ── Namnregeln ──

        [Theory]
        [InlineData("abc.pdf")]
        [InlineData("3f2a9c.jpg")]
        [InlineData("123/st1_A_tg1.png")]
        [InlineData("utkast/8315/st2_C_tg1.jpg")]
        [InlineData("1-3 bla.jpg")]                  // figurkatalogen har mellanslag
        [InlineData("foreningsinstruktor/01-intro/lektion.html")]
        [InlineData("Lönsboda.pdf")]
        public void Giltiga_namn_godtas(string name) => FileStoreNames.IsValidName(name).Should().BeTrue();

        [Theory]
        [InlineData("")]
        [InlineData("../x.pdf")]
        [InlineData("a/../b.pdf")]
        [InlineData("..")]
        [InlineData("/abs.pdf")]
        [InlineData("a//b.pdf")]
        [InlineData("a\\b.pdf")]
        [InlineData(".hidden")]
        [InlineData(" leading.pdf")]
        [InlineData("trailing.pdf ")]
        [InlineData("c:evil.pdf")]
        [InlineData("a?b.pdf")]
        public void Namn_som_kan_peka_ut_vägras(string name) => FileStoreNames.IsValidName(name).Should().BeFalse();

        [Fact]
        public void Ett_uppladdat_namn_blir_ett_giltigt_segment()
        {
            FileStoreNames.SafeFileName("Klubbens logga (1)").Should().Be("Klubbens-logga-1");
            FileStoreNames.SafeFileName("Lönsboda årsmöte").Should().Be("Lönsboda-årsmöte");
            FileStoreNames.IsValidName(FileStoreNames.SafeFileName("../../etc/passwd")).Should().BeTrue();
            FileStoreNames.SafeFileName("").Should().Be("fil");
        }

        [Theory]
        [InlineData("/media/profile-pictures/1_2.jpg", "profile-pictures/1_2.jpg")]
        [InlineData("/media/event-images/Sommar (1)_abc.jpg", "event-images/Sommar (1)_abc.jpg")]
        [InlineData("/media/x/y.pdf?v=3", "x/y.pdf")]
        [InlineData("/media/a%20b/c.jpg", "a b/c.jpg")]
        public void Mediaadressen_blir_en_sökväg(string url, string expected) =>
            MediaFiles.PathFromUrl(url).Should().Be(expected);

        [Theory]
        [InlineData(null)]
        [InlineData("/images/uploads/x.jpg")]           // utanför mediamappen
        [InlineData("/media/../appsettings.json")]
        [InlineData("/media/%2e%2e/web.config")]
        [InlineData("https://evil.example/media/x.jpg")]
        public void En_adress_utanför_mediamappen_ger_ingen_sökväg(string? url) =>
            MediaFiles.PathFromUrl(url).Should().BeNull();

        [Theory]
        [InlineData("/images/uploads/abc.jpg", "uploads", "abc.jpg")]
        [InlineData("/images/field-targets/1-3%20bla.jpg", "field-targets", "1-3 bla.jpg")]
        [InlineData("/images/faltskytte/123/st1_A_tg1.png", "faltskytte", "123/st1_A_tg1.png")]
        [InlineData("/IMAGES/Faltkonfig/7/x.jpg", "faltkonfig", "7/x.jpg")]
        public void Publika_bildadresser_tolkas(string path, string area, string name)
        {
            PublicImageMiddleware.TryParse(path, out var a, out var n).Should().BeTrue();
            a.Should().Be(area);
            n.Should().Be(name);
        }

        [Theory]
        [InlineData("/images/HPSK_Logo.png")]             // inte ett område
        [InlineData("/images/marken/x.png")]              // statisk, inte ett område
        [InlineData("/images/uploads/../../appsettings.json")]
        [InlineData("/media/uploads/x.jpg")]
        public void Annat_än_publika_områden_lämnas_till_de_statiska_filerna(string path) =>
            PublicImageMiddleware.TryParse(path, out _, out _).Should().BeFalse();

        // ── Kontraktet ──

        private static async Task Exercise(IFileStore store, string area)
        {
            var name = $"test/{Guid.NewGuid():N}.txt";
            (await store.ExistsAsync(area, name)).Should().BeFalse();
            (await store.OpenReadAsync(area, name)).Should().BeNull();

            await store.SaveAsync(area, name, new MemoryStream(Encoding.UTF8.GetBytes("första")));
            (await store.ExistsAsync(area, name)).Should().BeTrue();
            (await ReadAll(store, area, name)).Should().Be("första");

            // Save skriver över; CreateIfAbsent rör inte en befintlig fil.
            await store.SaveAsync(area, name, new MemoryStream(Encoding.UTF8.GetBytes("andra")));
            (await store.CreateIfAbsentAsync(area, name, new MemoryStream(Encoding.UTF8.GetBytes("tredje")))).Should().BeFalse();
            (await ReadAll(store, area, name)).Should().Be("andra");

            var other = $"test/{Guid.NewGuid():N}.txt";
            (await store.CreateIfAbsentAsync(area, other, new MemoryStream(Encoding.UTF8.GetBytes("x")))).Should().BeTrue();

            var listed = new List<StoredFileInfo>();
            await foreach (var f in store.ListAsync(area, "test/")) listed.Add(f);
            listed.Select(f => f.Name).Should().Contain(new[] { name, other });
            listed.Single(f => f.Name == name).Size.Should().Be(Encoding.UTF8.GetByteCount("andra"));

            (await store.DeleteAsync(area, name)).Should().BeTrue();
            (await store.DeleteAsync(area, name)).Should().BeFalse();
            (await store.ExistsAsync(area, name)).Should().BeFalse();
            await store.DeleteAsync(area, other);

            // Ett namn som kan peka ut läses aldrig, och skrivs aldrig.
            (await store.OpenReadAsync(area, "../appsettings.json")).Should().BeNull();
            var act = () => store.SaveAsync(area, "../x.txt", new MemoryStream());
            await act.Should().ThrowAsync<ArgumentException>();
        }

        private static async Task<string> ReadAll(IFileStore store, string area, string name)
        {
            await using var s = await store.OpenReadAsync(area, name);
            s.Should().NotBeNull();
            using var r = new StreamReader(s!);
            return await r.ReadToEndAsync();
        }

        [Fact]
        public async Task Disken_håller_kontraktet_för_privata_och_publika_områden()
        {
            var root = Path.Combine(Path.GetTempPath(), "hpsk-fs-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DiskFileStore(Path.Combine(root, "App_Data"), Path.Combine(root, "wwwroot", "images"));
                await Exercise(store, FileStoreAreas.LedgerAttachments);
                await Exercise(store, FileStoreAreas.Uploads);

                // Områdena ligger där sajten alltid haft dem.
                await store.SaveAsync(FileStoreAreas.Uploads, "a.jpg", new MemoryStream(new byte[] { 1 }));
                File.Exists(Path.Combine(root, "wwwroot", "images", "uploads", "a.jpg")).Should().BeTrue();
                await store.SaveAsync(FileStoreAreas.StandardMedalProofs, "b.pdf", new MemoryStream(new byte[] { 1 }));
                File.Exists(Path.Combine(root, "App_Data", "standard-medal-proofs", "b.pdf")).Should().BeTrue();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [BlobFact]
        public async Task Blobblagringen_håller_samma_kontrakt()
        {
            var container = new BlobContainerClient(Environment.GetEnvironmentVariable("HPSK_TEST_BLOB"), "hpsk-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new AzureBlobFileStore(container);
                await Exercise(store, FileStoreAreas.LedgerAttachments);
                await Exercise(store, FileStoreAreas.Uploads);

                // Samma uppdelning som på disk.
                await store.SaveAsync(FileStoreAreas.Uploads, "a.jpg", new MemoryStream(new byte[] { 1 }));
                (await container.GetBlobClient("images/uploads/a.jpg").ExistsAsync()).Value.Should().BeTrue();
                await store.SaveAsync(FileStoreAreas.StandardMedalProofs, "b.pdf", new MemoryStream(new byte[] { 1 }));
                (await container.GetBlobClient("app-data/standard-medal-proofs/b.pdf").ExistsAsync()).Value.Should().BeTrue();
            }
            finally { await container.DeleteIfExistsAsync(); }
        }

        [Theory]
        [InlineData("ABC123_8315_1_20261019.jpg", "ABC123_*.jpg", true)]
        [InlineData("abc123_8315_1_20261019.JPG", "ABC123_*.jpg", true)]
        [InlineData("ABC1234_8315_1_20261019.jpg", "ABC123_*.jpg", false)]   // en annan match
        [InlineData("XABC123_1.jpg", "ABC123_*.jpg", false)]
        [InlineData("ABC123_1.png", "ABC123_*.jpg", false)]
        [InlineData("A.B(1)_x.jpg", "A.B(1)_*.jpg", true)]                   // specialtecken är bokstavliga
        public void Målbildsstädningens_mönster(string file, string pattern, bool expected) =>
            MediaFiles.Matches(file, pattern).Should().Be(expected);

        // ── AI-chattens gamla loggfiler ──

        [Fact]
        public void Gamla_chattloggen_läses_med_svar_som_innehåller_linjer()
        {
            var text = "[2026-09-01 10:00:00]\nK: a; b\nQ: Hur?\nflera rader\nA: Så här.\n\n---\n\nmer svar\n---\n" +
                       "[2026-09-02 11:30:00] gammal@epost.se\nQ: Andra\nA: Svar 2\n---\n";
            var e = AiChatLogService.ParseLegacy(text);
            e.Should().HaveCount(2);
            e[0].Question.Should().Be("Hur?\nflera rader");
            e[0].Answer.Should().Be("Så här.\n\n---\n\nmer svar");
            e[0].AskedUtc.Should().Be(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
            e[0].AskedUtc.Kind.Should().Be(DateTimeKind.Utc);
            e[1].Answer.Should().Be("Svar 2");
        }
    }
}
