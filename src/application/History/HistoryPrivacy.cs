using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryPrivacy(HistoryRepository repository, HistoryOptions options)
{
    public bool Allows(long chat) => options.Allows(chat);

    public Task Grant(long chat, long user, CancellationToken cancellation) =>
        repository.SetConsent(chat, user, true, cancellation);

    public async Task Forget(long chat, long user, CancellationToken cancellation)
    {
        if (!options.Allows(chat)) throw new InvalidOperationException("History is disabled in this chat.");
        await repository.SetConsent(chat, user, false, cancellation, purging: true);
        await ResumeForget(chat, user, cancellation);
    }

    public async Task ResumeForget(long chat, long user, CancellationToken cancellation)
    {
        while (true)
        {
            var sources = await repository.Query<HistoryMessage>(chat, new QueryDefinition(
                "SELECT TOP 16 * FROM c WHERE c.kind = 'message' AND c.userId = @user")
                .WithParameter("@user", user), 16, cancellation);
            if (sources.Count == 0) break;
            foreach (var bucket in sources.Select(source => source.Bucket).Distinct())
            {
                while (true)
                {
                    var chunks = await repository.Query<string>(chat, new QueryDefinition(
                        "SELECT TOP 16 VALUE c.id FROM c WHERE c.bucket = @bucket AND IS_DEFINED(c.profile)")
                        .WithParameter("@bucket", bucket), 16, cancellation, chunks: true);
                    if (chunks.Count == 0) break;
                    foreach (var id in chunks) await repository.DeleteChunk(chat, id, cancellation);
                }
            }
            foreach (var source in sources)
            {
                source.Deleted = true;
                source.Consented = false;
                source.Text = "";
                source.User = "";
                source.UserId = null;
            }
            await repository.SaveBatch(sources, cancellation);
            foreach (var bucket in sources.Select(source => source.Bucket).Distinct())
            {
                var work = await repository.GetWork(chat, bucket, cancellation);
                if (work == null) continue;
                work.ActiveChunks = Array.Empty<string>();
                work.Priority = 0;
                work.DueUtc = DateTime.UtcNow;
                await repository.SaveWork(work, cancellation);
            }
        }
        var counters = await repository.Query<HistoryActivity>(chat,
            new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity' AND c.userId = @user")
                .WithParameter("@user", user), 200, cancellation);
        foreach (var counter in counters)
        {
            if (counter.Messages != 0) throw new InvalidOperationException("Author purge has an inconsistent activity counter.");
            await repository.DeleteActivity(chat, counter.Id, cancellation);
        }
        await repository.PurgeRolling(chat, user, cancellation);
        await repository.SetConsent(chat, user, false, cancellation);
    }
}
