using Microsoft.Azure.Cosmos;
using Ungerfall.ChatGpt.TelegramBot.History;

internal static class ClientVerification
{
    public static async Task Run(HistoryOptions configured)
    {
        var chat = -4000000000000000 - Random.Shared.NextInt64(1, 1000000000);
        var options = new HistoryOptions
        {
            Enabled = true, Chats = new HashSet<long> { chat }, MessagesContainer = configured.MessagesContainer,
            ChunksContainer = configured.ChunksContainer,
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var client = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new HistoryCosmosSerializer(), ConnectionMode = ConnectionMode.Gateway });
        var database = Environment.GetEnvironmentVariable("CosmosDatabase")!;
        var repository = new HistoryRepository(client, database, options);
        try
        {
            var original = new HistoryMessage
            {
                ChatId = chat, SourceChatId = chat, MessageId = -1, Origin = "export",
                Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Text = "Synthetic compatibility fixture", Consented = true, UserId = 42, User = "Synthetic",
            };
            await repository.EnsureImportConsent(chat, 42, cancellation.Token);
            if (!await repository.SaveMessage(original, cancellation.Token)
                || (await repository.GetMessage(chat, original.Id, cancellation.Token))?.Text != original.Text)
                throw new InvalidOperationException("Production Cosmos SDK source roundtrip failed.");
            var vector = new float[512];
            vector[0] = 1;
            await repository.SaveChunk(new HistoryChunk
            {
                Id = "client-verification", ChatId = chat, Embedding = vector, MessageRefs = [original.Id],
                Revisions = [original.Revision], Starts = [0], Lengths = [original.Text.Length],
            }, cancellation.Token);
            var hits = await repository.Query<System.Text.Json.JsonElement>(chat,
                new QueryDefinition("SELECT TOP 1 c.id FROM c ORDER BY VectorDistance(c.embedding, @vector)")
                    .WithParameter("@vector", vector), 1, cancellation.Token, chunks: true);
            if (hits.Count != 1 || hits[0].GetProperty("id").GetString() != "client-verification")
                throw new InvalidOperationException("Production Cosmos SDK vector query failed.");
            var work = (await repository.GetWork(chat, original.Bucket, cancellation.Token))!;
            await new HistoryIndexer(repository, options, new ArchiveEmbeddingClient())
                .Index(new(chat, work.Id, work.Version), cancellation.Token);
            var indexed = (await repository.GetWork(chat, work.Id, cancellation.Token))!;
            if (indexed.IndexedVersion != indexed.Version || indexed.ActiveChunks.Length != 1)
                throw new InvalidOperationException("Production Cosmos SDK asynchronous index path failed.");
            var tools = new HistoryTools(client, database, options, new ArchiveEmbeddingClient());
            var context = new Ungerfall.ChatGpt.TelegramBot.Storage.StorageContext(chat, HistoryOptions.ReaderUserId, 2, "Synthetic exact count");
            var count = await tools.Execute("history_query",
                """{"select":"VALUE COUNT(1)","predicate":"","groupBy":null,"orderBy":null,"limit":1,"cursor":null}""",
                context, cancellation.Token);
            if (count.Status != "ok")
                throw new InvalidOperationException("Production Cosmos SDK history query failed.");
            var oldGeneration = await repository.GetContextGeneration(chat, 42, cancellation.Token);
            await repository.SetConsent(chat, 42, false, cancellation.Token, purging: true);
            var epoch = (await repository.GetPurgeEpoch(chat, 42, cancellation.Token))!;
            var privacy = new HistoryPrivacy(repository, options);
            await privacy.Forget(chat, 42, cancellation.Token);
            if ((await repository.GetMessage(chat, original.Id, cancellation.Token))?.UserId != null)
                throw new InvalidOperationException("Production Cosmos SDK author purge failed.");
            await repository.SetConsent(chat, 42, true, cancellation.Token);
            if (await repository.IsContextAllowed(chat, 42, oldGeneration, cancellation.Token))
                throw new InvalidOperationException("Production Cosmos SDK stale context generation remained visible.");
            var later = new HistoryMessage
            {
                ChatId = chat, SourceChatId = chat, MessageId = -2, Origin = "export",
                Date = original.Date, Text = "Synthetic later fixture", Consented = true, UserId = 42, User = "Synthetic",
            };
            await repository.SaveMessage(later, cancellation.Token);
            await privacy.ResumeForget(chat, 42, cancellation.Token);
            if (!await repository.HasConsent(chat, 42, cancellation.Token)
                || (await repository.GetMessage(chat, later.Id, cancellation.Token))?.Text != later.Text)
                throw new InvalidOperationException("Production Cosmos SDK stale purge changed renewed consent.");
            try
            {
                await repository.CompletePurge(chat, 42, epoch, cancellation.Token);
                throw new InvalidOperationException("Production Cosmos SDK stale purge completion was accepted.");
            }
            catch (HistoryPurgeSupersededException) { }
            Console.WriteLine($"Actual application Cosmos SDK {typeof(CosmosClient).Assembly.GetName().Version}: source roundtrip, vector SQL, embedding/index activation, history query, purge and stale generation fencing passed.");
        }
        catch (CosmosException error)
        {
            throw new InvalidOperationException($"Production client verification failed: HTTP {(int)error.StatusCode}; details withheld.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var chunks = await repository.Query<string>(chat, new QueryDefinition("SELECT VALUE c.id FROM c"),
                20, cleanup.Token, chunks: true);
            foreach (var id in chunks) await repository.DeleteChunk(chat, id, cleanup.Token);
            var ids = await repository.Query<string>(chat,
                new QueryDefinition("SELECT VALUE c.id FROM c"), 20, cleanup.Token);
            foreach (var id in ids)
                await client.GetContainer(database, options.MessagesContainer).DeleteItemAsync<object>(
                    id, new PartitionKey(chat), cancellationToken: cleanup.Token);
        }
    }
}
