using FluentAssertions;
using HpskSite.Services.AiChat;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// AI-chattens sökning: hur kunskapsbasen delas i stycken och vilka stycken en fråga får med sig.
    ///
    /// <para>Före sökningen skickades hela kunskapsbasen (~115 000 tokens) med varje fråga, för
    /// ~€0,18 per fråga. Testerna här mäter de rena funktionerna; anropet mot embeddings-API:t
    /// prövas i dev mot Mistral.</para>
    /// </summary>
    public class KnowledgeRetrievalTests
    {
        private static KnowledgeBaseDoc Doc(string content, string file = "test.md", params string[] roles)
            => new() { FileName = file, Content = content, Roles = roles.Length > 0 ? roles.ToList() : new List<string> { "public" } };

        [Fact]
        public void Split_DelarVidH2OchH3_OchBarRubrikvagen()
        {
            var doc = Doc("# Anmälan\nInledning.\n\n## Betalning\nOm betalning.\n\n### Swish\nScanna koden.\n\n## Avbokning\nRing klubben.");

            var chunks = KnowledgeChunker.Split(doc);

            chunks.Select(c => c.Path).Should().Equal(
                "Anmälan",
                "Anmälan › Betalning",
                "Anmälan › Betalning › Swish",
                "Anmälan › Avbokning");
            chunks[2].Body.Should().Be("Scanna koden.");
        }

        [Fact]
        public void Split_DjupareRubrikerStannarIStycket()
        {
            var chunks = KnowledgeChunker.Split(Doc("# T\n## Steg\n#### Steg 1\nGör så.\n#### Steg 2\nSen så."));

            chunks.Should().ContainSingle();
            chunks[0].Body.Should().Contain("#### Steg 1").And.Contain("#### Steg 2");
        }

        [Fact]
        public void Split_RubrikIKodblockArIngenGrans()
        {
            var chunks = KnowledgeChunker.Split(Doc("# T\n## Exempel\n```\n## inte en rubrik\n```\nSlut."));

            chunks.Should().ContainSingle();
            chunks[0].Body.Should().Contain("## inte en rubrik");
        }

        [Fact]
        public void Split_TomRubrikGerInget_TomtStycke()
        {
            var chunks = KnowledgeChunker.Split(Doc("# T\n## Tom\n## Med text\nInnehåll."));

            chunks.Should().ContainSingle().Which.Heading.Should().Be("Med text");
        }

        [Fact]
        public void Split_StycketArverDokumentetsRoller()
        {
            var chunks = KnowledgeChunker.Split(Doc("# T\n## A\nText.", "x.md", "club-admin"));

            chunks.Single().Roles.Should().Equal("club-admin");
        }

        [Fact]
        public void Split_LangtStyckeDelasUtanAttTextTappas()
        {
            var paragraphs = Enumerable.Range(1, 40).Select(i => $"Paragraf {i} " + new string('x', 200)).ToList();
            var chunks = KnowledgeChunker.Split(Doc("# T\n## Lång\n" + string.Join("\n\n", paragraphs)));

            chunks.Should().HaveCountGreaterThan(1);
            chunks.Should().OnlyContain(c => c.Body.Length <= KnowledgeChunker.MaxChunkChars);
            chunks.Should().OnlyContain(c => c.Heading == "Lång");
            foreach (var p in paragraphs)
                chunks.Count(c => c.Body.Contains(p)).Should().Be(1, $"'{p.Substring(0, 12)}' ska finnas exakt en gång");
        }

        [Fact]
        public void Hash_FoljerInnehalletInteFilenEllerPositionen()
        {
            var a = KnowledgeChunker.Split(Doc("# T\n## A\nSamma text.", "a.md"))[0];
            var b = KnowledgeChunker.Split(Doc("# T\n## Ny\nAnnat.\n## A\nSamma text.", "a.md"))[1];
            var changed = KnowledgeChunker.Split(Doc("# T\n## A\nÄndrad text.", "a.md"))[0];

            b.Hash("m").Should().Be(a.Hash("m"), "ett oförändrat stycke behåller sin vektor när andra läggs till");
            changed.Hash("m").Should().NotBe(a.Hash("m"), "ändrad text måste räknas om");
            a.Hash("annan-modell").Should().NotBe(a.Hash("m"), "vektorer från olika modeller går inte att blanda");
        }

        [Fact]
        public void Cosine_LikaRiktningArEtt_VinkelratArNoll()
        {
            KnowledgeRetrieval.Cosine(new[] { 1f, 2f }, new[] { 2f, 4f }).Should().BeApproximately(1, 1e-9);
            KnowledgeRetrieval.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }).Should().BeApproximately(0, 1e-9);
            KnowledgeRetrieval.Cosine(new[] { 0f, 0f }, new[] { 1f, 1f }).Should().Be(0);
        }

        private static KnowledgeChunk Chunk(string heading, int len, int order = 0, string file = "f.md")
            => new(file, "Titel", heading, new string('x', len), new List<string> { "public" }, order);

        [Fact]
        public void SelectTop_TarBastForstInomBadaTaken()
        {
            var scored = new[] { (Chunk("C", 100), 0.5), (Chunk("A", 100), 0.9), (Chunk("B", 100), 0.7), (Chunk("D", 100), 0.1) };

            KnowledgeRetrieval.SelectTop(scored, maxChunks: 2, maxChars: 10000)
                .Select(c => c.Heading).Should().Equal("A", "B");
        }

        [Fact]
        public void SelectTop_HopparOverForStort_MenFortsatter()
        {
            var big = Chunk("Stor", 5000);
            var scored = new[] { (Chunk("A", 100), 0.9), (big, 0.8), (Chunk("C", 100), 0.7) };

            KnowledgeRetrieval.SelectTop(scored, maxChunks: 10, maxChars: 1000)
                .Select(c => c.Heading).Should().Equal("A", "C");
        }

        [Fact]
        public void SelectTop_BastaStycketTasAlltidMed_AvenOmDetArForLangt()
        {
            var scored = new[] { (Chunk("Stor", 5000), 0.9), (Chunk("Liten", 100), 0.5) };

            KnowledgeRetrieval.SelectTop(scored, maxChunks: 10, maxChars: 1000)
                .Select(c => c.Heading).Should().Equal("Stor");
        }

        [Fact]
        public void Format_GrupperarPerDokument_OchSkriverILasordning()
        {
            var picked = new List<KnowledgeChunk>
            {
                Chunk("Steg 3", 1, order: 3, file: "a.md"),
                Chunk("Annat", 1, order: 0, file: "b.md"),
                Chunk("Steg 1", 1, order: 1, file: "a.md"),
            };

            var text = KnowledgeRetrieval.Format(picked);

            text.IndexOf("Steg 1", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Steg 3", StringComparison.Ordinal));
            text.IndexOf("Steg 3", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Annat", StringComparison.Ordinal),
                "dokumentet med bäst träff (a.md) kommer först");
        }

        [Fact]
        public void BuildQuery_TarMedForegaendeFraga_ForFoljdfragor()
        {
            var history = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hur efteranmäler jag en skytt?" },
                new() { Role = "assistant", Content = "Så här..." },
            };

            KnowledgeRetrieval.BuildQuery("Och på mobilen?", history)
                .Should().Be("Hur efteranmäler jag en skytt?\nOch på mobilen?");
            KnowledgeRetrieval.BuildQuery("Hej", new List<ChatMessage>()).Should().Be("Hej");
        }

        [Fact]
        public void Options_HarlederMistralsEmbeddingsEndpointOchModell()
        {
            var o = new AiChatOptions { Provider = "Mistral", Endpoint = "https://api.mistral.ai/v1/chat/completions" };

            o.ResolveEmbeddingEndpoint().Should().Be("https://api.mistral.ai/v1/embeddings");
            o.ResolveEmbeddingModel().Should().Be("mistral-embed");
            o.RetrievalSupported.Should().BeTrue();
            new AiChatOptions { Provider = "Claude" }.RetrievalSupported.Should().BeFalse();
        }
    }
}
