using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class HistoryPocTests
{
    [Fact]
    public async Task UnrelatedBadRequestMentioningServerlessDoesNotAuthorizeContainerCreation()
    {
        using var client = Substitute.For<CosmosClient>();
        var database = Substitute.For<Microsoft.Azure.Cosmos.Database>();
        client.GetDatabase("serverless-named-db").Returns(database);
        database.ReadThroughputAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<int?>(
            new CosmosException("Invalid request for serverless-named-db", HttpStatusCode.BadRequest, 0, "test", 1)));
        await Assert.ThrowsAsync<CosmosException>(() =>
            CosmosHistoryPocStore.CreateAsync(client, "serverless-named-db", default));
        await database.DidNotReceiveWithAnyArgs().CreateContainerAsync(default(ContainerProperties)!, default(int?), default(RequestOptions), default);
    }

    [Fact]
    public void ExportSampleFlattensEntitiesAndPreservesOrderAndTopicBoundaries()
    {
        using var export = JsonDocument.Parse("""
        {"messages":[
          {"id":1,"type":"message","date":"2026-11-20T01:00:00","text":["first ",{"type":"bold","text":"message"}]},
          {"id":2,"type":"message","date":"2026-11-20T02:00:00","text":"second"},
          {"id":3,"type":"message","date":"2026-11-20T03:00:00","reply_to_top_id":50,"text":"another topic"},
          {"id":4,"type":"service","date":"2026-11-20T04:00:00","text":""},
          {"id":5,"type":"message","date":"2026-11-21T01:00:00","text":"new day"}
        ]}
        """);
        var chunks = TelegramHistoryPocSample.Read(export.RootElement, 10);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new long[] { 1, 2 }, chunks[0].MessageIds);
        Assert.Equal("first message\nsecond", chunks[0].Text);
        Assert.Equal(new long[] { 3 }, chunks[1].MessageIds);
        Assert.Equal(new long[] { 5 }, chunks[2].MessageIds);
    }

    [Fact]
    public void ExportSampleIsBoundedToRecentChunksAndDeterministic()
    {
        var text = new string('a', 900);
        using var export = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            messages = Enumerable.Range(1, 8).Select(id => new { id, type = "message", date = "2026-11-20T01:00:00", text }),
        }));
        var first = TelegramHistoryPocSample.Read(export.RootElement, 2);
        var second = TelegramHistoryPocSample.Read(export.RootElement, 2);
        Assert.Equal(2, first.Count);
        Assert.Equal(new long[] { 7 }, first[0].MessageIds);
        Assert.Equal(new long[] { 8 }, first[1].MessageIds);
        Assert.Equal(first[0].Revision, second[0].Revision);
        Assert.All(first, chunk => Assert.True(chunk.Text.Length <= 1500));
    }

    [Fact]
    public void ZipIsReadWithoutExtractingPathsAndAmbiguousExportsAreRejected()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in new[] { "../result.json", "other/result.json" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write("""{"messages":[]}""");
            }
        }
        bytes.Position = 0;
        Assert.Throws<InvalidDataException>(() => TelegramHistoryPocSample.ReadZip(bytes));
    }

    [Theory]
    [InlineData("http://example.org/")]
    [InlineData("https://example.org/path")]
    [InlineData("https://secret@example.org/")]
    [InlineData("https://example.org/?key=secret")]
    public void EmbeddingEndpointRejectsUnsafeOrDeploymentSpecificAddresses(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new HistoryEmbeddingClient(endpoint, "bot-embeddings", "test-key"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingClientSerializesDimensionsAndUsesDedicatedCredential(bool reversed)
    {
        using var http = new HttpClient(new EmbeddingResponse(reversed));
        var client = new HistoryEmbeddingClient("https://example.org/", "bot-embeddings", "embedding-key", http);
        var result = await client.EmbedAsync(["first", "second"], default);
        Assert.Equal(2, result.Vectors.Length);
        Assert.Equal(7, result.InputTokens);
        Assert.Equal(512, result.Vectors[0].Length);
        Assert.Equal(1f, result.Vectors[0][0]);
        Assert.Equal(2f, result.Vectors[1][0]);
    }

    [Fact]
    public async Task EmbeddingClientRejectsDuplicateProviderIndices()
    {
        using var http = new HttpClient(new EmbeddingResponse(duplicateIndices: true));
        var client = new HistoryEmbeddingClient("https://example.org/", "bot-embeddings", "embedding-key", http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(["first", "second"], default));
    }

    [Fact]
    public async Task EmbeddingClientRejectsOversizedInputsBeforeSending()
    {
        using var http = new HttpClient(new EmbeddingResponse());
        var client = new HistoryEmbeddingClient("https://example.org/", "bot-embeddings", "embedding-key", http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync([new string('x', 8001)], default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync(Enumerable.Repeat("x", 33).ToArray(), default));
    }

    private sealed class EmbeddingResponse(bool reversed = false, bool duplicateIndices = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal("/openai/v1/embeddings", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("embedding-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal("bot-embeddings", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(512, body.RootElement.GetProperty("dimensions").GetInt32());
            Assert.Equal(2, body.RootElement.GetProperty("input").GetArrayLength());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    @object = "list", model = "text-embedding-3-small",
                    data = (reversed ? new[] { 1, 0 } : new[] { 0, 1 }).Select(position => new
                    {
                        @object = "embedding", index = duplicateIndices ? 0 : position,
                        embedding = Convert.ToBase64String(Enumerable.Repeat((float)(position + 1), 512)
                            .SelectMany(BitConverter.GetBytes).ToArray()),
                    }),
                    usage = new { prompt_tokens = 7, total_tokens = 7 },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
