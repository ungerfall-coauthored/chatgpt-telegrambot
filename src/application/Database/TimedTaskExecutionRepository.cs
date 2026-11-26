using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;

namespace Ungerfall.ChatGpt.TelegramBot.Database;
public class TimedTaskExecutionRepository(
    CosmosClient cosmos,
    IOptions<CosmosDbOptions> options) : ITimedTaskExecutionRepository
{
    private readonly CosmosDbOptions _cosmosDbOptions = options.Value;

    public async Task Create(TimedTaskExecution timedTask, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        await container.CreateItemAsync(
            timedTask,
            new PartitionKey(timedTask.ChatId),
            new ItemRequestOptions
            {
                EnableContentResponseOnWrite = false,
            },
            cancellation);
    }

    public async Task Create<T>(IEnumerable<T> items, Func<T, long> chatIdSelector, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        foreach (var item in items)
        {
            await container.CreateItemAsync(
                item,
                new PartitionKey(chatIdSelector(item)),
                new ItemRequestOptions { EnableContentResponseOnWrite = false },
                cancellation);
        }
    }

    public async Task<bool> Exists(long chatId, string name, DateTime date, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        DateTime startOfDay = date.Date;
        DateTime startOfNextDay = startOfDay.AddDays(1d);
        var query = new QueryDefinition("""
            SELECT VALUE c
            FROM c
            WHERE c.chatId = @chatId
                AND c.name = @name
                AND c.date >= @start AND c.date < @end
            """)
            .WithParameter("@chatId", chatId)
            .WithParameter("@name", name)
            .WithParameter("@start", startOfDay)
            .WithParameter("@end", startOfNextDay);

        using var it = container.GetItemQueryIterator<TimedTaskExecution>(
            query,
            requestOptions: new QueryRequestOptions
            {
                MaxConcurrency = 1,
                PartitionKey = new PartitionKey(chatId)
            });
        while (it.HasMoreResults)
        {
            return (await it.ReadNextAsync(cancellation)).Count != 0;
        }

        return false;
    }
    public async Task<TimedTaskQuiz?> GetQuiz(long chatId, string type, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        var partition = new PartitionKey(chatId);
        var lockId = $"quiz-delivery-lock:{type}";
        ItemResponse<JsonElement> deliveryLock;
        try
        {
            deliveryLock = await container.CreateItemAsync(JsonSerializer.SerializeToElement(new
            {
                id = lockId, chatId, type = "quiz-delivery-lock"
            }), partition, cancellationToken: cancellation);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException("Quiz delivery is already pending; manual reconciliation may be required.", ex);
        }
        var query = new QueryDefinition(@"SELECT * FROM c
                WHERE (NOT IS_DEFINED(c.posted) OR c.posted = false)
                    AND c.chatId = @chatId
                    AND c.type = @type
                ORDER BY c._ts
                OFFSET 0 LIMIT 1")
            .WithParameter("@type", type)
            .WithParameter("@chatId", chatId);
        using var it = container.GetItemQueryIterator<TimedTaskQuiz>(
            query,
            requestOptions: new QueryRequestOptions
            {
                MaxConcurrency = 1,
                PartitionKey = new PartitionKey(chatId),
            });
        while (it.HasMoreResults)
        {
            FeedResponse<TimedTaskQuiz> response = await it.ReadNextAsync(cancellation);
            var candidate = response.FirstOrDefault();
            if (candidate == null) break;
            var current = await container.ReadItemAsync<TimedTaskQuiz>(candidate.Id, new PartitionKey(chatId),
                cancellationToken: cancellation);
            if (current.Resource.Posted || current.Resource.Type != type || current.Resource.DeliveryPending)
                throw new InvalidOperationException("Quiz is already posted or its delivery is pending; manual reconciliation may be required.");
            var reserved = await container.PatchItemAsync<TimedTaskQuiz>(candidate.Id, new PartitionKey(chatId),
                new[] { PatchOperation.Set("/deliveryPending", true) },
                new PatchItemRequestOptions { IfMatchEtag = current.ETag }, cancellation);
            reserved.Resource.ETag = reserved.ETag;
            reserved.Resource.DeliveryLockETag = deliveryLock.ETag;
            return reserved.Resource;
        }

        await container.DeleteItemAsync<JsonElement>(lockId, partition,
            new ItemRequestOptions { IfMatchEtag = deliveryLock.ETag }, cancellation);
        return null;
    }

    public async Task CompleteQuiz(TimedTaskQuiz quiz, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        var batch = container.CreateTransactionalBatch(new PartitionKey(quiz.ChatId));
        batch.PatchItem(quiz.Id,
            new[] { PatchOperation.Set("/posted", true), PatchOperation.Set("/deliveryPending", false),
                PatchOperation.Set("/date", DateTime.UtcNow) },
            new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = quiz.ETag ?? throw new InvalidOperationException("Quiz was not reserved.") });
        batch.DeleteItem($"quiz-delivery-lock:{quiz.Type}",
            new TransactionalBatchItemRequestOptions { IfMatchEtag = quiz.DeliveryLockETag ?? throw new InvalidOperationException("Quiz delivery was not locked.") });
        using var response = await batch.ExecuteAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not complete quiz delivery (HTTP {(int)response.StatusCode}); manual reconciliation is required.");
    }

    public async Task<TldrCache> BeginTldr(long chatId, string id, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var cache = new TldrCache { Id = id, ChatId = chatId };
            try
            {
                var current = await container.ReadItemAsync<TldrCache>(id, new PartitionKey(chatId), cancellationToken: cancellation);
                cache = current.Resource;
                cache.ETag = current.ETag;
                if (cache.Summary != null) return cache;
                if (cache.LeaseUntilUtc > DateTime.UtcNow)
                    throw new InvalidOperationException("TLDR for this history is already being generated; retry later.");
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }

            cache.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(10);
            try
            {
                var saved = cache.ETag == null
                    ? await container.CreateItemAsync(cache, new PartitionKey(chatId), cancellationToken: cancellation)
                    : await container.ReplaceItemAsync(cache, id, new PartitionKey(chatId),
                        new ItemRequestOptions { IfMatchEtag = cache.ETag }, cancellation);
                cache.ETag = saved.ETag;
                return cache;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) { }
        }
        throw new InvalidOperationException("Unable to reserve TLDR generation; retry later.");
    }

    public async Task CompleteTldr(TldrCache cache, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        await container.ReplaceItemAsync(cache, cache.Id, new PartitionKey(cache.ChatId),
            new ItemRequestOptions { IfMatchEtag = cache.ETag, EnableContentResponseOnWrite = false }, cancellation);
    }

    public async Task ReleaseTldr(TldrCache cache, CancellationToken cancellation)
    {
        var container = cosmos.GetContainer(_cosmosDbOptions.DatabaseId, _cosmosDbOptions.TimedTasksContainerId);
        await container.DeleteItemAsync<TldrCache>(cache.Id, new PartitionKey(cache.ChatId),
            new ItemRequestOptions { IfMatchEtag = cache.ETag }, cancellation);
    }
}
