using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryRepository
{
    private readonly Container _messages;
    private readonly Container _chunks;
    private readonly HistoryOptions _options;
    public double RequestUnits { get; private set; }

    public HistoryRepository(CosmosClient client, string database, HistoryOptions options)
    {
        _options = options;
        _messages = client.GetContainer(database, options.MessagesContainer);
        _chunks = client.GetContainer(database, options.ChunksContainer);
    }

    private void Scope(long chat)
    {
        if (!_options.Chats.Contains(chat)) throw new InvalidOperationException("History chat is not configured.");
    }

    private async Task<(T Value, string ETag)?> Read<T>(Container container, long chat, string id, CancellationToken cancellation)
    {
        Scope(chat);
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(chat), cancellationToken: cancellation);
            RequestUnits += response.RequestCharge;
            return (response.Resource, response.ETag);
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            RequestUnits += error.RequestCharge;
            return null;
        }
    }

    public async Task<HistoryMessage?> GetMessage(long chat, string id, CancellationToken cancellation)
    {
        var value = await Read<HistoryMessage>(_messages, chat, id, cancellation);
        if (value == null) return null;
        value.Value.Value.ETag = value.Value.ETag;
        return value.Value.Value;
    }

    public async Task<HistoryWork?> GetWork(long chat, string id, CancellationToken cancellation)
    {
        var value = await Read<HistoryWork>(_messages, chat, id, cancellation);
        if (value == null) return null;
        value.Value.Value.ETag = value.Value.ETag;
        return value.Value.Value;
    }

    public async Task<bool> HasConsent(long chat, long user, CancellationToken cancellation) =>
        (await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation))?.Value.Granted == true;

    public async Task SetConsent(long chat, long user, bool granted, CancellationToken cancellation)
    {
        Scope(chat);
        if (user <= 0) throw new ArgumentException("Invalid consent identity.");
        var response = await _messages.UpsertItemAsync(new HistoryConsent
        {
            Id = $"consent:{user}", ChatId = chat, UserId = user, Granted = granted, ChangedUtc = DateTime.UtcNow,
        }, new PartitionKey(chat), cancellationToken: cancellation);
        RequestUnits += response.RequestCharge;
    }

    public async Task<bool> EnsureImportConsent(long chat, long user, CancellationToken cancellation)
    {
        Scope(chat);
        if (user <= 0) throw new ArgumentException("Invalid consent identity.");
        var existing = await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation);
        if (existing != null) return existing.Value.Value.Granted;
        try
        {
            var response = await _messages.CreateItemAsync(new HistoryConsent
            {
                Id = $"consent:{user}", ChatId = chat, UserId = user, Granted = true, ChangedUtc = DateTime.UtcNow,
            }, new PartitionKey(chat), cancellationToken: cancellation);
            RequestUnits += response.RequestCharge;
            return true;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            RequestUnits += error.RequestCharge;
            return await HasConsent(chat, user, cancellation);
        }
    }

    public static bool Supersedes(HistoryMessage incoming, HistoryMessage existing)
    {
        if (existing.Deleted && !incoming.Deleted) return false;
        if (existing.Revision == incoming.Revision) return false;
        if (incoming.Deleted && !existing.Deleted) return true;
        if (incoming.Origin == "legacy") return false;
        var incomingDate = incoming.EditedAtUtc ?? incoming.Date;
        var existingDate = existing.EditedAtUtc ?? existing.Date;
        if (incomingDate != existingDate) return incomingDate > existingDate;
        if (incoming.Origin == "export" && existing.Origin == "live") return false;
        if (incoming.Origin == "live" && existing.Origin == "live") return incoming.UpdateId > existing.UpdateId;
        return true;
    }

    public async Task<bool> SaveMessage(HistoryMessage message, CancellationToken cancellation) =>
        await SaveBatch(new[] { message }, cancellation) > 0;

    private async Task<Dictionary<string, (JsonElement Value, string ETag)>> ReadMany(long chat,
        IEnumerable<string> ids, CancellationToken cancellation)
    {
        var keys = ids.Distinct().Select(id => (id, new PartitionKey(chat))).ToArray();
        if (keys.Length == 0) return [];
        var response = await _messages.ReadManyItemsAsync<JsonElement>(keys, cancellationToken: cancellation);
        RequestUnits += response.RequestCharge;
        return response.ToDictionary(value => value.GetProperty("id").GetString()!,
            value => (value, value.GetProperty("_etag").GetString()!));
    }

    public async Task<int> SaveBatch(IReadOnlyList<HistoryMessage> messages, CancellationToken cancellation,
        HistoryImport? import = null)
    {
        if (messages.Count is < 1 or > 16) throw new ArgumentException("History batches require 1-16 messages.");
        var chat = messages[0].ChatId;
        Scope(chat);
        if (import != null && import.ChatId != chat) throw new ArgumentException("Import checkpoint chat differs.");
        foreach (var message in messages)
        {
            if (message.ChatId != chat) throw new ArgumentException("History batch contains multiple chats.");
            message.Normalize();
        }
        if (messages.Select(message => message.Id).Distinct().Count() != messages.Count)
            throw new ArgumentException("History batch contains duplicate source identities.");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var consentIds = messages.Where(message => message.Consented && !message.Deleted && message.UserId != null)
                .Select(message => $"consent:{message.UserId}").Distinct().ToArray();
            var savedMessages = await ReadMany(chat, messages.Select(message => message.Id).Concat(consentIds), cancellation);
            var changed = new List<(HistoryMessage New, HistoryMessage? Old, string? ETag)>();
            foreach (var message in messages)
            {
                if (message.Consented && !message.Deleted && message.UserId != null
                    && (!savedMessages.TryGetValue($"consent:{message.UserId}", out var consent)
                        || !consent.Value.GetProperty("granted").GetBoolean()))
                    continue;
                var saved = savedMessages.TryGetValue(message.Id, out var row) ? row : default;
                var existing = saved.Value.ValueKind == JsonValueKind.Undefined ? null
                    : saved.Value.Deserialize<HistoryMessage>(HistoryCosmosSerializer.Options);
                if (existing != null && !Supersedes(message, existing)) continue;
                if (existing != null && (existing.Date != message.Date || existing.ThreadId != message.ThreadId))
                    throw new InvalidOperationException("History source timestamp or topic changed.");
                changed.Add((message, existing, saved.ETag));
            }
            if (changed.Count == 0)
            {
                if (import != null) await SaveImport(import, cancellation);
                return 0;
            }
            var deltas = new Dictionary<string, (HistoryActivity Value, int Delta)>();
            foreach (var item in changed)
            {
                foreach (var part in new[] { (Counter(item.Old), -1), (Counter(item.New), 1) })
                {
                    if (part.Item1 == null) continue;
                    var counter = part.Item1;
                    deltas[counter.Id] = (counter, deltas.GetValueOrDefault(counter.Id).Delta + part.Item2);
                }
            }
            var buckets = changed.GroupBy(item => item.New.Bucket).ToArray();
            var controls = await ReadMany(chat, deltas.Keys.Concat(buckets.Select(bucket => bucket.Key)), cancellation);
            var transaction = _messages.CreateTransactionalBatch(new PartitionKey(chat));
            void Add<T>(string id, T record, string? etag)
            {
                if (etag == null) transaction.CreateItem(record);
                else transaction.ReplaceItem(id, record, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag });
            }
            foreach (var item in changed) Add(item.New.Id, item.New, item.ETag);
            foreach (var bucket in buckets)
            {
                controls.TryGetValue(bucket.Key, out var saved);
                var work = saved.Value.ValueKind == JsonValueKind.Undefined
                    ? new HistoryWork { Id = bucket.Key, ChatId = chat }
                    : saved.Value.Deserialize<HistoryWork>(HistoryCosmosSerializer.Options)!;
                var priority = bucket.Any(item => item.New.Origin == "live") ? 0 : 1;
                work.Priority = work.Version > work.IndexedVersion ? Math.Min(work.Priority, priority) : priority;
                work.Version++;
                work.DueUtc = DateTime.UtcNow.AddSeconds(priority == 0 ? 30 : 120);
                work.DispatchUntilUtc = null;
                Add(work.Id, work, saved.ETag);
            }
            foreach (var delta in deltas)
            {
                controls.TryGetValue(delta.Key, out var saved);
                var counter = saved.Value.ValueKind == JsonValueKind.Undefined ? delta.Value.Value
                    : saved.Value.Deserialize<HistoryActivity>(HistoryCosmosSerializer.Options)!;
                counter.Messages += delta.Value.Delta;
                counter.User = delta.Value.Value.User;
                if (counter.Messages < 0) throw new InvalidOperationException("History activity counter is inconsistent.");
                Add(counter.Id, counter, saved.ETag);
            }
            var guards = changed.Where(item => item.New.Consented && !item.New.Deleted && item.New.UserId != null)
                .Select(item => $"consent:{item.New.UserId}").Distinct().ToArray();
            foreach (var id in guards)
            {
                var consent = savedMessages[id];
                Add(id, consent.Value, consent.ETag);
            }
            var checkpointIndex = changed.Count + buckets.Length + deltas.Count + guards.Length;
            if (import != null)
            {
                ValidateImport(import);
                Add(import.Id, import, import.ETag);
            }
            using var response = await transaction.ExecuteAsync(cancellation);
            RequestUnits += response.RequestCharge;
            if (response.IsSuccessStatusCode)
            {
                if (import != null) import.ETag = response.GetOperationResultAtIndex<HistoryImport>(checkpointIndex).ETag;
                return changed.Count;
            }
            if (import != null && response[checkpointIndex].StatusCode == HttpStatusCode.PreconditionFailed)
                throw new InvalidOperationException("Import lease changed; no source batch was committed.");
            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
                throw new InvalidOperationException($"History transaction failed: HTTP {(int)response.StatusCode}; details withheld.");
            await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellation);
        }
        throw new InvalidOperationException("History transaction contention exceeded its retry limit.");
    }

    private static HistoryActivity? Counter(HistoryMessage? message) => message == null || message.Deleted || !message.Consented
        ? null : new HistoryActivity
        {
            Id = $"activity:{(message.UserId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? HistoryHash.Of(message.User))}:{message.YearUtc}",
            ChatId = message.ChatId, UserId = message.UserId, User = message.User, YearUtc = message.YearUtc,
        };

    public async Task<bool> SaveWork(HistoryWork work, CancellationToken cancellation)
    {
        Scope(work.ChatId);
        try
        {
            var response = await _messages.ReplaceItemAsync(work, work.Id, new PartitionKey(work.ChatId),
                new ItemRequestOptions { IfMatchEtag = work.ETag }, cancellation);
            RequestUnits += response.RequestCharge;
            work.ETag = response.ETag;
            return true;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            RequestUnits += error.RequestCharge;
            return false;
        }
    }

    public async Task<IReadOnlyList<T>> Query<T>(long chat, QueryDefinition query, int maximum,
        CancellationToken cancellation, bool chunks = false, double ruLimit = 1000)
    {
        Scope(chat);
        if (maximum is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximum));
        var result = new List<T>();
        double charge = 0;
        using var pages = (chunks ? _chunks : _messages).GetItemQueryIterator<T>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(chat), MaxItemCount = Math.Min(maximum, 100),
                MaxConcurrency = 1,
            });
        while (pages.HasMoreResults)
        {
            if (charge >= ruLimit) throw new InvalidOperationException("History query RU budget exceeded; result is incomplete.");
            var page = await pages.ReadNextAsync(cancellation);
            RequestUnits += page.RequestCharge;
            charge += page.RequestCharge;
            result.AddRange(page);
            if (result.Count > maximum) throw new InvalidOperationException("History query result limit exceeded; result is incomplete.");
        }
        return result;
    }

    public async Task<(JsonElement[] Rows, string? Continuation)> Page(long chat, QueryDefinition query,
        int limit, string? continuation, CancellationToken cancellation)
    {
        Scope(chat);
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        using var pages = _messages.GetItemQueryIterator<JsonElement>(query, continuation,
            new QueryRequestOptions { PartitionKey = new PartitionKey(chat), MaxItemCount = limit, MaxConcurrency = 1 });
        var response = await pages.ReadNextAsync(cancellation);
        RequestUnits += response.RequestCharge;
        if (response.Count > limit) throw new InvalidOperationException("History page exceeded its limit.");
        return (response.ToArray(), response.ContinuationToken);
    }

    public async Task SaveChunk(HistoryChunk chunk, CancellationToken cancellation)
    {
        Scope(chunk.ChatId);
        var response = await _chunks.UpsertItemAsync(chunk, new PartitionKey(chunk.ChatId), cancellationToken: cancellation);
        RequestUnits += response.RequestCharge;
    }

    public async Task DeleteChunk(long chat, string id, CancellationToken cancellation)
    {
        Scope(chat);
        try
        {
            var response = await _chunks.DeleteItemAsync<HistoryChunk>(id, new PartitionKey(chat), cancellationToken: cancellation);
            RequestUnits += response.RequestCharge;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            RequestUnits += error.RequestCharge;
        }
    }

    public async Task<HistoryImport?> GetImport(long chat, string hash, CancellationToken cancellation)
    {
        var response = await Read<HistoryImport>(_messages, chat, "import:" + hash, cancellation);
        if (response == null) return null;
        response.Value.Value.ETag = response.Value.ETag;
        return response.Value.Value;
    }

    private static void ValidateImport(HistoryImport import)
    {
        if (import.Id != "import:" + import.ArchiveHash || import.LeaseOwner.Length != 32
            || import.LeaseUntilUtc <= DateTime.UtcNow || import.ProcessedEntries < 0)
            throw new InvalidOperationException("Import checkpoint requires an active lease.");
    }

    public async Task SaveImport(HistoryImport import, CancellationToken cancellation)
    {
        Scope(import.ChatId);
        ValidateImport(import);
        var response = import.ETag == null
            ? await _messages.CreateItemAsync(import, new PartitionKey(import.ChatId), cancellationToken: cancellation)
            : await _messages.ReplaceItemAsync(import, import.Id, new PartitionKey(import.ChatId),
                new ItemRequestOptions { IfMatchEtag = import.ETag }, cancellation);
        RequestUnits += response.RequestCharge;
        import.ETag = response.ETag;
    }
}
