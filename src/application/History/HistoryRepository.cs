using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public interface IHistoryConsent
{
    bool Allows(long chat);
    Task<bool> IsRevoked(long chat, long user, CancellationToken cancellation);
    Task<bool> HasRevocations(long chat, CancellationToken cancellation);
    Task<string?> GetContextGeneration(long chat, long user, CancellationToken cancellation);
    Task<bool> IsContextAllowed(long chat, long user, string? generation, CancellationToken cancellation);
}

public sealed class HistoryPurgeSupersededException : InvalidOperationException;

public sealed class HistoryRepository : IHistoryConsent
{
    private readonly Container _messages;
    private readonly Container _chunks;
    private readonly HistoryOptions _options;
    public double RequestUnits { get; private set; }
    public static decimal RuCost(double units) => (decimal)units * 0.305m / 1000000;

    private async Task<HistoryReservation?> ChangeBudget(string id, string? token, decimal? reserve,
        decimal charge, CancellationToken cancellation)
    {
        var partition = new PartitionKey(0);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            HistoryUsage ledger;
            string? etag = null;
            try
            {
                var read = await _messages.ReadItemAsync<HistoryUsage>(id, partition, cancellationToken: cancellation);
                RequestUnits += read.RequestCharge;
                ledger = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                RequestUnits += error.RequestCharge;
                ledger = new HistoryUsage { Id = id, ChatId = 0 };
            }
            if (reserve != null)
            {
                if (ledger.UsedUsd + ledger.Reservations.Values.Sum() + reserve > _options.MonthlyBudgetUsd) return null;
                ledger.Reservations.Add(token!, reserve.Value);
            }
            else if (token != null && !ledger.Reservations.Remove(token)) return null;
            ledger.UsedUsd += charge;
            try
            {
                var written = etag == null
                    ? await _messages.CreateItemAsync(ledger, partition, cancellationToken: cancellation)
                    : await _messages.ReplaceItemAsync(ledger, id, partition,
                        new ItemRequestOptions { IfMatchEtag = etag }, cancellation);
                RequestUnits += written.RequestCharge;
                return new(id, token ?? "");
            }
            catch (CosmosException error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                RequestUnits += error.RequestCharge;
            }
        }
        throw new InvalidOperationException("History budget contention exceeded its retry limit.");
    }

    public Task<HistoryReservation?> ReserveBudget(decimal reserve, CancellationToken cancellation)
    {
        if (reserve <= 0) throw new ArgumentOutOfRangeException(nameof(reserve));
        return ChangeBudget($"budget:{DateTime.UtcNow:yyyyMM}", Guid.NewGuid().ToString("N"), reserve, 0, cancellation);
    }

    public Task SettleBudget(HistoryReservation? reservation, decimal charge, CancellationToken cancellation)
    {
        if (charge < 0) throw new ArgumentOutOfRangeException(nameof(charge));
        return ChangeBudget(reservation?.LedgerId ?? $"budget:{DateTime.UtcNow:yyyyMM}", reservation?.Token,
            null, charge, cancellation);
    }

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

    public async Task<bool> IsRevoked(long chat, long user, CancellationToken cancellation) =>
        _options.Allows(chat) && (await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation))?.Value.Granted == false;

    public bool Allows(long chat) => _options.Allows(chat);

    public async Task<bool> HasRevocations(long chat, CancellationToken cancellation) =>
        _options.Allows(chat) && (await Query<int>(chat, new QueryDefinition(
            "SELECT TOP 1 VALUE 1 FROM c WHERE c.kind = 'consent' AND (c.granted = false OR c.wasRevoked = true)"), 1, cancellation)).Count != 0;

    public async Task<string?> GetContextGeneration(long chat, long user, CancellationToken cancellation) =>
        !_options.Allows(chat) ? null : (await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation))?.Value.Generation;

    public async Task<bool> IsContextAllowed(long chat, long user, string? generation, CancellationToken cancellation)
    {
        if (!_options.Allows(chat)) return true;
        var consent = (await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation))?.Value;
        if (consent == null) return generation == null;
        if (!consent.Granted) return false;
        return generation == null ? !consent.WasRevoked : generation == consent.Generation;
    }

    public async Task<string?> GetPurgeEpoch(long chat, long user, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var read = await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation);
            if (read == null || read.Value.Value.Granted || !read.Value.Value.Purging) return null;
            if (!string.IsNullOrEmpty(read.Value.Value.PurgeEpoch)) return read.Value.Value.PurgeEpoch;
            read.Value.Value.PurgeEpoch = Guid.NewGuid().ToString("N");
            try
            {
                var response = await _messages.ReplaceItemAsync(read.Value.Value, read.Value.Value.Id, new PartitionKey(chat),
                    new ItemRequestOptions { IfMatchEtag = read.Value.ETag }, cancellation);
                RequestUnits += response.RequestCharge;
                return read.Value.Value.PurgeEpoch;
            }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                RequestUnits += error.RequestCharge;
            }
        }
        throw new InvalidOperationException("History purge contention exceeded its retry limit.");
    }

    private async Task<(HistoryConsent Value, string ETag)> RequirePurge(long chat, long user, string epoch,
        CancellationToken cancellation)
    {
        var consent = await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation);
        if (consent == null || consent.Value.Value.Granted || !consent.Value.Value.Purging
            || consent.Value.Value.PurgeEpoch != epoch) throw new HistoryPurgeSupersededException();
        return consent.Value;
    }

    public async Task PurgeRolling(long chat, long user, string epoch, CancellationToken cancellation)
    {
        Scope(chat);
        var name = Environment.GetEnvironmentVariable("CosmosTelegramMessagesContainer");
        if (string.IsNullOrWhiteSpace(name)) return;
        var container = _messages.Database.GetContainer(name);
        using var iterator = container.GetItemQueryIterator<JsonElement>(
            new QueryDefinition("SELECT c.id, c._etag FROM c WHERE c.userId = @user").WithParameter("@user", user),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(chat), MaxItemCount = 50 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellation);
            RequestUnits += page.RequestCharge;
            await RequirePurge(chat, user, epoch, cancellation);
            foreach (var row in page)
            {
                try
                {
                    var deleted = await container.DeleteItemAsync<JsonElement>(row.GetProperty("id").GetString()!,
                        new PartitionKey(chat), new ItemRequestOptions { IfMatchEtag = row.GetProperty("_etag").GetString() },
                        cancellationToken: cancellation);
                    RequestUnits += deleted.RequestCharge;
                }
                catch (CosmosException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                    RequestUnits += error.RequestCharge;
                }
            }
        }
    }

    public async Task SetConsent(long chat, long user, bool granted, CancellationToken cancellation, bool purging = false)
    {
        Scope(chat);
        if (user <= 0) throw new ArgumentException("Invalid consent identity.");
        var existing = await Read<HistoryConsent>(_messages, chat, $"consent:{user}", cancellation);
        if (granted && existing?.Value.Purging == true)
            throw new InvalidOperationException("Author purge is still in progress.");
        if (granted && existing?.Value.Granted == true) return;
        if (!granted && purging && existing?.Value.Purging == true) return;
        var consent = new HistoryConsent
        {
            Id = $"consent:{user}", ChatId = chat, UserId = user, Granted = granted, ChangedUtc = DateTime.UtcNow,
            Purging = purging, PurgeEpoch = purging ? Guid.NewGuid().ToString("N") : null,
            Generation = Guid.NewGuid().ToString("N"),
            WasRevoked = !granted || existing?.Value.WasRevoked == true || existing?.Value.Granted == false,
        };
        var response = existing == null
            ? await _messages.CreateItemAsync(consent, new PartitionKey(chat), cancellationToken: cancellation)
            : await _messages.ReplaceItemAsync(consent, consent.Id, new PartitionKey(chat),
                new ItemRequestOptions { IfMatchEtag = existing.Value.ETag }, cancellation);
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
                Generation = Guid.NewGuid().ToString("N"),
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
        if (existing.Deleted && incoming.Deleted && incoming.UserId == null && incoming.User.Length == 0
            && incoming.Text.Length == 0 && !incoming.Consented && (existing.UserId != null || existing.User.Length != 0))
            return true;
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
        HistoryImport? import = null, (long UserId, string Epoch)? purge = null)
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
            var purgeId = purge == null ? null : $"consent:{purge.Value.UserId}";
            var savedMessages = await ReadMany(chat, messages.Select(message => message.Id).Concat(consentIds)
                .Concat(purgeId == null ? Array.Empty<string>() : new[] { purgeId }), cancellation);
            if (purge != null && (!savedMessages.TryGetValue(purgeId!, out var purgeConsent)
                || purgeConsent.Value.Deserialize<HistoryConsent>(HistoryCosmosSerializer.Options) is not
                    { Granted: false, Purging: true } currentPurge
                || currentPurge.PurgeEpoch != purge.Value.Epoch))
                throw new HistoryPurgeSupersededException();
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
                if (purge != null && existing?.UserId != purge.Value.UserId) continue;
                if (message.Origin == "export" && message.ThreadId == null && existing?.ThreadId != null)
                {
                    // Desktop exports can omit topic metadata retained by live or rolling capture.
                    message.ThreadId = existing.ThreadId;
                    message.Normalize();
                }
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
            var inconsistentCounter = false;
            foreach (var delta in deltas)
            {
                controls.TryGetValue(delta.Key, out var saved);
                var counter = saved.Value.ValueKind == JsonValueKind.Undefined ? delta.Value.Value
                    : saved.Value.Deserialize<HistoryActivity>(HistoryCosmosSerializer.Options)!;
                counter.Messages += delta.Value.Delta;
                counter.User = delta.Value.Value.User;
                if (counter.Messages < 0) { inconsistentCounter = true; break; }
                Add(counter.Id, counter, saved.ETag);
            }
            if (inconsistentCounter)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellation);
                continue;
            }
            var guards = changed.Where(item => item.New.Consented && !item.New.Deleted && item.New.UserId != null)
                .Select(item => $"consent:{item.New.UserId}").Distinct().ToArray();
            foreach (var id in guards)
            {
                var consent = savedMessages[id];
                Add(id, consent.Value, consent.ETag);
            }
            if (purgeId != null && !guards.Contains(purgeId))
            {
                var consent = savedMessages[purgeId];
                Add(purgeId, consent.Value, consent.ETag);
            }
            var checkpointIndex = changed.Count + buckets.Length + deltas.Count + guards.Length
                + (purgeId != null && !guards.Contains(purgeId) ? 1 : 0);
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
        int limit, string? continuation, CancellationToken cancellation, bool chunks = false)
    {
        Scope(chat);
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        using var pages = (chunks ? _chunks : _messages).GetItemQueryIterator<JsonElement>(query, continuation,
            new QueryRequestOptions { PartitionKey = new PartitionKey(chat), MaxItemCount = limit, MaxConcurrency = 1 });
        var response = await pages.ReadNextAsync(cancellation);
        RequestUnits += response.RequestCharge;
        if (response.Count > limit) throw new InvalidOperationException("History page exceeded its limit.");
        return (response.ToArray(), response.ContinuationToken);
    }

    public async Task<HistorySweep> GetSweep(long chat, CancellationToken cancellation)
    {
        var saved = await Read<HistorySweep>(_messages, chat, "maintenance:sweep", cancellation);
        if (saved == null) return new HistorySweep { ChatId = chat };
        saved.Value.Value.ETag = saved.Value.ETag;
        return saved.Value.Value;
    }

    public async Task SaveSweep(HistorySweep sweep, CancellationToken cancellation)
    {
        Scope(sweep.ChatId);
        try
        {
            var response = sweep.ETag == null
                ? await _messages.CreateItemAsync(sweep, new PartitionKey(sweep.ChatId), cancellationToken: cancellation)
                : await _messages.ReplaceItemAsync(sweep, sweep.Id, new PartitionKey(sweep.ChatId),
                    new ItemRequestOptions { IfMatchEtag = sweep.ETag }, cancellation);
            RequestUnits += response.RequestCharge;
        }
        catch (CosmosException error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            RequestUnits += error.RequestCharge;
        }
    }

    public async Task SaveChunk(HistoryChunk chunk, CancellationToken cancellation)
    {
        Scope(chunk.ChatId);
        var response = await _chunks.UpsertItemAsync(chunk, new PartitionKey(chunk.ChatId), cancellationToken: cancellation);
        RequestUnits += response.RequestCharge;
    }

    public async Task DeleteActivity(long chat, long user, string epoch, string id, CancellationToken cancellation)
    {
        Scope(chat);
        if (!id.StartsWith("activity:", StringComparison.Ordinal)) throw new ArgumentException("Invalid activity identity.");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var consent = await RequirePurge(chat, user, epoch, cancellation);
            var counter = await Read<HistoryActivity>(_messages, chat, id, cancellation);
            if (counter == null) return;
            if (counter.Value.Value.UserId != user || counter.Value.Value.Messages != 0)
                throw new InvalidOperationException("Author purge has an inconsistent activity counter.");
            var batch = _messages.CreateTransactionalBatch(new PartitionKey(chat));
            batch.ReplaceItem(consent.Value.Id, consent.Value,
                new TransactionalBatchItemRequestOptions { IfMatchEtag = consent.ETag });
            batch.DeleteItem(id, new TransactionalBatchItemRequestOptions { IfMatchEtag = counter.Value.ETag });
            using var response = await batch.ExecuteAsync(cancellation);
            RequestUnits += response.RequestCharge;
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound))
                throw new InvalidOperationException("Author activity purge failed; provider details withheld.");
        }
        throw new InvalidOperationException("Author activity purge contention exceeded its retry limit.");
    }

    public async Task CompletePurge(long chat, long user, string epoch, CancellationToken cancellation)
    {
        var consent = await RequirePurge(chat, user, epoch, cancellation);
        consent.Value.Purging = false;
        consent.Value.PurgeEpoch = null;
        try
        {
            var response = await _messages.ReplaceItemAsync(consent.Value, consent.Value.Id, new PartitionKey(chat),
                new ItemRequestOptions { IfMatchEtag = consent.ETag }, cancellation);
            RequestUnits += response.RequestCharge;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            RequestUnits += error.RequestCharge;
            throw new HistoryPurgeSupersededException();
        }
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
