using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using System.Text.Json.Serialization;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed record HistoryDispatch(
    [property: JsonPropertyName("chatId")] long ChatId,
    [property: JsonPropertyName("workId")] string WorkId,
    [property: JsonPropertyName("version")] long Version);

public sealed class HistoryIndexer(HistoryRepository repository, HistoryOptions options, ArchiveEmbeddingClient embeddings)
{
    public async Task Index(HistoryDispatch dispatch, CancellationToken cancellation)
    {
        if (!options.Allows(dispatch.ChatId)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var work = await repository.GetWork(dispatch.ChatId, dispatch.WorkId, timeout.Token);
        if (work == null || work.Version != dispatch.Version || work.IndexedVersion == work.Version) return;
        var before = repository.RequestUnits;
        var reservation = await repository.ReserveBudget(0.1m, timeout.Token)
            ?? throw new InvalidOperationException("History indexing paused: monthly budget exhausted.");
        var tokens = 0;
        var providerUncertain = false;
        try
        {
            var sources = await repository.Query<HistoryMessage>(dispatch.ChatId, new QueryDefinition(
                "SELECT * FROM c WHERE c.kind = 'message' AND c.bucket = @bucket AND c.deleted = false AND c.consented = true AND c.forwarded = false")
                .WithParameter("@bucket", work.Id), 5000, timeout.Token);
            if (sources.Sum(source => (long)source.Text.Length) > 2000000)
                throw new InvalidOperationException("Archive hour exceeds its bounded source capacity.");
            var authors = sources.Where(source => source.UserId != null).Select(source => source.UserId!.Value).Distinct().ToArray();
            var eligible = new System.Collections.Generic.HashSet<long>();
            foreach (var author in authors)
                if (await repository.HasConsent(dispatch.ChatId, author, timeout.Token)) eligible.Add(author);
            var chunks = ArchiveChunker.Create(sources.Where(source => source.UserId != null
                && eligible.Contains(source.UserId.Value)).ToArray(), work, Guid.NewGuid().ToString("N"));
            var written = new System.Collections.Generic.List<string>();
            var activated = false;
            try
            {
                foreach (var batch in chunks.Chunk(32))
                {
                    providerUncertain = true;
                    var response = await embeddings.Embed(batch.Select(chunk => chunk.Input).ToArray(), timeout.Token);
                    providerUncertain = false;
                    tokens += response.Tokens;
                    for (var index = 0; index < batch.Length; index++)
                    {
                        batch[index].Record.Embedding = response.Vectors[index];
                        await repository.SaveChunk(batch[index].Record, timeout.Token);
                        written.Add(batch[index].Record.Id);
                    }
                }
                foreach (var author in eligible)
                    if (!await repository.HasConsent(dispatch.ChatId, author, timeout.Token)) return;
                var previous = work.ActiveChunks;
                work.ActiveChunks = written.ToArray();
                work.IndexedVersion = work.Version;
                work.DispatchUntilUtc = null;
                if (!await repository.SaveWork(work, timeout.Token)) return;
                activated = true;
                foreach (var id in previous) await repository.DeleteChunk(dispatch.ChatId, id, timeout.Token);
            }
            finally
            {
                if (!activated)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    foreach (var id in written) await repository.DeleteChunk(dispatch.ChatId, id, cleanup.Token);
                }
            }
        }
        finally
        {
            using var billing = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await repository.SettleBudget(reservation, providerUncertain ? 0.1m
                : HistoryRepository.RuCost(repository.RequestUnits - before + 7) + tokens * 0.027m / 1000000, billing.Token);
        }
    }
}