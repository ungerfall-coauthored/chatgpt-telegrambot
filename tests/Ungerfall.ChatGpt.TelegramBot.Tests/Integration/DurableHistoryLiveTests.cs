using System.Net;
using Microsoft.Azure.Cosmos;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class DurableHistoryLiveTests
{
    [SkippableFact]
    public async Task CanonicalBatchesReplayEditsDeletionConsentAndExactActivityUseRealCosmos()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DURABLE_HISTORY_LIVE_TEST") == "1",
            "Durable history live tests are not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var client = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new HistoryCosmosSerializer(), ConnectionMode = ConnectionMode.Gateway });
        var database = client.GetDatabase(Environment.GetEnvironmentVariable("CosmosDatabase"));
        Container? fixture = null;
        try
        {
            var serverless = false;
            try { await database.ReadThroughputAsync(cancellationToken: timeout.Token); }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.BadRequest
                && (error.Message.Contains("not supported for serverless accounts", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("not allowed in serverless accounts", StringComparison.OrdinalIgnoreCase)))
            {
                serverless = true;
            }
            Assert.True(serverless, "No fixture may be created without serverless verification.");
            var name = "history-core-test-" + Guid.NewGuid().ToString("N");
            fixture = (await database.CreateContainerAsync(
                new ContainerProperties(name, "/chatId") { DefaultTimeToLive = 3600 },
                cancellationToken: timeout.Token)).Container;
            var repository = new HistoryRepository(client, database.Id, new HistoryOptions
            {
                Enabled = true, Chats = new HashSet<long> { -1, -2 }, MessagesContainer = name, ChunksContainer = name,
            });
            await repository.EnsureImportConsent(-1, 42, timeout.Token);
            var records = Enumerable.Range(1, 16).Select(index => new HistoryMessage
            {
                ChatId = -1, SourceChatId = -1, MessageId = index, UserId = 42, User = "Synthetic",
                Date = new DateTime(2024, 1, 1, 0, 0, index, DateTimeKind.Utc),
                Text = index == 1 ? new string('x', 10000) : "Synthetic original message",
                Consented = true, Origin = "live", UpdateId = index,
            }).ToArray();
            Assert.Equal(16, await repository.SaveBatch(records, timeout.Token));
            Assert.Equal(0, await repository.SaveBatch(records, timeout.Token));
            var activity = await repository.Query<HistoryActivity>(-1,
                new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity'"), 10, timeout.Token);
            Assert.Equal(16, Assert.Single(activity).Messages);
            var original = (await repository.GetMessage(-1, records[0].Id, timeout.Token))!;
            Assert.Equal(new string('x', 10000), original.Text);
            Assert.Null(await repository.GetMessage(-2, original.Id, timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetMessage(-3, original.Id, timeout.Token));
            var oldImport = new HistoryMessage
            {
                ChatId = -1, SourceChatId = -1, MessageId = 1, UserId = 42, User = "Synthetic",
                Date = original.Date, Text = "Older export", Origin = "export", Consented = true,
            };
            Assert.False(await repository.SaveMessage(oldImport, timeout.Token));
            original.Text = "Corrected current source";
            original.UpdateId = 100;
            Assert.True(await repository.SaveMessage(original, timeout.Token));
            original.Deleted = true;
            original.Text = "";
            Assert.True(await repository.SaveMessage(original, timeout.Token));
            Assert.False(await repository.SaveMessage(oldImport, timeout.Token));
            activity = await repository.Query<HistoryActivity>(-1,
                new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity'"), 10, timeout.Token);
            Assert.Equal(15, Assert.Single(activity).Messages);
            var exact = await repository.Query<System.Text.Json.JsonElement>(-1, new QueryDefinition(
                "SELECT c.yearUtc, COUNT(1) AS messages FROM c WHERE c.kind = 'message' AND c.deleted = false GROUP BY c.yearUtc"),
                10, timeout.Token);
            Assert.Equal(15, Assert.Single(exact).GetProperty("messages").GetInt32());
            var hash = HistoryHash.Of("Synthetic lease fixture");
            var checkpoint = new HistoryImport
            {
                Id = "import:" + hash, ArchiveHash = hash, ChatId = -1, LeaseOwner = Guid.NewGuid().ToString("N"),
                LeaseUntilUtc = DateTime.UtcNow.AddMinutes(3),
            };
            await repository.SaveImport(checkpoint, timeout.Token);
            var stale = (await repository.GetImport(-1, hash, timeout.Token))!;
            checkpoint.ProcessedEntries = 10;
            await repository.SaveImport(checkpoint, timeout.Token);
            var uncommitted = new HistoryMessage
            {
                ChatId = -1, SourceChatId = -1, MessageId = 100, Date = records[1].Date,
                Text = "Synthetic uncommitted record", Consented = true, UserId = 42,
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SaveBatch([uncommitted], timeout.Token, stale));
            Assert.Null(await repository.GetMessage(-1, uncommitted.Id, timeout.Token));
            Assert.Equal(10, (await repository.GetImport(-1, hash, timeout.Token))!.ProcessedEntries);
            await repository.SetConsent(-1, 42, false, timeout.Token);
            Assert.False(await repository.EnsureImportConsent(-1, 42, timeout.Token));
            Assert.False(await repository.HasConsent(-1, 42, timeout.Token));
            Assert.False(await repository.SaveMessage(uncommitted, timeout.Token));
        }
        catch (CosmosException error)
        {
            throw new InvalidOperationException($"Durable history live test failed: HTTP {(int)error.StatusCode}; details withheld.");
        }
        finally
        {
            if (fixture != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await fixture.DeleteContainerAsync(cancellationToken: cleanup.Token);
            }
        }
    }
}
