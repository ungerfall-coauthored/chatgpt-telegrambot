using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

public sealed class HistoryFunctions(HistoryRepository repository, HistoryOptions options,
    ServiceBusClient bus, HistoryIndexer indexer, HistoryPrivacy privacy)
{
    [Function("DispatchHistory")]
    public async Task Dispatch([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken cancellation)
    {
        if (!options.Enabled) return;
        await using var sender = bus.CreateSender(HistoryOptions.QueueName);
        foreach (var chat in options.Chats)
        {
            var before = repository.RequestUnits;
            var purges = await repository.Query<HistoryConsent>(chat, new QueryDefinition(
                "SELECT TOP 4 * FROM c WHERE c.kind = 'consent' AND c.purging = true"), 4, cancellation);
            foreach (var purge in purges) await privacy.ResumeForget(chat, purge.UserId, cancellation);
            var work = await repository.Query<HistoryWork>(chat, new QueryDefinition(
                "SELECT TOP 16 * FROM c WHERE c.kind = 'work' AND c.version > c.indexedVersion AND c.dueUtc <= @now "
                + "AND (IS_NULL(c.dispatchUntilUtc) OR NOT IS_DEFINED(c.dispatchUntilUtc) OR c.dispatchUntilUtc < @now) ORDER BY c.priority ASC")
                .WithParameter("@now", DateTime.UtcNow.ToString("O")), 16, cancellation);
            foreach (var item in work)
            {
                var current = await repository.GetWork(chat, item.Id, cancellation);
                if (current == null || current.Version == current.IndexedVersion || current.DispatchUntilUtc > DateTime.UtcNow) continue;
                current.DispatchUntilUtc = DateTime.UtcNow.AddMinutes(5);
                if (!await repository.SaveWork(current, cancellation)) continue;
                var payload = new HistoryDispatch(chat, current.Id, current.Version);
                await HistoryQueueClient.Send(sender, payload, cancellation);
            }
            if (DateTime.UtcNow.Minute % 5 == 0)
            {
                var sweep = await repository.GetSweep(chat, cancellation);
                if (sweep.Continuation == null) sweep.Cutoff = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                var page = await repository.Page(chat, new QueryDefinition(
                    "SELECT c.id, c.bucket FROM c WHERE IS_DEFINED(c.profile) AND c._ts < @cutoff")
                    .WithParameter("@cutoff", sweep.Cutoff), 32, sweep.Continuation, cancellation, chunks: true);
                foreach (var row in page.Rows)
                {
                    var id = row.GetProperty("id").GetString()!;
                    var manifest = await repository.GetWork(chat, row.GetProperty("bucket").GetString()!, cancellation);
                    if (manifest == null || !manifest.ActiveChunks.Contains(id))
                        await repository.DeleteChunk(chat, id, cancellation);
                }
                sweep.Continuation = page.Continuation;
                await repository.SaveSweep(sweep, cancellation);
            }
            await repository.SettleBudget(null, HistoryRepository.RuCost(repository.RequestUnits - before + 7), cancellation);
        }
    }

    [Function("IndexHistory")]
    public Task Index([ServiceBusTrigger(HistoryOptions.QueueName, Connection = "ServiceBusConnection")] HistoryDispatch dispatch,
        CancellationToken cancellation) => indexer.Index(dispatch, cancellation);
}