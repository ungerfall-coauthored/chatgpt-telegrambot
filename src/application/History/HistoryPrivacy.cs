using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryPrivacy(HistoryRepository repository, HistoryOptions options)
{
    public bool Allows(long chat) => options.Allows(chat);

    public static bool TryParseCommand(string text, string botUsername, out bool forget)
    {
        var command = text.Trim();
        forget = command.Equals("/historyforget", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/historyforget" + botUsername, StringComparison.OrdinalIgnoreCase);
        return forget || command.Equals("/historyconsent", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/historyconsent" + botUsername, StringComparison.OrdinalIgnoreCase);
    }

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
        var epoch = await repository.GetPurgeEpoch(chat, user, cancellation);
        if (epoch == null) return;
        try
        {
            while (true)
            {
                var sources = await repository.Query<HistoryMessage>(chat, new QueryDefinition(
                    "SELECT TOP 16 * FROM c WHERE c.kind = 'message' AND c.userId = @user")
                    .WithParameter("@user", user), 16, cancellation);
                if (sources.Count == 0) break;
                var chunks = new System.Collections.Generic.List<string>();
                foreach (var bucket in sources.Select(source => source.Bucket).Distinct())
                {
                    chunks.AddRange(await repository.Query<string>(chat, new QueryDefinition(
                        "SELECT VALUE c.id FROM c WHERE c.bucket = @bucket AND IS_DEFINED(c.profile)")
                        .WithParameter("@bucket", bucket), 5000, cancellation, chunks: true));
                }
                foreach (var source in sources)
                {
                    source.Deleted = true;
                    source.Consented = false;
                    source.Text = "";
                    source.User = "";
                    source.UserId = null;
                }
                if (await repository.SaveBatch(sources, cancellation, purge: (user, epoch)) > 0)
                    foreach (var id in chunks) await repository.DeleteChunk(chat, id, cancellation);
            }
            var counters = await repository.Query<HistoryActivity>(chat,
                new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity' AND c.userId = @user")
                    .WithParameter("@user", user), 200, cancellation);
            foreach (var counter in counters)
                await repository.DeleteActivity(chat, user, epoch, counter.Id, cancellation);
            await repository.PurgeRolling(chat, user, epoch, cancellation);
            await repository.CompletePurge(chat, user, epoch, cancellation);
        }
        catch (HistoryPurgeSupersededException)
        {
        }
    }
}
