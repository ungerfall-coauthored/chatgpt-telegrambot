using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Azure.Cosmos;
using Ungerfall.ChatGpt.TelegramBot.History;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var options = HistoryOptions.FromEnvironment();
if (options.Chats.Count == 0) throw new InvalidOperationException("History chats must be configured explicitly.");
using var client = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString")
    ?? throw new InvalidOperationException("CosmosDbConnectionString is required."));
var database = client.GetDatabase(Environment.GetEnvironmentVariable("CosmosDatabase")
    ?? throw new InvalidOperationException("CosmosDatabase is required."));
try
{
    var serverless = false;
    try
    {
        await database.ReadThroughputAsync(cancellationToken: timeout.Token);
    }
    catch (CosmosException error) when (error.StatusCode == HttpStatusCode.BadRequest
        && (error.Message.Contains("not supported for serverless accounts", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("not allowed in serverless accounts", StringComparison.OrdinalIgnoreCase)))
    {
        serverless = true;
    }
    if (!serverless) throw new InvalidOperationException("Serverless was not verified; no container created.");
    foreach (var vector in new[] { false, true })
    {
        var name = vector ? options.ChunksContainer : options.MessagesContainer;
        var properties = new ContainerProperties(name, "/chatId") { DefaultTimeToLive = -1 };
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = vector ? "/embedding/*" : "/text/?" });
        if (vector)
        {
            properties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
            {
                new() { Path = "/embedding", DataType = VectorDataType.Float32, Dimensions = 512, DistanceFunction = DistanceFunction.Cosine },
            });
            properties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath { Path = "/embedding", Type = VectorIndexType.QuantizedFlat });
        }
        try
        {
            var existing = (await database.GetContainer(name).ReadContainerAsync(cancellationToken: timeout.Token)).Resource;
            if (existing.PartitionKeyPath != "/chatId" || existing.DefaultTimeToLive != -1
                || vector && (existing.VectorEmbeddingPolicy?.Embeddings.Count != 1
                    || existing.VectorEmbeddingPolicy.Embeddings[0].Dimensions != 512
                    || existing.VectorEmbeddingPolicy.Embeddings[0].Path != "/embedding"
                    || existing.VectorEmbeddingPolicy.Embeddings[0].DataType != VectorDataType.Float32
                    || existing.VectorEmbeddingPolicy.Embeddings[0].DistanceFunction != DistanceFunction.Cosine
                    || existing.IndexingPolicy.VectorIndexes.Count != 1
                    || !existing.IndexingPolicy.VectorIndexes.Any(index => index.Path == "/embedding"
                        && index.Type == VectorIndexType.QuantizedFlat))
                || !existing.IndexingPolicy.ExcludedPaths.Any(path => path.Path == (vector ? "/embedding/*" : "/text/?"))
                || !existing.IndexingPolicy.IncludedPaths.Any(path => path.Path == "/*")
                || !existing.IndexingPolicy.Automatic || existing.IndexingPolicy.IndexingMode != IndexingMode.Consistent)
                throw new InvalidOperationException("Existing history container policy differs; nothing was changed.");
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            await database.CreateContainerAsync(properties, cancellationToken: timeout.Token);
        }
    }
    Console.WriteLine("Persistent history containers verified; serverless retained; no throughput requested.");
}
catch (CosmosException error)
{
    throw new InvalidOperationException($"History provisioning failed: HTTP {(int)error.StatusCode}; provider details withheld.");
}
