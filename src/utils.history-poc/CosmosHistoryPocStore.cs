using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryPocDocument
{
    public string Id { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string Text { get; set; } = "";
    public long[] MessageIds { get; set; } = [];
    public string Revision { get; set; } = "";
    public float[] Embedding { get; set; } = [];
}

public sealed class HistoryPocHit
{
    public string Id { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Revision { get; set; } = "";
    public double Distance { get; set; }
}

public sealed record HistoryPocRead(HistoryPocDocument Document, string ETag);
public sealed record HistoryPocSearch(IReadOnlyList<HistoryPocHit> Hits, double RequestUnits);

// This intentionally creates only disposable test containers and is not registered in the bot.
public sealed class CosmosHistoryPocStore : IAsyncDisposable
{
    private readonly Container _container;
    private CosmosHistoryPocStore(Container container) => _container = container;
    public double RequestUnits { get; private set; }
    public const double MaximumRequestUnits = 50000;

    public static async Task<CosmosHistoryPocStore> CreateAsync(CosmosClient client, string databaseId,
        CancellationToken cancellation)
    {
        var database = client.GetDatabase(databaseId);
        var serverless = false;
        try
        {
            await database.ReadThroughputAsync(cancellationToken: cancellation);
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.BadRequest
            && (error.Message.Contains("not supported for serverless accounts", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("not allowed in serverless accounts", StringComparison.OrdinalIgnoreCase)))
        {
            serverless = true;
        }
        if (!serverless)
            throw new InvalidOperationException("The account was not verified as serverless. No container was created.");
        var properties = new ContainerProperties("history-poc-" + Guid.NewGuid().ToString("N"), "/chatId")
        {
            DefaultTimeToLive = 3600,
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
            {
                new()
                {
                    Path = "/embedding", DataType = VectorDataType.Float32,
                    Dimensions = HistoryEmbeddingClient.Dimensions, DistanceFunction = DistanceFunction.Cosine,
                },
            }),
            IndexingPolicy = new IndexingPolicy
            {
                IncludedPaths = { new IncludedPath { Path = "/*" } },
                ExcludedPaths = { new ExcludedPath { Path = "/embedding/*" }, new ExcludedPath { Path = "/text/?" } },
                VectorIndexes = { new VectorIndexPath { Path = "/embedding", Type = VectorIndexType.DiskANN } },
            },
        };
        ContainerResponse response;
        try
        {
            response = await database.CreateContainerAsync(properties, cancellationToken: cancellation);
        }
        catch (CosmosException error)
        {
            var details = error.ResponseBody ?? "";
            var reason = details.Contains("EnableNoSQLVectorSearch", StringComparison.OrdinalIgnoreCase)
                || (details.Contains("capability", StringComparison.OrdinalIgnoreCase)
                    && details.Contains("not", StringComparison.OrdinalIgnoreCase)
                    && details.Contains("enabled", StringComparison.OrdinalIgnoreCase))
                ? "vector_account_capability"
                : details.Contains("serverless", StringComparison.OrdinalIgnoreCase) ? "serverless_vector_compatibility"
                : details.Contains("index", StringComparison.OrdinalIgnoreCase) ? "vector_index_policy"
                : "container_configuration";
            throw new InvalidOperationException($"PoC container creation failed: HTTP {(int)error.StatusCode}; reason={reason}. Provider details withheld.");
        }
        var store = new CosmosHistoryPocStore(response.Container);
        store.Charge(response.RequestCharge);
        return store;
    }

    public async Task<HistoryPocRead> SaveAsync(HistoryPocDocument document, string? etag, CancellationToken cancellation)
    {
        Validate(document);
        CheckBudget();
        try
        {
            ItemResponse<HistoryPocDocument> response;
            if (etag == null)
                response = await _container.CreateItemAsync(document, new PartitionKey(document.ChatId),
                    cancellationToken: cancellation);
            else
                response = await _container.ReplaceItemAsync(document, document.Id, new PartitionKey(document.ChatId),
                    new ItemRequestOptions { IfMatchEtag = etag }, cancellation);
            Charge(response.RequestCharge);
            return new(response.Resource, response.ETag);
        }
        catch (CosmosException error)
        {
            Charge(error.RequestCharge);
            if (error.StatusCode != HttpStatusCode.Conflict || etag != null) throw;
            var existing = await ReadAsync(document.Id, document.ChatId, cancellation);
            if (existing.Document.Text != document.Text || existing.Document.Revision != document.Revision
                || !existing.Document.MessageIds.SequenceEqual(document.MessageIds)
                || !existing.Document.Embedding.SequenceEqual(document.Embedding))
                throw new InvalidOperationException("The idempotency key already contains a different revision.");
            return existing;
        }
    }

    public async Task<HistoryPocRead> ReadAsync(string id, string authorizedChatId, CancellationToken cancellation)
    {
        CheckBudget();
        try
        {
            var response = await _container.ReadItemAsync<HistoryPocDocument>(id, new PartitionKey(authorizedChatId),
                cancellationToken: cancellation);
            Charge(response.RequestCharge);
            return new(response.Resource, response.ETag);
        }
        catch (CosmosException error) { Charge(error.RequestCharge); throw; }
    }

    public async Task DeleteAsync(string id, string authorizedChatId, string etag, CancellationToken cancellation)
    {
        CheckBudget();
        try
        {
            var response = await _container.DeleteItemAsync<HistoryPocDocument>(id, new PartitionKey(authorizedChatId),
                new ItemRequestOptions { IfMatchEtag = etag }, cancellation);
            Charge(response.RequestCharge);
        }
        catch (CosmosException error) { Charge(error.RequestCharge); throw; }
    }

    public async Task<HistoryPocSearch> SearchAsync(string authorizedChatId, float[] vector, int limit,
        bool exact, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(authorizedChatId) || limit is < 1 or > 5) throw new ArgumentException("Invalid search scope or limit.");
        ValidateVector(vector);
        var distance = exact ? "VectorDistance(c.embedding, @vector, true)" : "VectorDistance(c.embedding, @vector)";
        var query = new QueryDefinition(
            $"SELECT TOP @limit c.id, c.chatId, c.text, c.revision, {distance} AS distance FROM c "
            + $"WHERE c.chatId = @chat ORDER BY {distance}")
            .WithParameter("@limit", limit).WithParameter("@vector", vector).WithParameter("@chat", authorizedChatId);
        using var pages = _container.GetItemQueryIterator<HistoryPocHit>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(authorizedChatId), MaxItemCount = limit, MaxConcurrency = 1,
        });
        var hits = new List<HistoryPocHit>();
        double requestUnits = 0;
        while (pages.HasMoreResults)
        {
            CheckBudget();
            try
            {
                var page = await pages.ReadNextAsync(cancellation);
                Charge(page.RequestCharge);
                requestUnits += page.RequestCharge;
                hits.AddRange(page);
            }
            catch (CosmosException error) { Charge(error.RequestCharge); throw; }
        }
        if (hits.Count > limit || hits.Any(hit => hit.ChatId != authorizedChatId))
            throw new InvalidOperationException("Search returned an invalid scope or result count.");
        return new(hits, requestUnits);
    }

    private static void Validate(HistoryPocDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.ChatId)
            || string.IsNullOrWhiteSpace(document.Text) || document.Text.Length > 8000
            || string.IsNullOrWhiteSpace(document.Revision) || document.MessageIds.Length is < 1 or > 10)
            throw new ArgumentException("Invalid PoC document.");
        ValidateVector(document.Embedding);
    }

    private static void ValidateVector(float[] vector)
    {
        if (vector.Length != HistoryEmbeddingClient.Dimensions || vector.Any(value => !float.IsFinite(value))
            || vector.All(value => value == 0))
            throw new ArgumentException("Invalid query or document vector.");
    }

    private void CheckBudget()
    {
        if (RequestUnits >= MaximumRequestUnits)
            throw new InvalidOperationException("PoC observed RU budget exhausted.");
    }

    private void Charge(double requestUnits) => RequestUnits += requestUnits;

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await _container.DeleteContainerAsync(cancellationToken: cleanup.Token);
        Charge(response.RequestCharge);
    }
}
