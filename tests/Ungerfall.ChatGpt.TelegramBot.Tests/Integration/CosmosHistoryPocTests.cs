using Microsoft.Azure.Cosmos;
using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
public class CosmosHistoryPocTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [SkippableFact]
    public async Task DedicatedEmbeddingDeploymentReturnsReal512DimensionalVectors()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("HISTORY_POC_LIVE_TEST") == "1", "History PoC live test is not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var embeddings = await HistoryEmbeddingClient.FromEnvironment().EmbedAsync(
                ["Linux listening TCP sockets", "PostgreSQL backup and restore"], timeout.Token);
            Assert.Equal(2, embeddings.Vectors.Length);
            Assert.All(embeddings.Vectors, vector => Assert.Equal(512, vector.Length));
            Assert.False(embeddings.Vectors[0].SequenceEqual(embeddings.Vectors[1]));
            Assert.True(embeddings.InputTokens > 0);
            output.WriteLine($"Dedicated embeddings verified; vectors=2; dimensions=512; inputTokens={embeddings.InputTokens}.");
        }
        catch (ClientResultException error)
        {
            throw new InvalidOperationException($"Embedding smoke test failed: HTTP {error.Status}; provider details withheld.");
        }
    }

    [SkippableFact]
    public async Task RealEmbeddingsAndCosmosProveScopedRetrievalReplayEditsDeletionAndRuCost()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("HISTORY_POC_LIVE_TEST") == "1", "History PoC live test is not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        var embeddings = new HistoryEmbeddingClient(
            Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_ENDPOINT")
                ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_DEPLOYMENT") ?? "",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_API_KEY") ?? "", http);
        using var cosmos = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString")
            ?? throw new InvalidOperationException("CosmosDbConnectionString is required."), new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                ConsistencyLevel = ConsistencyLevel.Session,
                SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
                RequestTimeout = TimeSpan.FromSeconds(30),
                MaxRetryAttemptsOnRateLimitedRequests = 3,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(10),
            });
        CosmosHistoryPocStore? store = null;
        try
        {
            store = await CosmosHistoryPocStore.CreateAsync(cosmos, Environment.GetEnvironmentVariable("CosmosDatabase")
                ?? throw new InvalidOperationException("CosmosDatabase is required."), timeout.Token);
            var facts = new[]
            {
                ("Ravenwood incident: Linux TCP port 43127 was investigated with ss -ltn to list listening sockets.",
                    "Which command did we use to find the listening TCP sockets during the Ravenwood incident?"),
                ("Maplewood restore procedure: PostgreSQL backups use pg_dump and are restored with pg_restore.",
                    "What utilities were chosen to back up and restore the Maplewood PostgreSQL database?"),
                ("Cedarwood retrieval decision: embeddings have 512 dimensions and use the text-embedding-3-small model.",
                    "Which embedding model and vector size did we choose for Cedarwood?"),
                ("Birchwood release policy: update announcements go only to administrators, not to all chat participants.",
                    "Who should receive Birchwood release notifications?"),
            };
            IReadOnlyList<HistoryPocChunk> sample;
            if (Environment.GetEnvironmentVariable("HISTORY_POC_EXPORT") is { Length: > 0 } path)
            {
                using var file = File.OpenRead(path);
                sample = TelegramHistoryPocSample.ReadZip(file);
                Assert.Equal(1000, sample.Count);
            }
            else
            {
                sample = Enumerable.Range(0, 1000).Select(index =>
                {
                    var text = $"Discussion {index}: {facts[index % facts.Length].Item1} "
                        + $"The participants reviewed service availability, backups and permissions for their project {index}.";
                    return new HistoryPocChunk([index + 1], text, Hash(text));
                }).ToArray();
            }
            var documents = sample.Select((chunk, index) => new HistoryPocDocument
            {
                Id = "sample-" + index, ChatId = "poc-chat-a", Text = chunk.Text,
                MessageIds = chunk.MessageIds, Revision = chunk.Revision,
            }).Concat(facts.Select((fact, index) => new HistoryPocDocument
            {
                Id = "fact-" + index, ChatId = "poc-chat-a", Text = fact.Item1,
                MessageIds = [20000 + index], Revision = Hash(fact.Item1),
            })).ToArray();
            var inputTokens = 0;
            var started = Stopwatch.GetTimestamp();
            foreach (var batch in documents.Chunk(32))
            {
                var inputs = batch.Select(document => document.Text).ToArray();
                Assert.True(inputTokens + inputs.Sum(embeddings.CountTokens) <= 500000, "PoC embedding token budget exhausted.");
                var vectors = await embeddings.EmbedAsync(inputs, timeout.Token);
                inputTokens += vectors.InputTokens;
                for (var index = 0; index < batch.Length; index++)
                {
                    batch[index].Embedding = vectors.Vectors[index];
                    await store.SaveAsync(batch[index], null, timeout.Token);
                }
            }
            output.WriteLine($"Indexed documents={documents.Length}; embeddingInputTokens={inputTokens}; writeRU={store.RequestUnits:F2}; elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            var original = await store.ReadAsync("fact-0", "poc-chat-a", timeout.Token);
            var replay = await store.SaveAsync(documents[^4], null, timeout.Token);
            Assert.Equal(original.ETag, replay.ETag);
            var wrongChat = await Assert.ThrowsAsync<CosmosException>(() => store.ReadAsync("fact-0", "poc-chat-b", timeout.Token));
            Assert.Equal(HttpStatusCode.NotFound, wrongChat.StatusCode);
            var foreign = new HistoryPocDocument
            {
                Id = "foreign", ChatId = "poc-chat-b", Text = facts[0].Item1,
                MessageIds = [1], Revision = Hash(facts[0].Item1), Embedding = documents[^4].Embedding,
            };
            await store.SaveAsync(foreign, null, timeout.Token);
            var queries = await embeddings.EmbedAsync(facts.Select(fact => fact.Item2).ToArray(), timeout.Token);
            inputTokens += queries.InputTokens;
            var overlaps = new List<int>();
            for (var index = 0; index < facts.Length; index++)
            {
                started = Stopwatch.GetTimestamp();
                var approximate = await store.SearchAsync("poc-chat-a", queries.Vectors[index], 5, false, timeout.Token);
                var exact = await store.SearchAsync("poc-chat-a", queries.Vectors[index], 5, true, timeout.Token);
                Assert.Contains(approximate.Hits, hit => hit.Id == "fact-" + index);
                Assert.All(approximate.Hits, hit => Assert.Equal("poc-chat-a", hit.ChatId));
                var overlap = approximate.Hits.Select(hit => hit.Id).Intersect(exact.Hits.Select(hit => hit.Id)).Count();
                overlaps.Add(overlap);
                output.WriteLine($"Query={index}; top5Overlap={overlap}/5; approximateRU={approximate.RequestUnits:F2}; exactRU={exact.RequestUnits:F2}; elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            }
            Assert.True(overlaps.Average() >= 4, "Approximate search missed too many exact top-five results.");
            var replacementText = "Ravenwood was resolved with a network configuration change. The former socket command is obsolete.";
            var replacementVector = await embeddings.EmbedAsync([replacementText], timeout.Token);
            inputTokens += replacementVector.InputTokens;
            original.Document.Text = replacementText;
            original.Document.Revision = Hash(replacementText);
            original.Document.Embedding = replacementVector.Vectors[0];
            var changed = await store.SaveAsync(original.Document, original.ETag, timeout.Token);
            Assert.Equal(replacementText, (await store.ReadAsync("fact-0", "poc-chat-a", timeout.Token)).Document.Text);
            var conflict = await Assert.ThrowsAsync<CosmosException>(() => store.SaveAsync(original.Document, original.ETag, timeout.Token));
            Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
            await store.DeleteAsync("fact-0", "poc-chat-a", changed.ETag, timeout.Token);
            var deleted = await Assert.ThrowsAsync<CosmosException>(() => store.ReadAsync("fact-0", "poc-chat-a", timeout.Token));
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
            var afterDelete = await store.SearchAsync("poc-chat-a", queries.Vectors[0], 5, false, timeout.Token);
            Assert.DoesNotContain(afterDelete.Hits, hit => hit.Id == "fact-0" || hit.Id == "foreign");
            output.WriteLine($"PoC passed: four factual queries; replay/partition/ETag/delete checks; embeddingInputTokens={inputTokens}; observedRU={store.RequestUnits:F2}; private content omitted.");
        }
        catch (CosmosException error)
        {
            throw new InvalidOperationException($"Cosmos history PoC failed: HTTP {(int)error.StatusCode}; provider details withheld.");
        }
        catch (ClientResultException error)
        {
            throw new InvalidOperationException($"Embedding history PoC failed: HTTP {error.Status}; provider details withheld.");
        }
        finally
        {
            if (store != null)
            {
                try
                {
                    await store.DisposeAsync();
                    output.WriteLine($"Disposable container deleted; total observed RU including cleanup={store.RequestUnits:F2}.");
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException($"PoC container cleanup failed ({error.GetType().Name}); documents have a one-hour TTL.");
                }
            }
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
