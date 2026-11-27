using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Database;
public class TelegramMessageRepository : ITelegramMessageRepository
{
    private readonly CosmosClient _cosmos;
    private readonly CosmosDbOptions _options;
    private readonly ILogger<TelegramMessageRepository> _logger;
    private readonly int _messageTtlSeconds;
    private readonly HistoryRepository? _history;

    public TelegramMessageRepository(
        CosmosClient cosmos,
        IOptions<CosmosDbOptions> options,
        ILogger<TelegramMessageRepository> logger, HistoryRepository? history = null)
    {
        _cosmos = cosmos;
        _options = options.Value;
        _messageTtlSeconds = _options.MessageTtlSeconds;
        _logger = logger;
        _history = history;
    }

    public async Task Create(TelegramMessage message, CancellationToken cancellation)
    {
        if (_history != null && await _history.IsRevoked(message.ChatId, message.UserId, cancellation)) return;
        var container = _cosmos.GetContainer(_options.DatabaseId, _options.MessagesContainerId);
        var date = DateTime.UtcNow.ToString(TelegramMessage.DATE_UTC_FORMAT);
        await container.UpsertItemAsync(
            new TelegramMessage
            {
                Id = $"message:{message.MessageId}",
                ChatId = message.ChatId,
                User = message.User,
                UserId = message.UserId,
                MessageId = message.MessageId,
                MessageThreadId = message.MessageThreadId,
                Message = message.Message,
                Date = message.Date,
                DateUtc = date,
                TTL = _messageTtlSeconds,
            },
            new PartitionKey(message.ChatId),
            new ItemRequestOptions
            {
                EnableContentResponseOnWrite = false,
            },
            cancellation);
    }

    public async IAsyncEnumerable<TelegramMessage> Get(long chatId, DateOnly dateUtc, [EnumeratorCancellation] CancellationToken cancellation)
    {
        var container = _cosmos.GetContainer(_options.DatabaseId, _options.MessagesContainerId);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.dateUtc = @dateUtc ORDER BY c.date DESC")
            .WithParameter("@dateUtc", dateUtc.ToString(TelegramMessage.DATE_UTC_FORMAT));
        using var it = container.GetItemQueryIterator<TelegramMessage>(
            query,
            requestOptions: new QueryRequestOptions
            {
                MaxConcurrency = 1,
                PartitionKey = new PartitionKey(chatId),
            });
        while (it.HasMoreResults)
        {
            FeedResponse<TelegramMessage> response = await it.ReadNextAsync(cancellation);
            foreach (TelegramMessage item in response)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_history != null && await _history.IsRevoked(chatId, item.UserId, cancellation)) continue;
                yield return item;
            }

            if (response.Diagnostics != null)
            {
                _logger.LogWarning("Diagnostics: {diagnostics}", response.Diagnostics.ToString());
            }
        }
    }

    public async IAsyncEnumerable<TelegramMessage> GetAllOrderByDateDescending(long chatId, [EnumeratorCancellation] CancellationToken cancellation)
    {
        var container = _cosmos.GetContainer(_options.DatabaseId, _options.MessagesContainerId);
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.date DESC");
        using var it = container.GetItemQueryIterator<TelegramMessage>(
            query,
            requestOptions: new QueryRequestOptions
            {
                MaxConcurrency = 1,
                PartitionKey = new PartitionKey(chatId),
            });
        while (it.HasMoreResults)
        {
            FeedResponse<TelegramMessage> response = await it.ReadNextAsync(cancellation);
            foreach (TelegramMessage item in response)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_history != null && await _history.IsRevoked(chatId, item.UserId, cancellation)) continue;
                yield return item;
            }

            if (response.Diagnostics != null)
            {
                _logger.LogWarning("Diagnostics: {diagnostics}", response.Diagnostics.ToString());
            }
        }
    }
}
